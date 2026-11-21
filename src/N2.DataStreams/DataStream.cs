using System.Collections.Concurrent;

namespace N2.DataStreams
{
    public delegate void StreamDataEventHandler(object sender, StreamDataEventArgs e);

    public delegate void DataAvailableEventHandler(object sender, StreamDataAvailableEventArgs e);

    /// <summary>
    /// A timer driven data stream with a bounded FIFO buffer.
    /// </summary>
    /// <remarks>
    /// Data points are produced at the polling interval by a value source. The default source generates
    /// random demo values; pass your own <c>valueSource</c> for real data. When the queue is full the
    /// oldest item is dropped. Exceptions thrown by event handlers never escape the timer thread: they are
    /// reported through <see cref="HandlerFailed"/>.
    /// </remarks>
    public class DataStream : IDataStream
    {
        private readonly TimeProvider _time;
        private readonly ITimer _timer;
        private readonly Func<double> _valueSource;
        private readonly ConcurrentQueue<StreamData> _stream = new();
        private readonly object _queueLock = new();
        private long _lastTick;
        private int _ticking;
        private volatile bool _disposed;

        public event StreamDataEventHandler? StreamDataHandler;

        public event DataAvailableEventHandler? DataAvailableHandler;

        /// <summary>
        /// Raised when an event handler throws. The exception is contained, so the stream keeps running.
        /// </summary>
        public event Action<Exception>? HandlerFailed;

        public int PollingInterval { get; private set; }

        public bool TimeStreamAvailable { get; private set; }

        public bool Configured { get; private set; }

        public bool DataAvailable => !_stream.IsEmpty;

        public int MaximumQueueLength { get; private set; }

        public int QueueLength => _stream.Count;

        /// <param name="streamConfig">The stream configuration.</param>
        /// <param name="timeProvider">Time source for the timer and time stamps. Defaults to the system clock.</param>
        /// <param name="valueSource">Produces the value of each data point. Defaults to random demo values.</param>
        public DataStream(StreamConfig streamConfig, TimeProvider? timeProvider = null, Func<double>? valueSource = null)
        {
            _time = timeProvider ?? TimeProvider.System;
            _valueSource = valueSource ?? (() => 1.0345 * Random.Shared.Next(0, 10000));

            StreamType config = StreamType.None;
            PollingInterval = streamConfig.IntervalInMilliseconds > 0 ? streamConfig.IntervalInMilliseconds : Constants.DefaultPollingInterval;
            MaximumQueueLength = streamConfig.MaximumQueueLength > 0 ? streamConfig.MaximumQueueLength : Constants.DefaultMaximumQueueLength;

            // Check configuration with the possibilities within this class: a timer is available.
            if ((streamConfig.StreamType & StreamType.Time) == StreamType.Time)
            {
                TimeStreamAvailable = true;
                config |= StreamType.Time;
            }
            Configured = (streamConfig.StreamType == config) && config != StreamType.None;

            // The timer is created stopped; Start() arms it.
            _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Last: everything the first tick needs is set up.
            if (streamConfig.AutoStart)
            {
                Start();
            }
        }

        protected virtual void OnDataAvailable(StreamType streamType, float reliability)
        {
            var eventArgs = new StreamDataAvailableEventArgs(streamType, reliability);
            Raise(DataAvailableHandler, handler => handler(this, eventArgs));
        }

        protected virtual void OnDataAvailable(StreamData streamData)
        {
            var eventArgs = new StreamDataEventArgs(streamData);
            Raise(StreamDataHandler, handler => handler(this, eventArgs));
        }

        // Invokes every subscriber separately, so one failing handler neither crashes the process
        // (exceptions on a timer thread are fatal) nor prevents the others from running.
        private void Raise<TDelegate>(TDelegate? handlers, Action<TDelegate> invoke) where TDelegate : Delegate
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    invoke((TDelegate)handler);
                }
#pragma warning disable CA1031 // A subscriber must never be able to stop the stream.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ReportHandlerFailure(ex);
                }
            }
        }

        private void ReportHandlerFailure(Exception exception)
        {
            try
            {
                HandlerFailed?.Invoke(exception);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Nothing left to report to.
            }
        }

        private void Tick()
        {
            // Ticks never overlap: a slow handler makes the next tick skip instead of running concurrently.
            if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
            {
                return;
            }

            try
            {
                long now = _time.GetTimestamp();
                double elapsedMs = _time.GetElapsedTime(Interlocked.Exchange(ref _lastTick, now), now).TotalMilliseconds;

                if (!TimeStreamAvailable)
                {
                    return;
                }

                // 1.0 = on time; falls to 0.0 when a tick is 100 ms or more away from the polling interval.
                float reliability = (float)Math.Clamp((100 - Math.Abs(PollingInterval - elapsedMs)) / 100, 0.0, 1.0);

                var streamData = new StreamData(StreamType.Time, 0, _valueSource(), reliability, time: _time.GetUtcNow().UtcDateTime);
                PushData(streamData);
                OnDataAvailable(StreamType.Time, reliability);
                OnDataAvailable(streamData);
            }
            finally
            {
                Volatile.Write(ref _ticking, 0);
            }
        }

        // FIFO with drop-oldest. The lock keeps the bound exact with concurrent producers.
        private void PushData(StreamData streamData)
        {
            lock (_queueLock)
            {
                _stream.Enqueue(streamData);
                while (_stream.Count > MaximumQueueLength)
                {
                    _stream.TryDequeue(out _);
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                _timer.Dispose();
                StreamDataHandler = null;
                DataAvailableHandler = null;
                HandlerFailed = null;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Exchange(ref _lastTick, _time.GetTimestamp());
            _timer.Change(TimeSpan.FromMilliseconds(PollingInterval), TimeSpan.FromMilliseconds(PollingInterval));
        }

        public void Stop()
        {
            if (_disposed)
            {
                return;
            }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Removes and returns the oldest item, or <c>default</c> when the queue is empty.
        /// Prefer <see cref="TryNextResult"/>, which tells the two cases apart.
        /// </summary>
        public StreamData NextResult() => _stream.TryDequeue(out StreamData data) ? data : default;

        public bool TryNextResult(out StreamData data) => _stream.TryDequeue(out data);

        public void ResetQueue()
        {
            lock (_queueLock)
            {
                _stream.Clear();
            }
        }
    }
}
