using System;
using System.IO;
using System.Reflection;
using System.CommandLine;
using System.Reflection.Metadata.Ecma335;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.File;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using FneLogLevel = fnecore.LogLevel;
using fnecore.Utility;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;

namespace rc2_dvm
{
    /// <summary>
    /// Enum for program exit codes
    /// </summary>
    public enum ERRNO : int
    {
        /// <summary>
        /// No errors, normal exit
        /// </summary>
        ENOERROR = 0,
        /// <summary>
        /// Bad command line options provided
        /// </summary>
        EBADOPTIONS = 1,
        /// <summary>
        /// No config file provided
        /// </summary>
        ENOCONFIG = 2,
        /// <summary>
        /// Config file malformed
        /// </summary>
        EBADCONFIG = 3,
        /// <summary>
        /// Unhandled exit code
        /// </summary>
        EUNHANDLED = 99
    }

    public class RC2DVM
    {
        /// <summary>
        /// Flag to indicate that the daemon should shutdown
        /// </summary>
        public static bool shutdown = false;
        /// <summary>
        /// Master configuration
        /// </summary>
        private static ConfigObject config;

        /// <summary>
        /// Gets the instance of the <see cref="ConfigObject"/>
        /// </summary>
        public static ConfigObject Configuration => config;

        /// <summary>
        /// Gets the <see cref="fnecore.LogLevel"/>
        /// </summary>
        public static FneLogLevel FneLogLevel
        {
            get;
            private set;
        } = FneLogLevel.INFO;

        /// <summary>
        /// FNE this instance is connected to
        /// </summary>
        public static PeerSystem fneSystem;

        /// <summary>
        /// List of configured virtual channels for this instance
        /// </summary>
        public static List<VirtualChannel> VirtualChannels = new List<VirtualChannel>();

        /// <summary>
        /// Key container used for all virtual channels
        /// </summary>
        public static KeyContainer keyContainer = new KeyContainer();

        /// <summary>
        /// Long-format version string
        /// </summary>
        public static readonly string SWVersionLong = $"RC2-DVM v{ThisAssembly.Git.SemVer.Major}.{ThisAssembly.Git.SemVer.Minor}.{ThisAssembly.Git.SemVer.Patch}{ThisAssembly.Git.SemVer.DashLabel} ({ThisAssembly.Git.Commit.ToUpper()})";

        /// <summary>
        /// Short-format version string
        /// </summary>
        public static readonly string SWVersionShort = $"RC2DVM_{ThisAssembly.Git.SemVer.Major}.{ThisAssembly.Git.SemVer.Minor}.{ThisAssembly.Git.SemVer.Patch}{ThisAssembly.Git.SemVer.DashLabel}";

        public static async Task<int> Main(string[] args)
        {
            // Config File Command Line Option
            Option<FileInfo> configFile = new Option<FileInfo>(
                name: "--config",
                description: "YAML configuration file to read");
            configFile.AddAlias("-c");
            // Default config path should be the same directory as the exe
            string executingDirectory = System.AppContext.BaseDirectory;
            configFile.SetDefaultValue(new FileInfo(executingDirectory + "config.yml"));

            // Root Command
            RootCommand root = new RootCommand("DVM FNE daemon for RadioConsole2");
            root.AddOption(configFile);

            // Config file handler (also starts main runtime)
            root.SetHandler((config) =>
            {
                ReadConfig(config);
            },
            configFile);

            // Start the handlers
            return await root.InvokeAsync(args);
        }

