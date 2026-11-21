#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using N2.DataStreams.A2Protocol;

namespace DataStreams.UnitTests
{
    [TestClass]
    public class AS2Should
    {
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes("<order id=\"42\">\u00e9\u20ac</order>");

        private string _drop = string.Empty;
        private X509Certificate2 _partner = null!;      // sender: private key
        private X509Certificate2 _local = null!;        // receiver: private key

        [TestInitialize]
        public void Init()
        {
            _drop = Path.Combine(Path.GetTempPath(), "as2-" + Guid.NewGuid().ToString("N"), "drop");
            _partner = CreateCertificate("partner");
            _local = CreateCertificate("local");
        }

        [TestCleanup]
        public void Cleanup()
        {
            string? parent = Path.GetDirectoryName(_drop);
            if (parent != null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }

        private static X509Certificate2 CreateCertificate(string name)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.NonRepudiation, false));
            using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
        }

        private static X509Certificate2 PublicOnly(X509Certificate2 certificate)
            => X509CertificateLoader.LoadCertificate(certificate.RawData);

        private AS2Receiver CreateReceiver(
            bool requireSigned = true,
            bool allowLegacy = false,
            bool withRecipient = true,
            X509Certificate2? partnerCertificate = null,
            long maxBytes = 1024 * 1024) =>
            new(new AS2ReceiveOptions
            {
                DropDirectory = _drop,
                LocalId = "LOCAL",
                PartnerCertificates = new Dictionary<string, X509Certificate2> { ["PARTNER"] = partnerCertificate ?? PublicOnly(_partner) },
                RecipientCertificate = withRecipient ? _local : null,
                RequireSigned = requireSigned,
                AllowLegacyEncryption = allowLegacy,
                MaxMessageBytes = maxBytes
            });

        private AS2SendOptions SendOptions(bool sign, bool encrypt, string algorithm = EncryptionAlgorithm.AES256) => new()
        {
            Endpoint = new Uri("https://partner.example/as2"),
            From = "PARTNER",
            To = "LOCAL",
            SigningCertificate = sign ? _partner : null,
            RecipientCertificate = encrypt ? PublicOnly(_local) : null,
            EncryptionAlgorithm = algorithm
        };

        private static AS2ReceivedMessage ToReceived(HttpRequestMessage request)
        {
            string Header(string name) => request.Headers.GetValues(name).Single();
            return new AS2ReceivedMessage(
                Header("AS2-From"),
                Header("AS2-To"),
                Header("Message-Id"),
                request.Content!.Headers.GetValues("Content-Type").Single(),
                request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }

        private string[] DroppedFiles() => Directory.Exists(_drop) ? Directory.GetFiles(_drop) : [];

        // --- crypto layer ---

        [TestMethod]
        public void VerifyOwnSignature()
        {
            byte[] signature = AS2Encryption.Encode(Payload, _partner);
            Assert.IsTrue(AS2Encryption.VerifySignature(Payload, signature, PublicOnly(_partner), DateTimeOffset.UtcNow, out _));
        }

        [TestMethod]
        public void RejectTamperedContent()
        {
            byte[] signature = AS2Encryption.Encode(Payload, _partner);
            byte[] tampered = (byte[])Payload.Clone();
            tampered[3] ^= 0x01;
            Assert.IsFalse(AS2Encryption.VerifySignature(tampered, signature, PublicOnly(_partner), DateTimeOffset.UtcNow, out _));
        }

        [TestMethod]
        public void RejectSignatureFromAnotherCertificate()
        {
            byte[] signature = AS2Encryption.Encode(Payload, _local);
            Assert.IsFalse(AS2Encryption.VerifySignature(Payload, signature, PublicOnly(_partner), DateTimeOffset.UtcNow, out _));
        }

        [TestMethod]
        public void RejectSignatureWhenCertificateExpired()
        {
            byte[] signature = AS2Encryption.Encode(Payload, _partner);
            Assert.IsFalse(AS2Encryption.VerifySignature(Payload, signature, PublicOnly(_partner), DateTimeOffset.UtcNow.AddDays(365), out _));
        }

        [TestMethod]
        [DataRow(EncryptionAlgorithm.AES256)]
        [DataRow(EncryptionAlgorithm.AES128)]
        [DataRow(EncryptionAlgorithm.DES3)]
        public void DecryptToTheOriginalContent(string algorithm)
        {
            byte[] encrypted = AS2Encryption.Encrypt(Payload, PublicOnly(_local), algorithm);
            byte[] decrypted = AS2Encryption.Decrypt(encrypted, _local, out string oid);

            CollectionAssert.AreEqual(Payload, decrypted);
            Assert.AreEqual(algorithm != EncryptionAlgorithm.DES3, EncryptionAlgorithm.IsAes(oid));
        }

        [TestMethod]
        public void ParseSignedMessageIntoPartsThatVerify()
        {
            byte[] entity = AS2Utilities.CreateMessage("application/xml", "binary", "", Payload);
            byte[] signed = AS2Utilities.Sign(entity, _partner, out string contentType);

            Assert.IsTrue(AS2Utilities.TryParseSignedMessage(signed, contentType, out SignedMessageParts? parts));
            CollectionAssert.AreEqual(Payload, parts!.Payload);
            CollectionAssert.AreEqual(entity, parts.SignedPart);
            Assert.IsTrue(AS2Encryption.VerifySignature(parts.SignedPart, parts.Signature, PublicOnly(_partner), DateTimeOffset.UtcNow, out _));
            StringAssert.Contains(contentType, "sha-256");
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("multipart/signed")]
        [DataRow("multipart/signed; boundary=")]
        [DataRow("multipart/signed; boundary=\"unterminated")]
        public void NotThrowOnMalformedContentTypeWhenParsing(string? contentType)
        {
            Assert.IsFalse(AS2Utilities.TryParseSignedMessage(Encoding.ASCII.GetBytes("garbage"), contentType, out _));
        }

        // --- full send -> receive ---

        [TestMethod]
        [DataRow(true, true)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(false, false)]
        public void ReceiveWhatWasSent(bool sign, bool encrypt)
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(sign, encrypt), "order.xml", Payload);
            AS2ReceiveResult result = CreateReceiver(requireSigned: sign).Process(ToReceived(request));

            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, result.Reason);
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(result.FilePath!));
            Assert.AreEqual(".xml", Path.GetExtension(result.FilePath));
            Assert.AreEqual(Path.GetFullPath(_drop), Path.GetDirectoryName(result.FilePath));
        }

        [TestMethod]
        public void ReceiveBinaryPayloadUnchanged()
        {
            byte[] binary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, true), "data.bin", binary);
            AS2ReceiveResult result = CreateReceiver().Process(ToReceived(request));

            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, result.Reason);
            CollectionAssert.AreEqual(binary, File.ReadAllBytes(result.FilePath!));
        }

        [TestMethod]
        public void RejectUnsignedMessageWhenSignatureRequired()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(false, false), "order.xml", Payload);
            AS2ReceiveResult result = CreateReceiver().Process(ToReceived(request));

            Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            Assert.AreEqual(0, DroppedFiles().Length);
        }

        [TestMethod]
        public void RejectTamperedSignedMessage()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2ReceivedMessage received = ToReceived(request);
            byte[] body = (byte[])received.Body!.Clone();
            int index = Encoding.Latin1.GetString(body).IndexOf("<order", StringComparison.Ordinal);
            body[index + 2] ^= 0x01;

            AS2ReceiveResult result = CreateReceiver().Process(received with { Body = body });

            Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            Assert.AreEqual(0, DroppedFiles().Length);
        }

        [TestMethod]
        public void RejectMessageSignedByAnotherCertificate()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2Receiver receiver = CreateReceiver(partnerCertificate: PublicOnly(_local)); // wrong pinned certificate

            Assert.AreEqual(HttpStatusCode.Unauthorized, receiver.Process(ToReceived(request)).StatusCode);
        }

        [TestMethod]
        public void RejectUnknownPartner()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2ReceivedMessage received = ToReceived(request) with { From = "SOMEONE-ELSE" };

            Assert.AreEqual(HttpStatusCode.Forbidden, CreateReceiver().Process(received).StatusCode);
        }

        [TestMethod]
        public void RejectWrongRecipientId()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2ReceivedMessage received = ToReceived(request) with { To = "NOT-US" };

            Assert.AreEqual(HttpStatusCode.BadRequest, CreateReceiver().Process(received).StatusCode);
        }

        [TestMethod]
        public void RejectDuplicateMessageId()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2Receiver receiver = CreateReceiver();
            AS2ReceivedMessage received = ToReceived(request);

            Assert.AreEqual(HttpStatusCode.OK, receiver.Process(received).StatusCode);
            Assert.AreEqual(HttpStatusCode.Conflict, receiver.Process(received).StatusCode);
            Assert.AreEqual(1, DroppedFiles().Length);
        }

        [TestMethod]
        public void AllowRetryAfterFailedMessage()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            AS2Receiver receiver = CreateReceiver();
            AS2ReceivedMessage received = ToReceived(request);
            byte[] bad = (byte[])received.Body!.Clone();
            bad[bad.Length / 2] ^= 0x01;

            Assert.AreNotEqual(HttpStatusCode.OK, receiver.Process(received with { Body = bad }).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, receiver.Process(received).StatusCode);
        }

        [TestMethod]
        public void RejectOversizedMessage()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);

            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, CreateReceiver(maxBytes: 100).Process(ToReceived(request)).StatusCode);
        }

        [TestMethod]
        [DataRow("multipart/signed; protocol=\"application/pkcs7-signature\"; boundary=\"x\"")]
        [DataRow("multipart/signed")]
        [DataRow("application/pkcs7-mime; smime-type=enveloped-data")]
        [DataRow("")]
        public void AnswerBadRequestForGarbageWithoutThrowing(string contentType)
        {
            var received = new AS2ReceivedMessage("PARTNER", "LOCAL", "<id@x>", contentType, Encoding.ASCII.GetBytes("not a mime message"));

            AS2ReceiveResult result = CreateReceiver().Process(received);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, result.StatusCode.ToString());
            Assert.AreEqual(0, DroppedFiles().Length);
        }

        [TestMethod]
        public void RejectEncryptedMessageWithoutRecipientCertificate()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, true), "order.xml", Payload);

            Assert.AreEqual(HttpStatusCode.BadRequest, CreateReceiver(withRecipient: false).Process(ToReceived(request)).StatusCode);
        }

        [TestMethod]
        public void RejectLegacyEncryptionUnlessAllowed()
        {
            using HttpRequestMessage request = AS2Send.BuildRequest(SendOptions(true, true, EncryptionAlgorithm.DES3), "order.xml", Payload);
            AS2ReceivedMessage received = ToReceived(request);

            Assert.AreEqual(HttpStatusCode.BadRequest, CreateReceiver().Process(received).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, CreateReceiver(allowLegacy: true).Process(received).StatusCode);
        }

        [TestMethod]
        public void RejectMessageEncryptedForAnotherCertificate()
        {
            AS2SendOptions options = new()
            {
                Endpoint = new Uri("https://partner.example/as2"),
                From = "PARTNER",
                To = "LOCAL",
                SigningCertificate = _partner,
                RecipientCertificate = PublicOnly(CreateCertificate("somebody-else"))
            };
            using HttpRequestMessage request = AS2Send.BuildRequest(options, "order.xml", Payload);

            Assert.AreEqual(HttpStatusCode.BadRequest, CreateReceiver().Process(ToReceived(request)).StatusCode);
        }

        // --- sender input validation ---

        [TestMethod]
        [DataRow("..\\..\\evil.xml")]
        [DataRow("../evil.xml")]
        [DataRow("a/b.xml")]
        [DataRow("..")]
        [DataRow("name\r\nInjected: yes.xml")]
        [DataRow("quote\".xml")]
        [DataRow("")]
        public void RejectUnsafeFileNamesWhenSending(string filename)
        {
            Assert.ThrowsExactly<ArgumentException>(() => AS2Send.BuildRequest(SendOptions(true, false), filename, Payload));
        }

        [TestMethod]
        public void RejectHeaderInjectionInPartnerIds()
        {
            AS2SendOptions options = new()
            {
                Endpoint = new Uri("https://partner.example/as2"),
                From = "PARTNER\r\nX-Evil: 1",
                To = "LOCAL"
            };
            Assert.ThrowsExactly<ArgumentException>(() => AS2Send.BuildRequest(options, "order.xml", Payload));
        }

        [TestMethod]
        public void RejectPlainHttpEndpointUnlessAllowed()
        {
            AS2SendOptions options = new() { Endpoint = new Uri("http://partner.example/as2"), From = "A", To = "B" };
            Assert.ThrowsExactly<ArgumentException>(() => AS2Send.BuildRequest(options, "order.xml", Payload));
        }

        [TestMethod]
        public void UseUniqueMessageIds()
        {
            using HttpRequestMessage first = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);
            using HttpRequestMessage second = AS2Send.BuildRequest(SendOptions(true, false), "order.xml", Payload);

            Assert.AreNotEqual(first.Headers.GetValues("Message-Id").Single(), second.Headers.GetValues("Message-Id").Single());
        }

        [TestMethod]
        public void NeverWriteOutsideTheDropDirectory()
        {
            // Many messages, including hostile ids; every file must be a direct child of the drop directory.
            AS2Receiver receiver = CreateReceiver(requireSigned: false);
            string[] ids = ["<..\\..\\x@y>", "<../../x@y>", "<a/b@y>", "<C:\\evil@y>"];
            foreach (string id in ids)
            {
                AS2ReceivedMessage message = new("PARTNER", "LOCAL", id, "application/xml", Payload);
                AS2ReceiveResult result = receiver.Process(message);
                Assert.IsTrue(result.Success, result.Reason);
                Assert.AreEqual(Path.GetFullPath(_drop), Path.GetDirectoryName(result.FilePath));
            }

            string parent = Path.GetDirectoryName(Path.GetFullPath(_drop))!;
            Assert.AreEqual(0, Directory.GetFiles(parent).Length);
            Assert.AreEqual(ids.Length, DroppedFiles().Length);
        }
    }
}
