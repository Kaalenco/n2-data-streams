using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace N2.DataStreams.A2Protocol;

/// <summary>
/// The parts of a parsed <c>multipart/signed</c> message.
/// </summary>
/// <param name="SignedPart">The exact bytes that the signature covers (MIME headers and body of the first part).</param>
/// <param name="Signature">The decoded detached CMS signature.</param>
/// <param name="Payload">The body of the first part (the business payload).</param>
/// <param name="PayloadContentType">The Content-Type of the first part, if present.</param>
public sealed record SignedMessageParts(byte[] SignedPart, byte[] Signature, byte[] Payload, string? PayloadContentType);

/// <summary>
/// Helpers for creating and parsing the MIME structures used by AS2.
/// All line endings are CRLF, as required by the RFCs, on every platform.
/// </summary>
public static class AS2Utilities
{
    public const string Crlf = "\r\n";
    public const string MESSAGE_SEPARATOR = "\r\n\r\n";

    private const int MaxBoundaryLength = 70;

    /// <summary>
    /// Extracts the boundary from a Content-Type value, for example
    /// <c>multipart/signed; protocol="application/pkcs7-signature"; boundary="_abc_"</c>.
    /// Returns <c>null</c> when there is no usable boundary.
    /// </summary>
    public static string? GetBoundaryFromContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        const string key = "boundary=";
        int index = contentType.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int start = index + key.Length;
        string boundary;
        if (start < contentType.Length && contentType[start] == '"')
        {
            int end = contentType.IndexOf('"', start + 1);
            if (end < 0)
            {
                return null;
            }
            boundary = contentType.Substring(start + 1, end - start - 1);
        }
        else
        {
            int end = contentType.IndexOf(';', start);
            boundary = (end < 0 ? contentType.Substring(start) : contentType.Substring(start, end - start)).Trim();
        }

