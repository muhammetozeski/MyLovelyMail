using System.Globalization;
using System.Text;
using MyLovelyMail.MainProject.Constants;

namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Why a run ended. Only the endings the process can still see are listed: Task Manager's
    /// "End task", a power cut and a debugger stop all go through TerminateProcess, which runs no
    /// handler whatsoever - those runs leave no Stopped line, and that absence is the answer.
    /// </summary>
    public enum AppExitReason
    {
        /// <summary>Written while the app is up. Not an ending - the run has not ended yet.</summary>
        Running,

        /// <summary>"❌ Exit" in the tray icon's menu.</summary>
        TrayExit,

        /// <summary>The window's close button, with close-to-tray off so it really is a quit.</summary>
        WindowClosed,

        /// <summary>Windows is shutting down or restarting.</summary>
        SystemShutdown,

        /// <summary>The user is signing out of Windows.</summary>
        UserLogOff,

        /// <summary>The app stopped itself to let a staged update be applied.</summary>
        UpdateRestart,

        /// <summary>An exception nobody caught took the process down.</summary>
        UnhandledException,

        /// <summary>The process exited normally with nothing more specific known about it.</summary>
        ProcessExit
    }

    /// <summary>
    /// A plain-text file in <see cref="AppPaths.AppCache"/> that answers two questions from
    /// outside the process: is the app running right now, and how did the last run end.
    /// <para>
    /// Running is answered by the file HANDLE, not by its contents. The app holds it open for
    /// writing and shares only reads, so any other process that fails to open it for writing has
    /// its answer. Windows closes the handle as the process is torn down - killing the app does
    /// not leave a stale lock behind - so the signal cannot outlive the run it describes.
    /// </para>
    /// <para>
    /// The deploy script needs this. It used to delete the live install and find out it could not
    /// finish only when it reached the locked executable, having already deleted everything that
    /// sorted before it.
    /// </para>
    /// <para>
    /// Every line states something that is true when it is read. "Unexpected" is about how the run
    /// ENDED, so while the app is up it says "-" rather than pre-judging an ending that has not
    /// happened: false once a deliberate stop is recorded, true for a crash, and "-" left standing
    /// beside an empty Stopped line for a run that was killed. That last case is the one nobody
    /// could write from inside, and the next run names it on the PreviousRun line.
    /// </para>
    /// </summary>
    public static class RunLock
    {
        public const string FileName = "run-lock.txt";

        /// <summary>Timestamp shape of every stamp in the file, e.g. 2026.08.20 14.46.28.</summary>
        public const string TimestampFormat = "yyyy.MM.dd HH.mm.ss";

        const string Unknown = "-";
        const int KeyColumnWidth = 13;
        static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

        static readonly object gate = new();
        static FileStream? held;
        static Timer? heartbeat;
        static string startedAt = Unknown;
        static string previousRun = Unknown;
        static AppExitReason noted = AppExitReason.ProcessExit;

        public static string FilePath => Path.Combine(AppPaths.AppCache, FileName);

        /// <summary>Takes the lock and writes the opening lines. Safe to call twice; the second call does nothing.</summary>
        public static void Claim()
        {
            lock (gate)
            {
                if (held != null) return;
                try
                {
                    Directory.CreateDirectory(AppPaths.AppCache);
                    // Read the last run's verdict BEFORE the handle below truncates the file.
                    previousRun = ReadPreviousVerdict();
                    // FileShare.Read: everyone may read the state, nobody may write or delete it.
                    // That refused write is the whole "the app is running" signal.
                    held = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    startedAt = Stamp();
                    noted = AppExitReason.ProcessExit;
                    Write(AppExitReason.Running, stoppedAt: null, toDisk: true);
                    heartbeat = new Timer(static _ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
                }
                catch (Exception ex)
                {
                    // Never block startup over a diagnostics file. The deploy script falls back to
                    // the process list when the lock is missing.
                    held?.Dispose();
                    held = null;
                    Log($"Run lock could not be claimed: {ex.Message}", LogLevel.Warning);
                }
            }
        }

        /// <summary>
        /// Records why the app is stopping; the line itself is written by <see cref="Release"/>.
        /// First caller wins - the earliest cause seen is the real one, and every later step of a
        /// shutdown (a tray quit closes the window too) would otherwise overwrite it with its own.
        /// </summary>
        public static void NoteExitReason(AppExitReason reason)
        {
            lock (gate)
            {
                if (held != null && noted == AppExitReason.ProcessExit)
                    noted = reason;
            }
        }

        /// <summary>Writes the closing lines and drops the lock. Safe to call twice, and from any exit path.</summary>
        public static void Release(AppExitReason? reason = null)
        {
            lock (gate)
            {
                if (held == null) return;
                try
                {
                    heartbeat?.Dispose();
                    heartbeat = null;
                    Write(reason ?? noted, Stamp(), toDisk: true);
                }
                catch (Exception ex)
                {
                    Log($"Run lock could not be closed cleanly: {ex.Message}", LogLevel.Warning);
                }
                finally
                {
                    held.Dispose();
                    held = null;
                }
            }
        }

        static void Beat()
        {
            lock (gate)
            {
                if (held == null) return;
                try
                {
                    // Not flushed to the platter every second: another process reads through the
                    // same file cache, so the tick is visible immediately either way, and the one
                    // case a flush would cover - the power going out - is already an unexpected
                    // end by definition.
                    Write(AppExitReason.Running, stoppedAt: null, toDisk: false);
                }
                catch (Exception ex)
                {
                    heartbeat?.Dispose();
                    heartbeat = null;
                    Log($"Run lock heartbeat stopped: {ex.Message}", LogLevel.Warning);
                }
            }
        }

        static void Write(AppExitReason reason, string? stoppedAt, bool toDisk)
        {
            if (held == null) return;

            string text = new StringBuilder()
                .AppendLine(Row("Started", startedAt))
                .AppendLine(Row("Stopped", stoppedAt ?? Unknown))
                .AppendLine(Row("Reason", reason.ToString()))
                .AppendLine(Row("ReasonText", DescriptionOf(reason)))
                .AppendLine(Row("Unexpected", stoppedAt == null ? Unknown : IsUnexpected(reason) ? "true" : "false"))
                .AppendLine(Row("Alive", Stamp()))
                .AppendLine(Row("Pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)))
                .AppendLine(Row("Version", AppConstants.AppVersion))
                .AppendLine(Row("PreviousRun", previousRun))
                .ToString();

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            held.Position = 0;
            held.Write(bytes, 0, bytes.Length);
            held.SetLength(bytes.Length);
            held.Flush(toDisk);
        }

        /// <summary>
        /// Did this ending happen against the user's wishes? Every reason the app can write is a
        /// deliberate stop except a crash. The other unwanted endings - a kill, a power cut, a
        /// debugger stop - write nothing at all, so they are recognised by the missing Stopped
        /// line instead, and named as such in the next run's PreviousRun.
        /// </summary>
        static bool IsUnexpected(AppExitReason reason) => reason == AppExitReason.UnhandledException;

        static string Row(string key, string value) => key.PadRight(KeyColumnWidth) + value;

        static string Stamp() => DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// What the file left behind says about the run before this one. A file whose Stopped line
        /// never arrived belongs to a run that was killed, crashed hard or lost power.
        /// </summary>
        static string ReadPreviousVerdict()
        {
            try
            {
                if (!File.Exists(FilePath)) return "None";

                string[] lines = File.ReadAllLines(FilePath);
                string stopped = ValueOf(lines, "Stopped");
                if (stopped.Length == 0 || stopped == Unknown)
                    return $"Unexpected - killed, crashed or lost power, last alive {Fallback(ValueOf(lines, "Alive"))}";

                return $"{Fallback(ValueOf(lines, "Reason"))} at {stopped}";
            }
            catch (Exception ex)
            {
                return $"Unreadable ({ex.GetType().Name})";
            }
        }

        static string ValueOf(string[] lines, string key)
        {
            foreach (string line in lines)
            {
                if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                string value = line[key.Length..].Trim();
                // "Started" must not answer for "Stopped": only a key followed by padding counts.
                if (value.Length > 0 && line.Length > key.Length && char.IsWhiteSpace(line[key.Length]))
                    return value;
            }
            return string.Empty;
        }

        static string Fallback(string value) => value.Length == 0 ? Unknown : value;

        static string DescriptionOf(AppExitReason reason) => reason switch
        {
            AppExitReason.Running => "Still running; if no copy is up, this run was killed",
            AppExitReason.TrayExit => "Quit from the tray icon menu",
            AppExitReason.WindowClosed => "Window closed with close-to-tray off",
            AppExitReason.SystemShutdown => "Windows shut down or restarted",
            AppExitReason.UserLogOff => "The user signed out of Windows",
            AppExitReason.UpdateRestart => "Stopped to let a staged update be applied",
            AppExitReason.UnhandledException => "An unhandled exception took the process down",
            _ => "Exited with no more specific reason known"
        };
    }
}
