using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace N2.DataStreams
{
    public static class StreamDataExtensions
    {
        /// <summary>
        /// The default maximum size of decompressed data, as protection against decompression bombs.
        /// </summary>
        public const int DefaultMaxDecompressedBytes = 16 * 1024 * 1024;

        public static byte[] GetBytes<T>(this T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            return Compress(bytes);
        }

        /// <exception cref="InvalidDataException">The data is not valid, or it expands beyond <paramref name="maxDecompressedBytes"/>.</exception>
        public static T? Deserialize<T>(this byte[] data, int maxDecompressedBytes = DefaultMaxDecompressedBytes)
        {
            ArgumentNullException.ThrowIfNull(data);
            var bytes = Decompress(data, maxDecompressedBytes);
            return JsonSerializer.Deserialize<T>(new ReadOnlySpan<byte>(bytes));
        }

        public static Crc Double(this Crc crc, double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            crc.Update(bytes);
            return crc;
        }

        public static Crc Float(this Crc crc, float value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
            crc.Update(bytes);
            return crc;
        }

        public static Crc Integer(this Crc crc, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            crc.Update(bytes);
            return crc;
        }

        public static Crc Long(this Crc crc, long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            crc.Update(bytes);
            return crc;
        }

        /// <summary>
        /// Adds the position. Latitude and longitude are scaled to micro-degrees, rounded (not truncated).
        /// </summary>
        public static Crc Geo(this Crc crc, Geoposition value)
        {
            return crc
                .Integer(value.Altitude)
                .Long((long)Math.Round(value.Latitude * 1_000_000d))
                .Long((long)Math.Round(value.Longitude * 1_000_000d));
        }

        /// <summary>
        /// Adds the time as UTC ticks (local times are converted first).
        /// </summary>
        public static Crc DateTime(this Crc crc, DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
            return crc.Long(utc.Ticks);
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var dstream = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                dstream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private static byte[] Decompress(byte[] data, int maxDecompressedBytes)
        {
            if (maxDecompressedBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDecompressedBytes));
            }

            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using var dstream = new DeflateStream(input, CompressionMode.Decompress);

            var buffer = new byte[8192];
            int read;
            while ((read = dstream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxDecompressedBytes)
                {
                    throw new InvalidDataException($"Decompressed data exceeds the limit of {maxDecompressedBytes} bytes.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
