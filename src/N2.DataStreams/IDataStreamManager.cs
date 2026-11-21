namespace N2.DataStreams
{
    public interface IDataStreamManager
    {
        /// <summary>
        /// Full path name for the configuration file.
        /// </summary>
        string Configfile { get; set; }

        /// <summary>
        /// Descriptive name for the configuration.
        /// </summary>
        string FileName { get; set; }

        /// <summary>
        /// Stream configurations.
        /// </summary>
        List<StreamConfig> StreamConfig { get; }

        /// <summary>
        /// Create the client using the name to select the client.
        /// </summary>
        /// <param name="streamName">The name of the stream.</param>
        /// <returns></returns>
        IDataStream CreateClient(string streamName);

        /// <summary>
        /// Create the client using the type to create the client.
        /// </summary>
        /// <param name="streamType"></param>
        /// <returns></returns>
        IDataStream CreateClient(StreamType streamType);

        /// <summary>
        /// Initialize the DataStreamManager using the current StreamConfig configuration.
        /// </summary>
        void Initialize();

        List<string> ListClients();

        /// <summary>
        /// Save the current stream configuration to a file.
        /// Returns the number of characters written to the config file.
        /// </summary>
        /// <param name="path">The file to write.</param>
        int SaveConfig(string path);
    }
}