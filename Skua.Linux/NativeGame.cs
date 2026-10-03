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
///   RUFFLE_QUALITY     low, medium, high... (low)
///   RUFFLE_GRAPHICS    vulkan, gl...    (the player's default)
///   RUFFLE_ARGS        more player switches, space separated
///   RUFFLE_LOG         its RUST_LOG     (warn,ruffle_core::avm2=off: the
///                      player otherwise logs every ActionScript error the
///                      game throws, with its stack, up to ~1900 lines/s)
/// </summary>
public sealed class NativeGame
{
    public static bool Enabled => SkuaRuntime.EnvRaw("SKUA_GAME") is { } g && g.Equals("native", StringComparison.OrdinalIgnoreCase);

    private readonly string _bridgeUrl;
    private readonly object _lock = new();
    private Process? _process;
    private bool _stopped;

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
        string bin = SkuaRuntime.Env("SKUA_RUFFLE_BIN", "/opt/ruffle/ruffle_desktop");
        string swf = SkuaRuntime.Env("SKUA_SWF", Path.Combine(AppContext.BaseDirectory, "skua.swf"));
        var delay = TimeSpan.FromSeconds(2);
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
            try
            {
                using var process = Launch(bin, swf);
                await process.WaitForExitAsync();
                Console.Error.WriteLine($"[game] the player exited ({process.ExitCode})");
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
        info.ArgumentList.Add("--quality");
        info.ArgumentList.Add(SkuaRuntime.Env("RUFFLE_QUALITY", "low"));
        if (SkuaRuntime.EnvRaw("RUFFLE_GRAPHICS") is { } graphics)
        {
            info.ArgumentList.Add("--graphics");
            info.ArgumentList.Add(graphics);
        }
        foreach (string arg in (SkuaRuntime.EnvRaw("RUFFLE_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add(swf);

        info.Environment["SKUA_BRIDGE_URL"] = _bridgeUrl;
        info.Environment["RUST_LOG"] = SkuaRuntime.Env("RUFFLE_LOG", "warn,ruffle_core::avm2=off");
        // The player has no use for the accounts; Skua logs in (NativeSession).
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("AQW_PASS", StringComparison.Ordinal)).ToList())
            info.Environment.Remove(key);

        var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
        lock (_lock)
            _process = process;
        Console.Error.WriteLine($"[game] player started (pid {process.Id}): {swf}, bridge {_bridgeUrl}");
        _ = Task.Run(() => Forward(process.StandardOutput));
        _ = Task.Run(() => Forward(process.StandardError));
        return process;
    }

    // The player's log into Skua's, with repeats collapsed: the game can throw
    // the same error every frame.
    private static async Task Forward(StreamReader reader)
    {
        string? last = null;
        int repeats = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line == last)
            {
                repeats++;
                continue;
            }
            if (repeats > 0)
                Console.Error.WriteLine($"[game] (previous line repeated {repeats}x)");
            repeats = 0;
            last = line;
            Console.Error.WriteLine("[game] " + (line.Length > 500 ? line[..500] : line));
        }
    }
}
