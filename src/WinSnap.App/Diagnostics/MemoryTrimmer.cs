using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace WinSnap.App.Diagnostics;

public static class MemoryTrimmer
{
    private const long ManagedHeapTrimThresholdBytes = 48L * 1024 * 1024;
    private const long WorkingSetTrimThresholdBytes = 192L * 1024 * 1024;
    private const int HardTrimDelayMs = 650;
    private const int SoftTrimDelayMs = 250;
    private static int _pending;

    public static void TrimTransientCaptureBuffers()
        => QueueTrim(force: false, trimWorkingSet: false, delayMs: SoftTrimDelayMs);

    public static void TrimAfterCapture()
        => QueueTrim(force: true, trimWorkingSet: true, delayMs: HardTrimDelayMs);

    private static void QueueTrim(bool force, bool trimWorkingSet, int delayMs)
    {
        if (!force && Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        if (force)
            Volatile.Write(ref _pending, 1);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            QueueTrimNow(force, trimWorkingSet);
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(
                new Action(() => StartIdleTimer(dispatcher, force, trimWorkingSet, delayMs)),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "调度截图会话后内存清理失败，改为后台直接清理");
            QueueTrimNow(force, trimWorkingSet);
        }
    }

    private static void StartIdleTimer(Dispatcher dispatcher, bool force, bool trimWorkingSet, int delayMs)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            QueueTrimNow(force, trimWorkingSet);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            QueueTrimNow(force, trimWorkingSet);
        };
        timer.Start();
    }

    private static void QueueTrimNow(bool force, bool trimWorkingSet)
    {
        _ = Task.Run(() =>
        {
            try
            {
                TrimNow(force, trimWorkingSet);
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    private static void TrimNow(bool force, bool trimWorkingSet)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long beforeManaged = GC.GetTotalMemory(forceFullCollection: false);
            long beforeWorkingSet = process.WorkingSet64;
            if (!force &&
                beforeManaged < ManagedHeapTrimThresholdBytes &&
                beforeWorkingSet < WorkingSetTrimThresholdBytes)
            {
                Log.Debug(
                    "截图会话后跳过内存清理：托管堆 {Managed:N0}，工作集 {WorkingSet:N0}",
                    beforeManaged,
                    beforeWorkingSet);
                return;
            }

            if (force)
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: force);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: force);
            GC.WaitForPendingFinalizers();

            if (trimWorkingSet && !EmptyWorkingSet(process.Handle))
            {
                int error = Marshal.GetLastPInvokeError();
                Log.Debug("截图会话后工作集修剪失败：0x{Error:X8}", error);
            }

            process.Refresh();
            long afterManaged = GC.GetTotalMemory(forceFullCollection: false);
            long afterWorkingSet = process.WorkingSet64;
            Log.Debug(
                "截图会话后内存清理：force={Force} trimWorkingSet={TrimWorkingSet}，托管堆 {ManagedBefore:N0} -> {ManagedAfter:N0}，工作集 {WorkingSetBefore:N0} -> {WorkingSetAfter:N0}",
                force,
                trimWorkingSet,
                beforeManaged,
                afterManaged,
                beforeWorkingSet,
                afterWorkingSet);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "截图会话后内存清理失败");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
