using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace N2.DataStreams.A2Protocol;

/// <summary>
/// Settings for sending a file to an AS2 partner.
/// </summary>
public sealed class AS2SendOptions
{
    /// <summary>The partner's AS2 endpoint. Must be https unless <see cref="AllowInsecureHttp"/> is set.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Our AS2 identifier (AS2-From).</summary>
    public required string From { get; init; }

    /// <summary>The partner's AS2 identifier (AS2-To).</summary>
    public required string To { get; init; }

    /// <summary>Certificate with private key used to sign. Null sends an unsigned message.</summary>
    public X509Certificate2? SigningCertificate { get; init; }

    /// <summary>The partner's public certificate used to encrypt. Null sends an unencrypted message.</summary>
    public X509Certificate2? RecipientCertificate { get; init; }

    /// <summary>See <see cref="EncryptionAlgorithm"/>. Defaults to AES-256.</summary>
    public string EncryptionAlgorithm { get; init; } = A2Protocol.EncryptionAlgorithm.AES256;

    public string UserAgent { get; init; } = "N2.DataStreams-AS2";

    public long MaxPayloadBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Only for local testing. Sending signed or encrypted business data over plain http is not recommended.</summary>
    public bool AllowInsecureHttp { get; init; }
}

public static class AS2Send
{
    /// <summary>
    /// Creates an <see cref="HttpClient"/> suitable for AS2: no automatic redirects (a redirect would
    /// forward signed and encrypted business data to another host) and an optional proxy.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout, ProxySettings? proxySettings = null)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (proxySettings != null && !string.IsNullOrEmpty(proxySettings.Name))
        {
            handler.Proxy = new WebProxy(proxySettings.Name)
            {
                Credentials = new NetworkCredential
                {
                    Domain = proxySettings.Domain,
                    UserName = proxySettings.Username,
                    Password = proxySettings.Password
                }
            };
            handler.UseProxy = true;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>
    /// Sends a file and returns the HTTP status code. Non-success status codes throw
    /// <see cref="HttpRequestException"/>. Note that the partner's MDN (receipt) is not processed.
    /// </summary>
    public static async Task<HttpStatusCode> SendFileAsync(
        HttpClient http,
        AS2SendOptions options,
        string filename,
        byte[] fileData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        using HttpRequestMessage request = BuildRequest(options, filename, fileData);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response.StatusCode;
    }

    /// <summary>
    /// Builds the signed and/or encrypted HTTP request without sending it.
    /// </summary>
    public static HttpRequestMessage BuildRequest(AS2SendOptions options, string filename, byte[] fileData)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileData);

        if (fileData.Length == 0) throw new ArgumentException("File data must not be empty.", nameof(fileData));
        if (fileData.Length > options.MaxPayloadBytes) throw new ArgumentException("File data exceeds the maximum payload size.", nameof(fileData));
        if (options.Endpoint.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureHttp && options.Endpoint.Scheme == Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The endpoint must use https.", nameof(options));
        }

        ValidateHeaderValue(options.From, nameof(options.From));
        ValidateHeaderValue(options.To, nameof(options.To));
        ValidateFileName(filename);
        ValidateHeaderValue(options.UserAgent, nameof(options.UserAgent));

        bool sign = options.SigningCertificate != null;
        bool encrypt = options.RecipientCertificate != null;

        string payloadContentType = string.Equals(Path.GetExtension(filename), ".xml", StringComparison.OrdinalIgnoreCase)
            ? "application/xml"
            : "application/EDIFACT";

        var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        request.Headers.TryAddWithoutValidation("Mime-Version", "1.0");
        request.Headers.TryAddWithoutValidation("AS2-Version", "1.2");
        request.Headers.TryAddWithoutValidation("AS2-From", options.From);
        request.Headers.TryAddWithoutValidation("AS2-To", options.To);
        request.Headers.TryAddWithoutValidation("Subject", filename + " transmission.");
        // Unique per message: partners de-duplicate on this value.
        request.Headers.TryAddWithoutValidation("Message-Id", "<" + Guid.NewGuid().ToString("N") + "@as2.n2>");

        byte[] body;
        string contentType;

        if (!sign && !encrypt)
        {
            body = fileData;
            contentType = payloadContentType;
            request.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", "binary");
        }
        else
        {
            byte[] entity = AS2Utilities.CreateMessage(payloadContentType, "binary", "inline; filename=\"" + filename + "\"", fileData);
            if (sign)
            {
                body = AS2Utilities.Sign(entity, options.SigningCertificate!, out contentType);
                request.Headers.TryAddWithoutValidation("EDIINT-Features", "multiple-attachments");
                if (encrypt)
                {
                    // The encrypted content is a complete MIME entity: the Content-Type header, then the signed body
                    // (which starts with the blank line that ends the header block).
                    entity = AS2Utilities.ConcatBytes(
                        System.Text.Encoding.ASCII.GetBytes("Content-Type: " + contentType + AS2Utilities.Crlf),
                        body);
                }
            }
            else
            {
                body = entity;
                contentType = payloadContentType;
            }

            if (encrypt)
            {
                body = AS2Encryption.Encrypt(entity, options.RecipientCertificate!, options.EncryptionAlgorithm);
                contentType = "application/pkcs7-mime; smime-type=enveloped-data; name=\"smime.p7m\"";
            }
        }

        var content = new ByteArrayContent(body);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Content = content;
        return request;
    }

    private static void ValidateFileName(string filename)
    {
        ValidateHeaderValue(filename, nameof(filename));
        if (filename.AsSpan().IndexOfAny('/', '\\') >= 0 || filename is "." or "..")
        {
            throw new ArgumentException("The file name must not contain path separators.", nameof(filename));
        }
    }

    // Header values are limited to printable ASCII (no CR/LF or other control characters), no quotes or backslashes.
    private static void ValidateHeaderValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException($"{name} must be between 1 and 256 characters.", name);
        }

        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E || c == '"' || c == '\\')
            {
                throw new ArgumentException($"{name} contains characters that are not allowed.", name);
            }
        }
    }
}
