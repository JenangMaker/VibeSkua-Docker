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
/// </list>
/// A restarted player (crash, Reload game) loads again and is logged in again.
/// Recycling (RECYCLE_AFTER_*) is not done yet in this mode.
/// </summary>
public sealed class NativeSession(IServiceProvider services, RuffleBridge bridge, ScriptKeeper keeper)
{
    private static readonly TimeSpan ReloginGrace = TimeSpan.FromSeconds(60);
    private int _busy;
    private bool _playing;
    private DateTime? _idleSince;
    private bool _clientLoaded;

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
                _ = Task.Run(() => Login(_playing ? "relogin" : null));
            }
        };
        bridge.ConnectionChanged += up =>
        {
            if (!up)
                _clientLoaded = false;
        };
        _ = Task.Run(Watch);
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
            if (!bridge.IsConnected || !_clientLoaded || Volatile.Read(ref _busy) == 1)
                continue;
            try
            {
                if (InGame())
                {
                    _playing = true;
                    _idleSince = null;
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
            Volatile.Write(ref _busy, 0);
        }
    }
}
