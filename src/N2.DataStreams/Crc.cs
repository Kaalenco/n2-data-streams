namespace N2.DataStreams
{
    /// <summary>
    /// A cyclic redundancy check (CRC) is an error-detecting code commonly
    /// used in digital networks and storage devices to detect accidental changes to raw data.
    /// </summary>
    /// <remarks>
    /// This is CRC-16/CCITT-FALSE (polynomial 0x1021, initial value 0xFFFF), computed over the
    /// little-endian bytes of each field in a fixed order, so the value is the same on every platform.
    /// It detects accidental corruption only; it is not a defence against tampering.
    /// </remarks>
    public class Crc
    {
        public const int InitialValue = 0xFFFF;

        public int Value { get; set; } = InitialValue;

        public void Update(ReadOnlySpan<byte> data)
        {
            ushort crc = (ushort)Value;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }
            Value = crc;
        }
    }
}
