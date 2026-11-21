using System.Collections.Specialized;
using System.Net;
using System.Text;

namespace N2.DataStreams.A2Protocol;

public interface IHttpContext
{
    WebRequest Request { get; }
    WebResponse Response { get; }
    NameValueCollection QueryString { get; }
}

public class A2Listener
{
    public void ProcessRequest(IHttpContext context, string dropLocation)
    {
        var sTo = context.Request.Headers["AS2-To"];
        var sFrom = context.Request.Headers["AS2-From"];
        var sMessageID = context.Request.Headers["Message-ID"];

        if (context.Request.Method == "POST" || context.Request.Method == "PUT" ||
           (context.Request.Method == "GET" && context.QueryString.Count > 0))
        {
            if (string.IsNullOrEmpty(sFrom)
                || string.IsNullOrEmpty(sTo))
            {
                //Invalid AS2 Request.
                //Section 6.2 The AS2-To and AS2-From header fields MUST be present
                //    in all AS2 messages
                if (!(context.Request.Method == "GET" && string.IsNullOrEmpty(context.QueryString[0])))
                {
                    AS2Receive.BadRequest(context.Response, "Invalid or unauthorized AS2 request received.");
                }
            }
            else
            {
                AS2Receive.Process(context.Request, dropLocation);
            }
        }
        else
        {
            AS2Receive.GetMessage(context.Response);
        }
    }
}

