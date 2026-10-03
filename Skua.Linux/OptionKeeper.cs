using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// Puts Skua's game-side options back after a login. Hide Players, Disable FX
/// and Disable Collisions are modules inside the SWF, switched on once when
/// the option is ticked; Lag Killer hides the game's world object. A page
/// reload (session.js's recycle, a WebGL-loss reload, Reload game) starts a
/// fresh SWF with every module off, and a new login builds a new world, while
/// Skua still shows the options ticked: they did nothing until unticked and
/// ticked again. On Windows the Flash client is never reloaded, so Skua never
/// had to send them again.
/// </summary>
public sealed class OptionKeeper(IServiceProvider services, RuffleBridge bridge)
{
    public void Start() => _ = Task.Run(Watch);

    private async Task Watch()
    {
        bool wasIn = false;
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                var player = services.GetRequiredService<IScriptInterface>().Player;
                bool isIn = bridge.IsConnected && player.LoggedIn;
                if (isIn && !wasIn)
                {
                    await Task.Delay(3000);   // let the world and its avatars load
                    Reapply();
                }
                wasIn = isIn;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[host] option keeper: {e.Message}");
                wasIn = false;
            }
        }
    }

    private void Reapply()
    {
        var options = services.GetRequiredService<IScriptOption>();
        var resent = new List<string>();
        // Each setter sends the value to the game even when it is unchanged.
        void Resend(string name, bool on, Action set)
        {
            if (!on)
                return;
            set();
            resent.Add(name);
        }
        Resend("Hide Players", options.HidePlayers, () => options.HidePlayers = true);
        Resend("Disable FX", options.DisableFX, () => options.DisableFX = true);
        Resend("Disable Collisions", options.DisableCollisions, () => options.DisableCollisions = true);
        Resend("Disable Death Ads", options.DisableDeathAds, () => options.DisableDeathAds = true);
        Resend("Lag Killer", options.LagKiller, () => options.LagKiller = true);
        if (resent.Count > 0)
            Console.WriteLine($"[host] logged in: options sent to the game again ({string.Join(", ", resent)})");
    }
}
