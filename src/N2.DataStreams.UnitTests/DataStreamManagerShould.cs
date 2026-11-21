using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using N2.DataStreams;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace DataStreams.UnitTests
{
    [TestClass]
    public class DataStreamManagerShould
    {
        private readonly Mock<IFileSystem> _fileSystemMock = new Mock<IFileSystem>();

        private const string ConfigFileData =
@"{
    ""FileName"": ""example.json"",
    ""StreamConfig"": [
        {   ""Name"": ""Stream"",
            ""StreamType"": ""Time""
        }
    ]
}";

        [TestInitialize]
        public void TestInitialize()
        {
            _fileSystemMock
                .Setup(m => m.File.ReadAllText(It.IsAny<string>()))
                .Returns(ConfigFileData);
        }

        [TestMethod]
        public void SaveConfiguration()
        {
            IDataStreamManager sut = new DataStreamManager(_fileSystemMock.Object);
            sut.StreamConfig.Add(new StreamConfig { Name = "Name", StreamType = StreamType.Position });
            var bytesWritten = sut.SaveConfig("Example.json");
            Assert.IsTrue(bytesWritten > 0);
        }

        [TestMethod]
        public void ConstructDataStreamManager()
        {
            IDataStreamManager sut = new DataStreamManager(_fileSystemMock.Object);
            Assert.IsNotNull(sut);
        }

        [TestMethod]
        [DataRow("C:\\Temp\\example.json")]
        public void InitializeUsingConfigFile(string configFile)
        {
            var sut = DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, configFile);
            Assert.AreEqual("C:\\Temp\\example.json", sut.Configfile);
            Assert.AreEqual("example.json", sut.FileName);
            Assert.IsTrue(sut.ListClients().Contains("Stream"));
        }

        [TestMethod]
        public void CreateStreamClient()
        {
            var sut = DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "C:\\Temp\\example.json");
            List<string> clients = sut.ListClients();
            IDataStream client = sut.CreateClient(clients.First());
            Assert.IsNotNull(client);
        }

        [TestMethod]
        [DataRow(StreamType.Position, false)]
        [DataRow(StreamType.Time | StreamType.Position, false)]
        [DataRow(StreamType.Time, true)]
        public void FindStreamClient(StreamType streamType, bool expectedResult)
        {
            var sut = DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "C:\\Temp\\example.json");
            var client = sut.CreateClient(streamType);
            Assert.AreEqual(expectedResult, client.Configured);
        }

        [TestMethod]
        public void ListClientsThatWereAddedToTheConfiguration()
        {
            var sut = new DataStreamManager(_fileSystemMock.Object);
            sut.StreamConfig.Add(new StreamConfig { Name = "Added", StreamType = StreamType.Time });

            CollectionAssert.AreEqual(new[] { "Added" }, sut.ListClients());
            Assert.IsNotNull(sut.CreateClient("Added"));
        }

        [TestMethod]
        public void ExplainUnknownStreamName()
        {
            var sut = DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "C:\\Temp\\example.json");
            var ex = Assert.ThrowsExactly<KeyNotFoundException>(() => sut.CreateClient("Nope"));
            StringAssert.Contains(ex.Message, "Nope");
            StringAssert.Contains(ex.Message, "Stream");
        }

        [TestMethod]
        public void TryCreateClientWithoutThrowing()
        {
            var sut = (DataStreamManager)DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "C:\\Temp\\example.json");
            Assert.IsTrue(sut.TryCreateClient("Stream", out var found));
            Assert.IsNotNull(found);
            Assert.IsFalse(sut.TryCreateClient("Nope", out var missing));
            Assert.IsNull(missing);
            Assert.IsFalse(sut.TryCreateClient(StreamType.Position, out _));
            Assert.IsTrue(sut.TryCreateClient(StreamType.Time, out _));
        }

        [TestMethod]
        [DataRow("{ not json")]
        [DataRow("null")]
        [DataRow("{\"StreamConfig\":[{\"Name\":\"\"}]}")]
        [DataRow("{\"StreamConfig\":[{\"Name\":\"A\"},{\"Name\":\"A\"}]}")]
        public void RejectInvalidConfigFilesWithClearError(string content)
        {
            _fileSystemMock.Setup(m => m.File.ReadAllText(It.IsAny<string>())).Returns(content);
            var ex = Assert.ThrowsExactly<InvalidDataException>(() => DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "cfg.json"));
            StringAssert.Contains(ex.Message, "cfg.json");
        }

        [TestMethod]
        public void NotLetTheConfigFileRedirectTheConfigPath()
        {
            _fileSystemMock.Setup(m => m.File.ReadAllText(It.IsAny<string>()))
                .Returns("{\"Configfile\":\"C:\\\\evil.json\",\"StreamConfig\":[{\"Name\":\"A\",\"StreamType\":\"Time\"}]}");
            var sut = DataStreamManager.CreateDatastreamManager(_fileSystemMock.Object, "good.json");
            Assert.AreEqual("good.json", sut.Configfile);
        }

        [TestMethod]
        public void SaveAtomicallyAndReadBackWhatWasSaved()
        {
            var files = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
            files.Directory.CreateDirectory("/cfg");
            var sut = new DataStreamManager(files);
            sut.FileName = "stream.json";
            sut.StreamConfig.Add(new StreamConfig { Name = "A", StreamType = StreamType.Time | StreamType.Position, IntervalInMilliseconds = 250, MaximumQueueLength = 7, AutoStart = true });

            Assert.IsTrue(sut.SaveConfig("/cfg/out.json") > 0);
            Assert.IsTrue(files.File.Exists("/cfg/out.json"));
            Assert.AreEqual(1, files.AllFiles.Count(), "no temp file may be left behind");

            var loaded = DataStreamManager.CreateDatastreamManager(files, "/cfg/out.json");
            Assert.AreEqual("stream.json", loaded.FileName);
            Assert.AreEqual(StreamType.Time | StreamType.Position, loaded.StreamConfig[0].StreamType);
            Assert.AreEqual(250, loaded.StreamConfig[0].IntervalInMilliseconds);
            Assert.AreEqual(7, loaded.StreamConfig[0].MaximumQueueLength);
            Assert.IsTrue(loaded.StreamConfig[0].AutoStart);
        }
    }
}