public static class AS2Receive
{
    public static void GetMessage(WebResponse response)
    {
        var httpResponseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 3.2 Final//EN"">"
            + @"<HTML><HEAD><TITLE>Generic AS2 Receiver</TITLE></HEAD>"
            + @"<BODY><H1>200 Okay</H1><HR>This is to inform you that the AS2 interface is working and is "
            + @"accessable from your location.  This is the standard response to all who would send a GET "
            + @"request to this page instead of the POST context.Request defined by the AS2 Draft Specifications.<HR></BODY></HTML>")
        };
        var content = httpResponseMessage.GetBytes();
        response.GetResponseStream().Write(content, 0, content.Length);
    }

    public static void BadRequest(WebResponse response, string message)
    {
        var httpResponseMessage = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(@"<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 3.2 Final//EN"">"
            + @"<HTML><HEAD><TITLE>400 Bad context.Request</TITLE></HEAD>"
            + @"<BODY><H1>400 Bad context.Request</H1><HR>There was a error processing this context.Request.  The reason given by the server was:"
            + @"<P><font size=-1>" + message + @"</Font><HR></BODY></HTML>")
        };
        var content = httpResponseMessage.GetBytes();
        response.GetResponseStream().Write(content, 0, content.Length);
    }

    public static void DmnMessage(HttpWebRequest request)
    {
        var response = request.GetResponse();
        //response.Clear();

        var cert = request.ClientCertificates[0];
        var clientCertificatePath = cert.Subject;

        response.Headers.Add("Date", DateTime.UtcNow.ToString("ddd, dd MMM yyy HH: mm:ss") + " GMT");

        var incoming_subject = request.Headers["Subject"];
        var response_subject = "Re:";
        if (incoming_subject != null) response_subject = response_subject + incoming_subject;
        response.Headers.Add("Subject", response_subject);

        response.Headers.Add("Mime - Version", "1.0");
        response.Headers.Add("AS2 - Version", "1.2");
        response.Headers.Add("From", "XXXXXXXXXX");
        response.Headers.Add("AS2 - To", request.Headers["AS2 - From"]);
        response.Headers.Add("AS2 - From", request.Headers["AS2 - To"]);
        response.Headers.Add("Connection", "Close");
        response.ContentType = "multipart/report; report-type=disposition-notification;";
        //response.StatusCode = HttpStatusCode.Accepted;

        var incoming_date = request.Headers["Date"];
        var dateTimeSent = "Unknown date";
        if (incoming_date != null) dateTimeSent = incoming_date;

        var responseContent1 = "The Message from ‘" + request.Headers["AS2 - From"] + "‘ to ‘" +
        request.Headers["AS2 - To"] + "‘ " + Environment.NewLine +
            "with MessageID ‘" + request.Headers["Message - Id"] + "‘" + Environment.NewLine + "sent " + dateTimeSent +
            " has been accepted for processing. " + Environment.NewLine +
            "This does not guarantee that the message has been read or understood." + Environment.NewLine;

        var responseContent2 = "Reporting - UA: AS2 Adapter" + Environment.NewLine +
            "Final - Recipient: rfc822;" + request.Headers["AS2 - From"] + Environment.NewLine +
            "Original - Message - ID: " + request.Headers["Message - ID"] + Environment.NewLine +
            "Disposition: automatic - action / MDN - Sent - automatically; processed";

        var finalBodyContent = Encoding.ASCII.GetBytes(responseContent1);
        var finalBodyContent2 = Encoding.ASCII.GetBytes(responseContent2);

        //Wrap the file data with a mime header
        finalBodyContent2 = AS2Utilities.CreateMessage("message / disposition - notification", "7bit", "", finalBodyContent2);

        var PublicAndPrivateKeyPath = "some path";
        var SigningPassword = "take it from app config";

        finalBodyContent2 = AS2Utilities.Sign(finalBodyContent2, PublicAndPrivateKeyPath, SigningPassword, out var contentType);
        response.Headers.Add("EDIINT - Features", "AS2 - Reliability");

        byte[] signedContentTypeHeader = Encoding.ASCII.GetBytes("Content - Type: " + "text / plain" + Environment.NewLine);
        byte[] contentWithContentTypeHeaderAdded = AS2Utilities.ConcatBytes(signedContentTypeHeader, finalBodyContent2);

        finalBodyContent2 = AS2Encryption.Encrypt(
            contentWithContentTypeHeaderAdded,
            clientCertificatePath,
            EncryptionAlgorithm.DES3);

        byte[] finalResponse = finalBodyContent.Concat(finalBodyContent2).ToArray();

        using var output = response.GetResponseStream();
        output.WriteAsync(finalResponse, 0, finalResponse.Length);
    }

    public static void Process(WebRequest request, string dropLocation)
    {
        string filename = request.Headers["Subject"] ?? throw new InvalidDataException("Missing Subject header.");
        byte[] data = new byte[request.ContentLength];
        _ = request.GetRequestStream().Read(data, 0, data.Length);
        string requestContentType = request.ContentType ?? string.Empty;
        bool isEncrypted = requestContentType.Contains("application/pkcs7-mime");
        bool isSigned = requestContentType.Contains("application/pkcs7-signature");

        string message = string.Empty;

        if (isSigned)
        {
            string messageWithMIMEHeaders = Encoding.ASCII.GetString(data);
            string contentType = request.Headers["Content-Type"] ?? requestContentType;

            message = AS2Utilities.ExtractPayload(messageWithMIMEHeaders, contentType);
        }
        else if (isEncrypted) // encrypted and signed inside
        {
            byte[] decryptedData = AS2Encryption.Decrypt(data, out var algorithm);

            string messageWithContentTypeLineAndMIMEHeaders = Encoding.ASCII.GetString(decryptedData);

            // when encrypted, the Content-Type line is actually stored in the start of the message
            int firstBlankLineInMessage = messageWithContentTypeLineAndMIMEHeaders.IndexOf(Environment.NewLine + Environment.NewLine);
            string contentType = messageWithContentTypeLineAndMIMEHeaders.Substring(0, firstBlankLineInMessage);

            message = AS2Utilities.ExtractPayload(messageWithContentTypeLineAndMIMEHeaders, contentType);
        }
        else // not signed and not encrypted
        {
            message = Encoding.ASCII.GetString(data);
        }

        File.WriteAllText(dropLocation + filename, message);
    }
}