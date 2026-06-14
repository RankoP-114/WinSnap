using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;

namespace WinSnap.App.Diagnostics;

public static class MemoryTrimmer
{
    private static bool _pending;

    public static void TrimAfterCapture()
    {
        if (_pending)
            return;
        _pending = true;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pending = false;
            TrimNow();
        };
        timer.Start();
    }

    private static void TrimNow()
    {
        try
        {
            long beforePrivate = GC.GetTotalMemory(forceFullCollection: false);

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            EmptyWorkingSet(Process.GetCurrentProcess().Handle);

            long afterPrivate = GC.GetTotalMemory(forceFullCollection: false);
            Log.Debug("截图会话后内存清理：托管堆 {Before:N0} -> {After:N0}", beforePrivate, afterPrivate);
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
