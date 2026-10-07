using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.Core.Scripts;

/// <summary>
/// Contains / GetItem for the bank, inventory and house without reading the
/// whole list. CoreBots' CheckInventory asks the inventory, house and bank
/// several times a second while a script farms an item, and each read was the
/// whole list as JSON, built on the game's thread and parsed here (a bank of
/// 1455 items is ~1 MB). skua.swf's findItems returns only the entries whose
/// ItemID matches or whose name matches ignoring case and whitespace, and the
/// rules below are ICheckInventory's own, applied to those few (string == in
/// Skua.Core ignores case: Caseless.Fody). Without findItems (an older
/// skua.swf) they read the whole list as before.
/// </summary>
internal static class ItemLookup
{
    private static List<InventoryItem>? Candidates(IFlashUtil flash, string path, string? name, int id)
    {
        string? json = flash.Call("findItems", path, name ?? string.Empty, id);
        if (json is null)
            return null;
        try
        {
            return JsonConvert.DeserializeObject<List<InventoryItem>>(json);
        }
        catch
        {
            return null;
        }
    }

    public static bool Contains(IFlashUtil flash, string path, Func<List<InventoryItem>> all, string name, int quantity)
    {
        return quantity == 0 || (Candidates(flash, path, name, 0) ?? all()).Any(i => i.Name == name && (i.Quantity >= quantity || i.Category == ItemCategory.Class));
    }

    public static bool Contains(IFlashUtil flash, string path, Func<List<InventoryItem>> all, int id, int quantity)
    {
        return quantity == 0 || (Candidates(flash, path, null, id) ?? all()).Any(i => i.ID == id && (i.Quantity >= quantity || i.Category == ItemCategory.Class));
    }

    public static InventoryItem? GetItem(IFlashUtil flash, string path, Func<List<InventoryItem>> all, string name)
    {
        return (Candidates(flash, path, name, 0) ?? all())?.Find(x => x.Name == name);
    }

    public static InventoryItem? GetItem(IFlashUtil flash, string path, Func<List<InventoryItem>> all, int id)
    {
        return (Candidates(flash, path, null, id) ?? all())?.Find(x => x.ID == id);
    }
}
