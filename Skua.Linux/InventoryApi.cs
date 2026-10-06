using System.Dynamic;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.Linux;

/// <summary>
/// The logged-in account's inventory for a remote page (the web manager's
/// Inventory tab), and equipping or unequipping its items.
///
///   GET  /inventory          slots, the player's level and membership, and
///                            each item: its slot, and whether it can be
///                            equipped or unequipped (or why not)
///   POST /inventory/equip    body {"id": 123, "equip": true|false}
///
/// The rules are the game's own (Game.toggleItemEquip and the inventory
/// panel, LPFLayoutInvShopEnh): what can be equipped is class, weapon, helm
/// and cape (enhanced), armor, pet, necklace, ground item, and usable items
/// (type Item with a link: potions and the like, in the item slot); the
/// class and the weapon cannot be unequipped; nothing changes in combat; a
/// member item needs membership and an item its level. Refused while a
/// script runs, which may depend on what is worn.
/// </summary>
public sealed partial class HostApi
{
    private object Inventory()
    {
        if (InventoryUnavailable() is { } error)
            return error;
        return InventoryState(services.GetRequiredService<IScriptInterface>());
    }

    private async Task<object> InventoryEquip(HttpListenerRequest request)
    {
        if (InventoryUnavailable() is { } error)
            return error;
        if (services.GetRequiredService<IScriptManager>().ScriptRunning)
            return new { error = "stop the script first: it may depend on what is worn" };

        EquipInput? input;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            try { input = JsonSerializer.Deserialize<EquipInput>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException e) { return new { error = $"bad JSON: {e.Message}" }; }
        }
        if (input is null || input.Id <= 0 || input.Equip is null)
            return new { error = "send {\"id\": <item id>, \"equip\": true or false}" };

        var bot = services.GetRequiredService<IScriptInterface>();
        var item = bot.Inventory.Items.FirstOrDefault(i => i.ID == input.Id);
        if (item is null)
            return new { error = "that item is not in the inventory" };
        if (bot.Player.InCombat)
            return new { error = "not in combat: the game does not change equipment then" };

        bool equip = input.Equip.Value;
        var (canEquip, canUnequip, why) = EquipRules(bot, item);
        if (equip && item.Equipped)
            return new { error = $"{item.Name} is already equipped" };
        if (!equip && !item.Equipped)
            return new { error = $"{item.Name} is not equipped" };
        if (equip && !canEquip || !equip && !canUnequip)
            return new { error = why ?? $"{item.Name} cannot be {(equip ? "equipped" : "unequipped")}" };

        bool usable = Usable(item);
        bool done = await Task.Run(() =>
        {
            if (equip)
            {
                if (usable)
                    bot.Inventory.EquipUsableItem(item);
                else
                    bot.Inventory.EquipItem(item.ID);
            }
            else if (usable)
            {
                // No argument: the game clears its item slot.
                bot.Flash.CallGameFunction("world.unequipUseableItem");
            }
            else
            {
                dynamic arg = new ExpandoObject();
                arg.ItemID = item.ID;
                bot.Flash.CallGameFunction("world.sendUnequipItemRequest", arg);
            }
            // Equipping waits in Skua already; both wait here for the inventory to say so.
            for (int waited = 0; waited < 5000; waited += 200)
            {
                if (bot.Inventory.Items.FirstOrDefault(i => i.ID == item.ID)?.Equipped == equip)
                    return true;
                Thread.Sleep(200);
            }
            return false;
        });

        string verb = equip ? "equipped" : "unequipped";
        return new
        {
            done,
            message = done ? $"{item.Name} {verb}" : $"{item.Name} was not {verb} (the game did not confirm it)",
            state = InventoryState(bot),
        };
    }

    private object? InventoryUnavailable()
    {
        var player = services.GetRequiredService<IScriptPlayer>();
        if (!services.GetRequiredService<Skua.Ruffle.RuffleBridge>().IsConnected || !player.LoggedIn)
            return new { error = "log in first: the inventory is the logged-in account's" };
        return null;
    }

    private static object InventoryState(IScriptInterface bot)
    {
        var items = bot.Inventory.Items;
        return new
        {
            slots = bot.Inventory.Slots,
            used = bot.Inventory.UsedSlots,
            level = bot.Player.Level,
            member = bot.Player.IsMember,
            inCombat = bot.Player.InCombat,
            items = items.Select(i =>
            {
                var (canEquip, canUnequip, why) = EquipRules(bot, i);
                return new
                {
                    id = i.ID,
                    name = i.Name,
                    category = i.CategoryString,
                    slot = SlotName(i),
                    quantity = i.Quantity,
                    maxStack = i.MaxStack,
                    ac = i.Coins,
                    member = i.Upgrade,
                    equipped = i.Equipped,
                    enhancement = i.EnhancementLevel > 0 ? EnhancementName(i.EnhancementPatternID) : null,
                    level = i.EnhancementLevel,
                    proc = ProcName(i.ProcID),
                    canEquip,
                    canUnequip,
                    why,
                };
            }).ToList(),
        };
    }

    // A usable item: type Item with a link (sLink, Skua's FileName), as the
    // game's inventory panel decides.
    private static bool Usable(InventoryItem i) =>
        string.Equals(i.CategoryString, "Item", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(i.FileName) && !string.Equals(i.FileName, "none", StringComparison.OrdinalIgnoreCase);

    private static string? SlotName(InventoryItem i) => i.ItemGroup?.ToLowerInvariant() switch
    {
        "ar" => "Class",
        "weapon" => "Weapon",
        "co" => "Armor",
        "he" => "Helm",
        "ba" => "Cape",
        "pe" => "Pet",
        "am" => "Necklace",
        "mi" => "Ground",
        _ => Usable(i) ? "Item" : null,
    };

    /// <summary>Whether the game would equip or unequip this item now, and if
    /// not, why (for the page to show).</summary>
    private static (bool CanEquip, bool CanUnequip, string? Why) EquipRules(IScriptInterface bot, InventoryItem i)
    {
        string? slot = i.ItemGroup?.ToLowerInvariant();
        bool usable = Usable(i);
        string link = (i.FileName ?? "").ToLowerInvariant();
        if (usable && link is "elixir" or "tonic")
            return (false, false, "elixirs and tonics are used, not equipped");
        bool wearable = usable || slot is "weapon" or "he" or "ar" or "ba" or "co" or "pe" or "am" or "mi";
        if (!wearable)
            return (false, false, null);

        bool canUnequip = i.Equipped && slot is not ("weapon" or "ar");
        string? why = i.Equipped && !canUnequip ? "the class and the weapon cannot be unequipped, only replaced" : null;
        if (i.Equipped)
            return (false, canUnequip, why);

        if (i.Upgrade && !bot.Player.IsMember)
            return (false, false, "a member item");
        // As the game checks it: the enhancement's level against the player's.
        if (i.EnhancementLevel > bot.Player.Level)
            return (false, false, $"needs level {i.EnhancementLevel}");
        if (!usable && slot is "weapon" or "he" or "ar" or "ba" && i.EnhancementLevel <= 0)
            return (false, false, "needs an enhancement first");
        return (true, false, null);
    }

    private sealed record EquipInput(int Id, bool? Equip);
}