        return boundary.Length is > 0 and <= MaxBoundaryLength ? boundary : null;
    }

    /// <summary>
    /// Returns the value of a header from a block of MIME headers, or <c>null</c>.
    /// </summary>
    public static string? GetHeader(string headerBlock, string name)
    {
        ArgumentNullException.ThrowIfNull(headerBlock);
        string prefix = name + ":";
        string[] lines = headerBlock.Split(["\r\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = new StringBuilder(lines[i].Substring(prefix.Length).Trim());
            // header folding: continuation lines start with whitespace
            while (i + 1 < lines.Length && lines[i + 1].Length > 0 && (lines[i + 1][0] == ' ' || lines[i + 1][0] == '\t'))
            {
                i++;
                value.Append(' ').Append(lines[i].Trim());
            }
            return value.ToString();
        }
        return null;
    }

    /// <summary>
    /// Parses a <c>multipart/signed</c> body into its signed part, signature and payload.
    /// Never throws on malformed input; it returns <c>false</c> instead.
    /// </summary>
    public static bool TryParseSignedMessage(byte[] body, string? contentType, out SignedMessageParts? parts)
    {
        parts = null;
        ArgumentNullException.ThrowIfNull(body);

        string? boundary = GetBoundaryFromContentType(contentType);
        if (boundary == null)
        {
            return false;
        }

        // Latin1 maps bytes to chars one-to-one, so offsets and bytes round-trip exactly.
        string s = Encoding.Latin1.GetString(body);
        string delimiter = "--" + boundary;
        string crlfDelimiter = Crlf + delimiter;

        int first = s.StartsWith(delimiter, StringComparison.Ordinal) ? 0 : s.IndexOf(crlfDelimiter, StringComparison.Ordinal) + 2;
        if (first < 2 && !s.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        int part1Start = first + delimiter.Length;
        if (!s.AsSpan(part1Start).StartsWith(Crlf, StringComparison.Ordinal))
        {
            return false;
        }
        part1Start += 2;

        int second = s.IndexOf(crlfDelimiter, part1Start, StringComparison.Ordinal);
        if (second < 0)
        {
            return false;
        }

        int part2Start = second + crlfDelimiter.Length;
        if (!s.AsSpan(part2Start).StartsWith(Crlf, StringComparison.Ordinal))
        {
            return false;
        }
        part2Start += 2;

        int third = s.IndexOf(crlfDelimiter, part2Start, StringComparison.Ordinal);
        if (third < 0)
        {
            return false;
        }

        string part1 = s.Substring(part1Start, second - part1Start);
        string part2 = s.Substring(part2Start, third - part2Start);

        // Signature part
        int sigSeparator = part2.IndexOf(MESSAGE_SEPARATOR, StringComparison.Ordinal);
        if (sigSeparator < 0
            || part2.IndexOf("application/pkcs7-signature", 0, sigSeparator, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        string base64 = RemoveWhitespace(part2.Substring(sigSeparator + MESSAGE_SEPARATOR.Length));
        var signature = new byte[(base64.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(base64, signature, out int written) || written == 0)
        {
            return false;
        }
        Array.Resize(ref signature, written);

        // Payload: the body of the first part
        int payloadSeparator = part1.IndexOf(MESSAGE_SEPARATOR, StringComparison.Ordinal);
        if (payloadSeparator < 0)
        {
            return false;
        }

        string payloadHeaders = part1.Substring(0, payloadSeparator);
        string payload = part1.Substring(payloadSeparator + MESSAGE_SEPARATOR.Length);

        parts = new SignedMessageParts(
            Encoding.Latin1.GetBytes(part1),
            signature,
            Encoding.Latin1.GetBytes(payload),
            GetHeader(payloadHeaders, "Content-Type"));
        return true;
    }

    private static string RemoveWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns a unique MIME boundary (a GUID without punctuation).
    /// </summary>
    private static string MimeBoundary() => "_" + Guid.NewGuid().ToString("N") + "_";

    /// <summary>
    /// Creates a MIME header block (terminated by an empty line).
    /// </summary>
    public static string MIMEHeader(string contentType, string encoding, string disposition)
    {
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(contentType).Append(Crlf);
        if (encoding.Length > 0)
        {
            sb.Append("Content-Transfer-Encoding: ").Append(encoding).Append(Crlf);
        }
        if (disposition.Length > 0)
        {
            sb.Append("Content-Disposition: ").Append(disposition).Append(Crlf);
        }
        sb.Append(Crlf);
        return sb.ToString();
    }

    /// <summary>
    /// Returns a single array of bytes out of all the supplied byte arrays.
    /// </summary>
    public static byte[] ConcatBytes(params byte[][] arBytes)
    {
        ArgumentNullException.ThrowIfNull(arBytes);
        long length = 0;
        foreach (byte[] ar in arBytes)
        {
            length += ar.Length;
        }

        byte[] result = new byte[length];
        long position = 0;
        foreach (byte[] ar in arBytes)
        {
            ar.CopyTo(result, position);
            position += ar.Length;
        }
        return result;
    }

    /// <summary>
    /// Creates a single-part MIME entity: headers followed by the payload bytes.
    /// </summary>
    public static byte[] CreateMessage(string contentType, string encoding, string disposition, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] header = Encoding.ASCII.GetBytes(MIMEHeader(contentType, encoding, disposition));
        return ConcatBytes(header, payload);
    }

    /// <summary>
    /// Signs a message and returns the <c>multipart/signed</c> body. The signature is SHA-256,
    /// announced as <c>micalg="sha-256"</c>.
    /// </summary>
    public static byte[] Sign(byte[] message, X509Certificate2 signerCertificate, out string contentType)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signerCertificate);

        string boundary = MimeBoundary();
        contentType = "multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=\"sha-256\"; boundary=\"" + boundary + "\"";

        byte[] boundaryLine = Encoding.ASCII.GetBytes(Crlf + "--" + boundary + Crlf);
        byte[] signatureHeader = Encoding.ASCII.GetBytes(
            MIMEHeader("application/pkcs7-signature; name=\"smime.p7s\"", "base64", "attachment; filename=smime.p7s"));
        byte[] signature = Encoding.ASCII.GetBytes(
            Convert.ToBase64String(AS2Encryption.Encode(message, signerCertificate), Base64FormattingOptions.InsertLineBreaks)
            + MESSAGE_SEPARATOR);
        byte[] footer = Encoding.ASCII.GetBytes("--" + boundary + "--" + Crlf);

        return ConcatBytes(boundaryLine, message, boundaryLine, signatureHeader, signature, footer);
    }
}
