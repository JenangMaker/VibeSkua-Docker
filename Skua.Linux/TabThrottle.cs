using System.ComponentModel;
using System.Runtime;
using System.Runtime.InteropServices;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// Keeping the game cheap when nobody looks at it, as Skua.App.WPF does:
/// <list type="bullet">
/// <item>A tab that is not on screen (POST /army/throttle?on=1, the
/// TabbedHostWindow's WM_SKUA_THROTTLE) runs at SKUA_HIDDEN_FPS.</item>
/// <item>Headless Mode (Options > Game, or Army Control) runs at 1 fps whether
/// the tab is shown or not (GameContainerUserControl's HeadlessMode).</item>
/// </list>
/// Either way the game window shrinks to 1x1 (see <see cref="Shrunk"/>),
/// memory is handed back, and the page stops drawing (Ruffle's maxRenderFps 0,
/// web/public/index.html's pauseDrawing): the game keeps running, but neither
/// the page nor the GPU process shared by all tabs spends anything on frames
/// nobody sees. SKUA_HIDDEN_DRAW=1 keeps drawing hidden tabs (not headless
/// ones). Unlike there, all this is put back every few seconds: Skua's own FPS
/// option (Options > SetFPS) writes stage.frameRate whenever a script starts,
/// and a reloaded page starts out drawing.
/// </summary>
public sealed partial class HostApi
{
    /// <summary>SKUA_HIDDEN_FPS: the frame rate of a tab that is not on screen (default 2).</summary>
    public static int HiddenFps { get; } = int.TryParse(SkuaRuntime.EnvRaw("SKUA_HIDDEN_FPS"), out int fps) ? Math.Clamp(fps, 1, 60) : 2;

    private const int HeadlessFps = 1;

    /// <summary>SKUA_HIDDEN_DRAW=1: keep drawing a tab that is hidden (not one in Headless Mode).</summary>
    private static readonly bool KeepDrawingHidden =
        SkuaRuntime.EnvRaw("SKUA_HIDDEN_DRAW")?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>Raised (on any thread) when the game window should shrink to 1x1 (true) or fill its area (false).</summary>
    public static event Action<bool>? Shrunk;

    /// <summary>Raised (on any thread) when Headless Mode goes on or off.</summary>
    public static event Action<bool>? HeadlessChanged;

    /// <summary>Whether the game window is shrunk now.</summary>
    public static bool IsShrunk { get; private set; }

    /// <summary>Whether Headless Mode is on now.</summary>
    public static bool IsHeadless { get; private set; }

    private readonly object _throttleLock = new();
    private bool _hidden;
    private int _hiddenFps = HiddenFps;
    private int _throttleFps;
    private CancellationTokenSource? _throttleLoop;

    private void WatchHeadless()
    {
        if (Get<IScriptOption>() is INotifyPropertyChanged options)
            options.PropertyChanged += (_, e) =>
            {
                // Often raised on the UI thread; the game call must not block it.
                if (e.PropertyName == nameof(IScriptOption.HeadlessMode))
                    _ = Task.Run(UpdateThrottle);
            };
        UpdateThrottle();
    }

    private object Throttle(bool on, int fps)
    {
        lock (_throttleLock)
        {
            _hidden = on;
            _hiddenFps = fps;
        }
        UpdateThrottle();
        return new { throttled = on, fps = on ? fps : (int?)null, headless = IsHeadless };
    }

    private void UpdateThrottle()
    {
        bool headless = Get<IScriptOption>().HeadlessMode;
        bool shrink, restore = false;
        int target;
        lock (_throttleLock)
        {
            target = headless ? HeadlessFps : _hidden ? _hiddenFps : 0;
            shrink = target > 0;
            if (target > 0 && _throttleLoop is null)
            {
                var loop = _throttleLoop = new CancellationTokenSource();
                _ = Task.Run(() => KeepThrottled(loop.Token));
            }
            else if (target == 0 && _throttleLoop is not null)
            {
                _throttleLoop.Cancel();
                _throttleLoop = null;
                restore = true;
            }
            _throttleFps = target;
        }
        if (target > 0)
        {
            ApplyFrameRate(target);
            PauseDrawing(true, headless);
        }
        else if (restore)
        {
            int own = Get<IScriptOption>().SetFPS;
            ApplyFrameRate(own > 0 ? own : 30);
            PauseDrawing(false, headless);
        }
        if (headless != IsHeadless)
        {
            IsHeadless = headless;
            HeadlessChanged?.Invoke(headless);
        }
        if (shrink != IsShrunk)
        {
            IsShrunk = shrink;
            Shrunk?.Invoke(shrink);
            if (shrink)
                _ = Task.Run(TrimMemory);
        }
    }

    private async Task KeepThrottled(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ApplyFrameRate(_throttleFps);
            PauseDrawing(true, IsHeadless);
            ClearDropToasts();
            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { }
        }
    }

    // The game's item toasts ("added", quest rewards) go away after a number of
    // frames: at 1-2 fps they pile up, each looping a goto every frame, and a
    // farming tab's game cost grew all session (enter phase 25 -> 316 ms a
    // frame in 5 minutes). Nobody sees them while the tab is throttled.
    private void ClearDropToasts()
    {
        if (!Get<Skua.Ruffle.RuffleBridge>().IsConnected)
            return;
        try { Get<IScriptInterface>().Flash.Call("clearDropToasts"); }
        catch { }
    }

    private void ApplyFrameRate(int fps)
    {
        if (fps <= 0 || !Get<Skua.Ruffle.RuffleBridge>().IsConnected)
            return;
        try { Get<IScriptInterface>().Flash.SetGameObject("stage.frameRate", fps); }
        catch { }
    }

    // The page's pauseDrawing; an older page without it just says so.
    private void PauseDrawing(bool paused, bool headless)
    {
        var bridge = Get<Skua.Ruffle.RuffleBridge>();
        if (!bridge.IsConnected || (paused && !headless && KeepDrawingHidden))
            return;
        try { bridge.Invoke("page.pauseDrawing", paused); }
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
