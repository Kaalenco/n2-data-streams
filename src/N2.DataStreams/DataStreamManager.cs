using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace N2.DataStreams
{
    public class DataStreamManager : IDataStreamManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
        };

        // The shape of the config file. The manager itself is not deserialized, so a file cannot set
        // runtime state such as the path that is loaded or saved next.
        private sealed class ConfigFile
        {
            public string? FileName { get; set; }
            public List<StreamConfig>? StreamConfig { get; set; }
        }

        private readonly object _lock = new();
        private readonly IFileSystem _fileSystem;

        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// The configured streams. The list is the single source of truth: <see cref="ListClients"/> and
        /// <see cref="CreateClient(string)"/> always reflect its current content. Mutating it while other
        /// threads create clients is not synchronized; configure first, then share the manager.
        /// </summary>
        public List<StreamConfig> StreamConfig { get; } = [];

        public DataStreamManager()
        {
            _fileSystem = new FileSystem();
        }

        public DataStreamManager(IFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            _fileSystem = fileSystem;
        }

        public string Configfile { get; set; } = string.Empty;

        /// <exception cref="InvalidDataException">The file is not valid JSON, or a stream has no name or a duplicate name.</exception>
        public void Initialize()
        {
            var jsonData = _fileSystem.File.ReadAllText(Configfile);

            ConfigFile? configData;
            try
            {
                configData = JsonSerializer.Deserialize<ConfigFile>(jsonData, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Error reading config file '{Configfile}': {ex.Message}", ex);
            }

            if (configData == null)
            {
                throw new InvalidDataException($"Error reading config file '{Configfile}': the file is empty.");
            }

            var streams = configData.StreamConfig ?? [];
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stream in streams)
            {
                if (string.IsNullOrWhiteSpace(stream.Name))
                {
                    throw new InvalidDataException($"Error reading config file '{Configfile}': a stream has no name.");
                }
                if (!names.Add(stream.Name))
                {
                    throw new InvalidDataException($"Error reading config file '{Configfile}': duplicate stream name '{stream.Name}'.");
                }
            }

            lock (_lock)
            {
                FileName = !string.IsNullOrEmpty(configData.FileName) ? configData.FileName : FileName;
                StreamConfig.Clear();
                StreamConfig.AddRange(streams);
            }
        }

        /// <summary>
        /// Factory for data stream manager.
        /// </summary>
        /// <param name="fileSystem">File system abstraction</param>
        /// <param name="configFile">The name for the config file</param>
        public static IDataStreamManager CreateDatastreamManager(IFileSystem fileSystem, string configFile)
        {
            var result = new DataStreamManager(fileSystem);
            result.Configfile = configFile;
            result.Initialize();
            return result;
        }

        /// <exception cref="KeyNotFoundException">There is no stream with that name.</exception>
        public IDataStream CreateClient(string streamName)
        {
            return TryCreateClient(streamName, out var client)
                ? client!
                : throw new KeyNotFoundException($"There is no stream named '{streamName}'. Available: {string.Join(", ", ListClients())}.");
        }

        public bool TryCreateClient(string streamName, out IDataStream? client)
        {
            client = null;
            StreamConfig? found = null;
            lock (_lock)
            {
                foreach (var candidate in StreamConfig)
                {
                    if (string.Equals(candidate.Name, streamName, StringComparison.Ordinal))
                    {
                        found = candidate;
                        break;
                    }
                }
            }

            if (found == null)
            {
                return false;
            }

            client = new DataStream(found.Value);
            return true;
        }

        /// <summary>
        /// Creates a client for the first configured stream that supports <paramref name="streamType"/>.
        /// When no stream matches, the returned client is not configured (<see cref="IDataStream.Configured"/> is false).
        /// Use <see cref="TryCreateClient(StreamType, out IDataStream?)"/> to detect that case explicitly.
        /// </summary>
        public IDataStream CreateClient(StreamType streamType)
        {
            return TryCreateClient(streamType, out var client) ? client! : new DataStream(default);
        }

        public bool TryCreateClient(StreamType streamType, out IDataStream? client)
        {
            client = null;
            StreamConfig? found = null;
            lock (_lock)
            {
                foreach (var candidate in StreamConfig)
                {
                    if ((candidate.StreamType & streamType) == streamType)
                    {
                        found = candidate;
                        break;
                    }
                }
            }

            if (found == null)
            {
                return false;
            }

            client = new DataStream(found.Value);
            return true;
        }

        public List<string> ListClients()
        {
            lock (_lock)
            {
                return StreamConfig.Select(c => c.Name).ToList();
            }
        }

        /// <summary>
        /// Writes the configuration to <paramref name="path"/> (atomically: temp file, then move)
        /// and returns the number of characters written.
        /// </summary>
        public int SaveConfig(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string serialized;
            lock (_lock)
            {
                serialized = JsonSerializer.Serialize(
                    new ConfigFile { FileName = FileName, StreamConfig = [.. StreamConfig] },
                    JsonOptions);
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _fileSystem.File.WriteAllText(temp, serialized);
                _fileSystem.File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { _fileSystem.File.Delete(temp); } catch (IOException) { }
                throw;
            }
            return serialized.Length;
        }
    }
}
