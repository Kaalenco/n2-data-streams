using System.Security.Cryptography;

namespace N2.DataStreams.A2Protocol;

/// <summary>
/// Content encryption algorithms for AS2 messages.
/// </summary>
public static class EncryptionAlgorithm
{
    /// <summary>AES-256 in CBC mode. This is the default.</summary>
    public const string AES256 = "AES256";

    /// <summary>AES-128 in CBC mode.</summary>
    public const string AES128 = "AES128";

    /// <summary>Triple DES. Legacy: only for partners that cannot do AES. Receivers reject it unless explicitly allowed.</summary>
    public const string DES3 = "3DES";

    internal const string Aes128Oid = "2.16.840.1.101.3.4.1.2";
    internal const string Aes192Oid = "2.16.840.1.101.3.4.1.22";
    internal const string Aes256Oid = "2.16.840.1.101.3.4.1.42";

    internal static Oid ToOid(string algorithm) => algorithm switch
    {
        AES256 => new Oid(Aes256Oid),
        AES128 => new Oid(Aes128Oid),
        DES3 => new Oid("1.2.840.113549.3.7"),
        _ => throw new ArgumentException($"Unsupported encryption algorithm '{algorithm}'. Use AES256, AES128 or 3DES.", nameof(algorithm))
    };

    public static bool IsAes(string? oid) =>
        oid is Aes128Oid or Aes192Oid or Aes256Oid;
}
