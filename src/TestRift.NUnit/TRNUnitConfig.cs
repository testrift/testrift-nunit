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

    public class GroupConfig
    {
        public string Name { get; set; } = "";
        public List<MetadataEntry> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Configuration for URL file generation.
    /// When set, the NUnit plugin will write the test run or group URL to the specified file path.
    /// This is useful for CI scripts to discover the URL to the test run results.
    /// </summary>
    public class UrlFilesConfig
    {
        /// <summary>
        /// File path to write the test run URL. If not set, no URL file is generated.
        /// </summary>
        public string RunUrlFile { get; set; }

        /// <summary>
        /// File path to write the group runs URL. If not set, no URL file is generated.
        /// Only generated if the test run belongs to a group.
        /// </summary>
        public string GroupUrlFile { get; set; }
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
        public GroupConfig Group { get; set; }
        public UrlFilesConfig UrlFiles { get; set; }
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

                if (cfg.Group != null)
                {
                    cfg.Group.Name = VarExpander.Expand(cfg.Group.Name ?? "");
                    cfg.Group.Metadata ??= new List<MetadataEntry>();

                    foreach (var entry in cfg.Group.Metadata)
                    {
                        entry.Name = VarExpander.Expand(entry.Name);
                        entry.Value = VarExpander.Expand(entry.Value);
                        if (entry.Url != null)
                            entry.Url = VarExpander.Expand(entry.Url);
                    }
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

                // Expand URL file paths
                if (cfg.UrlFiles != null)
                {
                    if (!string.IsNullOrEmpty(cfg.UrlFiles.RunUrlFile))
                        cfg.UrlFiles.RunUrlFile = VarExpander.Expand(cfg.UrlFiles.RunUrlFile);
                    if (!string.IsNullOrEmpty(cfg.UrlFiles.GroupUrlFile))
                        cfg.UrlFiles.GroupUrlFile = VarExpander.Expand(cfg.UrlFiles.GroupUrlFile);
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