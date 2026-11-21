namespace N2.DataStreams
{
    public static class Constants
    {
        public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public const int DefaultPollingInterval = 1000;
        public const int DefaultMaximumQueueLength = 2000;

        [Obsolete("Misspelled. Use DefaultMaximumQueueLength.")]
        public const int DefaultMaxiumQueueLength = DefaultMaximumQueueLength;
    }
}
