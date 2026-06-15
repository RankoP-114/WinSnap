using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace WinSnap.App.Diagnostics;

public static class MemoryTrimmer
{
    private const long ManagedHeapTrimThresholdBytes = 96L * 1024 * 1024;
    private const long WorkingSetTrimThresholdBytes = 384L * 1024 * 1024;
    private static int _pending;

    public static void TrimAfterCapture()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            QueueTrimNow();
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(new Action(() => StartIdleTimer(dispatcher)), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "调度截图会话后内存清理失败，改为后台直接清理");
            QueueTrimNow();
        }
    }

    private static void StartIdleTimer(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            QueueTrimNow();
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            QueueTrimNow();
        };
        timer.Start();
    }

    private static void QueueTrimNow()
    {
        _ = Task.Run(() =>
        {
            try
            {
                TrimNow();
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    private static void TrimNow()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long beforeManaged = GC.GetTotalMemory(forceFullCollection: false);
            long beforeWorkingSet = process.WorkingSet64;
            if (beforeManaged < ManagedHeapTrimThresholdBytes &&
                beforeWorkingSet < WorkingSetTrimThresholdBytes)
            {
                Log.Debug(
                    "截图会话后跳过内存清理：托管堆 {Managed:N0}，工作集 {WorkingSet:N0}",
                    beforeManaged,
                    beforeWorkingSet);
                return;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);

            process.Refresh();
            long afterManaged = GC.GetTotalMemory(forceFullCollection: false);
            long afterWorkingSet = process.WorkingSet64;
            Log.Debug(
                "截图会话后内存清理：托管堆 {ManagedBefore:N0} -> {ManagedAfter:N0}，工作集 {WorkingSetBefore:N0} -> {WorkingSetAfter:N0}",
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
}
