using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Tor
{
    public static partial class TorProcess
    {
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
    }
}
