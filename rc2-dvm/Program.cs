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

        public static async Task<int> Main(string[] args)
        {
            // Config File Command Line Option
            Option<FileInfo> configFile = new Option<FileInfo>(
                name: "--config",
                description: "YAML configuration file to read");
            configFile.AddAlias("-c");
            configFile.IsRequired = true;

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
                            .Build();

                        config = ymlDeserializer.Deserialize<ConfigObject>(yml);
                    }
                }

                // Start main runtime
                Runtime();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to read configuration file {configFile}\n{e.ToString()}");
                Environment.Exit((int)ERRNO.ENOCONFIG);
            }
        }

        public static void Runtime()
        {
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
            Log.Logger.Information(AssemblyVersion._VERSION);
            Log.Logger.Information(AssemblyVersion._COPYRIGHT);

            // Start things up
            try
            {
                Log.Logger.Information("[RC2-DVM] Starting up RC2-DVM services");

                // Instantiate FNE system
                Log.Logger.Information($"[RC2-DVM] Starting FNE system");

                fneSystem = new PeerSystem();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                fneSystem.StartListeningAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                fneSystem.Start();

                // Instantiate virtual channels
                Configuration.VirtualChannels.ForEach(channel =>
                {
                    VirtualChannels.Add(new VirtualChannel(channel));
                });

                Log.Logger.Information("[RC2-DVM] All processes running");
                while (!shutdown)
                {
                    Thread.Sleep(100);
                }
                
                if (fneSystem.IsStarted)
                {
                    Log.Logger.Information($"[RC2-DVM] Stopping FNE system");
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