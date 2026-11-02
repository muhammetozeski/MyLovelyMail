using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>Where a tor executable was found, which is worth showing: an app-managed copy and the user's own daemon behave differently.</summary>
    public sealed record TorExecutable(string Path, string FoundBy);

    /// <summary>Starts Tor with state under AppCache. Android supplies the bundled executable; Windows discovers an existing installation.</summary>
    public static partial class TorProcess
    {
        const bool IsLogEnabled = true;

        /// <summary>Everything this class writes (state, cached consensus) lives in AppCache: re-creatable, never user data.</summary>
        public static string DataDirectory => Path.Combine(AppPaths.AppCache, "Tor");

        static string ManagedStateFilePath => Path.Combine(DataDirectory, "managed-tor.json");

        sealed record ManagedTorState(int Pid, int SocksPort, long StartTicks, string ExecutablePath);

        static Process? running;
        static readonly Lock startGate = new();
        static readonly SemaphoreSlim startAsyncGate = new(1, 1);

        /// <summary>
        /// Bumped by every <see cref="Stop"/>. A start records it on the way in and checks it again
        /// once the process exists, which is the only way a stop that arrived DURING the start can
        /// be honoured: until the process is assigned, Stop() has nothing to look at.
        /// </summary>
        static int stopGeneration;

        /// <summary>The last 200 lines tor printed, so a failed start can be read instead of guessed at.</summary>
        static readonly Queue<string> log = new();
        const int MaxLoggedLines = 200;

        public static IReadOnlyCollection<string> RecentOutput
        {
            get { lock (log) return [.. log]; }
        }

        public static bool IsRunning => running is { HasExited: false };

        static int? ownSocksPort;

        /// <summary>The managed process's SOCKS port while that process is alive. A stale port must never be trusted as an app-managed Tor endpoint.</summary>
        public static int? OwnSocksPort => IsRunning ? ownSocksPort : null;

        static void Remember(string line)
        {
            lock (log)
            {
                log.Enqueue(line);
                while (log.Count > MaxLoggedLines) log.Dequeue();
            }
        }

        #region Starting one

        /// <summary>A TCP port nothing holds right now, asked of the OS rather than guessed at.</summary>
        public static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>
        /// Starts a tor whose SOCKS port belongs to this app, and returns that port once tor has
        /// bootstrapped. Throws when no executable exists, when tor exits, or when the bootstrap
        /// does not finish inside <see cref="Settings.TorStartupTimeoutSeconds"/>.
        /// </summary>
        public static async Task<int> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await startAsyncGate.WaitAsync(cancellationToken);
            try
            {
                int myStopGeneration;
                lock (startGate)
                {
                    myStopGeneration = stopGeneration;
                    if (IsRunning && OwnSocksPort is { } already)
                    {
                        Log($"Tor is already running under this app on port {already}.", Run: IsLogEnabled);
                        return already;
                    }
                }

                var sw = Stopwatch.StartNew();
                var executable = Find() ?? throw new TorUnavailableException(MissingExecutableAdvice, recoverable: false);

                string activeDataDirectory = PrepareDataDirectory(executable.Path);
                int socksPort = FindFreePort();

                var startInfo = new ProcessStartInfo(executable.Path)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(executable.Path) ?? activeDataDirectory
                };

                // IsolateSOCKSAuth is tor's default and is what makes the per-account SOCKS credentials
                // land on separate circuits; it is spelled out so a machine-wide torrc cannot turn the
                // isolation off underneath us.
                startInfo.ArgumentList.Add("--SocksPort");
                startInfo.ArgumentList.Add($"{socksPort} IsolateSOCKSAuth IsolateDestAddr IsolateDestPort");
                startInfo.ArgumentList.Add("--DataDirectory");
                startInfo.ArgumentList.Add(activeDataDirectory);
                startInfo.ArgumentList.Add("--ClientOnly");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("--ControlPort");
                startInfo.ArgumentList.Add("0");
                startInfo.ArgumentList.Add("--AvoidDiskWrites");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("--Log");
                startInfo.ArgumentList.Add("notice stdout");

                AddGeoIpFiles(startInfo, executable.Path);

                // Before the process exists, so a missing transport plugin is reported as itself rather
                // than as a tor that would not bootstrap.
                TorBridges.AddTo(startInfo, executable.Path);
                lastStartArguments = [.. startInfo.ArgumentList];

                lock (log) log.Clear();

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var bootstrapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Tor can exit long after this method gave up waiting, and the Exited handler below
                // faults the task either way. Observing it here keeps that late fault from surfacing as
                // an unobserved task exception with no caller left to explain it.
                _ = bootstrapped.Task.ContinueWith(static finished => _ = finished.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

                process.OutputDataReceived += (_, e) => ReadTorLine(e.Data, bootstrapped);
                process.ErrorDataReceived += (_, e) => ReadTorLine(e.Data, bootstrapped);
                process.Exited += (_, _) =>
                {
                    int exitCode = SafeExitCode(process);
                    string lastLines = LastMeaningfulLine();
                    Log($"The tor process (PID {SafeProcessId(process)}) exited with code {exitCode}. Output: {lastLines}", LogLevel.Warning, Run: IsLogEnabled);
                    bootstrapped.TrySetException(new TorUnavailableException(
                        $"Tor exited before it finished starting (exit code {exitCode}). Last output: {lastLines}"));
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    Log($"Failed to start tor process from '{executable.Path}':\n{ex}", LogLevel.Error, Run: IsLogEnabled);
                    process.Dispose();
                    throw;
                }

                AttachToKillOnCloseJob(process);
                SaveManagedState(process, socksPort, executable.Path);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (startGate)
                {
                    if (stopGeneration != myStopGeneration)
                    {
                        Log("Tor was asked to stop while it was starting; ending the process that just appeared.", LogLevel.Warning, Run: IsLogEnabled);
                        KillQuietly(process);
                        DeleteManagedState();
                        throw new TorUnavailableException("Tor was stopped while it was starting.", recoverable: false);
                    }

                    running?.Dispose();
                    running = process;
                    ownSocksPort = socksPort;
                }
                RegisterShutdownHook();
                Log($"Started tor (PID {SafeProcessId(process)}) from '{executable.Path}' on SOCKS port {socksPort} with DataDirectory '{activeDataDirectory}'.", Run: IsLogEnabled);

                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Settings.TorStartupTimeoutSeconds.Value)));
                try
                {
                    await bootstrapped.Task.WaitAsync(budget.Token);
                    Log($"Tor bootstrapped in {sw.ElapsedMilliseconds} ms; its SOCKS port {socksPort} is ready.", Run: IsLogEnabled);
                    return socksPort;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TorUnavailableException(
                        $"Tor did not finish starting within {Settings.TorStartupTimeoutSeconds.Value}s. Last output: {LastMeaningfulLine()}");
                }
            }
            finally
            {
                startAsyncGate.Release();
            }
        }

        /// <summary>
        /// Ensures <see cref="DataDirectory"/> is not locked by an orphaned <c>tor</c> process left behind
        /// by an earlier app session that was forcibly terminated. If the lock is still held after cleanup,
        /// falls back to a clean per-process subdirectory so startup never fails on a stale lock file.
        /// </summary>
        static string PrepareDataDirectory(string executablePath)
        {
            Directory.CreateDirectory(DataDirectory);

            // 1. Check persisted managed-tor.json from a previous app session.
            try
            {
                if (File.Exists(ManagedStateFilePath))
                {
                    var state = JsonSerializer.Deserialize<ManagedTorState>(File.ReadAllText(ManagedStateFilePath));
                    if (state != null)
                    {
                        TryKillOrphanProcessById(state.Pid, "managed-tor.json");
                    }
                    DeleteManagedState();
                }
            }
            catch (Exception ex)
            {
                Log($"Could not inspect managed-tor.json: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            }

            // 2. Check if DataDirectory\lock is still held by an older orphan tor process.
            string lockFilePath = Path.Combine(DataDirectory, "lock");
            if (IsLockFileHeld(lockFilePath))
            {
                Log($"Tor lock file '{lockFilePath}' is held by an orphaned process; cleaning up matching tor instances.", LogLevel.Warning, Run: IsLogEnabled);
                KillOrphanTorHoldingDataDirectory(executablePath);

                var waitSw = Stopwatch.StartNew();
                while (IsLockFileHeld(lockFilePath) && waitSw.ElapsedMilliseconds < 4000)
                {
                    Thread.Sleep(150);
                }
            }

            // 3. If the lock file is still held by an external process we cannot terminate, isolate this process's DataDirectory.
            if (IsLockFileHeld(lockFilePath))
            {
                string fallbackDir = Path.Combine(DataDirectory, $"instance-{Environment.ProcessId}");
                Directory.CreateDirectory(fallbackDir);
                Log($"Tor lock file '{lockFilePath}' remained locked; using isolated directory '{fallbackDir}'.", LogLevel.Warning, Run: IsLogEnabled);
                return fallbackDir;
            }

            return DataDirectory;
        }

        static bool IsLockFileHeld(string lockFilePath)
        {
            if (!File.Exists(lockFilePath)) return false;
            try
            {
                using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void TryKillOrphanProcessById(int pid, string reason)
        {
            if (pid <= 0 || pid == Environment.ProcessId) return;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited) return;
                if (!proc.ProcessName.Contains("tor", StringComparison.OrdinalIgnoreCase)) return;

                Log($"Terminating orphaned tor process PID {pid} ({reason}).", LogLevel.Warning, Run: IsLogEnabled);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
            catch (Exception ex)
            {
                Log($"Could not terminate orphaned tor PID {pid}: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            }
        }

        static void KillOrphanTorHoldingDataDirectory(string executablePath)
        {
            string normalizedExec = Path.GetFullPath(executablePath);
            string? scoopTorAppsRoot = null;
            int scoopIdx = normalizedExec.IndexOf(@"\scoop\apps\tor\", StringComparison.OrdinalIgnoreCase);
            if (scoopIdx >= 0)
                scoopTorAppsRoot = normalizedExec[..(scoopIdx + @"\scoop\apps\tor\".Length)];

            foreach (var proc in Process.GetProcessesByName("tor"))
            {
                try
                {
                    if (proc.HasExited) continue;
                    string? procPath = null;
                    try { procPath = proc.MainModule?.FileName; } catch { }

                    if (string.IsNullOrWhiteSpace(procPath)) continue;
                    string fullProcPath = Path.GetFullPath(procPath);

                    bool isSameTorBinary =
                        string.Equals(fullProcPath, normalizedExec, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ResolveFinalExecutablePath(fullProcPath), normalizedExec, StringComparison.OrdinalIgnoreCase) ||
                        (scoopTorAppsRoot != null && fullProcPath.StartsWith(scoopTorAppsRoot, StringComparison.OrdinalIgnoreCase));

                    if (!isSameTorBinary) continue;

                    Log($"Terminating orphan tor process PID {proc.Id} ('{fullProcPath}') holding '{DataDirectory}'.", LogLevel.Warning, Run: IsLogEnabled);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log($"Could not inspect or stop tor process PID {proc.Id}: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }

        static void SaveManagedState(Process process, int socksPort, string executablePath)
        {
            try
            {
                long ticks = 0;
                try { ticks = process.StartTime.ToUniversalTime().Ticks; } catch { }
                var state = new ManagedTorState(SafeProcessId(process), socksPort, ticks, executablePath);
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(ManagedStateFilePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                Log($"Could not write managed-tor.json: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            }
        }

        static void DeleteManagedState()
        {
            try
            {
                if (File.Exists(ManagedStateFilePath))
                    File.Delete(ManagedStateFilePath);
            }
            catch { }
        }

        static int SafeExitCode(Process process)
        {
            try { return process.ExitCode; }
            catch { return -1; }
        }

        static int SafeProcessId(Process process)
        {
            try { return process.Id; }
            catch { return -1; }
        }

        static string LastMeaningfulLine()
        {
            var lines = RecentOutput;
            if (lines.Count == 0) return "(tor printed nothing)";

            var important = lines
                .Where(static l => l.Contains("[warn]", StringComparison.OrdinalIgnoreCase) || l.Contains("[err]", StringComparison.OrdinalIgnoreCase))
                .TakeLast(4)
                .ToList();

            if (important.Count > 0)
                return string.Join(" | ", important);

            return string.Join(" | ", lines.TakeLast(3));
        }

        /// <summary>Tor warns without these and still runs; passing them when they exist keeps the notice log clean and path selection informed.</summary>
        static void AddGeoIpFiles(ProcessStartInfo startInfo, string executablePath)
        {
            string? folder = Path.GetDirectoryName(executablePath);
            if (folder == null) return;

            foreach (var (option, fileName) in new[] { ("--GeoIPFile", "geoip"), ("--GeoIPv6File", "geoip6") })
            {
                foreach (string candidate in new[]
                         {
                             Path.Combine(folder, fileName),
                             Path.Combine(folder, "data", fileName),
                             Path.Combine(folder, "..", "data", fileName),
                             Path.Combine(folder, "..", "data.original", fileName),
                             Path.Combine(folder, "..", "Data", "Tor", fileName)
                         })
                {
                    if (!File.Exists(candidate)) continue;
                    startInfo.ArgumentList.Add(option);
                    startInfo.ArgumentList.Add(Path.GetFullPath(candidate));
                    break;
                }
            }
        }

        /// <summary>How far tor has got, and what it is stuck on if it is stuck.</summary>
        /// <param name="Percent">0-100 as tor last reported it.</param>
        /// <param name="Phase">Tor's own word for the step: "conn", "handshake", "requesting_descriptors", "done".</param>
        /// <param name="Problem">Set only while tor is reporting itself stuck; cleared by the next step forward.</param>
        public sealed record BootstrapState(int Percent, string Phase, string Summary, string? Problem, DateTime AtUtc);

        /// <summary>
        /// The most recent progress tor reported, or null before one was started.
        /// </summary>
        public static BootstrapState? Bootstrap { get; private set; }

        static string[] lastStartArguments = [];

        /// <summary>The exact command line the last start used. "Did it actually get my bridges?" has no other answer.</summary>
        public static IReadOnlyList<string> LastStartArguments => lastStartArguments;

        [GeneratedRegex(@"Bootstrapped (\d+)%\s*\(([^)]*)\):\s*(.*)", RegexOptions.IgnoreCase)]
        private static partial Regex BootstrapProgress();

        [GeneratedRegex(@"Problem bootstrapping.*?Stuck at (\d+)%(?:\s*\(([^)]*)\))?:\s*(.*)", RegexOptions.IgnoreCase)]
        private static partial Regex BootstrapStuck();

        /// <summary>Tor's notice log carries its own progress; "Bootstrapped 100%" is the line that means the SOCKS port will answer.</summary>
        static void ReadTorLine(string? line, TaskCompletionSource<bool> bootstrapped)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Remember(line);
            TrackBootstrap(line);

            if (line.Contains("Bootstrapped 100%", StringComparison.OrdinalIgnoreCase))
            {
                Log("Tor: " + line, Run: IsLogEnabled);
                bootstrapped.TrySetResult(true);
            }
            else if (line.Contains("[err]", StringComparison.OrdinalIgnoreCase))
            {
                Log("Tor: " + line, LogLevel.Error, Run: IsLogEnabled);
            }
            else if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase) || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
            {
                Log("Tor: " + line, LogLevel.Warning, Run: IsLogEnabled);
            }
        }

        static void TrackBootstrap(string line)
        {
            if (BootstrapProgress().Match(line) is { Success: true } progress
                && int.TryParse(progress.Groups[1].Value, out int percent))
            {
                Bootstrap = new BootstrapState(percent, progress.Groups[2].Value.Trim(), progress.Groups[3].Value.Trim(), null, DateTime.UtcNow);
                return;
            }

            if (BootstrapStuck().Match(line) is { Success: true } stuck
                && int.TryParse(stuck.Groups[1].Value, out int stuckPercent))
            {
                string phase = stuck.Groups[2].Success ? stuck.Groups[2].Value.Trim() : Bootstrap?.Phase ?? "stuck";
                Bootstrap = new BootstrapState(stuckPercent, phase,
                    Bootstrap?.Summary ?? string.Empty, stuck.Groups[3].Value.Trim(), DateTime.UtcNow);
            }
        }

        static bool shutdownHookRegistered;

        /// <summary>A tor this app started must not outlive it; without this a crash-restart cycle leaves one orphan per run.</summary>
        static void RegisterShutdownHook()
        {
            lock (startGate)
            {
                if (shutdownHookRegistered) return;
                shutdownHookRegistered = true;
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        }

        /// <summary>
        /// Ends the tor this app started. Safe to call when none is running — and a call that
        /// lands while one is starting is remembered rather than dropped: <see cref="StartAsync"/>
        /// checks <see cref="stopGeneration"/> once its process exists and kills it there.
        /// </summary>
        public static void Stop()
        {
            Process? process;
            lock (startGate)
            {
                stopGeneration++;
                process = running;
                running = null;
                ownSocksPort = null;
            }
            DeleteManagedState();
            if (process == null) return;

            KillQuietly(process);
            Log("The tor process this app started was stopped.", Run: IsLogEnabled);
        }

        /// <summary>Ends one process and lets go of its handle. Never throws: stopping is cleanup, and cleanup that fails must not take the caller with it.</summary>
        static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Log($"Could not stop the tor process: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            }
            finally
            {
                try { process.Dispose(); } catch { /* Disposing an already-gone process is not a failure worth surfacing. */ }
            }
        }

        #endregion

        #region Windows Job Object (Kill child tor.exe when parent process exits or is killed)

        static IntPtr windowsJobHandle;
        static readonly Lock jobGate = new();

        static void AttachToKillOnCloseJob(Process process)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                lock (jobGate)
                {
                    if (windowsJobHandle == IntPtr.Zero)
                    {
                        windowsJobHandle = CreateJobObjectW(IntPtr.Zero, null);
                        if (windowsJobHandle != IntPtr.Zero)
                        {
                            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                            {
                                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                                {
                                    LimitFlags = 0x00002000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                }
                            };
                            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                            if (!SetInformationJobObject(windowsJobHandle, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)size))
                            {
                                CloseHandle(windowsJobHandle);
                                windowsJobHandle = IntPtr.Zero;
                            }
                        }
                    }

                    if (windowsJobHandle != IntPtr.Zero)
                    {
                        AssignProcessToJobObject(windowsJobHandle, process.Handle);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Could not attach tor process to Windows Job Object: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}
