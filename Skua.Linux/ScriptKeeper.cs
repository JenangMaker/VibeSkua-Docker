using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// Keeps a running script going through what the game page does on its own
/// and what Ruffle does differently from Flash:
/// <list type="bullet">
/// <item><b>Page logins.</b> After a scheduled reload, or when Skua did not log
/// back in after a disconnect (web/public/session.js), the page logs in and
/// sends <c>vibeskua.relogged</c>. The script was left mid-loop in a game that
/// went away (a hunt waiting for monsters of a map it is no longer in), so it
/// is restarted, as Skua's own relogin does; CoreBots scripts carry on from
/// their saved progress.</item>
/// <item><b>Untargetable monsters.</b> After some map loads and cell changes,
/// Ruffle leaves a cell's monsters without their sprites (<c>pMC</c>), and
/// Skua only targets monsters that have one: the bot stands still until it
/// goes AFK. A move request rebuilds them, so after 20 s of living monsters
/// in the cell and none targetable, this moves to another cell and back.</item>
/// </list>
/// </summary>
public sealed class ScriptKeeper(IServiceProvider services, RuffleBridge bridge)
{
    public const string ReloggedEvent = "vibeskua.relogged";

    private int _restarting;

    public void Start()
    {
        bridge.FlashCall += (name, args) =>
        {
            if (name == ReloggedEvent)
                _ = Task.Run(() => RestartAfterPageLogin(args.Length > 0 ? args[0]?.ToString() : null));
        };
        _ = Task.Run(WatchTargets);
    }

    /// <summary>
    /// The game logged in again without Skua's own relogin (NativeSession, the
    /// native counterpart of session.js's <c>vibeskua.relogged</c>).
    /// </summary>
    public void Relogged(string reason) => _ = Task.Run(() => RestartAfterPageLogin(reason));

    private async Task RestartAfterPageLogin(string? reason)
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1)
            return;
        try
        {
            await Task.Delay(3000);   // let the map settle
            var manager = services.GetRequiredService<IScriptManager>();
            string name = Path.GetFileNameWithoutExtension(manager.LoadedScript ?? "");
            if (!manager.ScriptRunning)
                return;
            if (services.GetService<ScriptSchedulerViewModel>() is { IsRunningQueue: true })
            {
                // The scheduler decides what runs next; leave it to it.
                Console.WriteLine($"[host] the page logged in again ({reason}); the scheduler is running, so {name} is left as it is");
                return;
            }
            Console.WriteLine($"[host] the page logged in again ({reason}); restarting {name}");
            await manager.StopScript();
            for (int i = 0; i < 60 && manager.ScriptRunning; i++)
                await Task.Delay(500);
            await Task.Delay(1500);
            if (await manager.StartScript() is { } error)
                Console.Error.WriteLine($"[host] could not restart {name}: {error.Message}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] restarting the script after a page login: {e.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    private async Task WatchTargets()
    {
        const int checkMs = 5000, stuckChecks = 4, backoffChecks = 6;
        int streak = 0;
        while (true)
        {
            await Task.Delay(checkMs);
            try
            {
                var manager = services.GetRequiredService<IScriptManager>();
                var bot = services.GetRequiredService<IScriptInterface>();
                if (!bridge.IsConnected || !manager.ScriptRunning || manager.ScriptPaused
                    || !bot.Player.LoggedIn || !bot.Player.Loaded)
                {
                    streak = 0;
                    continue;
                }
                int living = bot.Monsters.CurrentMonsters.Count(m => m.Alive);
                if (living == 0 || bot.Monsters.CurrentAvailableMonsters.Count > 0)
                {
                    streak = Math.Min(streak, 0) + (streak < 0 ? 1 : 0);   // count down a backoff, else reset
                    continue;
                }
                if (++streak < stuckChecks)
                    continue;

                string cell = bot.Player.Cell, pad = bot.Player.Pad;
                string? other = bot.Map.Cells?.FirstOrDefault(c => !string.Equals(c, cell, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"[host] {bot.Map.Name} {cell}: {living} living monster(s) but none targetable for {stuckChecks * checkMs / 1000} s; rebuilding the cell");
                if (other is not null)
                {
                    bot.Flash.CallGameFunction("world.moveToCell", other, "Left");
                    await Task.Delay(1500);
                }
                bot.Flash.CallGameFunction("world.moveToCell", cell, string.IsNullOrEmpty(pad) ? "Left" : pad);
                streak = -backoffChecks;   // give it 30 s before trying again
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[host] target watch: {e.Message}");
                streak = 0;
            }
        }
    }
}
