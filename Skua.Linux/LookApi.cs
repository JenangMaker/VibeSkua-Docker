using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// What the logged-in character looks like, for the web manager's Preview:
/// enough to draw it with AQW's own character viewer (characterB.swf, the
/// one account.aq.com's character page runs in Ruffle) in the browser.
///
///   GET /look    gender, hair, the six colors, whether helm and cape show
///                (showHelm, showCloak, ia1), and each slot's art as the
///                game shows it now
///
/// slots come from world.myAvatar.objData.eqp, what the avatar is drawn
/// with (a worn cosmetic shows there, not the item under it): class (ar),
/// armor (co), helm (he), cape (ba), weapon (Weapon), pet (pe) and ground
/// item (mi); each has file (sFile), link (sLink), type (sType), id and the
/// item's name when it is in the inventory. Only the look is returned: the
/// object also holds account details, which stay out.
/// </summary>
public sealed partial class HostApi
{
    private static readonly (string Key, string Slot)[] LookSlots =
    {
        ("ar", "class"), ("co", "armor"), ("he", "helm"), ("ba", "cape"),
        ("Weapon", "weapon"), ("pe", "pet"), ("mi", "misc"),
    };

    private object Look()
    {
        if (InventoryUnavailable() is not null)
            return new { error = "log in first: the look is the logged-in account's" };
        var bot = services.GetRequiredService<IScriptInterface>();
        var flash = services.GetRequiredService<IFlashUtil>();

        T Get<T>(string key, T fallback) => flash.GetGameObject($"world.myAvatar.objData.{key}", fallback);
        var eqp = flash.GetGameObject<Dictionary<string, JToken>>("world.myAvatar.objData.eqp") ?? new();
        var items = bot.Inventory.Items;

        var slots = new Dictionary<string, object?>();
        foreach (var (key, slot) in LookSlots)
        {
            if (!eqp.TryGetValue(key, out JToken? e) || e is not JObject o)
            {
                slots[slot] = null;
                continue;
            }
            int id = o.Value<int?>("ItemID") ?? 0;
            slots[slot] = new
            {
                id,
                name = items.FirstOrDefault(i => i.ID == id)?.Name,
                file = o.Value<string>("sFile"),
                link = o.Value<string>("sLink"),
                type = o.Value<string>("sType"),
            };
        }

        return new
        {
            name = bot.Player.Username,
            level = bot.Player.Level,
            gender = Get("strGender", "M"),
            hair = new { file = Get("strHairFilename", ""), name = Get("strHairName", "") },
            colors = new
            {
                skin = Get("intColorSkin", 0),
                hair = Get("intColorHair", 0),
                eye = Get("intColorEye", 0),
                @base = Get("intColorBase", 0),
                trim = Get("intColorTrim", 0),
                accessory = Get("intColorAccessory", 0),
            },
            showHelm = Get("showHelm", true),
            showCloak = Get("showCloak", true),
            // The account's flags; the viewer hides the cape, helm and pet
            // when bits 0, 1 and 2 are set.
            ia1 = Get("ia1", 0L),
            slots,
        };
    }
}