        public static void ReadConfig(FileInfo configFile)
        {
            try
            {
                // Open and parse config file
                using (FileStream stream = new FileStream(configFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (TextReader reader = new StreamReader(stream))
                    {
                        string yml = reader.ReadToEnd();

                        IDeserializer ymlDeserializer = new DeserializerBuilder()
                            .WithNamingConvention(CamelCaseNamingConvention.Instance)
                            //.IgnoreUnmatchedProperties()
                            .Build();

                        config = ymlDeserializer.Deserialize<ConfigObject>(yml);
                    }
                }

                // Start main runtime
                Runtime();
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Could not find config file {configFile.FullName}! Please ensure the file exists and try again");
                Environment.Exit((int)ERRNO.ENOCONFIG);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to read configuration file {configFile}\n{e.ToString()}");
                Environment.Exit((int)ERRNO.EBADCONFIG);
            }
        }

        public static void Runtime()
        {
            // Setup CTRL+C handler
            ManualResetEvent startShutdown = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                startShutdown.Set();
            };

            // Setup Logging
            LoggerConfiguration logConfig = new LoggerConfiguration();
            logConfig.MinimumLevel.Debug();
            const string logTemplate = "{Level:u1}: {Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message}{NewLine}{Exception}";

            // File Logging Config
            LogEventLevel fileLevel = LogEventLevel.Information;
            switch (config.Log.FileLevel)
            {
                case 1:
                    fileLevel = LogEventLevel.Debug;
                    FneLogLevel = FneLogLevel.DEBUG;
                    break;
                case 2:
                case 3:
                default:
                    fileLevel = LogEventLevel.Information;
                    FneLogLevel = FneLogLevel.INFO;
                    break;
                case 4:
                    fileLevel = LogEventLevel.Warning;
                    FneLogLevel = FneLogLevel.WARNING;
                    break;
                case 5:
                    fileLevel = LogEventLevel.Error;
                    FneLogLevel = FneLogLevel.ERROR;
                    break;
                case 6:
                    fileLevel = LogEventLevel.Fatal;
                    FneLogLevel = FneLogLevel.FATAL;
                    break;
            }
            logConfig.WriteTo.File(
                Path.Combine(new string[] { config.Log.FilePath, config.Log.FileRoot + "-.log" }),
                fileLevel,
                logTemplate,
                rollingInterval: RollingInterval.Day);

            // Display Logging Config
            LogEventLevel dispLevel = LogEventLevel.Information;
            switch (config.Log.DisplayLevel)
            {
                case 1:
                    dispLevel = LogEventLevel.Debug;
                    FneLogLevel = FneLogLevel.DEBUG;
                    break;
                case 2:
                case 3:
                default:
                    dispLevel = LogEventLevel.Information;
                    FneLogLevel = FneLogLevel.INFO;
                    break;
                case 4:
                    dispLevel = LogEventLevel.Warning;
                    FneLogLevel = FneLogLevel.WARNING;
                    break;
                case 5:
                    dispLevel = LogEventLevel.Error;
                    FneLogLevel = FneLogLevel.ERROR;
                    break;
                case 6:
                    dispLevel = LogEventLevel.Fatal;
                    FneLogLevel = FneLogLevel.FATAL;
                    break;
            }
            logConfig.WriteTo.Console(dispLevel, logTemplate);

            // Initialize Logger
            Log.Logger = logConfig.CreateLogger();
            Log.Logger.Information($"{SWVersionLong}");
            Log.Logger.Information(AssemblyVersion._COPYRIGHT);

            // Start things up
            try
            {
                Log.Logger.Information("[RC2-DVM] Starting up RC2-DVM services");

                // Load EKC
                keyContainer = new KeyContainer();
                if (!string.IsNullOrEmpty(config.Encryption.KeyFile))
                {
                    Log.Logger.Information("[RC2-DVM] Loading EKC {file:l}", config.Encryption.KeyFile);
                    IDeserializer keyDeserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    keyContainer = keyDeserializer.Deserialize<KeyContainer>(File.ReadAllText(config.Encryption.KeyFile));
                }

                if (keyContainer.Keys.Count > 0)
                {
                    Log.Logger.Information("    Loaded {keys} keys from local keyfile", keyContainer.Keys.Count);
                    keyContainer.Keys.ForEach((key) =>
                    {
                        Log.Logger.Information("        {algo:l} (0x{algoId:X2}) Key ID 0x{keyId:X4}", Enum.GetName(typeof(Algorithm), key.AlgId), key.AlgId, key.KeyId);
                    });
                }

                // Instantiate FNE system
                Log.Logger.Information("[RC2-DVM] Starting FNE system");

                fneSystem = new PeerSystem();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                fneSystem.StartListeningAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                fneSystem.Start();

                // Instantiate virtual channels
                Log.Logger.Debug("Creating {0} virtual channels", Configuration.VirtualChannels.Count);
                Configuration.VirtualChannels.ForEach(channel =>
                {
                    VirtualChannels.Add(new VirtualChannel(channel, keyContainer));
                });

                // Start
                Log.Logger.Information("Starting virtual channels");
                VirtualChannels.ForEach(channel =>
                {
                    try
                    {
                        channel.Start();
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        Log.Logger.Error(ex, "Failed to start virtual channel {0:l} on {1}:{2}", channel.Config.Name, channel.Config.ListenAddress, channel.Config.ListenPort);
                        Environment.Exit((int)ERRNO.EBADCONFIG);
                    }
                });

                // Done starting up
                Log.Logger.Information("[RC2-DVM] All processes running");

                // Wait for ctrl+c
                startShutdown.WaitOne();
                
                // Stop virtual channels
                Log.Logger.Information("[RC2-DVM] Stopping virtual channels");
                VirtualChannels.ForEach(channel =>
                {
                    channel.Stop();
                });
                
                // Stop FNE connection
                if (fneSystem.IsStarted)
                {
                    Log.Logger.Information("[RC2-DVM] Stopping FNE system");
                    fneSystem.Stop();
                }
                    
                Environment.Exit(0);
            }
            catch (System.DllNotFoundException)
            {
                Log.Logger.Error("vocoder.dll/vocoder.so not found!");
                Environment.Exit((int)ERRNO.EBADCONFIG);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "An unhandled exception occurred");
                Environment.Exit((int)ERRNO.EUNHANDLED);
            }
        }
    }
}