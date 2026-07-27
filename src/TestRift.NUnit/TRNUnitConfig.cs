using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TestRift.NUnit
{
    public class MetadataEntry
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Url { get; set; }
    }

    public class SourceConfig
    {
        public string Branch { get; set; } = "";
        public string Revision { get; set; } = "";
        public string RepositoryUrl { get; set; }
        public bool? Dirty { get; set; }
    }

    /// <summary>
    /// Configuration for URL file generation.
    /// When set, the NUnit plugin will write the test run or Target URL to the specified file path.
    /// This is useful for CI scripts to discover the URL to the test run results.
    /// </summary>
    public class UrlFilesConfig
    {
        /// <summary>
        /// File path to write the test run URL. If not set, no URL file is generated.
        /// </summary>
        public string RunUrlFile { get; set; }

        /// <summary>
        /// File path to write the Target URL. If not set, no URL file is generated.
        /// </summary>
        public string TargetUrlFile { get; set; }
    }

    public class AutoStartServerConfig
    {
        /// <summary>
        /// Enable automatic starting of TestRift Server.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Optional path to a TestRift Server YAML config file that will be provided to the server
        /// via the TESTRIFT_SERVER_YAML environment variable when starting it.
        /// Supports environment variable expansion with ${env:VAR_NAME} syntax.
        /// </summary>
        public string ServerYaml { get; set; }

        /// <summary>
        /// If true, the server is started with --restart-on-config so that if a server is already running
        /// with a different config, it will be restarted with the new config.
        /// </summary>
        public bool RestartOnConfigChange { get; set; } = false;
    }

    public class Config
    {
        public AutoStartServerConfig AutoStartServer { get; set; } = new();

        /// <summary>
        /// Base URL to the TestRift server (used for WebSocket + HTTP).
        /// Example: http://localhost:8080
        /// Supports environment variable expansion with ${env:VAR_NAME} syntax.
        /// </summary>
        public string ServerUrl { get; set; }

        /// <summary>
        /// Human-readable name for this test run, displayed in the UI instead of the run_id.
        /// Supports environment variable expansion with ${env:VAR_NAME} syntax.
        /// </summary>
        public string RunName { get; set; }

        /// <summary>
        /// Optional custom run ID for this test run. If set, this run ID will be sent to the server.
        /// The server will validate that the run ID is URL-safe and not already in use.
        /// Supports environment variable expansion with ${env:VAR_NAME} syntax.
        /// If not set, the server will generate a unique run ID automatically.
        /// </summary>
        public string RunId { get; set; }

        public List<MetadataEntry> Metadata { get; set; } = new();
        public string Target { get; set; }
        public string Purpose { get; set; } = "manual";
        public string ParentRunId { get; set; }
        public Dictionary<string, SourceConfig> Sources { get; set; } = new();
        public UrlFilesConfig UrlFiles { get; set; }

        /// <summary>
        /// AI failure analysis preference: "auto", "manual", or "off".
        /// When "auto", the server runs analysis automatically when the run finishes.
        /// When "manual", analysis can be triggered from the UI.
        /// When "off", analysis is disabled for this run.
        /// </summary>
        public string AiAnalysis { get; set; }

        /// <summary>
        /// AI email preference: "auto", "manual", or "off".
        /// Controls whether an email summary is sent after analysis completes.
        /// </summary>
        public string AiEmail { get; set; }

        /// <summary>
        /// Override email recipients for AI analysis reports.
        /// If set, these addresses are used instead of the server default.
        /// </summary>
        public List<string> AiEmailTo { get; set; }
    }

    public static class ConfigManager
    {
        private static Config _config;
        private static readonly object _lock = new();
        private static readonly Regex VarRegex = new(@"\$\{env:(?<name>[A-Za-z0-9_]+)\}");

        public static void Load(string filePath)
        {
            lock (_lock)
            {
                var yamlText = File.ReadAllText(filePath);
                var configDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var cfg = deserializer.Deserialize<Config>(yamlText);

                // Expand variables in all fields
                cfg.Metadata ??= new List<MetadataEntry>();

                foreach (var entry in cfg.Metadata)
                {
                    entry.Name = VarExpander.Expand(entry.Name);
                    entry.Value = VarExpander.Expand(entry.Value);
                    if (entry.Url != null)
                        entry.Url = VarExpander.Expand(entry.Url);
                }

                cfg.Target = Environment.GetEnvironmentVariable("TESTRIFT_TARGET") ?? cfg.Target;
                if (!string.IsNullOrEmpty(cfg.Target))
                    cfg.Target = VarExpander.Expand(cfg.Target);
                if (!string.IsNullOrEmpty(cfg.Purpose))
                    cfg.Purpose = VarExpander.Expand(cfg.Purpose);
                if (!string.IsNullOrEmpty(cfg.ParentRunId))
                    cfg.ParentRunId = VarExpander.Expand(cfg.ParentRunId);

                cfg.Sources ??= new Dictionary<string, SourceConfig>();
                foreach (var source in cfg.Sources.Values)
                {
                    source.Branch = VarExpander.Expand(source.Branch ?? "");
                    source.Revision = VarExpander.Expand(source.Revision ?? "");
                    if (source.RepositoryUrl != null)
                        source.RepositoryUrl = VarExpander.Expand(source.RepositoryUrl);
                }

                // Expand run name
                if (!string.IsNullOrEmpty(cfg.RunName))
                    cfg.RunName = VarExpander.Expand(cfg.RunName);

                // Expand server URL
                if (!string.IsNullOrEmpty(cfg.ServerUrl))
                    cfg.ServerUrl = VarExpander.Expand(cfg.ServerUrl);

                // Expand auto-start server YAML path (if configured)
                if (cfg.AutoStartServer != null && !string.IsNullOrEmpty(cfg.AutoStartServer.ServerYaml))
                {
                    cfg.AutoStartServer.ServerYaml = VarExpander.Expand(cfg.AutoStartServer.ServerYaml);
                    if (!Path.IsPathRooted(cfg.AutoStartServer.ServerYaml))
                    {
                        cfg.AutoStartServer.ServerYaml = Path.GetFullPath(Path.Combine(configDir, cfg.AutoStartServer.ServerYaml));
                    }
                }

                // Expand run ID
                if (!string.IsNullOrEmpty(cfg.RunId))
                    cfg.RunId = VarExpander.Expand(cfg.RunId);

                // Expand AI analysis preferences
                if (!string.IsNullOrEmpty(cfg.AiAnalysis))
                    cfg.AiAnalysis = VarExpander.Expand(cfg.AiAnalysis);
                if (!string.IsNullOrEmpty(cfg.AiEmail))
                    cfg.AiEmail = VarExpander.Expand(cfg.AiEmail);

                // Expand URL file paths
                if (cfg.UrlFiles != null)
                {
                    if (!string.IsNullOrEmpty(cfg.UrlFiles.RunUrlFile))
                        cfg.UrlFiles.RunUrlFile = VarExpander.Expand(cfg.UrlFiles.RunUrlFile);
                    if (!string.IsNullOrEmpty(cfg.UrlFiles.TargetUrlFile))
                        cfg.UrlFiles.TargetUrlFile = VarExpander.Expand(cfg.UrlFiles.TargetUrlFile);
                }

                _config = cfg;
            }
        }

        public static Config Get()
        {
            if (_config == null)
                throw new InvalidOperationException("Config not loaded. Call ConfigManager.Load(path) first.");
            return _config;
        }

    }
}