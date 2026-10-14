using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// The logged-in character's sheet for a remote page (the web manager's
/// Character and Reputation sections on a tab card), read only when asked:
/// not part of the status the page polls.
///
///   GET /character    level, gold, AC, the class and its rank, max HP, the
///                     stats, the boosts and every faction's rank and
///                     reputation
///
/// stats: the game's own stat table (world.myAvatar.dataLeaf.sta, the totals
/// with gear and auras, what its Stats panel shows), each key without its
/// "$": STR..LCK; ap, sp; thi hit, tha haste (cooldown reduction), tcr
/// critical chance, scm critical multiplier, tdo evasion (fractions, 0.05 is
/// 5%); and the c.. multipliers, i incoming, o outgoing (1 is none): cai/cao
/// all damage, cpi/cpo physical, cmi/cmo magical, chi/cho healing, cdi/cdo
/// damage over time, cmc mana consumption.
/// boosts: the minutes left of each boost (XP, gold, reputation, class
/// points), 0 when off.
/// gear: the boosts of what is worn, from each equipped item's sMeta
/// ("dmgAll:1.25,gold:1.1,Undead:1.3"): the boost (dmgAll, gold, rep, xp, cp,
/// or a monster race for damage to it), its multiplier and the item.
/// </summary>
public sealed partial class HostApi
{
    private object Character()
    {
        if (InventoryUnavailable() is not null)
            return new { error = "log in first: the character is the logged-in account's" };
        var bot = services.GetRequiredService<IScriptInterface>();
        var flash = services.GetRequiredService<IFlashUtil>();
        var player = bot.Player;
        // The numbers only: one odd entry must not lose the whole table.
        var sta = flash.GetGameObject<Dictionary<string, Newtonsoft.Json.Linq.JToken>>("world.myAvatar.dataLeaf.sta");
        int Boost(string key) => flash.GetGameObject($"world.myAvatar.objData.{key}", 0);
        return new
        {
            level = player.Level,
            gold = player.Gold,
            coins = player.Coins,
            className = player.CurrentClass?.Name,
            classRank = player.CurrentClassRank,
            maxHp = player.MaxHealth,
            stats = sta?
                .Where(kv => kv.Value.Type is Newtonsoft.Json.Linq.JTokenType.Integer or Newtonsoft.Json.Linq.JTokenType.Float)
                .ToDictionary(kv => kv.Key.TrimStart('$'), kv => kv.Value.ToObject<double>()),
            boosts = new { xp = Boost("iBoostXP"), gold = Boost("iBoostG"), rep = Boost("iBoostRep"), classPoints = Boost("iBoostCP") },
            gear = GearBoosts(bot),
            factions = bot.Reputation.FactionList
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new { id = f.ID, name = f.Name, rank = f.Rank, totalRep = f.TotalRep, rep = f.Rep, repToRank = f.RequiredRep })
                .ToList(),
        };
    }

    private static List<object> GearBoosts(IScriptInterface bot)
    {
        var list = new List<object>();
        foreach (var item in bot.Inventory.Items.Where(i => i.Equipped && !string.IsNullOrWhiteSpace(i.Meta)))
            foreach (string part in item.Meta.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = part.LastIndexOf(':');
                if (colon > 0 && double.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
                    list.Add(new { boost = part[..colon].Trim(), value, item = item.Name });
            }
        return list;
    }
}
