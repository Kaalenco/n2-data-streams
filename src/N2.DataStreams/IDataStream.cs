namespace N2.DataStreams
{
    public interface IDataStream : IDisposable
    {
        bool Configured { get; }

        event DataAvailableEventHandler DataAvailableHandler;

        event StreamDataEventHandler StreamDataHandler;

        /// <summary>Raised when an event handler throws. The exception is contained.</summary>
        event Action<Exception> HandlerFailed;

        int PollingInterval { get; }
        bool DataAvailable { get; }
        int MaximumQueueLength { get; }
        int QueueLength { get; }

        void Start();

        void Stop();

        /// <summary>Removes and returns the oldest item, or <c>default</c> when the queue is empty.</summary>
        StreamData NextResult();

        /// <summary>Removes the oldest item. Returns <c>false</c> when the queue is empty.</summary>
        bool TryNextResult(out StreamData data);

        void ResetQueue();
    }
}
