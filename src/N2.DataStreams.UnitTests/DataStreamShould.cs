using Microsoft.VisualStudio.TestTools.UnitTesting;
using N2.DataStreams;
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Time.Testing;

namespace DataStreams.UnitTests
{
    [TestClass]
    public class DataStreamShould
    {
        private StreamConfig _streamconfig = new StreamConfig
        {
            Name = "TestStream",
            StreamType = StreamType.Time,
            IntervalInMilliseconds = 100
        };

        private IDataStream _sut;

        [TestInitialize]
        public void TestInitialize()
        {
            _sut = new DataStream(_streamconfig);
        }

        [TestMethod]
        public void UsingMaximumQueueLength()
        {
            var time = new FakeTimeProvider();
            var config = new StreamConfig { Name = "t", StreamType = StreamType.Time, IntervalInMilliseconds = 100, MaximumQueueLength = 3 };
            using var sut = new DataStream(config, time);
            Assert.AreEqual(3, sut.MaximumQueueLength);
            sut.Start();

            time.Advance(TimeSpan.FromSeconds(6));
            sut.Stop();

            // unrestricted there would be 60 items
            Assert.AreEqual(3, sut.QueueLength);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _sut?.Dispose();
        }

        [TestMethod]
        public void ConstructStreamClient()
        {
            IDataStream sut = new DataStream(_streamconfig);
            Assert.IsNotNull(sut);
            Assert.IsTrue(sut.Configured);
        }

        [TestMethod]
        public void InitializeWithEmptyConfig()
        {
            IDataStream sut = new DataStream(new StreamConfig());
            Assert.IsNotNull(sut);
            Assert.IsFalse(sut.Configured);
        }

        [TestMethod]
        public void StreamClientIsDisposable()
        {
            Assert.IsNotNull(_sut as IDisposable);
        }

        [TestMethod]
        public void StreamClientHasHandlerMethods()
        {
            _sut.DataAvailableHandler += (object o, StreamDataAvailableEventArgs e) =>
            {
                Assert.IsNotNull(o);
                Assert.IsNotNull(e);
            };
        }

        [TestMethod]
        public void StreamClientCanStartAndStop()
        {
            _sut.Start();
            _sut.Stop();
            Assert.IsTrue(true, "Start and stop executed");
        }

        [TestMethod]
        public void StreamClientRaisedEvents()
        {
            var eventRaised = false;
            _sut.Start();
            _sut.DataAvailableHandler += (object o, StreamDataAvailableEventArgs e) =>
            {
                Assert.IsNotNull(o);
                Assert.IsNotNull(e);
                eventRaised = true;
            };
            Thread.Sleep(200);
            Assert.IsTrue(eventRaised);
        }

        [TestMethod]
        public void StreamClientRaisedDataEvents()
        {
            StreamData eventRaised = new StreamData();
            _sut.Start();
            _sut.StreamDataHandler += (object o, StreamDataEventArgs e) =>
            {
                Assert.IsNotNull(o);
                Assert.IsNotNull(e);
                eventRaised = e.StreamData;
            };
            Thread.Sleep(200);
            Assert.AreNotEqual(0.0f, eventRaised.Reliability);
        }

        [TestMethod]
        public void StreamClientProducesResults()
        {
            _sut.Start();
            Thread.Sleep(200);
            Assert.IsTrue(_sut.DataAvailable);
            var data = _sut.NextResult();
            Assert.IsNotNull(data);
        }

        [TestMethod]
        public void StreamClientCanresetQueue()
        {
            _sut.Start();
            Thread.Sleep(200);
            _sut.Stop();
            Assert.IsTrue(_sut.DataAvailable);
            _sut.ResetQueue();
            Assert.IsFalse(_sut.DataAvailable);
        }

        private static StreamConfig Config(int interval = 100, int max = 100) =>
            new StreamConfig { Name = "t", StreamType = StreamType.Time, IntervalInMilliseconds = interval, MaximumQueueLength = max };

        [TestMethod]
        public void ReturnItemsOldestFirst()
        {
            var time = new FakeTimeProvider();
            double next = 0;
            using var sut = new DataStream(Config(), time, () => ++next);
            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(500));

