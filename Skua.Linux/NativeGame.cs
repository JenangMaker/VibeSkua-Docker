using System.Diagnostics;

namespace Skua.Linux;

/// <summary>
/// The game without a browser (SKUA_GAME=native): this tab's Skua starts
/// Ruffle's desktop player (the JenangMaker/ruffle fork, branch native-skua)
/// on skua.swf, pointed at this Skua's bridge, and starts it again if it
/// exits. It replaces the Electron page web/main.js opens per tab: the player
/// fetches from game.aq.com and connects to the game server itself, so there
/// is no page server, CORS fix or socket proxy, and the bridge speaks the
/// page's protocol (Skua.Ruffle/RuffleBridge.cs is unchanged).
///
/// Environment:
///   SKUA_GAME          native: this mode (anything else: the Electron page)
///   SKUA_RUFFLE_BIN    the player       (/opt/ruffle/ruffle_desktop)
///   SKUA_SWF           skua.swf         (next to Skua's own files)
///   RUFFLE_QUALITY     low, medium, high... (low); also the most the game
///                      can raise it to (RUFFLE_MAX_QUALITY, below)
///   RUFFLE_MAX_QUALITY the highest anti-aliasing the game may set with
///                      stage.quality (default: RUFFLE_QUALITY; Windows
///                      player only)
///   RUFFLE_GRAPHICS    vulkan, gl...    (the player's default; gl when
///                      RUFFLE_FILTERS is on, see InitialGraphics). If the GPU
///                      fails while the player draws, it is started again one
///                      step down: Vulkan (or the default) -> gl -> gl in
///                      software (llvmpipe), for as long as this Skua runs
///   RUFFLE_ARGS        more player switches, space separated
///   RUFFLE_MAX_FPS     a starting cap on pictures drawn a second (1-60)
///   RUFFLE_PRESENT     auto (default), immediate, mailbox or fifo: how the
///                      player presents frames (passed on as it is)
///   RUFFLE_FILTERS     on to draw filters (glows, blurs), bitmap caches and
///                      blend modes; off by default, as Ruffle's browser WebGL
///                      renderer under Electron draws (see Start). On by
///                      default on Windows: a desktop PC's GPU draws them, and
///                      the game there is watched, not a headless bot
///   RUFFLE_LOG         its RUST_LOG     (warn,ruffle_core::avm2=off: the
///                      player otherwise logs every ActionScript error the
///                      game throws, with its stack, up to ~1900 lines/s;
///                      plus the line naming the graphics adapter it draws
///                      with, to tell a GPU from software rendering)
/// </summary>
public sealed class NativeGame
{
    /// <summary>RUFFLE_FILTERS as the player reads it: only on, 1 or true turn
    /// the effects on (render/wgpu/src/backend.rs, offscreen_effects).</summary>
    private static bool FiltersOn()
    {
        string v = FiltersSetting.Trim();
        return v.Equals("on", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RUFFLE_FILTERS, else on in the desktop client and off in Docker.</summary>
    private static string FiltersSetting => SkuaRuntime.Env("RUFFLE_FILTERS", SkuaRuntime.DesktopClient ? "on" : "off");

    // In the desktop client always: there is no Electron page there.
    public static bool Enabled => SkuaRuntime.DesktopClient
        || SkuaRuntime.EnvRaw("SKUA_GAME") is { } g && g.Equals("native", StringComparison.OrdinalIgnoreCase);

    private readonly string _bridgeUrl;
    private readonly object _lock = new();
    private Process? _process;
    private bool _stopped;

    // The graphics the player starts with (null: its default, Vulkan with a
    // GPU; software: gl drawn by Mesa on the CPU). Keep moves it down a step
    // when the GPU fails (FallBack), rather than starting the player again on
    // the same GPU path to fail the same way.
    private string? _graphics;
    private bool _software;
    private bool _fellBack;
    // Filters dropped after a GPU fault with them on (FallBack's first step).
    private bool _filtersOff;
    private volatile bool _gpuFault;
    private Task _forwarding = Task.CompletedTask;

    // What the player logs when its GPU fails: a lost device (the kernel reset
    // a hung GPU; "Acquiring a texture failed" is how that shows when the next
    // frame starts), a wgpu error, or no usable graphics at all. "GUI
    // controller should be created": the graphics did not start, as when Mesa's
    // software Vulkan (a VM without 3D) offers less than the player asks for
    // ("Limit 'max_color_attachments' value 4 is better than allowed 2"),
    // where OpenGL still works.
    private static readonly System.Text.RegularExpressions.Regex GpuFault = new(
        @"Acquiring a texture failed|[Dd]evice ?[Ll]ost|wgpu error|Invalid surface|No compatible graphics backends|GUI controller should be created",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    // Of those, the ones where the graphics never started: filters are not
    // drawn yet then, so dropping them cannot help and Keep goes straight to
    // the next graphics. "Invalid surface": Vulkan cannot draw into the window,
    // as on a VM's virtual display with the GPU passed through for compute only.
    private static readonly System.Text.RegularExpressions.Regex StartFault = new(
        @"Invalid surface|No compatible graphics backends|GUI controller should be created",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private volatile bool _startFault;

    public NativeGame(string bridgePrefix)
    {
        // http://+:8790/ -> ws://127.0.0.1:8790/
        var uri = new UriBuilder(bridgePrefix.Replace("://+:", "://127.0.0.1:").Replace("://*:", "://127.0.0.1:"))
        {
            Scheme = "ws",
        };
        _bridgeUrl = uri.Uri.ToString();
    }

    public static NativeGame? Current { get; private set; }

    /// <summary>The player's process id, while it runs.</summary>
    public int? Pid
    {
        get
        {
            lock (_lock)
                return _process is { HasExited: false } p ? p.Id : null;
        }
    }

    public void Start()
    {
        Current = this;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        _ = Task.Run(Keep);
    }

    /// <summary>Ends the player; Keep starts a new one (a reload of the game).</summary>
    public void Restart()
    {
        lock (_lock)
        {
            if (_process is { HasExited: false } p)
                try { p.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            if (_process is { HasExited: false } p)
                try { p.Kill(entireProcessTree: true); } catch { }
        }
    }

    private async Task Keep()
    {
        string bin = SkuaRuntime.Env("SKUA_RUFFLE_BIN", SkuaRuntime.DesktopClient
            ? Path.Combine(AppContext.BaseDirectory, "ruffle", OperatingSystem.IsWindows() ? "ruffle_desktop.exe" : "ruffle_desktop")
            : "/opt/ruffle/ruffle_desktop");
        string swf = SkuaRuntime.Env("SKUA_SWF", Path.Combine(AppContext.BaseDirectory, "skua.swf"));
        var delay = TimeSpan.FromSeconds(2);
        _graphics = InitialGraphics();
        while (true)
        {
            lock (_lock)
                if (_stopped)
                    return;
            if (!File.Exists(bin) || !File.Exists(swf))
            {
                Console.Error.WriteLine($"[game] {(!File.Exists(bin) ? bin : swf)} does not exist; the game cannot start");
                await Task.Delay(TimeSpan.FromSeconds(30));
                continue;
            }
            var started = DateTime.UtcNow;
            _gpuFault = _startFault = false;
            try
            {
                using var process = Launch(bin, swf);
                await process.WaitForExitAsync();
                // Its last lines (the panic) may still be on their way.
                await Task.WhenAny(_forwarding, Task.Delay(TimeSpan.FromSeconds(3)));
                Console.Error.WriteLine($"[game] the player exited ({process.ExitCode})");
                if (_gpuFault && process.ExitCode != 0)
                {
                    // Filters first: their full-size textures are what ran an
                    // integrated GPU out of memory (wgpu "Out of Memory", 4 tabs
                    // drawing on a 512 MB AMD iGPU), cheaper to give up than the GPU.
                    if (FiltersOn() && !_filtersOff && !_startFault)
                    {
                        _filtersOff = true;
                        Console.Error.WriteLine("[game] the GPU failed with filters on; the player now draws without them (RUFFLE_FILTERS off for this tab)");
                    }
                    else
                    {
                        FallBack();
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[game] could not start the player: {e.Message}");
            }
            lock (_lock)
            {
                _process = null;
                if (_stopped)
                    return;
            }
            // Back off only if it keeps dying young.
            delay = DateTime.UtcNow - started > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            Console.Error.WriteLine($"[game] starting it again in {delay.TotalSeconds:0}s");
            await Task.Delay(delay);
        }
    }

    /// <summary>RUFFLE_GRAPHICS, else gl with filters on, else the player's
    /// default (also when RUFFLE_ARGS names its own --graphics).</summary>
    private static string? InitialGraphics()
    {
        if (SkuaRuntime.EnvRaw("RUFFLE_GRAPHICS") is { } graphics)
            return graphics;
        // With filters on, a drawn AQW frame is hundreds of full-size passes,
        // sent to Vulkan as one submission: on an Intel iGPU (HD P530) the
        // kernel reset the GPU as hung within seconds and the player panicked
        // ("Acquiring a texture failed"), every build back to the first. Mesa's
        // OpenGL driver splits the work into smaller batches and drew it
        // without a fault, so filters on start on gl.
        // Not in the desktop client: that was the server's Intel iGPU under
        // Mesa; on a PC the player's own choice (Vulkan, or DirectX 12 on
        // Windows) suits best, and Keep still falls back if the GPU fails.
        return FiltersOn() && !SkuaRuntime.DesktopClient ? "gl" : null;
    }

    /// <summary>The player died of a GPU fault: one step down, as Selkies
    /// falls back from a hardware encoder to a software one.</summary>
    private void FallBack()
    {
        string from = _software ? "gl in software" : _graphics ?? "the default graphics";
        if (_software)
        {
            Console.Error.WriteLine($"[game] the GPU failed on {from}, the last fallback; trying it again");
            return;
        }
        if (_graphics is not null && _graphics.Equals("gl", StringComparison.OrdinalIgnoreCase))
            _software = true;
        else
            _graphics = "gl";
        _fellBack = true;
        Console.Error.WriteLine($"[game] the GPU failed on {from}; the player now draws with {(_software ? "gl in software (llvmpipe, slower)" : "gl")}");
    }

    private Process Launch(string bin, string swf)
    {
        var info = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // No menu bar: the window sits inside Skua's, under Skua's own menu.
        info.ArgumentList.Add("--no-gui");
        info.ArgumentList.Add("--tcp-connections");
        info.ArgumentList.Add("allow");
        string quality = SkuaRuntime.Env("RUFFLE_QUALITY", "low");
        info.ArgumentList.Add("--quality");
        info.ArgumentList.Add(quality);
        string[] extraArgs = (SkuaRuntime.EnvRaw("RUFFLE_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A --graphics in RUFFLE_ARGS wins, until a fallback replaces it.
        string? graphics = _graphics;
        if (extraArgs.Contains("--graphics"))
        {
            if (_fellBack)
                extraArgs = DropGraphics(extraArgs);
            else
                graphics = null;
        }
        if (graphics is not null)
        {
            info.ArgumentList.Add("--graphics");
            info.ArgumentList.Add(graphics);
        }
        foreach (string arg in extraArgs)
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add(swf);

        info.Environment["SKUA_BRIDGE_URL"] = _bridgeUrl;
        // AQW sets stage.quality HIGH (4x anti-aliasing) whatever the player
        // started with; with filters on that made each blended object's pass
        // 4x and resolved the whole stage at every break (Battleon: 108-118 ms
        // of an integrated GPU a frame, ~15 ms capped at low).
        info.Environment["RUFFLE_MAX_QUALITY"] = SkuaRuntime.Env("RUFFLE_MAX_QUALITY", quality);
        // So the player ends with this Skua on Windows too, where it cannot
        // tell from its parent (the bridge's parent_watch).
        info.Environment["SKUA_PARENT_PID"] = Environment.ProcessId.ToString();
        info.Environment["RUST_LOG"] = SkuaRuntime.Env("RUFFLE_LOG", "warn,ruffle_core::avm2=off,ruffle_desktop::gui::controller=info");
        info.Environment["NO_COLOR"] = "1";
        if (_software)
            info.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        // Drawn as the browser build's WebGL renderer draws: the player's wgpu
        // renderer draws each filtered, cached or blended object into its own
        // full-size texture, and AQW has many. A drawing tab then took 4-36 s a
        // frame (on the server's GPU too) and Skua's calls waited behind them.
        info.Environment["RUFFLE_FILTERS"] = _filtersOff ? "off" : FiltersSetting;
        // The player has no use for the accounts; Skua logs in (NativeSession).
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("AQW_PASS", StringComparison.Ordinal)).ToList())
            info.Environment.Remove(key);
        // Desktop client: never the player's input log (every key, typed
        // passwords included, into the log file people send), even when set by hand.
        if (SkuaRuntime.DesktopClient)
            info.Environment.Remove("RUFFLE_INPUT_DEBUG");
        // Linux: the game window is embedded as an X11 window (GameEmbed), so
        // the player must open one through XWayland even on a Wayland desktop,
        // where winit would otherwise make a Wayland window nothing can embed.
        if (OperatingSystem.IsLinux())
            info.Environment.Remove("WAYLAND_DISPLAY");

        var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
        lock (_lock)
            _process = process;
        Console.Error.WriteLine($"[game] player started (pid {process.Id}): {swf}, bridge {_bridgeUrl}");
        _forwarding = Task.WhenAll(
            Task.Run(() => Forward(process.StandardOutput)),
            Task.Run(() => Forward(process.StandardError)));
        return process;
    }

    private static string[] DropGraphics(string[] args)
    {
        var kept = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--graphics")
            {
                i++;
                continue;
            }
            kept.Add(args[i]);
        }
        return kept.ToArray();
    }

    // The player's log into Skua's (and so the container's), kept small: repeats
    // collapsed whatever their timestamps, and at most MaxLinesPerSecond lines a
    // second. A player once logged ~100,000 audio errors a second, each with its
    // own timestamp, and filled the server's disk with the container log.
    private const int MaxLinesPerSecond = 20;
    private static readonly System.Text.RegularExpressions.Regex Noise = new(
        @"\x1b\[[0-9;]*m|^\S*\d{4}-\d\d-\d\dT[0-9:.]+Z\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task Forward(StreamReader reader)
    {
        string? last = null;
        int repeats = 0, inSecond = 0, dropped = 0;
        long second = 0;
        while (await reader.ReadLineAsync() is { } raw)
        {
            string line = Noise.Replace(raw, "");
            if (GpuFault.IsMatch(line))
                _gpuFault = true;
            if (StartFault.IsMatch(line))
                _startFault = true;
            if (line == last)
            {
                repeats++;
                continue;
            }
            long now = Environment.TickCount64 / 1000;
            if (now != second)
            {
                if (dropped > 0)
                    Console.Error.WriteLine($"[game] ({dropped} more lines in the last second not shown)");
                second = now;
                inSecond = dropped = 0;
            }
            if (repeats > 0)
                Console.Error.WriteLine($"[game] (previous line repeated {repeats}x)");
            repeats = 0;
            last = line;
            if (++inSecond > MaxLinesPerSecond)
            {
                dropped++;
                continue;
            }
            Console.Error.WriteLine("[game] " + (line.Length > 500 ? line[..500] : line));
        }
    }
}
