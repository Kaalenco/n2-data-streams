#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using N2.DataStreams;

namespace DataStreams.UnitTests
{
    [TestClass]
    public class StreamDataIntegrityShould
    {
        private static StreamData Sample(float lat = 52.37f, float lng = 4.89f, double value = 12.5) =>
            new StreamData(StreamType.Time | StreamType.Position, 7, value, 0.9f, 15, lat, lng, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        [TestMethod]
        public void ComputeStandardCrc16()
        {
            // CRC-16/CCITT-FALSE check value for "123456789" is 0x29B1
            var crc = new Crc();
            crc.Update(Encoding.ASCII.GetBytes("123456789"));
            Assert.AreEqual(0x29B1, crc.Value);
        }

        [TestMethod]
        public void VerifyUntouchedData()
        {
            Assert.IsTrue(Sample().VerifyCrc());
        }

        [TestMethod]
        public void DetectChangedValue()
        {
            var data = Sample();
            data.Value = 12.6;
            Assert.IsFalse(data.VerifyCrc());
        }

        [TestMethod]
        public void VerifyAfterRoundTrip()
        {
            var restored = Sample().GetBytes().Deserialize<StreamData>();
            Assert.IsTrue(restored.VerifyCrc());
        }

        [TestMethod]
        public void NotLoseFractionOfDegreesInCrc()
        {
            // 52.37 and 52.99 used to hash the same, because the value was truncated before scaling
            Assert.AreNotEqual(Sample(lat: 52.37f).Crc.Value, Sample(lat: 52.99f).Crc.Value);
            Assert.AreNotEqual(Sample(lng: 4.10f).Crc.Value, Sample(lng: 4.90f).Crc.Value);
        }

        [TestMethod]
        public void NeverReturnNullCrcForDefault()
        {
            Assert.IsNotNull(default(StreamData).Crc);
            Assert.IsFalse(default(StreamData).VerifyCrc());
        }

        [TestMethod]
        public void RejectDecompressionBomb()
        {
            // 20 MB of zeros compresses to a few KB
            byte[] bomb;
            using (var output = new MemoryStream())
            {
                using (var d = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    d.Write(new byte[20 * 1024 * 1024]);
                }
                bomb = output.ToArray();
            }
            Assert.IsTrue(bomb.Length < 100_000);
            Assert.ThrowsExactly<InvalidDataException>(() => bomb.Deserialize<StreamData>());
        }

        [TestMethod]
        public void HonourCustomDecompressionLimit()
        {
            byte[] bytes = Sample().GetBytes();
            Assert.ThrowsExactly<InvalidDataException>(() => bytes.Deserialize<StreamData>(maxDecompressedBytes: 10));
            Assert.IsNotNull(bytes.Deserialize<StreamData>(maxDecompressedBytes: 10_000));
        }

        [TestMethod]
        public void RejectGarbage()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.Deserialize<StreamData>());
        }

        [TestMethod]
        public void FormatHemispheres()
        {
            Assert.AreEqual("N52.37, E4.89, A15", Sample().Geoposition.ToString());
            Assert.AreEqual("S33.87, W70.65, A5", new Geoposition { Latitude = -33.87f, Longitude = -70.65f, Altitude = 5 }.ToString());
        }

        [TestMethod]
        public void SerializeCombinedStreamTypeAsFlags()
        {
            var type = StreamType.Time | StreamType.Position;
            Assert.AreEqual("Time, Position", type.ToString());
            Assert.AreEqual(DateTimeKind.Utc, Constants.Epoch.Kind);
        }
    }
}
