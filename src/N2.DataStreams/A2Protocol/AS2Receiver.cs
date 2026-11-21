using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace N2.DataStreams.A2Protocol;

/// <summary>
/// A received AS2 HTTP request, reduced to the parts that matter. The host (for example an ASP.NET Core
/// endpoint) copies the headers and the request body (read with a size limit) into this type.
/// </summary>
/// <param name="From">The AS2-From header.</param>
/// <param name="To">The AS2-To header.</param>
/// <param name="MessageId">The Message-Id header.</param>
/// <param name="ContentType">The Content-Type header.</param>
/// <param name="Body">The raw request body.</param>
public sealed record AS2ReceivedMessage(string? From, string? To, string? MessageId, string? ContentType, byte[]? Body);

/// <summary>
/// The outcome of processing a received message. <see cref="StatusCode"/> is what the host should answer with.
/// <see cref="Reason"/> is a short, fixed text that is safe to return to the sender.
/// </summary>
public sealed record AS2ReceiveResult(HttpStatusCode StatusCode, string Reason, string? FilePath = null)
{
    public bool Success => StatusCode == HttpStatusCode.OK;
}

public sealed class AS2ReceiveOptions
{
    /// <summary>Directory where accepted payloads are written. It is created when missing.</summary>
    public required string DropDirectory { get; init; }

    /// <summary>Our AS2 identifier. Messages for another AS2-To are rejected.</summary>
    public required string LocalId { get; init; }

    /// <summary>Registered partners: AS2-From identifier to the partner's public certificate (pinned).</summary>
    public required IReadOnlyDictionary<string, X509Certificate2> PartnerCertificates { get; init; }

    /// <summary>Certificate with private key, needed to decrypt. Without it encrypted messages are rejected.</summary>
    public X509Certificate2? RecipientCertificate { get; init; }

    public long MaxMessageBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Reject messages without a valid signature from the partner. Default true.</summary>
    public bool RequireSigned { get; init; } = true;

    /// <summary>Accept 3DES-encrypted messages. Default false: only AES is accepted.</summary>
    public bool AllowLegacyEncryption { get; init; }

    /// <summary>How long a Message-Id is remembered to reject duplicates (replays). Default 24 hours.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Validates and stores incoming AS2 messages. It authenticates the partner (signature pinned to a
/// registered certificate), decrypts, rejects duplicates, enforces size limits, and writes the payload
/// to the drop directory under a generated file name. Nothing from the message is ever used as a path.
/// </summary>
public sealed class AS2Receiver
{
    private const int MaxIdLength = 256;
    private const int MaxStoredMessageIds = 100_000;

    private readonly AS2ReceiveOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, X509Certificate2> _partners;
    private readonly string _dropDirectory;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    public AS2Receiver(AS2ReceiveOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LocalId)) throw new ArgumentException("LocalId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.DropDirectory)) throw new ArgumentException("DropDirectory is required.", nameof(options));
        if (options.PartnerCertificates == null || options.PartnerCertificates.Count == 0)
        {
            throw new ArgumentException("At least one partner certificate is required.", nameof(options));
        }
        if (options.MaxMessageBytes <= 0) throw new ArgumentException("MaxMessageBytes must be positive.", nameof(options));

        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _partners = new Dictionary<string, X509Certificate2>(options.PartnerCertificates, StringComparer.OrdinalIgnoreCase);

        _dropDirectory = Path.GetFullPath(options.DropDirectory);
        Directory.CreateDirectory(_dropDirectory);
    }

    public AS2ReceiveResult Process(AS2ReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Body == null || message.Body.Length == 0) return Fail(HttpStatusCode.BadRequest, "Empty message.");
        if (message.Body.Length > _options.MaxMessageBytes) return Fail(HttpStatusCode.RequestEntityTooLarge, "Message too large.");
        if (!IsValidId(message.From) || !IsValidId(message.To) || !IsValidId(message.MessageId))
        {
            return Fail(HttpStatusCode.BadRequest, "Invalid or missing AS2 headers.");
        }
        if (!string.Equals(message.To, _options.LocalId, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(HttpStatusCode.BadRequest, "Unknown recipient.");
        }
        if (!_partners.TryGetValue(message.From!, out X509Certificate2? partnerCertificate))
        {
            return Fail(HttpStatusCode.Forbidden, "Unknown partner.");
        }

        string key = message.From + "|" + message.MessageId;
        if (!TryReserve(key, out bool busy))
        {
            return busy
                ? Fail(HttpStatusCode.ServiceUnavailable, "Busy.")
                : Fail(HttpStatusCode.Conflict, "Duplicate message.");
        }

        bool completed = false;
        try
        {
            AS2ReceiveResult result = ProcessCore(message, partnerCertificate);
            completed = result.Success;
            return result;
        }
        catch (CryptographicException)
        {
            return Fail(HttpStatusCode.BadRequest, "Message could not be decrypted or verified.");
        }
        catch (IOException)
        {
            return Fail(HttpStatusCode.InternalServerError, "Could not store the message.");
        }
        finally
        {
            if (!completed)
            {
                Release(key);
            }
        }
    }

