using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>Where a tor executable was found, which is worth showing: an app-managed copy and the user's own daemon behave differently.</summary>
    public sealed record TorExecutable(string Path, string FoundBy);

    /// <summary>
    /// Finds a tor executable and runs one under the app's own data directory.
    /// <para>
    /// On Windows the app never installs Tor. If the machine has none this class finds none and says
    /// so — a Tor-only account then refuses to connect rather than quietly using the open network,
    /// which is the entire point of marking it Tor-only. On Android the app carries its own tor in
    /// its package (<see cref="BundledExecutablePath"/>), so there is nothing to search for.
    /// </para>
    /// </summary>
    public static partial class TorProcess
    {
        /// <summary>Everything this class writes (state, cached consensus) lives in AppCache: re-creatable, never user data.</summary>
        public static string DataDirectory => System.IO.Path.Combine(AppPaths.AppCache, "Tor");

        static Process? running;
        static readonly Lock startGate = new();

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

        /// <summary>
        /// The SOCKS port of the tor this app started, or null when no such tor is running. Read
        /// from the process rather than remembered: a tor that died on its own clears nothing, and
        /// a stale port here is dangerous rather than merely wrong — the endpoint built from it is
        /// labelled AppManaged, which <see cref="TorService"/> trusts without the RESOLVE proof, so
        /// anything that had since bound the freed port would carry a Tor-only account unverified.
        /// </summary>
        public static int? OwnSocksPort => IsRunning ? ownSocksPort : null;

        static void Remember(string line)
        {
            lock (log)
            {
                log.Enqueue(line);
                while (log.Count > MaxLoggedLines) log.Dequeue();
            }
        }

        #region Finding an executable

        /// <summary>
        /// The tor that ships inside the app package, or null where the app looks for a tor already on
        /// the machine. Android fills it in with libtor.so from the native library directory, because an
        /// app there cannot run a binary it did not bring; the user's own tor is Orbot, which
        /// <see cref="TorService"/> uses only when this one cannot start.
        /// </summary>
        public static string? BundledExecutablePath { get; } = ResolveBundledExecutablePath();

        static string? ResolveBundledExecutablePath()
        {
            string? path = null;
            ResolveBundledExecutable(ref path);
            return path;
        }

        /// <summary>Implemented by a platform that carries its own tor; the others leave the path null.</summary>
        /// <param name="path">Set to the full path of the bundled executable.</param>
        static partial void ResolveBundledExecutable(ref string? path);

        /// <summary>What to tell the user when <see cref="Find"/> finds nothing: the start error and every status line say the same.</summary>
        public static string MissingExecutableAdvice => BundledExecutablePath is { } missing
            ? $"The tor that ships inside the app is missing from {missing}; reinstalling the app puts it back."
            : "No tor executable was found. Install Tor (or the Tor Browser) and, if it lives somewhere unusual, "
              + "put its full path in the TorExecutablePath setting.";

        /// <summary>File name of the tor binary on each platform this app is built for.</summary>
        static string ExecutableName => OperatingSystem.IsWindows() ? "tor.exe" : "tor";

        /// <summary>
        /// Last answer of <see cref="Search"/>, keyed by the setting that steers it. Three status
        /// lines in the UI call <see cref="Find"/> straight from their render tree, so without a
        /// cache every re-render walked the whole PATH with File.Exists and appended another entry
        /// to a log buffer that has no size cap.
        /// </summary>
        static (string SettingKey, TorExecutable? Result, DateTime AtUtc)? lastSearch;
        static readonly Lock searchGate = new();

        /// <summary>
        /// How long "there is no tor on this machine" is worth believing. A found executable is
        /// cached until its file goes away, but a MISS has to expire: installing the Tor Browser or
        /// dropping tor.exe into the app's folder are the two documented ways to fix the problem
        /// the miss caused, and both were invisible until the app was restarted.
        /// </summary>
        static readonly TimeSpan MissingRecheckInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The tor bundled with the app where there is one; otherwise the first tor executable that
        /// exists, most explicit first: the setting, then PATH, then the places the usual Windows
        /// installs put one. Returns null when there is none.
        /// <para>
        /// The answer is remembered until the TorExecutablePath setting changes or the file behind
        /// it goes away, so this is cheap enough to call from a render.
        /// </para>
        /// </summary>
        public static TorExecutable? Find()
        {
            // A tor in the package is the answer by definition, and neither the setting nor PATH can
            // point at anything an Android app would be allowed to run.
            if (BundledExecutablePath is { } bundled)
                return File.Exists(bundled) ? new TorExecutable(bundled, "the app package") : null;

            string settingKey = Settings.TorExecutablePath.Value;
            lock (searchGate)
            {
                if (lastSearch is { } cached && cached.SettingKey == settingKey && IsStillGood(cached))
                    return cached.Result;

                var found = Search();
                lastSearch = (settingKey, found, DateTime.UtcNow);
                return found;
            }
        }

        /// <summary>
        /// A hit stands as long as its file is still there; a miss stands only for
        /// <see cref="MissingRecheckInterval"/>, long enough to keep a render cheap and short
        /// enough that a tor installed while the app is running is picked up without a restart.
        /// </summary>
        static bool IsStillGood((string SettingKey, TorExecutable? Result, DateTime AtUtc) cached) =>
            cached.Result != null
                ? File.Exists(cached.Result.Path)
                : DateTime.UtcNow - cached.AtUtc < MissingRecheckInterval;

        /// <summary>Walks the candidates for real. Only reached on a cache miss, so its logging stays readable.</summary>
        static TorExecutable? Search()
        {
            foreach (var (candidate, foundBy) in Candidates())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                    string resolved = ResolveScoopShim(candidate);
                    Log($"Tor executable found via {foundBy}: {resolved}");
                    return new TorExecutable(resolved, foundBy);
                }
                catch (Exception ex)
                {
                    Log($"Tor executable candidate '{candidate}' could not be checked: {ex.Message}", LogLevel.Warning);
                }
            }
            Log("No tor executable was found on this machine.", LogLevel.Warning);
            return null;
        }

        static IEnumerable<(string Path, string FoundBy)> Candidates()
        {
            if (Settings.TorExecutablePath.Value is { Length: > 0 } configured)
                yield return (configured.Trim('"'), "the TorExecutablePath setting");

            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate;
                try { candidate = System.IO.Path.Combine(directory, ExecutableName); }
                catch { continue; } // A malformed PATH entry is not worth failing the whole search over.
                yield return (candidate, "PATH");
            }

            // Beside the app, which is where a portable copy would be dropped.
            yield return (System.IO.Path.Combine(AppPaths.AppFolder, "Tor", ExecutableName), "the app folder");
            yield return (System.IO.Path.Combine(AppPaths.Root, "Tor", ExecutableName), "the install folder");

            if (!OperatingSystem.IsWindows()) yield break;

            foreach (string root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(static r => r.Length > 0).Distinct())
            {
                yield return (System.IO.Path.Combine(root, "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"), "a Tor Browser install");
                yield return (System.IO.Path.Combine(root, "Programs", "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"), "a Tor Browser install");
            }

            yield return (System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tor", "tor.exe"), "the Tor Expert Bundle");
        }

        /// <summary>
        /// Scoop puts a stub .exe on PATH next to a .shim file naming the real binary. Running the
        /// stub works, but it also injects the packaged torrc, which would fight the command line
        /// this class builds. Following the shim gets the real executable and only our own options.
        /// </summary>
        static string ResolveScoopShim(string candidate)
        {
            string shim = System.IO.Path.ChangeExtension(candidate, ".shim");
            if (!File.Exists(shim)) return candidate;

            foreach (string line in File.ReadAllLines(shim))
            {
                if (!line.TrimStart().StartsWith("path", StringComparison.OrdinalIgnoreCase)) continue;
                int separator = line.IndexOf('=');
                if (separator < 0) continue;

                string target = line[(separator + 1)..].Trim().Trim('"');
                if (File.Exists(target))
                {
                    Log($"Followed the scoop shim '{shim}' to '{target}'.");
                    return target;
                }
            }
            return candidate;
        }

        #endregion

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
            // Discovery can reach here on behalf of an operation that was cancelled while the last
            // candidate was being verified — VerifyAsync answers with a verdict rather than
            // throwing, so the cancellation is not noticed until something asks. Launching a tor
            // process for that operation and then throwing leaves a tor nobody asked for.
            cancellationToken.ThrowIfCancellationRequested();

            int myStopGeneration;
            lock (startGate) myStopGeneration = stopGeneration;

            lock (startGate)
            {
                if (IsRunning && OwnSocksPort is { } already)
                {
                    Log($"Tor is already running under this app on port {already}.");
                    return already;
                }
            }

            // recoverable:false, and it matters. Left recoverable, an empty PATH became six ladder
            // rungs, then six Polly retries on 3/6/12/24/48/96-second delays, then the same again
            // on the next sync pass and forever in the IDLE loop — minutes of spinner and a log
            // full of route reports for an answer that was knowable at once and cannot change
            // until the user installs something.
            var executable = Find() ?? throw new TorUnavailableException(MissingExecutableAdvice, recoverable: false);

            int socksPort = FindFreePort();
            Directory.CreateDirectory(DataDirectory);

            var startInfo = new ProcessStartInfo(executable.Path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(executable.Path) ?? DataDirectory
            };

            // IsolateSOCKSAuth is tor's default and is what makes the per-account SOCKS credentials
            // land on separate circuits; it is spelled out so a machine-wide torrc cannot turn the
            // isolation off underneath us.
            startInfo.ArgumentList.Add("--SocksPort");
            startInfo.ArgumentList.Add($"{socksPort} IsolateSOCKSAuth IsolateDestAddr IsolateDestPort");
            startInfo.ArgumentList.Add("--DataDirectory");
            startInfo.ArgumentList.Add(DataDirectory);
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
                Log($"The tor process exited with code {SafeExitCode(process)}.", LogLevel.Warning);
                bootstrapped.TrySetException(new TorUnavailableException(
                    $"Tor exited before it finished starting (exit code {SafeExitCode(process)}). Last output: {LastMeaningfulLine()}"));
            };

            try
            {
                process.Start();
            }
            catch
            {
                // A Process that never started still owns handles, and nothing else will ever
                // reach this one: it is not in `running` yet.
                process.Dispose();
                throw;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (startGate)
            {
                // A Stop() that arrived while this was starting saw running == null and returned,
                // so the tor appearing a moment later outlived the request to end it. The counter
                // is how that request is noticed by the start it was meant for.
                if (stopGeneration != myStopGeneration)
                {
                    Log("Tor was asked to stop while it was starting; ending the process that just appeared.", LogLevel.Warning);
                    KillQuietly(process);
                    throw new TorUnavailableException("Tor was stopped while it was starting.", recoverable: false);
                }

                // A tor that died on its own leaves its Process object here; replacing it without
                // disposing leaks the handle for the life of the app.
                running?.Dispose();
                running = process;
                ownSocksPort = socksPort;
            }
            RegisterShutdownHook();
            Log($"Started tor from '{executable.Path}' on SOCKS port {socksPort}.");

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Settings.TorStartupTimeoutSeconds.Value)));
            try
            {
                await bootstrapped.Task.WaitAsync(budget.Token);
                Log($"Tor bootstrapped; its SOCKS port {socksPort} is ready.");
                return socksPort;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Not killed: tor may still be climbing towards 100%, and the caller's own probe of
                // the SOCKS port is the better judge of "usable" than the bootstrap line alone.
                throw new TorUnavailableException(
                    $"Tor did not finish starting within {Settings.TorStartupTimeoutSeconds.Value}s. Last output: {LastMeaningfulLine()}");
            }
        }

        static int SafeExitCode(Process process)
        {
            try { return process.ExitCode; }
            catch { return -1; }
        }

        static string LastMeaningfulLine()
        {
            var lines = RecentOutput;
            return lines.Count == 0 ? "(tor printed nothing)" : lines.Last();
        }

        /// <summary>Tor warns without these and still runs; passing them when they exist keeps the notice log clean and path selection informed.</summary>
        static void AddGeoIpFiles(ProcessStartInfo startInfo, string executablePath)
        {
            string? folder = System.IO.Path.GetDirectoryName(executablePath);
            if (folder == null) return;

            foreach (var (option, fileName) in new[] { ("--GeoIPFile", "geoip"), ("--GeoIPv6File", "geoip6") })
            {
                foreach (string candidate in new[]
                         {
                             System.IO.Path.Combine(folder, fileName),
                             System.IO.Path.Combine(folder, "data", fileName),
                             System.IO.Path.Combine(folder, "..", "Data", "Tor", fileName)
                         })
                {
                    if (!File.Exists(candidate)) continue;
                    startInfo.ArgumentList.Add(option);
                    startInfo.ArgumentList.Add(System.IO.Path.GetFullPath(candidate));
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
        /// <para>
        /// Only "Bootstrapped 100%" used to mean anything; every other line went to the log viewer
        /// as a warning nobody reads. So a user watched "building a circuit…" for the whole
        /// TorStartupTimeoutSeconds — three minutes by default — while tor had said "Stuck at 10%"
        /// within seconds, which is the one message that would have told them what to do about it.
        /// </para>
        /// </summary>
        public static BootstrapState? Bootstrap { get; private set; }

        static string[] lastStartArguments = [];

        /// <summary>The exact command line the last start used. "Did it actually get my bridges?" has no other answer.</summary>
        public static IReadOnlyList<string> LastStartArguments => lastStartArguments;

        [GeneratedRegex(@"Bootstrapped (\d+)%\s*\(([^)]*)\):\s*(.*)", RegexOptions.IgnoreCase)]
        private static partial Regex BootstrapProgress();

        /// <summary>
        /// Tor writes the stuck report two ways — "Stuck at 10% (conn_done): …" and "Stuck at 5%: …"
        /// — so the phase is optional here. The tail is taken whole rather than by hunting for a
        /// parenthesis: a first attempt matched the FIRST bracketed group and reported the phase
        /// name ("conn_done") as the problem, which says nothing, while the reason the user needs
        /// ("Connection timed out; TIMEOUT; …") sat in the brackets after it.
        /// </summary>
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
                Log("Tor: " + line);
                bootstrapped.TrySetResult(true);
            }
            else if (line.Contains("[err]", StringComparison.OrdinalIgnoreCase))
            {
                Log("Tor: " + line, LogLevel.Error);
            }
            else if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase) || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
            {
                Log("Tor: " + line, LogLevel.Warning);
            }
        }

        /// <summary>
        /// Keeps <see cref="Bootstrap"/> current from tor's own notice lines. A "stuck" report
        /// attaches to the percentage already recorded rather than replacing it, so the display can
        /// say both where it got to and what is wrong; the next step forward clears the problem,
        /// because tor recovering on its own is the common case.
        /// </summary>
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
            if (process == null) return;

            KillQuietly(process);
            Log("The tor process this app started was stopped.");
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
                Log($"Could not stop the tor process: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                try { process.Dispose(); } catch { /* Disposing an already-gone process is not a failure worth surfacing. */ }
            }
        }

        #endregion
    }
}
