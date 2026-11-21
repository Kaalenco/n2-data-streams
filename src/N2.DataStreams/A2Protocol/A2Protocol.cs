using System.Net;

namespace N2.DataStreams.A2Protocol;

public class AS2Send
{
    public static HttpStatusCode SendFile(
        Uri uri, 
        string filename, 
        byte[] fileData, 
        string from, 
        string to, 
        ProxySettings proxySettings, 
        int timeoutMs, 
        string signingCertFilename, 
        string signingCertPassword, 
        string recipientCertFilename)
    {
        if (string.IsNullOrEmpty(filename)) throw new ArgumentNullException("filename");

        if (fileData.Length == 0) throw new ArgumentException("filedata");

        byte[] content = fileData;

        // Configure the handler (optional proxy)
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (!String.IsNullOrEmpty(proxySettings.Name))
        {
            var proxy = new WebProxy(proxySettings.Name)
            {
                Credentials = new NetworkCredential
                {
                    Domain = proxySettings.Domain,
                    UserName = proxySettings.Username,
                    Password = proxySettings.Password
                }
            };
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        using var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);

        request.Headers.UserAgent.ParseAdd("MY-SENDING-AGENT");

        //These Headers are common to all transactions
        request.Headers.TryAddWithoutValidation("Mime-Version", "1.0");
        request.Headers.TryAddWithoutValidation("AS2-Version", "1.2");
        request.Headers.TryAddWithoutValidation("AS2-From", from);
        request.Headers.TryAddWithoutValidation("AS2-To", to);
        request.Headers.TryAddWithoutValidation("Subject", filename + " transmission.");
        request.Headers.TryAddWithoutValidation("Message-Id", "<AS2_" + DateTime.Now.ToString("hhmmssddd") + ">");

        string contentType = (Path.GetExtension(filename) == ".xml") ? "application/xml" : "application/EDIFACT";

        bool encrypt = !string.IsNullOrEmpty(recipientCertFilename);
        bool sign = !string.IsNullOrEmpty(signingCertFilename);

        var contentHeaders = new List<KeyValuePair<string, string>>();
        if (!sign && !encrypt)
        {
            request.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", "binary");
            contentHeaders.Add(new("Content-Disposition", "inline; filename=\"" + filename + "\""));
        }
        if (sign)
        {
            // Wrap the file data with a mime header
            content = AS2Utilities.CreateMessage(contentType, "binary", "", content);

            content = AS2Utilities.Sign(content, signingCertFilename, signingCertPassword, out contentType);

            request.Headers.TryAddWithoutValidation("EDIINT-Features", "multiple-attachments");
        }
        if (encrypt)
        {
            byte[] signedContentTypeHeader = System.Text.Encoding.ASCII.GetBytes("Content-Type: " + contentType + Environment.NewLine);
            byte[] contentWithContentTypeHeaderAdded = AS2Utilities.ConcatBytes(signedContentTypeHeader, content);

            content = AS2Encryption.Encrypt(contentWithContentTypeHeaderAdded, recipientCertFilename, EncryptionAlgorithm.DES3);
            contentType = "application/pkcs7-mime; smime-type=enveloped-data; name=\"smime.p7m\"";
        }

        var body = new ByteArrayContent(content);
        body.Headers.Remove("Content-Type");
        body.Headers.TryAddWithoutValidation("Content-Type", contentType);
        foreach (var header in contentHeaders)
        {
            body.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = body;

        // Synchronous send keeps the public signature; non-success status codes throw, as before.
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.StatusCode;
    }
}