    private AS2ReceiveResult ProcessCore(AS2ReceivedMessage message, X509Certificate2 partnerCertificate)
    {
        byte[] body = message.Body!;
        string contentType = message.ContentType ?? string.Empty;

        // 1. Decrypt
        if (contentType.Contains("application/pkcs7-mime", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RecipientCertificate == null)
            {
                return Fail(HttpStatusCode.BadRequest, "Encrypted messages are not accepted.");
            }

            byte[] decrypted = AS2Encryption.Decrypt(body, _options.RecipientCertificate, out string algorithmOid);
            if (!EncryptionAlgorithm.IsAes(algorithmOid) && !_options.AllowLegacyEncryption)
            {
                return Fail(HttpStatusCode.BadRequest, "Encryption algorithm not accepted.");
            }

            // The decrypted content is a MIME entity: headers, a blank line, then the body.
            string text = Encoding.Latin1.GetString(decrypted);
            int separator = text.IndexOf(AS2Utilities.MESSAGE_SEPARATOR, StringComparison.Ordinal);
            if (separator < 0)
            {
                return Fail(HttpStatusCode.BadRequest, "Malformed message.");
            }

            string? innerContentType = AS2Utilities.GetHeader(text.Substring(0, separator), "Content-Type");
            if (innerContentType == null)
            {
                return Fail(HttpStatusCode.BadRequest, "Malformed message.");
            }

            contentType = innerContentType;
            body = Encoding.Latin1.GetBytes(text.Substring(separator + AS2Utilities.MESSAGE_SEPARATOR.Length));
        }

        // 2. Verify the signature and find the payload
        byte[] payload;
        string payloadContentType;
        if (contentType.StartsWith("multipart/signed", StringComparison.OrdinalIgnoreCase))
        {
            if (!AS2Utilities.TryParseSignedMessage(body, contentType, out SignedMessageParts? parts) || parts == null)
            {
                return Fail(HttpStatusCode.BadRequest, "Malformed message.");
            }

            if (!AS2Encryption.VerifySignature(parts.SignedPart, parts.Signature, partnerCertificate, _time.GetUtcNow(), out _))
            {
                return Fail(HttpStatusCode.Unauthorized, "Signature verification failed.");
            }

            payload = parts.Payload;
            payloadContentType = parts.PayloadContentType ?? string.Empty;
        }
        else
        {
            if (_options.RequireSigned)
            {
                return Fail(HttpStatusCode.Unauthorized, "A signed message is required.");
            }

            payload = body;
            payloadContentType = contentType;
        }

        if (payload.Length == 0)
        {
            return Fail(HttpStatusCode.BadRequest, "Empty payload.");
        }

        // 3. Store under a generated name. Nothing from the message is used as a file name.
        string path = WritePayload(payload, payloadContentType);
        return new AS2ReceiveResult(HttpStatusCode.OK, "Accepted.", path);
    }

    private string WritePayload(byte[] payload, string payloadContentType)
    {
        string extension =
            payloadContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? ".xml" :
            payloadContentType.Contains("edi", StringComparison.OrdinalIgnoreCase) ? ".edi" :
            ".bin";

        string fileName = _time.GetUtcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N") + extension;

        string fullPath = Path.GetFullPath(Path.Combine(_dropDirectory, fileName));
        if (!fullPath.StartsWith(_dropDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("Resolved path is outside the drop directory.");
        }

        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(payload, 0, payload.Length);
        return fullPath;
    }

    private static AS2ReceiveResult Fail(HttpStatusCode status, string reason) => new(status, reason);

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }
        return true;
    }

    // Reserves a Message-Id so that concurrent or repeated deliveries are rejected.
    private bool TryReserve(string key, out bool busy)
    {
        busy = false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_seen.Count >= MaxStoredMessageIds / 2)
            {
                foreach (string expired in _seen.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                {
                    _seen.Remove(expired);
                }
            }

            if (_seen.TryGetValue(key, out DateTimeOffset until) && until > now)
            {
                return false;
            }

            if (_seen.Count >= MaxStoredMessageIds)
            {
                busy = true;
                return false;
            }

            _seen[key] = now + _options.DuplicateWindow;
            return true;
        }
    }

    private void Release(string key)
    {
        lock (_lock)
        {
            _seen.Remove(key);
        }
    }
}
