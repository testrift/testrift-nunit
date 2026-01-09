using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TestRift.NUnit
{
    public static class ServerAutoStarter
    {
        private static readonly object _lock = new();
        private static bool _attempted = false;
        private const int DefaultStartupTimeoutMs = 5000;

        public static void EnsureServerRunning(string serverUrl, string serverYamlPath = null, bool restartOnConfigChange = false)
        {
            // Make it idempotent within the process to avoid multiple assemblies racing.
            lock (_lock)
            {
                if (_attempted) return;
                _attempted = true;
            }

            var baseUrl = string.IsNullOrWhiteSpace(serverUrl) ? "http://localhost:8080" : serverUrl;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                Console.WriteLine($"[TestRift] autoStartServer: invalid serverUrl '{baseUrl}' (skipping auto-start).");
                return;
            }

            // Safety: only auto-start for local targets.
            if (!string.Equals(baseUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[TestRift] autoStartServer: only supported for http:// URLs (got '{baseUri.Scheme}').");
                return;
            }

            var host = baseUri.Host ?? "";
            if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                  host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                  host.Equals("::1", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"[TestRift] autoStartServer: only supported for localhost targets (got host '{host}').");
                return;
            }

            // Probe using stable loopback addresses to avoid IPv6/localhost resolution issues on Windows.
            // Note: the server startup guard prints 127.0.0.1:PORT, and the server itself typically binds on IPv4.
            var probeBaseUris = GetProbeBaseUris(baseUri);
            var wasHealthy = IsHealthyAny(probeBaseUris, "/health");

            var serverPath = FindTestRiftServer();
            var isNuGet = serverPath != null && serverPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            var source = isNuGet ? "NuGet package" : "pip";
            Console.WriteLine($"[TestRift] autoStartServer: starting TestRift Server ({source}) for {baseUri}...");
            if (!string.IsNullOrWhiteSpace(serverYamlPath))
            {
                Console.WriteLine($"[TestRift] autoStartServer: using TESTRIFT_SERVER_YAML='{serverYamlPath}'");
                // Common Windows/YAML pitfall: if the YAML value is double-quoted, backslashes become escapes
                // (e.g. "C:\test\foo.yaml" turns \t into a TAB). Detect this and fail with a clear message.
                foreach (var ch in serverYamlPath)
                {
                    if (char.IsControl(ch))
                    {
                        throw new InvalidOperationException(
                            "autoStartServerYaml contains control characters. This usually means the YAML value was " +
                            "double-quoted and backslashes were treated as escapes (e.g. \\t). On Windows, use one of:\n" +
                            "- autoStartServerYaml: 'C:\\test\\TestRiftServer.yaml'   (single quotes)\n" +
                            "- autoStartServerYaml: C:/test/TestRiftServer.yaml       (forward slashes)\n" +
                            "- autoStartServerYaml: \"C:\\\\test\\\\TestRiftServer.yaml\" (double quotes with \\\\)"
                        );
                    }
                }
                if (!File.Exists(serverYamlPath))
                {
                    throw new FileNotFoundException(
                        $"autoStartServerYaml points to a file that does not exist: '{serverYamlPath}'.",
                        serverYamlPath
                    );
                }
            }

            var port = baseUri.IsDefaultPort ? 8080 : baseUri.Port;

            // Two modes:
            // - If server is already running, we still start it again and WAIT for that new process to exit.
            //   This triggers the server startup guard, which will exit quickly with 0 (same config) or non-zero (mismatch).
            // - If server is not running, we start it and block until it responds on /health or the process exits.
            try
            {
                if (wasHealthy && !restartOnConfigChange)
                {
                    // Block until the new process exits; do not proceed until we know config guard result.
                    RunStartAndWaitForExit(serverYamlPath, restartOnConfigChange);
                    return;
                }

                // Block until server is up (or startup failed).
                RunStartAndWaitForHealthy(serverYamlPath, restartOnConfigChange, port, DefaultStartupTimeoutMs);
                Console.WriteLine("[TestRift] autoStartServer: server is up.");
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "autoStartServer is enabled, but failed to start `testrift-server`. " +
                    "Ensure TestRift Server is installed via pip and available on PATH: `pip install testrift-server`.",
                    ex
                );
            }
        }

        private static void RunStartAndWaitForExit(string serverYamlPath, bool restartOnConfigChange)
        {
            if (IsWindows())
            {
                // Preflight: make sure the command is on PATH for the test process environment.
                // This often fails if testrift-server was installed into a venv that's not active for dotnet test.
                var diag = StartServerBreakawayWindows(serverYamlPath, restartOnConfigChange);
                WaitForSingleObject(diag.ProcessHandle, INFINITE);
                var winExitCode = GetExitCodeProcessInt(diag.ProcessHandle);
                CloseHandle(diag.ProcessHandle);
                if (winExitCode != 0)
                {
                    throw new InvalidOperationException($"`testrift-server` exited with code {winExitCode}.{diag.Format()}");
                }
                return;
            }

            // Non-Windows: best effort. If needed, we can implement a similar wrapper script.
            var exitCode = StartDetachedServerProcessPosix(serverYamlPath, restartOnConfigChange);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"`testrift-server` exited with code {exitCode}.");
            }
        }

        private static void RunStartAndWaitForHealthy(string serverYamlPath, bool restartOnConfigChange, int port, int startupTimeoutMs)
        {
            if (IsWindows())
            {
                // IMPORTANT: we must start the long-lived server "breakaway" from the testhost job object,
                // otherwise vstest/dotnet test can hang waiting for child processes to exit.
                var diag = StartServerBreakawayWindows(serverYamlPath, restartOnConfigChange);

                var winDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, startupTimeoutMs));
                var winProbe = new[] { new Uri($"http://127.0.0.1:{port}") };
                while (DateTime.UtcNow < winDeadline)
                {
                    if (IsHealthyAny(winProbe, "/health"))
                    {
                        // Server is up; close our handle and return (do NOT wait for server to exit).
                        CloseHandle(diag.ProcessHandle);
                        return;
                    }

                    // If the server process exits before becoming healthy, fail with captured output.
                    if (WaitForSingleObject(diag.ProcessHandle, 0) == WAIT_OBJECT_0)
                    {
                        var winExitCode = GetExitCodeProcessInt(diag.ProcessHandle);
                        CloseHandle(diag.ProcessHandle);
                        throw new InvalidOperationException($"`testrift-server` exited with code {winExitCode}.{diag.Format()}");
                    }

                    Thread.Sleep(200);
                }

                // Timeout: kill the process to avoid leaking a broken server.
                try { KillByPid(diag.ProcessId); } catch { }
                CloseHandle(diag.ProcessHandle);
                throw new TimeoutException($"autoStartServer timed out waiting for TestRift Server to become healthy at http://127.0.0.1:{port}/health.{diag.Format()}");
            }

            // Non-Windows: best effort without output capture.
            StartDetachedServerProcessPosix(serverYamlPath, restartOnConfigChange);

            var deadline = DateTime.UtcNow.AddMilliseconds(startupTimeoutMs);
            var probe = new[] { new Uri($"http://127.0.0.1:{port}") };
            while (DateTime.UtcNow < deadline)
            {
                if (IsHealthyAny(probe, "/health"))
                {
                    return;
                }
                Thread.Sleep(250);
            }
            throw new TimeoutException($"autoStartServer timed out waiting for TestRift Server to become healthy at http://127.0.0.1:{port}/health.");
        }

        private static int StartDetachedServerProcessPosix(string serverYamlPath, bool restartOnConfigChange)
        {
            // Try to find NuGet package first, then fall back to PATH
            var serverPath = FindNuGetPackageServerPosix();
            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            {
                serverPath = "testrift-server"; // Fall back to PATH
            }

            var envPrefixPosix = "";
            if (!string.IsNullOrWhiteSpace(serverYamlPath))
            {
                var escaped = EscapeForPosixSingleQuoted(serverYamlPath);
                envPrefixPosix = $"TESTRIFT_SERVER_YAML='{escaped}' ";
            }
            var args = restartOnConfigChange ? " --restart-on-config" : "";
            var escapedPath = EscapeForPosixSingleQuoted(serverPath);
            var psiPosix = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"{envPrefixPosix}{escapedPath}{args} >/dev/null 2>&1 &\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            using var p2 = Process.Start(psiPosix);
            if (p2 == null)
            {
                throw new InvalidOperationException("Failed to spawn shell to start `testrift-server`.");
            }
            p2.WaitForExit(15000);
            return p2.ExitCode;
        }

        private static string FindNuGetPackageServerPosix()
        {
            // Check NuGet global cache (~/.nuget/packages)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetCache = Path.Combine(home, ".nuget", "packages", "testrift.server");
            if (Directory.Exists(nugetCache))
            {
                var versions = Directory.GetDirectories(nugetCache)
                    .Select(d => new { Path = d, Version = Path.GetFileName(d) })
                    .Where(x => System.Version.TryParse(x.Version, out _))
                    .OrderByDescending(x => System.Version.Parse(x.Version))
                    .ToList();

                foreach (var version in versions)
                {
                    var wrapperPath = Path.Combine(version.Path, "tools", "testrift-server.sh");
                    if (File.Exists(wrapperPath))
                    {
                        return wrapperPath;
                    }
                }
            }

            return null;
        }

        private sealed class StartDiagnostics
        {
            public string ServerExePath { get; init; } = "";
            public string ServerYamlPath { get; init; } = "";
            public string StdoutPath { get; init; } = "";
            public string StderrPath { get; init; } = "";
            public IntPtr ProcessHandle { get; init; } = IntPtr.Zero;
            public int ProcessId { get; init; }

            public string Format()
            {
                var stdoutTail = TryReadTail(StdoutPath, 200);
                var stderrTail = TryReadTail(StderrPath, 200);
                return
                    "\n--- autoStart diagnostics ---" +
                    $"\nwhere.exe testrift-server: {ServerExePath}" +
                    (string.IsNullOrWhiteSpace(ServerYamlPath) ? "" : $"\nTESTRIFT_SERVER_YAML: {ServerYamlPath}") +
                    (string.IsNullOrWhiteSpace(StdoutPath) ? "" : $"\nstdout log: {StdoutPath}") +
                    (string.IsNullOrWhiteSpace(StderrPath) ? "" : $"\nstderr log: {StderrPath}") +
                    (string.IsNullOrWhiteSpace(stdoutTail) ? "" : "\n[testrift-server stdout]\n" + stdoutTail) +
                    (string.IsNullOrWhiteSpace(stderrTail) ? "" : "\n[testrift-server stderr]\n" + stderrTail) +
                    "\n--- end diagnostics ---";
            }
        }

        private static StartDiagnostics StartServerBreakawayWindows(string serverYamlPath, bool restartOnConfigChange)
        {
            var serverExePath = FindTestRiftServer();

            var stdoutPath = Path.Combine(Path.GetTempPath(), $"testrift-server-autostart-{Guid.NewGuid():N}.stdout.log");
            var stderrPath = Path.Combine(Path.GetTempPath(), $"testrift-server-autostart-{Guid.NewGuid():N}.stderr.log");

            // Build environment block (inherit + override).
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                var k = de.Key?.ToString();
                if (string.IsNullOrWhiteSpace(k)) continue;
                env[k] = de.Value?.ToString() ?? "";
            }
            if (!string.IsNullOrWhiteSpace(serverYamlPath))
            {
                env["TESTRIFT_SERVER_YAML"] = serverYamlPath;
            }

            // If this is a .ps1 script, wrap it in PowerShell invocation
            var isPs1Script = serverExePath?.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ?? false;
            string actualExePath;
            string actualArguments;

            if (isPs1Script)
            {
                actualExePath = "powershell";
                var scriptPath = serverExePath;
                var scriptArgs = restartOnConfigChange ? "--restart-on-config" : "";
                actualArguments = $"-NoProfile -NonInteractive -File \"{scriptPath}\" {scriptArgs}".Trim();
            }
            else
            {
                // pip-installed testrift-server
                actualExePath = serverExePath;
                actualArguments = restartOnConfigChange ? "--restart-on-config" : "";
            }

            var started = CreateProcessBreakawayWithRedirects(
                exePath: actualExePath,
                arguments: actualArguments,
                workingDirectory: Environment.CurrentDirectory,
                environment: env,
                stdoutPath: stdoutPath,
                stderrPath: stderrPath
            );

            return new StartDiagnostics
            {
                ServerExePath = serverExePath,
                ServerYamlPath = serverYamlPath ?? "",
                StdoutPath = stdoutPath,
                StderrPath = stderrPath,
                ProcessHandle = started.hProcess,
                ProcessId = (int)started.dwProcessId,
            };
        }

        /// <summary>
        /// Finds the TestRift Server executable/wrapper script.
        /// First checks for NuGet package (.ps1 on Windows), then falls back to PATH (pip-installed).
        /// </summary>
        private static string FindTestRiftServer()
        {
            // Try NuGet package first
            var nugetPath = FindNuGetPackageServer();
            if (!string.IsNullOrWhiteSpace(nugetPath) && File.Exists(nugetPath))
            {
                return nugetPath;
            }

            // Fall back to PATH (pip-installed)
            return FindCommandOnPathWindows("testrift-server");
        }

        /// <summary>
        /// Attempts to find the TestRift.Server NuGet package wrapper script (.ps1).
        /// </summary>
        private static string FindNuGetPackageServer()
        {
            // Check NuGet global cache (typically %USERPROFILE%\.nuget\packages)
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetCache = Path.Combine(userProfile, ".nuget", "packages", "testrift.server");
            if (Directory.Exists(nugetCache))
            {
                // Find the latest version
                var versions = Directory.GetDirectories(nugetCache)
                    .Select(d => new { Path = d, Version = Path.GetFileName(d) })
                    .Where(x => System.Version.TryParse(x.Version, out _))
                    .OrderByDescending(x => System.Version.Parse(x.Version))
                    .ToList();

                foreach (var version in versions)
                {
                    var wrapperPath = Path.Combine(version.Path, "tools", "testrift-server.ps1");
                    if (File.Exists(wrapperPath))
                    {
                        return wrapperPath;
                    }
                }
            }

            return null;
        }

        private static string FindCommandOnPathWindows(string command)
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            p.Start();
            // Keep it tight; this should be instant.
            if (!p.WaitForExit(1500))
            {
                try { KillProcessAndChildren(p); } catch { }
                throw new TimeoutException($"Timed out while checking PATH for '{command}' via where.exe.");
            }
            if (p.ExitCode != 0)
            {
                var stderr = (p.StandardError.ReadToEnd() ?? "").Trim();
                var extra = string.IsNullOrEmpty(stderr) ? "" : $" ({stderr})";
                throw new InvalidOperationException(
                    $"'{command}' was not found on PATH for the test process.{extra} " +
                    $"Install it in the same environment used to run tests or add it to PATH."
                );
            }

            var stdout = (p.StandardOutput.ReadToEnd() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return "(found on PATH, but where.exe output was empty)";
            }
            var firstLine = stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)[0];
            return firstLine.Trim();
        }

        private static string TryReadTail(string filePath, int maxLines)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return "";
                }
                var lines = File.ReadAllLines(filePath);
                if (lines.Length <= maxLines) return string.Join(Environment.NewLine, lines);
                var start = Math.Max(0, lines.Length - maxLines);
                var sb = new StringBuilder();
                for (var i = start; i < lines.Length; i++)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(lines[i]);
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string EscapeForPosixSingleQuoted(string s)
        {
            // Escape ' inside a single-quoted shell string: close, escape, reopen.
            // abc'def -> abc'"'"'def
            return (s ?? "").Replace("'", "'\"'\"'");
        }

        private static void KillByPid(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                KillProcessAndChildren(proc);
            }
            catch
            {
                // ignore
            }
        }

        private static bool IsWindows()
        {
#if NET462
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
            return OperatingSystem.IsWindows();
#endif
        }

        private static void KillProcessAndChildren(Process process)
        {
#if NET462
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }

        private static Uri[] GetProbeBaseUris(Uri baseUri)
        {
            // Prefer 127.0.0.1 first to avoid ::1-only attempts when server binds IPv4.
            var port = baseUri.IsDefaultPort ? (baseUri.Scheme == "https" ? 443 : 80) : baseUri.Port;
            var scheme = baseUri.Scheme;
            return new[]
            {
                new Uri($"{scheme}://127.0.0.1:{port}"),
                new Uri($"{scheme}://localhost:{port}"),
                new Uri($"{scheme}://[::1]:{port}"),
            };
        }

        private static bool IsHealthyAny(Uri[] probeBaseUris, string path)
        {
            foreach (var baseUri in probeBaseUris)
            {
                var healthUri = new Uri(baseUri, path);
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                    using var resp = http.GetAsync(healthUri).GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch
                {
                    // try next
                }
            }
            return false;
        }

        // ---- Win32 CreateProcess (breakaway) ----

        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint STARTF_USESTDHANDLES = 0x00000100;
        private const uint STARTF_USESHOWWINDOW = 0x00000001;
        private const int STD_INPUT_HANDLE = -10;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint INFINITE = 0xFFFFFFFF;
        private const short SW_HIDE = 0;
        private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        private static int GetExitCodeProcessInt(IntPtr hProcess)
        {
            if (hProcess == IntPtr.Zero) return 1;
            if (!GetExitCodeProcess(hProcess, out var code)) return 1;
            return unchecked((int)code);
        }

        private static PROCESS_INFORMATION CreateProcessBreakawayWithRedirects(
            string exePath,
            string arguments,
            string workingDirectory,
            IDictionary<string, string> environment,
            string stdoutPath,
            string stderrPath)
        {
            var envPtr = IntPtr.Zero;
            var attrList = IntPtr.Zero;
            var attrListSize = IntPtr.Zero;
            var handleListMem = IntPtr.Zero;
            FileStream stdoutFs = null;
            FileStream stderrFs = null;
            try
            {
                envPtr = BuildEnvironmentBlockUnicode(environment);

                stdoutFs = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                stderrFs = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                SetHandleInformation(stdoutFs.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
                SetHandleInformation(stderrFs.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);

                // Restrict handle inheritance to ONLY stdout/stderr (and optionally stdin).
                // This is critical: inheriting random handles from the test runner can cause dotnet test to hang.
                var handles = new[]
                {
                    stdoutFs.SafeFileHandle.DangerousGetHandle(),
                    stderrFs.SafeFileHandle.DangerousGetHandle(),
                };
                handleListMem = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (var i = 0; i < handles.Length; i++)
                {
                    Marshal.WriteIntPtr(handleListMem, i * IntPtr.Size, handles[i]);
                }

                // Allocate and initialize attribute list.
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
                attrList = Marshal.AllocHGlobal(attrListSize);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                {
                    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed (win32={Marshal.GetLastWin32Error()}).");
                }
                if (!UpdateProcThreadAttribute(
                        attrList,
                        0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                        handleListMem,
                        (IntPtr)(IntPtr.Size * handles.Length),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new InvalidOperationException($"UpdateProcThreadAttribute(HANDLE_LIST) failed (win32={Marshal.GetLastWin32Error()}).");
                }

                var siex = new STARTUPINFOEX();
                siex.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                siex.StartupInfo.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
                siex.StartupInfo.wShowWindow = SW_HIDE;
                siex.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
                siex.StartupInfo.hStdOutput = stdoutFs.SafeFileHandle.DangerousGetHandle();
                siex.StartupInfo.hStdError = stderrFs.SafeFileHandle.DangerousGetHandle();
                siex.lpAttributeList = attrList;

                var flags =
                    CREATE_UNICODE_ENVIRONMENT |
                    CREATE_NO_WINDOW |
                    CREATE_NEW_PROCESS_GROUP |
                    CREATE_BREAKAWAY_FROM_JOB |
                    EXTENDED_STARTUPINFO_PRESENT;

                var cmdLine = QuoteForCmdLine(exePath);
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    cmdLine += " " + arguments;
                }

                if (!CreateProcessW(
                        lpApplicationName: null,
                        lpCommandLine: cmdLine,
                        lpProcessAttributes: IntPtr.Zero,
                        lpThreadAttributes: IntPtr.Zero,
                        bInheritHandles: true,
                        dwCreationFlags: flags,
                        lpEnvironment: envPtr,
                        lpCurrentDirectory: workingDirectory,
                        lpStartupInfo: ref siex,
                        lpProcessInformation: out var pi))
                {
                    throw new InvalidOperationException($"CreateProcessW failed (win32={Marshal.GetLastWin32Error()}).");
                }

                // We no longer need the thread handle.
                CloseHandle(pi.hThread);

                return pi;
            }
            finally
            {
                try { stdoutFs?.Dispose(); } catch { }
                try { stderrFs?.Dispose(); } catch { }
                if (attrList != IntPtr.Zero)
                {
                    try { DeleteProcThreadAttributeList(attrList); } catch { }
                    try { Marshal.FreeHGlobal(attrList); } catch { }
                }
                if (handleListMem != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(handleListMem); } catch { }
                }
                if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
            }
        }

        private static IntPtr BuildEnvironmentBlockUnicode(IDictionary<string, string> env)
        {
            // Environment block is a sequence of null-terminated strings: "k=v\0k2=v2\0\0"
            var keys = new List<string>(env.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                sb.Append(k);
                sb.Append('=');
                sb.Append(env[k] ?? "");
                sb.Append('\0');
            }
            sb.Append('\0');
            var bytes = Encoding.Unicode.GetBytes(sb.ToString());
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        private static string QuoteForCmdLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.Contains(" ") || s.Contains("\t") || s.Contains("\""))
            {
                return "\"" + s.Replace("\"", "\\\"") + "\"";
            }
            return s;
        }
    }
}



