using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// The logged-in character's sheet for a remote page (the web manager's
/// Character section on a tab card), read only when asked: not part of the
/// status the page polls.
///
///   GET /character    level, gold, AC, the class and its rank, the stats
///                     (the totals with gear, as the game's Stats panel)
///                     and every faction's rank and reputation
///
/// Chances are fractions as the game keeps them (0.05 is 5%), the critical
/// multiplier too (1.5 is 150%).
/// </summary>
public sealed partial class HostApi
{
    private object Character()
    {
        if (InventoryUnavailable() is not null)
            return new { error = "log in first: the character is the logged-in account's" };
        var bot = services.GetRequiredService<IScriptInterface>();
        var player = bot.Player;
        var s = player.Stats;
        return new
        {
            level = player.Level,
            gold = player.Gold,
            coins = player.Coins,
            className = player.CurrentClass?.Name,
            classRank = player.CurrentClassRank,
            stats = s is null ? null : new
            {
                strength = s.Strength,
                intellect = s.Intellect,
                dexterity = s.Dexterity,
                endurance = s.Endurance,
                wisdom = s.Wisdom,
                luck = s.Luck,
                attackPower = s.AttackPower,
                spellPower = s.SpellPower,
                critChance = s.CriticalChance,
                critMultiplier = s.CriticalMultiplier,
                haste = s.Haste,
                evasion = s.EvasionChance,
            },
            factions = bot.Reputation.FactionList
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new { id = f.ID, name = f.Name, rank = f.Rank, totalRep = f.TotalRep, rep = f.Rep, repToRank = f.RequiredRep })
                .ToList(),
        };
    }
}
