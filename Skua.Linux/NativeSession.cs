using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// Logging in for the native game (SKUA_GAME=native), as web/public/session.js
/// does in the Electron page, which this mode has none of:
/// <list type="bullet">
/// <item><b>Auto-login.</b> Once the game client has loaded, log this tab's
/// account in (AccountStore.CredentialsFor): game.login, wait for the server
/// list, click the account's server or the first that lets us in.</item>
/// <item><b>Relogin.</b> After a disconnect Skua's own AutoRelogin gets the
/// first try (two logins at once trip each other up); if the login screen sits
/// untouched for a minute, log in here, and tell ScriptKeeper so it restarts a
/// script that was running.</item>
/// <item><b>Recycling.</b> Ruffle keeps much of what each map loads, so a
/// long session's player grows (~0.4 GB fresh, 0.7-0.95 GB after 7 hours).
/// RECYCLE_AFTER_MINUTES, RECYCLE_AFTER_MAP_CHANGES and RECYCLE_ABOVE_MB
/// restart the player out of combat, log back in, return to the same room and
/// cell, and start again the script that was running. One tab at a time
/// (a lock file), so the tabs don't all log in at once.</item>
/// </list>
/// A restarted player (crash, Reload game) loads again and is logged in again.
/// </summary>
public sealed class NativeSession(IServiceProvider services, RuffleBridge bridge, ScriptKeeper keeper)
{
    private static readonly TimeSpan ReloginGrace = TimeSpan.FromSeconds(60);
    private int _busy;
    private bool _playing;
    private DateTime? _idleSince;
    private bool _clientLoaded;

    private static int EnvInt(string name) => int.TryParse(SkuaRuntime.EnvRaw(name), out int n) && n > 0 ? n : 0;
    private readonly int _afterMinutes = EnvInt("RECYCLE_AFTER_MINUTES");
    private readonly int _afterMapChanges = EnvInt("RECYCLE_AFTER_MAP_CHANGES");
    private readonly int _aboveMb = EnvInt("RECYCLE_ABOVE_MB");
    private readonly int _minMinutes = int.TryParse(SkuaRuntime.EnvRaw("RECYCLE_MIN_MINUTES"), out int m) && m >= 0 ? m : 30;
    private const string RecycleLock = "/tmp/vibeskua-recycle.lock";

    // Where to go back to after a recycle, and whether a script ran there.
    private sealed record Place(string Area, string Map, string Cell, string Pad, bool Script);
    private volatile Place? _return;
    private volatile bool _recycling;
    private DateTime _gameStarted = DateTime.UtcNow;
    private int _mapChanges;
    private string? _lastPlace;

    private IFlashUtil Flash => services.GetRequiredService<IFlashUtil>();

    public void Start()
    {
        int tab = SkuaRuntime.Instance + 1;
        if (AccountStore.CredentialsFor(tab) is null)
        {
            Console.WriteLine($"[session] no account for tab {tab}: log in by hand");
            return;
        }
        // skua.swf says "loaded" once the game client is up (and again after a
        // restarted player loads it).
        bridge.FlashCall += (name, args) =>
        {
            if (name == "loaded")
            {
                _clientLoaded = true;
                _gameStarted = DateTime.UtcNow;
                _mapChanges = 0;
                _lastPlace = null;
                _ = Task.Run(() => Login(_playing ? "relogin" : null));
            }
        };
        bridge.ConnectionChanged += up =>
        {
            if (!up)
                _clientLoaded = false;
        };
        _ = Task.Run(Watch);
        var rules = new List<string>();
        if (_afterMinutes > 0) rules.Add($"after {_afterMinutes} min");
        if (_afterMapChanges > 0) rules.Add($"after {_afterMapChanges} map changes");
        if (_aboveMb > 0) rules.Add($"above {_aboveMb} MB (after {_minMinutes} min)");
        if (rules.Count > 0)
            Console.WriteLine($"[session] recycle the game {string.Join(", or ", rules)}");
    }

    private bool Has(string path)
    {
        try { return !Flash.IsNull(path); }
        catch { return false; }
    }

    private bool InGame()
    {
        try { return Flash.Call("isLoggedIn") == "true" && Flash.GetGameObject("world.strMapName") is { Length: > 2 }; }
        catch { return false; }
    }

