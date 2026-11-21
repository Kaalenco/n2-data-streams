using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using N2.DataStreams;
using System.Collections.Generic;
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
    }
}