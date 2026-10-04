using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Ruffle;

namespace Skua.Linux;

/// <summary>
/// Diagnostics for aura handling, off unless SKUA_AURA_WATCH names auras
/// (comma separated, e.g. "Counter Attack"; "*" for every aura). For this tab it logs, as
/// <c>[aura] ...</c>:
/// <list type="bullet">
/// <item>each server packet that mentions a watched aura: its command, and per
/// aura entry the command (aura+, aura-, ...), target and names;</item>
/// <item>each change in the watched auras the game lists on the monsters in
/// the player's cell (skua.swf's GetMonsterAuraByID, what
/// <c>Bot.Target.HasActiveAura</c> reads), checked twice a second.</item>
/// </list>
/// Written to compare when the server ends an aura with when the game drops it:
/// on Ultra Ezrajal a tab stayed paused for Counter Attack long after it should
/// have ended.
/// </summary>
public static class AuraWatch
{

    public static void Start(IServiceProvider services, RuffleBridge bridge)
    {
        string[] names = (SkuaRuntime.EnvRaw("SKUA_AURA_WATCH") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return;
        Console.WriteLine($"[aura] watching: {string.Join(", ", names)}");
        bridge.FlashCall += (name, args) =>
        {
            // The SWF's call arguments arrive as one array (args[0]).
            object? first = args.Length > 0 && args[0] is object?[] inner && inner.Length > 0 ? inner[0] : args.FirstOrDefault();
            if (name == "pext" && first is string packet
                && names.Any(n => packet.Contains(n == "*" ? "\"aura" : n, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("[aura] packet " + Describe(packet, names));
        };
        _ = Task.Run(() => WatchMonsters(services, bridge, names));
    }

    // The packet's command and, for each aura entry naming a watched aura, its
    // command, target (tInf) and aura names; the raw start if it does not parse.
    private static string Describe(string packet, string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(packet);
            var data = doc.RootElement.GetProperty("params").GetProperty("dataObj");
            string cmd = data.TryGetProperty("cmd", out var c) ? c.ToString() : "?";
            var parts = new List<string>();
            if (data.TryGetProperty("a", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    string text = entry.GetRawText();
                    if (!names.Any(n => n == "*" || text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    string ecmd = entry.TryGetProperty("cmd", out var ec) ? ec.ToString() : "?";
                    string tinf = entry.TryGetProperty("tInf", out var ti) ? ti.ToString() : "?";
                    var auraNames = new List<string>();
                    if (entry.TryGetProperty("auras", out var auras) && auras.ValueKind == JsonValueKind.Array)
                        auraNames.AddRange(auras.EnumerateArray().Select(AuraName));
                    if (entry.TryGetProperty("aura", out var aura))
                        auraNames.Add(AuraName(aura));
                    parts.Add($"{ecmd} {tinf} [{string.Join(", ", auraNames)}]");
                }
            }
            return parts.Count > 0 ? $"{cmd}: {string.Join("; ", parts)}" : $"{cmd}: {Trim(packet)}";
        }
        catch
        {
            return Trim(packet);
        }
    }

    private static string AuraName(JsonElement aura) =>
        aura.ValueKind == JsonValueKind.Object && aura.TryGetProperty("nam", out var n) ? n.ToString() : aura.ToString();

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "..." : s;

    private static async Task WatchMonsters(IServiceProvider services, RuffleBridge bridge, string[] names)
    {
        var last = new Dictionary<int, string>();
        while (true)
        {
            await Task.Delay(500);
            if (!bridge.IsConnected)
                continue;
            try
            {
                var bot = (IScriptInterface)services.GetService(typeof(IScriptInterface))!;
                if (!bot.Player.LoggedIn)
                    continue;
                var seen = new HashSet<int>();
                foreach (var monster in bot.Monsters.CurrentMonsters.Take(8))
                {
                    seen.Add(monster.MapID);
                    string json = bot.Flash.Call("GetMonsterAuraByID", monster.MapID) ?? "[]";
                    string now = Watched(json, names);
                    string before = last.GetValueOrDefault(monster.MapID, "");
                    if (now != before)
                    {
                        Console.WriteLine($"[aura] {monster.Name} ({monster.MapID}, {monster.HP}/{monster.MaxHP} HP) now: {(now.Length > 0 ? now : "none")}");
                        last[monster.MapID] = now;
                    }
                }
                foreach (int gone in last.Keys.Where(k => !seen.Contains(k)).ToList())
                    last.Remove(gone);
            }
            catch
            {
                // The game between maps or reloading: try again next time.
            }
        }
    }

    // The watched auras in a GetMonsterAuraByID answer, as "name (key=value ...)".
    private static string Watched(string json, string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "";
            var found = new List<string>();
            foreach (var aura in doc.RootElement.EnumerateArray())
            {
                string name = AuraName(aura);
                if (!names.Any(n => n == "*" || n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var fields = aura.EnumerateObject()
                    .Where(p => p.Name is "dur" or "ts" or "e" or "t" or "fx" or "val" or "isNew")
                    .Select(p => $"{p.Name}={p.Value}");
                found.Add($"{name} ({string.Join(" ", fields)})");
            }
            return string.Join(", ", found);
        }
        catch
        {
            return "";
        }
    }
}
