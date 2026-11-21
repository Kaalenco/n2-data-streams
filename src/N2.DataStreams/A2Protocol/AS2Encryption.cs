using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace N2.DataStreams.A2Protocol;

/// <summary>
/// CMS signing, signature verification, encryption and decryption for AS2.
/// </summary>
public static class AS2Encryption
{
    internal const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>
    /// Creates a detached CMS signature (SHA-256) over <paramref name="message"/>.
    /// </summary>
    public static byte[] Encode(byte[] message, X509Certificate2 signerCertificate)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signerCertificate);

        var signedCms = new SignedCms(new ContentInfo(message), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signerCertificate)
        {
            DigestAlgorithm = new Oid(Sha256Oid)
        };
        signedCms.ComputeSignature(signer, silent: true);
        return signedCms.Encode();
    }

    /// <summary>
    /// Verifies a detached CMS signature. The signature must be valid, made with exactly the
    /// pinned <paramref name="partnerCertificate"/>, and the certificate must be within its validity period.
    /// </summary>
    /// <param name="content">The signed bytes.</param>
    /// <param name="signature">The detached CMS signature.</param>
    /// <param name="partnerCertificate">The partner's public certificate (pinned trust anchor).</param>
    /// <param name="now">The current time, for the certificate validity check.</param>
    /// <param name="error">A short reason when verification fails.</param>
    public static bool VerifySignature(
        byte[] content,
        byte[] signature,
        X509Certificate2 partnerCertificate,
        DateTimeOffset now,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(partnerCertificate);

        try
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            cms.Decode(signature);

            if (cms.SignerInfos.Count != 1)
            {
                error = "Exactly one signer is required.";
                return false;
            }

            // Checks the signature itself; trust comes from pinning the certificate below.
            cms.CheckSignature(new X509Certificate2Collection(partnerCertificate), verifySignatureOnly: true);

            var signerCertificate = cms.SignerInfos[0].Certificate;
            if (signerCertificate == null
                || !signerCertificate.RawDataMemory.Span.SequenceEqual(partnerCertificate.RawDataMemory.Span))
            {
                error = "Signer is not the registered partner certificate.";
                return false;
            }

            if (now < partnerCertificate.NotBefore.ToUniversalTime() || now > partnerCertificate.NotAfter.ToUniversalTime())
            {
                error = "Partner certificate is not valid at this time.";
                return false;
            }

            error = null;
            return true;
        }
        catch (CryptographicException)
        {
            error = "Signature verification failed.";
            return false;
        }
    }

    /// <summary>
    /// Encrypts <paramref name="message"/> for the recipient (CMS enveloped data).
    /// </summary>
    public static byte[] Encrypt(byte[] message, X509Certificate2 recipientCertificate, string encryptionAlgorithm = EncryptionAlgorithm.AES256)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipientCertificate);

        var envelopedCms = new EnvelopedCms(
            new ContentInfo(message),
            new AlgorithmIdentifier(EncryptionAlgorithm.ToOid(encryptionAlgorithm)));
        envelopedCms.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, recipientCertificate));
        return envelopedCms.Encode();
    }

    /// <summary>
    /// Decrypts CMS enveloped data with the private key of <paramref name="recipientCertificate"/>.
    /// Returns the decrypted content (not the re-encoded envelope).
    /// </summary>
    /// <param name="encodedEncryptedMessage">The enveloped data.</param>
    /// <param name="recipientCertificate">Certificate that includes the private key.</param>
    /// <param name="encryptionAlgorithmOid">The OID of the content encryption algorithm that was used.</param>
    public static byte[] Decrypt(byte[] encodedEncryptedMessage, X509Certificate2 recipientCertificate, out string encryptionAlgorithmOid)
    {
        ArgumentNullException.ThrowIfNull(encodedEncryptedMessage);
        ArgumentNullException.ThrowIfNull(recipientCertificate);

        var envelopedCms = new EnvelopedCms();
        envelopedCms.Decode(encodedEncryptedMessage);
        envelopedCms.Decrypt(new X509Certificate2Collection(recipientCertificate));
        encryptionAlgorithmOid = envelopedCms.ContentEncryptionAlgorithm.Oid.Value ?? string.Empty;
        return envelopedCms.ContentInfo.Content;
    }
}

/// <summary>
/// Loads certificates from files with the non-obsolete loaders.
/// </summary>
public static class AS2Certificates
{
    /// <summary>Loads a PKCS#12 (.pfx) file that includes a private key.</summary>
    public static X509Certificate2 LoadWithPrivateKey(string pfxPath, string? password)
        => X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);

    /// <summary>Loads a public certificate (.cer / .pem / .crt).</summary>
    public static X509Certificate2 LoadPublic(string path)
        => X509CertificateLoader.LoadCertificateFromFile(path);
}
