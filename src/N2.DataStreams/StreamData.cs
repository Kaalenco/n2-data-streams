using System.Text;

namespace N2.DataStreams
{
    /// <summary>
    /// The stream data struct is used to store the data that is sent from the device to the server.
    /// It contains the stream type, the unit code, the value, the reliability, the geoposition and the time.
    /// The CRC is calculated from all the data in the struct when it is constructed. Use <see cref="VerifyCrc"/>
    /// to check the data integrity after it was stored or transmitted.
    /// </summary>
    public struct StreamData
    {
        private Crc? _crc;

        public StreamData(StreamType streamType, int unitCode, double value, float reliability = 0.0f, int alt = 0, float lat = 0.0f, float lng = 0.0f, DateTime? time = null)
        {
            StreamType = streamType;
            Reliability = reliability;
            DateTime = time ?? Constants.Epoch;
            Geoposition = new Geoposition { Altitude = alt, Latitude = lat, Longitude = lng };
            UnitCode = unitCode;
            Value = value;
            _crc = CalculateCrc(this);
        }

        public StreamType StreamType { get; set; }
        public Geoposition Geoposition { get; set; }
        public DateTime DateTime { get; set; }
        public float Reliability { get; set; }
        public int UnitCode { get; set; }
        public double Value { get; set; }

        /// <summary>
        /// The CRC as it was calculated when the value was created (never null, also for <c>default</c>).
        /// </summary>
        public Crc Crc
        {
            get => _crc ??= new Crc();
            set => _crc = value;
        }

        /// <summary>
        /// Recalculates the CRC from the current field values and compares it with <see cref="Crc"/>.
        /// </summary>
        public readonly bool VerifyCrc() => _crc != null && _crc.Value == CalculateCrc(this).Value;

        private static Crc CalculateCrc(StreamData data) => new Crc()
            .Integer((int)data.StreamType)
            .Integer(data.UnitCode)
            .Double(data.Value)
            .DateTime(data.DateTime)
            .Float(data.Reliability)
            .Geo(data.Geoposition);

        public override readonly string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Reliability : {Reliability:F2}");
            if ((StreamType & StreamType.Time) == StreamType.Time) sb.AppendLine($"Time : {DateTime:o}");
            if ((StreamType & StreamType.Position) == StreamType.Position) sb.AppendLine($"Geo : {Geoposition}");
            sb.AppendLine($"UnitCode : {UnitCode}");
            sb.AppendLine($"Value : {Value}");
            return sb.ToString();
        }
    }
}
