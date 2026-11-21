namespace N2.DataStreams
{
    [Flags]
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum StreamType
    {
        None = 0,
        Time = 1,
        Position = 2
    }
}
