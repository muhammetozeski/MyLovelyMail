using Android;
using Android.App;
using Android.Content;
using Android.OS;

[assembly: UsesPermission(Manifest.Permission.WakeLock)]
[assembly: UsesPermission(Manifest.Permission.UseExactAlarm)]
[assembly: UsesPermission(Manifest.Permission.ScheduleExactAlarm, MaxSdkVersion = 32)]

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// The Android <see cref="SyncScheduler.WaitBetweenPasses"/>. The wait is an exact alarm that wakes
    /// the phone even in Doze; when it fires, a partial wake lock keeps the CPU running until the pass it
    /// started has finished, which is the moment the loop asks for the next wait.
    /// <para>
    /// Exact alarms need USE_EXACT_ALARM from Android 13 on and SCHEDULE_EXACT_ALARM on 12, both granted
    /// at install. Should the phone refuse them anyway, the alarm falls back to the inexact kind, which
    /// Doze may hold back for several minutes but still delivers.
    /// </para>
    /// </summary>
    [BroadcastReceiver(Name = "com.muhammetozeski.mylovelymail.SyncAlarm", Exported = false)]
    public class SyncAlarm : BroadcastReceiver
    {
        const string WakeLockTag = "MyLovelyMail:SyncPass";

        /// <summary>Longest a pass may hold the CPU awake; a pass stuck on a dead connection must not keep the phone up all night.</summary>
        static readonly TimeSpan WakeLockLimit = TimeSpan.FromMinutes(10);

        static readonly Lock gate = new();
        static TaskCompletionSource? pendingWait;
        static PowerManager.WakeLock? passWakeLock;

        static AlarmManager Alarms => (AlarmManager)Application.Context.GetSystemService(Context.AlarmService)!;

        /// <summary>
        /// Waits <paramref name="interval"/> in a way that survives the phone sleeping. Called by the sync
        /// loop right after a pass, so it first lets go of the wake lock that pass ran under.
        /// </summary>
        /// <param name="interval">Time until the next pass.</param>
        /// <param name="cancellationToken">Stops the wait and cancels the alarm.</param>
        public static async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                ReleaseWakeLock();
                pendingWait = wait;
            }

            Schedule(interval);
            using var registration = cancellationToken.Register(() =>
            {
                Alarms.Cancel(AlarmIntent());
                lock (gate)
                {
                    if (pendingWait == wait) pendingWait = null;
                    ReleaseWakeLock();
                }
                wait.TrySetCanceled(cancellationToken);
            });
            await wait.Task;
        }

        /// <summary>
        /// The alarm fired: hold the CPU awake and let the waiting loop run its pass. An alarm left over
        /// from an earlier process finds nobody waiting — that process's successor runs its first pass on
        /// its own at start — and takes no wake lock.
        /// </summary>
        public override void OnReceive(Context? context, Intent? intent)
        {
            lock (gate)
            {
                if (pendingWait == null) return;

                passWakeLock ??= NewWakeLock();
                passWakeLock.Acquire((long)WakeLockLimit.TotalMilliseconds);
                pendingWait.TrySetResult();
                pendingWait = null;
            }
        }

        static void Schedule(TimeSpan interval)
        {
            long triggerAt = SystemClock.ElapsedRealtime() + (long)interval.TotalMilliseconds;
            if (!OperatingSystem.IsAndroidVersionAtLeast(31) || Alarms.CanScheduleExactAlarms())
            {
                Alarms.SetExactAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, triggerAt, AlarmIntent());
                return;
            }

            Log("Exact alarms are not allowed on this phone; the next mail check uses an inexact alarm.", LogLevel.Warning);
            Alarms.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, triggerAt, AlarmIntent());
        }

        /// <summary>The same PendingIntent every time, so scheduling replaces the previous alarm and Cancel finds it.</summary>
        static PendingIntent AlarmIntent()
        {
            var context = Application.Context;
            return PendingIntent.GetBroadcast(context, 0, new Intent(context, typeof(SyncAlarm)),
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        }

        /// <summary>Not reference counted: one release ends it however many passes acquired it.</summary>
        static PowerManager.WakeLock NewWakeLock()
        {
            var power = (PowerManager)Application.Context.GetSystemService(Context.PowerService)!;
            var wakeLock = power.NewWakeLock(WakeLockFlags.Partial, WakeLockTag)!;
            wakeLock.SetReferenceCounted(false);
            return wakeLock;
        }

        static void ReleaseWakeLock()
        {
            if (passWakeLock is { IsHeld: true }) passWakeLock.Release();
        }
    }
}
