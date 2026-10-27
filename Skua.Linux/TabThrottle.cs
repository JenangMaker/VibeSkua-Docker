using System.ComponentModel;
using System.Runtime;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

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
        WatchCutscenes();
        _ = Task.Run(RestoreHeadless);
    }

    // Headless Mode is a runtime option (Skua does not save it), so a redeploy
    // turned it off on every tab: hidden tabs ran at twice the frame rate and
    // the shown one drew at full rate, until someone switched it on again.
    // Each tab keeps its last state in headless-tab<N> next to accounts.json
    // (one file per tab: tabs never write the same file); SKUA_HEADLESS (1 or
    // 0) is the state of a tab with none saved yet.
    private static string HeadlessFile =>
        Path.Combine(Path.GetDirectoryName(AccountStore.FilePath)!, $"headless-tab{SkuaRuntime.Instance + 1}");

    private void RestoreHeadless()
    {
        bool? on = null;
        string from = "as before the restart";
        try
        {
            if (File.Exists(HeadlessFile))
                on = File.ReadAllText(HeadlessFile).Trim() == "1";
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] could not read {HeadlessFile}: {e.Message}");
        }
        if (on is null)
        {
            from = "SKUA_HEADLESS";
            on = SkuaRuntime.EnvRaw("SKUA_HEADLESS")?.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => null,
            };
        }
        var options = Get<IScriptOption>();
        if (on is not { } value || options.HeadlessMode == value)
            return;
        // As Army Control sets it, without sending it on to the other tabs.
        Get<IDispatcherService>().Invoke(() =>
        {
            options.IsIpcMessageProcessing = true;
            try { options.HeadlessMode = value; }
            finally { options.IsIpcMessageProcessing = false; }
        });
        Console.WriteLine($"[host] Headless Mode {(value ? "on" : "off")} ({from})");
    }

    private static void SaveHeadless(bool on)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeadlessFile)!);
            File.WriteAllText(HeadlessFile, on ? "1" : "0");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] could not save Headless Mode to {HeadlessFile}: {e.Message}");
        }
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
        bool headless, shrink, restore = false, headlessChanged, shrinkChanged;
        int target;
        // Updates run on several threads at once (a tab click and a Headless
        // toggle). The option is read, and the state decided, under the lock:
        // otherwise a thread holding an old value that finished last could leave
        // a shown tab's game shrunk to 1x1, or the state saying one thing and
        // the window another.
        lock (_throttleLock)
        {
            headless = Get<IScriptOption>().HeadlessMode;
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
            headlessChanged = headless != IsHeadless;
            IsHeadless = headless;
            shrinkChanged = shrink != IsShrunk;
            IsShrunk = shrink;
        }
        if (target > 0)
        {
            ApplyFrameRate(ThrottledFps());
            PauseDrawing(true, headless);
        }
        else if (restore)
        {
            ApplyFrameRate(FullFps());
            PauseDrawing(false, headless);
        }
        if (headlessChanged)
        {
            HeadlessChanged?.Invoke(headless);
            SaveHeadless(headless);
        }
        if (shrinkChanged)
        {
            Shrunk?.Invoke(shrink);
            if (shrink)
                _ = Task.Run(TrimMemory);
        }
    }

    private async Task KeepThrottled(CancellationToken token)
    {
        for (int tick = 0; !token.IsCancellationRequested; tick++)
        {
            // A missed cell event (a jump the game made itself) is caught here.
            bool changed = SetCutscene(CutsceneCell());
            if (changed || tick % 3 == 0)
            {
                ApplyFrameRate(ThrottledFps());
                PauseDrawing(true, IsHeadless);
                ClearDropToasts();
            }
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { }
        }
        _cutscene = null;
    }

    // Story quests that a cutscene completes: scripts jump into the map's
    // cutscene cell (Cut1, Cut2, ...), whose timeline sends the completion a
    // few frames in, and jump straight out again. At 30 fps those frames play
    // in a fraction of a second; at 1-2 fps the player had left before they
    // did (CruxShip's "Act 1 Complete" looped for hours on a Headless tab).
    // While the player is in such a cell the game runs at full speed; drawing
    // stays paused. The jump's own packet starts it, before the cell's first
    // frame: polling noticed too late.
    private string? _cutscene;

    private void WatchCutscenes() =>
        StrongReferenceMessenger.Default.Register<HostApi, CellChangedMessage, int>(this, (int)MessageChannels.GameEvents,
            (host, message) =>
            {
                if (host._throttleLoop is null)
                    return;
                string? cell = IsCutscene(message.Cell) ? message.Cell : null;
                // Raised on the bridge's thread: the game call must not wait on it.
                if (host.SetCutscene(cell))
                    _ = Task.Run(() => host.ApplyFrameRate(host.ThrottledFps()));
                if (cell is null)
                    _ = Task.Run(() => host.BoostJump(message.Cell));
            });

    private static bool IsCutscene(string? cell) => cell?.StartsWith("Cut", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The frame rate a throttled tab runs at now: full speed in a cutscene cell or just after a jump.</summary>
    private int ThrottledFps() => _cutscene is not null || _jumpBoost is not null ? FullFps() : _throttleFps;

    // After a jump the game makes the new cell's monster sprites on its next
    // frames, and Skua's attack skips a monster without one. At 1 fps that took
    // 0.5-0.7 s after the cell changed, and CoreBots' KillMonster without an
    // item attacks once and gives up 0.5 s later: a story kill was skipped, its
    // quest item never came, and the script relogged forever. From the jump's
    // packet until the cell's monsters all have sprites (2 s at most) the game
    // runs at full speed; drawing stays paused. At 30 fps that took 30-120 ms;
    // full speed cost a headless tab ~17% CPU against ~4% at 1 fps, so a tab
    // jumping every 5 s spends about 0.4% more. Only live monsters are waited
    // for: a dead one may never get its sprite back, which kept the boost on
    // for the whole 2 s.
    private object? _jumpBoost;
    private long _jumpBoosts, _jumpBoostMs, _jumpBoostsCapped;

    private async Task BoostJump(string cell)
    {
        var mine = new object();
        Interlocked.Exchange(ref _jumpBoost, mine);
        ApplyFrameRate(FullFps());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var bot = Get<IScriptInterface>();
            int want = bot.Monsters.MapMonsters.Count(m => m.Alive && string.Equals(m.Cell, cell, StringComparison.OrdinalIgnoreCase));
            while (clock.ElapsedMilliseconds < 2000 && Volatile.Read(ref _jumpBoost) == mine)
            {
                await Task.Delay(50);
                if (string.Equals(bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase)
                    && (want == 0 || bot.Monsters.CurrentAvailableMonsters.Count(m => m.Alive) >= want))
                    break;
            }
        }
        catch { }
        // A later jump took over: it ends the boost.
        if (Interlocked.CompareExchange(ref _jumpBoost, null, mine) != mine)
            return;
        Interlocked.Increment(ref _jumpBoosts);
        Interlocked.Add(ref _jumpBoostMs, clock.ElapsedMilliseconds);
        if (clock.ElapsedMilliseconds >= 2000)
            Interlocked.Increment(ref _jumpBoostsCapped);
        if (_throttleLoop is not null)
            ApplyFrameRate(ThrottledFps());
    }

    /// <summary>Records the cutscene cell the player is in (null: none); true if that changed.</summary>
    private bool SetCutscene(string? cell)
    {
        string? old = Interlocked.Exchange(ref _cutscene, cell);
        if (old == cell)
            return false;
        Console.WriteLine(cell is null
            ? $"[host] left cutscene cell {old}: back to {_throttleFps} fps"
            : $"[host] in cutscene cell {cell}: running the game at {FullFps()} fps until it leaves");
        return true;
    }

    private string? CutsceneCell()
    {
        if (!Get<Skua.Ruffle.RuffleBridge>().IsConnected)
            return null;
        try
        {
            var player = Get<IScriptInterface>().Player;
            string cell = player.LoggedIn ? player.Cell : "";
            return IsCutscene(cell) ? cell : null;
        }
        catch
        {
            return null;
        }
    }

    private int FullFps()
    {
        int own = Get<IScriptOption>().SetFPS;
        return own > 0 ? own : 30;
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
