using Newtonsoft.Json;
using Skua.Core.Models.Converters;

namespace Skua.Core.Models.Items;

public class ItemBase
{
    /// <summary>
    /// The ID of the item.
    /// </summary>
    [JsonProperty("ItemID")]
    public virtual int ID { get; set; }

    /// <summary>
    /// The name of the item.
    /// </summary>
    [JsonProperty("sName")]
    [JsonConverter(typeof(TrimConverter))]
    public virtual string Name { get; set; }

    /// <summary>
    /// The description of the item.
    /// </summary>
    [JsonProperty("sDesc")]
    public virtual string Description { get; set; }

    /// <summary>
    /// The quantity of the item in this stack.
    /// </summary>
    [JsonProperty("iQty")]
    public virtual int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            _hasQuantity = true;
        }
    }

    private int _quantity;
    private bool _hasQuantity;

    // An inventory entry the game rebuilt from a server item update can hold
    // its count only in iQtyNow (seen on a scroll after quest turn-ins): use
    // it then, else Quantity read 0. Where iQty is there too (drops: iQty is
    // the amount added), iQty wins, whichever comes first.
    [JsonProperty("iQtyNow")]
    private int QuantityNow
    {
        set
        {
            if (!_hasQuantity)
                _quantity = value;
        }
    }

    /// <summary>
    /// The maximum stack size this item can exist in.
    /// </summary>
    [JsonProperty("iStk")]
    public virtual int MaxStack { get; set; }

    /// <summary>
    /// Indicates if the item is a member/upgrade only item.
    /// </summary>
    [JsonProperty("bUpg")]
    [JsonConverter(typeof(StringBoolConverter))]
    public virtual bool Upgrade { get; set; }

    /// <summary>
    /// Indicates whether the item is currently being worn.
    /// </summary>
    [JsonProperty("bWear")]
    [JsonConverter(typeof(StringBoolConverter))]
    public virtual bool Wearing { get; set; }

    /// <summary>
    /// Indicates if the item is an AC item.
    /// </summary>
    [JsonProperty("bCoins")]
    [JsonConverter(typeof(StringBoolConverter))]
    public virtual bool Coins { get; set; }

    /// <summary>
    /// The category of the item.
    /// </summary>
    [JsonProperty("sType")]
    public virtual string CategoryString { get; set; }

    /// <summary>
    /// The enhancement pattern ID of the item. This identifies the current enhancement type of the item.
    /// <br> 1: Adventurer </br>
    /// <br> 2: Fighter </br>
    /// <br> 3: Thief </br>
    /// <br> 4: Armsman </br>
    /// <br> 5: Hybrid </br>
    /// <br> 6: Wizard </br>
    /// <br> 7: Healer </br>
    /// <br> 8: Spellbreaker </br>
    /// <br> 9: Lucky </br>
    /// <br> 10: Forge </br>
    /// <br> 11: Absolution </br>
    /// <br> 12: Avarice </br>
    /// <br> 23: Depths </br>
    /// <br> 24: Vainglory </br>
    /// <br> 25: Vim </br>
    /// <br> 26: Examen </br>
    /// <br> 27: Pneuma </br>
    /// <br> 28: Anima </br>
    /// <br> 29: Penitence </br>
    /// <br> 30: Lament </br>
    /// <br> 32: Hearty </br>
    /// </summary>
    [JsonProperty("EnhPatternID")]
    public virtual int EnhancementPatternID { get; set; }

    /// <summary>
    /// The ProcID of the item. This identifies the current special enhancement (E.G. Forge and AWE enhancements).
    /// <br> 2: Spiral Carve </br>
    /// <br> 3: Awe Blast </br>
    /// <br> 4: Health Vamp </br>
    /// <br> 5: Mana Vamp </br>
    /// <br> 6: Powerword DIE </br>
    /// <br> 7: Lacerate </br>
    /// <br> 8: Smite </br>
    /// <br> 9: Valiance </br>
    /// <br> 10: Arcana's Concerto </br>
    /// <br> 11: Acheron </br>
    /// <br> 12: Elysium </br>
    /// <br> 13: Praxis </br>
    /// <br> 14: Dauntless </br>
    /// <br> 15: Ravenous </br>
    /// </summary>
    [JsonProperty("ProcID")]
    [JsonConverter(typeof(IntConverter))] // Workaround for UI elements not being visible.
    public virtual int ProcID { get; set; }

    private ItemCategory? _category = null;

    public virtual ItemCategory Category => _category is not null
                ? (ItemCategory)_category
                : (ItemCategory)(_category = Enum.TryParse(CategoryString, true, out ItemCategory result) ? result : ItemCategory.Unknown);

    /// <summary>
    /// Indicates if the item is a temporary item.
    /// </summary>
    [JsonProperty("bTemp")]
    [JsonConverter(typeof(StringBoolConverter))]
    public virtual bool Temp { get; set; }

    /// <summary>
    /// The group of the item. co = Armor; ba = Cape; he = Helm; pe = Pet; Weapon = Weapon.
    /// </summary>
    [JsonProperty("sES")]
    public virtual string ItemGroup { get; set; }

    /// <summary>
    /// The name of the source file of the item.
    /// </summary>
    [JsonProperty("sLink")]
    public virtual string FileName { get; set; }

    /// <summary>
    /// The link to the source file of the item.
    /// </summary>
    [JsonProperty("sFile")]
    public virtual string FileLink { get; set; }

    /// <summary>
    /// The meta value of the item. This is used to link buffs (xp boosts etc).
    /// </summary>
    [JsonProperty("sMeta")]
    public virtual string Meta { get; set; }

    public override string ToString()
    {
        string tag = string.Empty;

        tag += Coins ? "AC " : string.Empty;
        tag += Upgrade ? "Member" : string.Empty;

        string itemGroup = ItemGroup switch
        {
            "co" => "(Armor)",
            "ba" => "(Cape)",
            "he" => "(Helm)",
            "pe" => "(Pet)",
            "Weapon" => "(Weapon)",
            _ => "(Item)"
        };

        return $"[{ID}]\t{itemGroup} {Name} x{Quantity} {tag}";
    }

    public override bool Equals(object? obj)
    {
        return obj is ItemBase item && ID == item.ID;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ID);
    }
}