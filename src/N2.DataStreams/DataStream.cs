using System.Collections.Concurrent;
using System.Diagnostics;

namespace N2.DataStreams
{
    public delegate void StreamDataEventHandler(object sender, StreamDataEventArgs e);

    public delegate void DataAvailableEventHandler(object sender, StreamDataAvailableEventArgs e);

    public class DataStream : IDataStream
    {
        private bool disposedValue;
        private readonly System.Timers.Timer _timer = new System.Timers.Timer();
        private readonly Stopwatch _stopwatch = new Stopwatch();

        public event StreamDataEventHandler? StreamDataHandler;

        public event DataAvailableEventHandler? DataAvailableHandler;

        public int PollingInterval { get; private set; }
        private readonly ConcurrentStack<StreamData> _stream = new ConcurrentStack<StreamData>();

        public bool TimeStreamAvailable { get; private set; }

        protected virtual void OnDataAvailable(StreamType streamType, float responsibility)
        {
            var eventArgs = new StreamDataAvailableEventArgs(streamType, responsibility);
            DataAvailableHandler?.Invoke(this, eventArgs);
        }

        protected virtual void OnDataAvailable(StreamData streamData)
        {
            var eventArgs = new StreamDataEventArgs(streamData);
            StreamDataHandler?.Invoke(this, eventArgs);
        }

        public bool Configured { get; private set; }

        public bool DataAvailable => !_stream.IsEmpty;

        public int MaximumQueueLength { get; private set; }

        public int QueueLength => _stream.Count;

        private static readonly Random random = new Random();

        public DataStream(StreamConfig streamConfig)
        {
            // Reset configuration
            StreamType config = StreamType.None;
            TimeStreamAvailable = false;
            PollingInterval = streamConfig.IntervalInMilliseconds > 0 ? streamConfig.IntervalInMilliseconds : Constants.DefaultPollingInterval;
            MaximumQueueLength = streamConfig.MaximumQueueLength > 0 ? streamConfig.MaximumQueueLength : Constants.DefaultMaxiumQueueLength;

            // Initialize polling timer
            _timer.Interval = PollingInterval;
            _timer.Elapsed += TimerElapsed;
            _timer.AutoReset = true;

            if (streamConfig.AutoStart)
            {
                _timer.Start();
            }

            // Check configuration with the possibilities within this class
            // A timer is available
            if ((streamConfig.StreamType & StreamType.Time) == StreamType.Time)
            {
                TimeStreamAvailable = true;

                _stopwatch.Reset();
                _stopwatch.Start();
                config |= StreamType.Time;
            }
            Configured = (streamConfig.StreamType == config) && config != StreamType.None;
        }

        private DateTime _lastTime = DateTime.MinValue;

        private void TimerElapsed(object? o, EventArgs e)
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            var reliability = (float)(100 - Math.Abs(_timer.Interval - elapsed)) / 100;

            // only add an entry if data is available.
            // for this 'dataservice' example, a datapoint is available when at least a second has passed
            var timeNow = DateTime.UtcNow;
            if ((timeNow - _lastTime).Duration().TotalMilliseconds > 1000)
            {
                _lastTime = timeNow;
                var value = 1.0345 * random.Next(0, 10000);
                var streamData = new StreamData(StreamType.Time, 0, value, reliability, time: timeNow);
                PushData(streamData);
                OnDataAvailable(StreamType.Time, reliability);
                OnDataAvailable(streamData);
            }
            _stopwatch.Restart();
        }

        private void PushData(StreamData streamData)
        {
            if (_stream.Count >= MaximumQueueLength)
            {
                _stream.TryPop(out _);
            }
            _stream.Push(streamData);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects)
                    if (_timer != null)
                    {
                        _timer.Elapsed -= TimerElapsed;
                        _timer.Stop();
                        _timer.Dispose();
                    }
                }

                // free unmanaged resources (unmanaged objects) and override finalizer
                // set large fields to null
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public StreamData NextResult()
        {
            if (_stream.IsEmpty) return default(StreamData);
            _stream.TryPop(out var data);
            return data;
        }

        public void ResetQueue()
        {
            _stream.Clear();
        }
    }
}