            var values = new List<double>();
            while (sut.TryNextResult(out var data)) values.Add(data.Value);

            CollectionAssert.AreEqual(new double[] { 1, 2, 3, 4, 5 }, values);
        }

        [TestMethod]
        public void DropOldestItemWhenQueueIsFull()
        {
            var time = new FakeTimeProvider();
            double next = 0;
            using var sut = new DataStream(Config(max: 3), time, () => ++next);
            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(500));

            var values = new List<double>();
            while (sut.TryNextResult(out var data)) values.Add(data.Value);

            CollectionAssert.AreEqual(new double[] { 3, 4, 5 }, values);
        }

        [TestMethod]
        public void TellEmptyQueueApartFromData()
        {
            using var sut = new DataStream(Config(), new FakeTimeProvider());
            Assert.IsFalse(sut.TryNextResult(out _));
            Assert.IsNotNull(sut.NextResult().Crc);   // default value is safe to use
        }

        [TestMethod]
        public void KeepRunningWhenAHandlerThrows()
        {
            var time = new FakeTimeProvider();
            using var sut = new DataStream(Config(), time);
            var failures = new List<Exception>();
            var secondHandlerCalls = 0;
            sut.HandlerFailed += failures.Add;
            sut.DataAvailableHandler += (o, e) => throw new InvalidOperationException("boom");
            sut.DataAvailableHandler += (o, e) => secondHandlerCalls++;
            sut.StreamDataHandler += (o, e) => throw new InvalidOperationException("boom 2");

            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(300));

            Assert.AreEqual(3, secondHandlerCalls);
            Assert.AreEqual(6, failures.Count);
            Assert.AreEqual(3, sut.QueueLength);
        }

        [TestMethod]
        public void UsePollingInterval()
        {
            var time = new FakeTimeProvider();
            using var sut = new DataStream(Config(interval: 250), time);
            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(1000));
            Assert.AreEqual(4, sut.QueueLength);
        }

        [TestMethod]
        public void StopProducingAfterStop()
        {
            var time = new FakeTimeProvider();
            using var sut = new DataStream(Config(), time);
            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(200));
            sut.Stop();
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, sut.QueueLength);
        }

        [TestMethod]
        public void StartAutomaticallyWhenConfigured()
        {
            var time = new FakeTimeProvider();
            var config = Config();
            config.AutoStart = true;
            using var sut = new DataStream(config, time);
            time.Advance(TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(3, sut.QueueLength);
        }

        [TestMethod]
        public void ReportFullReliabilityForOnTimeTicks()
        {
            var time = new FakeTimeProvider();
            using var sut = new DataStream(Config(interval: 100), time);
            sut.Start();
            for (int i = 0; i < 20; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));   // step by step: a big Advance fires all ticks at the final timestamp
            }

            Assert.AreEqual(20, sut.QueueLength);
            while (sut.TryNextResult(out var d))
            {
                Assert.AreEqual(1.0f, d.Reliability, 0.001f);
            }
        }

        [TestMethod]
        public void ProduceNothingWithoutTimeStream()
        {
            var time = new FakeTimeProvider();
            using var sut = new DataStream(new StreamConfig { StreamType = StreamType.Position, IntervalInMilliseconds = 100 }, time);
            sut.Start();
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, sut.QueueLength);
            Assert.IsFalse(sut.Configured);
        }

        [TestMethod]
        public void RejectStartAfterDispose()
        {
            var sut = new DataStream(Config(), new FakeTimeProvider());
            sut.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => sut.Start());
            sut.Stop();      // harmless
            sut.Dispose();   // idempotent
        }

        [TestMethod]
        public void StopRaisingEventsAfterDispose()
        {
            var time = new FakeTimeProvider();
            var sut = new DataStream(Config(), time);
            var calls = 0;
            sut.DataAvailableHandler += (o, e) => calls++;
            sut.Start();
            time.Advance(TimeSpan.FromMilliseconds(100));
            sut.Dispose();
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, calls);
        }
    }
}
