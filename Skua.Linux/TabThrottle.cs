using System.Runtime;
using System.Runtime.InteropServices;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// A tab that is not on screen (POST /army/throttle?on=1), as Skua.App.WPF's
/// TabbedHostWindow does with WM_SKUA_THROTTLE: the game runs at a low frame
/// rate, its window shrinks to 1x1 (see Throttled) and memory is handed back.
/// Unlike there, the frame rate is put back every few seconds: Skua's own FPS
/// option (Options > SetFPS) writes stage.frameRate whenever a script starts,
/// which would otherwise leave a hidden tab at full speed.
/// </summary>
public sealed partial class HostApi
{
    /// <summary>SKUA_HIDDEN_FPS: the frame rate of a tab that is not on screen (default 2).</summary>
    public static int HiddenFps { get; } = int.TryParse(SkuaRuntime.EnvRaw("SKUA_HIDDEN_FPS"), out int fps) ? Math.Clamp(fps, 1, 60) : 2;

    /// <summary>Raised (on a worker thread) when the tab goes off screen (true) or back (false).</summary>
    public static event Action<bool>? Throttled;

    /// <summary>Whether the tab is off screen now.</summary>
    public static bool IsThrottled { get; private set; }

    private int _throttleFps;
    private CancellationTokenSource? _throttleLoop;

    private object Throttle(bool on, int fps)
    {
        bool was = _throttleFps > 0;
        _throttleFps = on ? fps : 0;
        IsThrottled = on;
        if (on && !was)
        {
            var loop = _throttleLoop = new CancellationTokenSource();
            _ = Task.Run(() => KeepThrottled(loop.Token));
            Throttled?.Invoke(true);
            _ = Task.Run(TrimMemory);
        }
        else if (on)
        {
            ApplyFrameRate(fps);
        }
        else if (was)
        {
            _throttleLoop?.Cancel();
            _throttleLoop = null;
            Throttled?.Invoke(false);
            int own = Get<IScriptOption>().SetFPS;
            ApplyFrameRate(own > 0 ? own : 30);
        }
        return new { throttled = on, fps = on ? fps : (int?)null };
    }

    private async Task KeepThrottled(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ApplyFrameRate(_throttleFps);
            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { }
        }
    }

    private void ApplyFrameRate(int fps)
    {
        if (fps <= 0 || Get<IScriptInterface>().Options.HeadlessMode || !Get<Skua.Ruffle.RuffleBridge>().IsConnected)
            return;
        try { Get<IScriptInterface>().Flash.SetGameObject("stage.frameRate", fps); }
        catch { }
    }

    // What TrimWorkingSet does there: collect, compact, and let glibc return
    // the freed pages to the system.
    private static void TrimMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            malloc_trim(0);
        }
        catch { }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint pad);
}
