using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.Linux;

/// <summary>
/// The logged-in account's bank for a remote page (the web manager's Bank
/// tab), and moving items between it and the inventory.
///
///   GET  /bank              bank and inventory: slots and items. The bank
///                           is loaded from the game (its bank window) while
///                           its list is empty; after that the game keeps it
///                           current and each GET reads it again.
///   POST /bank/move         body {"id": 123, "to": "bank"|"inventory"}
///
/// Moves use Skua's own EnsureToBank / EnsureToInventory (which retry until
/// the item has moved) and are refused while a script runs, since a script
/// may need the item where it is. AC items take no bank slot, so only a
/// non-AC item needs a free one to be banked.
/// </summary>
public sealed partial class HostApi
{
    private object Bank()
    {
        if (BankUnavailable() is { } error)
            return error;
        var bot = services.GetRequiredService<IScriptInterface>();
        if (bot.Bank.Items.Count == 0)
            LoadBank(bot);
        return BankState(bot);
    }

    // The game's own bank window (Bank.Open, world.toggleBank): Skua's Load()
    // sends a bare loadBank request, and with the native player the game left
    // its bank list empty after it (109 slots used, 0 items, 5 s later), while
    // toggleBank, which also records the requested types, filled it. Waits for
    // the items: up to 8 s, or 1.5 s when no slot is used (AC items use none,
    // so there may still be some). The window stays open in the game.
    private static void LoadBank(IScriptInterface bot)
    {
        bot.Bank.Open();
        int waitMs = bot.Bank.UsedSlots > 0 ? 8000 : 1500;
        for (int waited = 0; waited < waitMs && bot.Bank.Items.Count == 0; waited += 200)
            Thread.Sleep(200);
    }

    private async Task<object> BankMove(HttpListenerRequest request)
    {
        if (BankUnavailable() is { } error)
            return error;
        if (services.GetRequiredService<IScriptManager>().ScriptRunning)
            return new { error = "stop the script first: it may need the item where it is" };

        BankMoveInput? input;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            try { input = JsonSerializer.Deserialize<BankMoveInput>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException e) { return new { error = $"bad JSON: {e.Message}" }; }
        }
        if (input is null || input.Id <= 0 || input.To is not ("bank" or "inventory"))
            return new { error = "send {\"id\": <item id>, \"to\": \"bank\" or \"inventory\"}" };

        var bot = services.GetRequiredService<IScriptInterface>();
        if (bot.Bank.Items.Count == 0)
            LoadBank(bot);

        bool moved;
        string name;
        if (input.To == "bank")
        {
            var item = bot.Inventory.Items.FirstOrDefault(i => i.ID == input.Id);
            if (item is null)
                return new { error = "that item is not in the inventory" };
            if (item.Equipped || item.Wearing)
                return new { error = $"{item.Name} is equipped: unequip it first" };
            if (!item.Coins && bot.Bank.FreeSlots <= 0)
                return new { error = $"the bank is full ({bot.Bank.UsedSlots}/{bot.Bank.Slots}) and {item.Name} is not an AC item, so it needs a free slot" };
            name = item.Name;
            moved = await Task.Run(() => bot.Inventory.EnsureToBank(item.ID));
        }
        else
        {
            var item = bot.Bank.Items.FirstOrDefault(i => i.ID == input.Id);
            if (item is null)
                return new { error = "that item is not in the bank" };
            if (bot.Inventory.FreeSlots <= 0)
                return new { error = $"the inventory is full ({bot.Inventory.UsedSlots}/{bot.Inventory.Slots})" };
            name = item.Name;
            moved = await Task.Run(() => bot.Bank.EnsureToInventory(item.ID, false));
        }

        return new
        {
            moved,
            message = moved ? $"{name} moved to the {input.To}" : $"{name} did not move to the {input.To}",
            state = BankState(bot),
        };
    }

    private object? BankUnavailable()
    {
        var player = services.GetRequiredService<IScriptPlayer>();
        if (!services.GetRequiredService<Skua.Ruffle.RuffleBridge>().IsConnected || !player.LoggedIn)
            return new { error = "log in first: the bank is the logged-in account's" };
        return null;
    }

    // Each Items read asks the game, so each list is read once.
    private static object BankState(IScriptInterface bot)
    {
        var bankItems = bot.Bank.Items;
        var invItems = bot.Inventory.Items;
        return new
        {
            bank = new
            {
                slots = bot.Bank.Slots,
                used = bot.Bank.UsedSlots,
                items = bankItems.Select(BankItem).ToList(),
            },
            inventory = new
            {
                slots = bot.Inventory.Slots,
                used = bot.Inventory.UsedSlots,
                items = invItems.Select(BankItem).ToList(),
            },
        };
    }

    private static object BankItem(InventoryItem i) => new
    {
        id = i.ID,
        name = i.Name,
        category = i.CategoryString,
        quantity = i.Quantity,
        maxStack = i.MaxStack,
        ac = i.Coins,
        member = i.Upgrade,
        equipped = i.Equipped || i.Wearing,
        enhancement = i.EnhancementLevel > 0 ? EnhancementName(i.EnhancementPatternID) : null,
        level = i.EnhancementLevel,
        proc = ProcName(i.ProcID),
    };

    private sealed record BankMoveInput(int Id, string? To);
}
