using Newtonsoft.Json;
using Skua.Core.Models.Converters;
using Skua.Core.Models.Items;

namespace Skua.Core.Models.Quests;

public class Quest
{
    /// <summary>
    /// The ID of the quest.
    /// </summary>
    [JsonProperty("QuestID")]
    public int ID { get; set; }

    /// <summary>
    /// The slot of the quest.
    /// </summary>
    [JsonProperty("iSlot")]
    public int Slot { get; set; }

    /// <summary>
    /// The value of the quest.
    /// </summary>
    [JsonProperty("iValue")]
    public int Value { get; set; }

    /// <summary>
    /// The name of the quest.
    /// </summary>
    [JsonProperty("sName")]
    public string Name { get; set; }

    /// <summary>
    /// The description of the quest.
    /// </summary>
    [JsonProperty("sDesc")]
    public string Description { get; set; }

    /// <summary>
    /// The description of the quest after completion.
    /// </summary>
    [JsonProperty("sEndText")]
    public string EndText { get; set; }

    /// <summary>
    /// Whether this quest can only be completed once.
    /// </summary>
    [JsonProperty("bOnce")]
    [JsonConverter(typeof(StringBoolConverter))]
    public bool Once { get; set; }

    /// <summary>
    /// The field of the quest.
    /// </summary>
    [JsonProperty("sField")]
    public string Field { get; set; }

    /// <summary>
    /// The index of the quest.
    /// </summary>
    [JsonProperty("iIndex")]
    public int Index { get; set; }

    /// <summary>
    /// Whether this quest requires upgrade.
    /// </summary>
    [JsonProperty("bUpg")]
    [JsonConverter(typeof(StringBoolConverter))]
    public bool Upgrade { get; set; }

    /// <summary>
    /// The level required to accept the quest.
    /// </summary>
    [JsonProperty("iLvl")]
    public int Level { get; set; }

    /// <summary>
    /// The id of the class required to accept the quest.
    /// </summary>
    [JsonProperty("iClass")]
    public int RequiredClassID { get; set; }

    /// <summary>
    /// The class points required to accept the quest.
    /// </summary>
    [JsonProperty("iReqCP")]
    public int RequiredClassPoints { get; set; }

    /// <summary>
    /// The faction required to accept the quest.
    /// </summary>
    [JsonProperty("FactionID")]
    public int RequiredFactionId { get; set; }

    /// <summary>
    /// The required faction rep to accept the quest.
    /// </summary>
    [JsonProperty("iReqRep")]
    public int RequiredFactionRep { get; set; }

    /// <summary>
    /// The amount of gold this quest gives as a reward.
    /// </summary>
    [JsonProperty("iGold")]
    public int Gold { get; set; }

    /// <summary>
    /// The amount of XP this quest gives as a reward.
    /// </summary>
    [JsonProperty("iExp")]
    public int XP { get; set; }

    /// <summary>
    /// The status of the quest.
    /// </summary>
    [JsonProperty("status")]
    public string Status { get; set; }

    /// <summary>
    /// Indicates whether the quest is active or not.
    /// </summary>
    public bool Active => Status != null;

    /// <summary>
    /// The items required in the player's inventory to accept the quest.
    /// </summary>
    [JsonProperty("oReqd")]
    [JsonConverter(typeof(DictionaryListConverter<int, ItemBase>))]
    public List<ItemBase> AcceptRequirements { get; set; } = new List<ItemBase>();

    [JsonProperty("oItems")]
    [JsonConverter(typeof(DictionaryListConverter<int, ItemBase>))]
    private List<ItemBase> _reqs = new();

    [JsonProperty("turnin")]
    private List<SimpleRequirement> _turnin = new();

    private List<ItemBase> _reqCache;

    /// <summary>
    /// The items used to turn in the quest, in the order Flash lists them.
    /// </summary>
    public List<ItemBase> Requirements => _reqCache ??= OrderLikeFlash(_reqs.Select(x => (x.Quantity = _turnin.Find(y => y.ID == x.ID).Quantity) != -1 ? x : x).ToList());

    /// <summary>
    /// A quest's requirement item ids in the order the Flash client lists
    /// them (QuestData.json has it), or null if unknown. Set by ScriptQuest.
    /// </summary>
    public static Func<int, IReadOnlyList<int>?>? FlashRequirementOrder { get; set; }

    // oItems is an object keyed by item id, so its order is the game
    // runtime's property order: Flash's and Ruffle's differ. Scripts are
    // written against Flash's (CoreStory.KillQuest pairs requirement i with
    // monster i), so under Ruffle they would farm the wrong monster. Order by
    // QuestData.json where it knows the quest, then by the server's turnin
    // list, which does not depend on the runtime.
    private List<ItemBase> OrderLikeFlash(List<ItemBase> reqs)
    {
        if (reqs.Count < 2)
            return reqs;
        IReadOnlyList<int>? flash = null;
        try { flash = FlashRequirementOrder?.Invoke(ID); }
        catch { }
        int Rank(ItemBase item)
        {
            int i = flash is null ? -1 : IndexOf(flash, item.ID);
            if (i >= 0)
                return i;
            int t = _turnin.FindIndex(y => y.ID == item.ID);
            return t >= 0 ? 10_000 + t : 20_000;
        }
        return reqs.OrderBy(Rank).ToList();   // stable: ties keep their order
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }

    /// <summary>
    /// The items given as a reward for completing the quest.
    /// </summary>
    [JsonProperty("oRewards")]
    [JsonConverter(typeof(QuestRewardConverter))]
    public List<ItemBase> Rewards { get; set; } = new List<ItemBase>();

    /// <summary>
    /// Item drop rates are mapped to their IDs in this list.
    /// </summary>
    [JsonProperty("reward")]
    public List<SimpleReward> SimpleRewards { get; set; } = new List<SimpleReward>();

    public override string ToString()
    {
        return $"{Name} [{ID}]";
    }
}