    private async Task Watch()
    {
        while (true)
        {
            await Task.Delay(3000);
            if (!bridge.IsConnected || !_clientLoaded || Volatile.Read(ref _busy) == 1 || _recycling)
                continue;
            try
            {
                if (InGame())
                {
                    _playing = true;
                    _idleSince = null;
                    CountMapChange();
                    if (RecycleDue() is { } why)
                        await Recycle(why);
                    continue;
                }
                if (!_playing)
                    continue;
                // Disconnected or kicked: give Skua's AutoRelogin a minute.
                bool idle = Has("mcLogin.ni") && !Has("mcLogin.sl.iList");
                if (!idle)
                {
                    _idleSince = null;
                    continue;
                }
                _idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _idleSince < ReloginGrace)
                    continue;
                Console.WriteLine("[session] Skua did not log back in; logging in");
                _idleSince = null;
                await Login("relogin");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[session] {e.Message}");
            }
        }
    }

    /// <summary>session.js's login(): the same steps as Skua's login.</summary>
    private async Task Login(string? reason)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            if (AccountStore.CredentialsFor(SkuaRuntime.Instance + 1) is not { } creds)
                return;
            for (int i = 0; i < 90 && !Has("mcLogin.ni"); i++)
                await Task.Delay(1000);
            if (!Has("mcLogin.ni"))
            {
                Console.Error.WriteLine("[session] auto-login: the login screen never appeared");
                return;
            }
            Flash.CallGameFunction("login", creds.User, creds.Pass);

            int listed = 0;
            for (int i = 0; i < 40 && listed == 0; i++)
            {
                await Task.Delay(1000);
                if (Has("mcLogin.sl.iList"))
                    listed = Flash.GetGameObject<int>("mcLogin.sl.iList.numChildren", 0);
            }
            if (listed == 0)
            {
                Console.Error.WriteLine("[session] auto-login: no server list - wrong credentials?");
                return;
            }
            await Task.Delay(1000);

            string? server = null;
            if (creds.Server is { } wanted && Flash.Call("clickServer", wanted) == "true")
                server = wanted;
            if (server is null)
            {
                var servers = await services.GetRequiredService<IScriptServers>().GetServers(true);
                foreach (var s in servers.Where(s => s.Online && !string.IsNullOrEmpty(s.Name)))
                {
                    if (Flash.Call("clickServer", s.Name) == "true")
                    {
                        server = s.Name;
                        break;
                    }
                }
            }
            if (server is null)
            {
                Console.Error.WriteLine("[session] auto-login: could not pick a server");
                return;
            }

            for (int i = 0; i < 60 && !InGame(); i++)
                await Task.Delay(1000);
            if (!InGame())
            {
                Console.Error.WriteLine($"[session] auto-login: {server} did not let us in");
                return;
            }
            Console.WriteLine($"[session] auto-login: in on {server}");
            _playing = true;
            if (_return is { } back)
            {
                _return = null;
                await ReturnTo(back);
                return;
            }
            // A script left mid-loop in the game that went away restarts from
            // its saved progress. Not after the first login: auto-start covers that.
            if (reason is not null)
                keeper.Relogged(reason);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[session] auto-login failed: {e.Message}");
        }
        finally
        {
            // A failed login after a recycle: the relogin watch takes over
            // (and returns, since _return is still set).
            _recycling = false;
            Volatile.Write(ref _busy, 0);
        }
    }

    private void CountMapChange()
    {
        string place = $"{Flash.GetGameObject("world.strMapName")}#{Flash.GetGameObject("world.curRoom")}";
        if (_lastPlace is not null && place != _lastPlace)
            _mapChanges++;
        _lastPlace = place;
    }

    // Why the game is due a recycle, or null.
    private string? RecycleDue()
    {
        if (_afterMinutes > 0 && DateTime.UtcNow - _gameStarted >= TimeSpan.FromMinutes(_afterMinutes))
            return $"{_afterMinutes} min";
        if (_afterMapChanges > 0 && _mapChanges >= _afterMapChanges)
            return $"{_mapChanges} map changes";
        // Memory only once the game has run a while: a script whose own
        // working set is above the line (one went from a fresh player to over
        // 850 MB in 4 minutes) restarted every few minutes.
        if (_aboveMb > 0 && DateTime.UtcNow - _gameStarted >= TimeSpan.FromMinutes(_minMinutes)
            && GameMemoryMb() is { } mb && mb >= _aboveMb)
        {
            if (DateTime.UtcNow - _gameStarted < TimeSpan.FromMinutes(_minMinutes + 5))
                Console.WriteLine($"[session] recycle: this tab's game is at {mb} MB already {_minMinutes} min after it started; "
                    + "its script may need that much (raise RECYCLE_ABOVE_MB or RECYCLE_MIN_MINUTES)");
            return $"{mb} MB";
        }
        return null;
    }

    private static long? GameMemoryMb()
    {
        if (NativeGame.Current?.Pid is not { } pid)
            return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.WorkingSet64 / 1048576;
        }
        catch
        {
            return null;
        }
    }

    private bool InCombat()
    {
        try { return Flash.GetGameObject<int>("world.myAvatar.dataLeaf.intState", 0) == 2; }
        catch { return false; }
    }

    // Waits up to the given time to be out of combat; true if it is.
    private async Task<bool> OutOfCombat(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (InCombat())
        {
            if (DateTime.UtcNow >= until)
                return false;
            await Task.Delay(500);
        }
        return true;
    }

    /// <summary>
    /// Restarts the player out of combat; Login brings it back (ReturnTo).
    /// Combat counts as session.js's does (intState 2), and a fight is waited
    /// out for up to 10 minutes. One tab at a time: the others wait for the
    /// lock file.
    /// </summary>
    private async Task Recycle(string why)
    {
        if (NativeGame.Current is not { } game)
            return;
        Console.WriteLine($"[session] recycle ({why}): waiting to be out of combat");
        if (!await OutOfCombat(TimeSpan.FromMinutes(10)))
            Console.WriteLine("[session] recycle: still in combat after 10 min; recycling anyway");

        FileStream? turn = null;
        for (int i = 0; turn is null; i++)
        {
            try { turn = new FileStream(RecycleLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (i == 0)
                    Console.WriteLine("[session] recycle: another tab is recycling; waiting for it");
                if (i >= 120)   // 10 min: try again on the next check
                    return;
                await Task.Delay(5000);
            }
        }
        using (turn)
        {
            // Waiting for the lock may have taken a while.
            await OutOfCombat(TimeSpan.FromMinutes(1));
            var bot = services.GetRequiredService<IScriptInterface>();
            var manager = services.GetRequiredService<IScriptManager>();
            var place = new Place(bot.Map.FullName, bot.Map.Name, bot.Player.Cell ?? "Enter", bot.Player.Pad ?? "Spawn", manager.ScriptRunning);
            if (place.Script)
            {
                // Stopped first: a running script would log back in by itself
                // (its AutoRelogin) while this does.
                await manager.StopScript();
                for (int i = 0; i < 60 && manager.ScriptRunning; i++)
                    await Task.Delay(500);
            }
            _return = place;
            _recycling = true;
            Console.WriteLine($"[session] recycle: restarting the game (back to {place.Area} {place.Cell}{(place.Script ? ", then the script" : "")})");
            game.Restart();
            // Hold the turn until this tab is back in, or its login gave up
            // (then the relogin watch takes over, and still returns).
            for (int i = 0; i < 360 && _recycling; i++)
                await Task.Delay(1000);
            _recycling = false;
        }
    }

    // After the login that follows a recycle: the same room (or map) and cell,
    // then the script that was running.
    private async Task ReturnTo(Place back)
    {
        await Task.Delay(3000);
        var bot = services.GetRequiredService<IScriptInterface>();
        string user = Flash.GetGameObject("world.myAvatar.objData.strUsername")?.Trim('"') ?? bot.Player.Username;
        foreach (string target in back.Area.Length > 0 && back.Area != back.Map ? new[] { back.Area, back.Map } : new[] { back.Map })
        {
            if (bot.Map.FullName == back.Area || (target == back.Map && bot.Map.Name == back.Map))
                break;
            string room = Flash.GetGameObject("world.curRoom") ?? "1";
            Flash.CallGameFunction("sfc.sendString", $"%xt%zm%cmd%{room}%tfer%{user}%{target}%{back.Cell}%{back.Pad}%");
            for (int i = 0; i < 20 && bot.Map.Name != back.Map; i++)
                await Task.Delay(1000);
        }
        if (bot.Map.Name == back.Map && bot.Player.Cell != back.Cell)
            Flash.CallGameFunction("world.moveToCell", back.Cell, back.Pad);
        _mapChanges = 0;
        _lastPlace = null;
        Console.WriteLine($"[session] recycle: back in {(bot.Map.Name == back.Map ? back.Area : bot.Map.Name)} {back.Cell}");
        if (!back.Script)
            return;
        await Task.Delay(1500);
        var manager = services.GetRequiredService<IScriptManager>();
        if (manager.ScriptRunning)
            return;
        if (await manager.StartScript() is { } error)
            Console.Error.WriteLine($"[session] recycle: could not start the script again: {error.Message}");
        else
            Console.WriteLine($"[session] recycle: script {Path.GetFileNameWithoutExtension(manager.LoadedScript ?? "")} started again");
    }
}
