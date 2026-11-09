using Newtonsoft.Json;

namespace Skua.Core.Models.Auras;

/// <summary>
/// Represents an aura effect, including its properties such as name, type, duration, and status information.
/// </summary>
/// <remarks>
/// An aura typically describes a temporary or passive effect applied to an entity, such as a buff,
/// debuff. This class provides information about the aura's identity, timing, and visual
/// representation. Some properties, such as TimeStamp and ExpiresAt, are computed for convenience.
/// </remarks>
public class Aura
{
    /// <summary>
    /// The aura's effect value. This is separate from the HUD snapshot's stack count.
    /// </summary>
    [JsonProperty("val")]
    public float Value { get; set; } = 1;

    /// <summary>
    /// The icon file name for the aura.
    /// </summary>
    [JsonProperty("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// The name of the aura.
    /// </summary>
    [JsonProperty("nam")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The type of the aura.
    /// </summary>
    [JsonProperty("t")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// This should always return "cycle_" as having cLeaf inside of an aura causes a circular reference.
    /// <br> We manually set the cLeaf to equal to "cycle_" to avoid this </br>.
    /// </summary>
    [JsonProperty("cLeaf")]
    public string cLeaf { get; set; } = string.Empty;

    /// <summary>
    /// The duration of the aura in seconds.
    /// </summary>
    [JsonProperty("dur")]
    public int Duration { get; set; } = 0;

    /// <summary>
    /// Whether this is a new aura.
    /// </summary>
    [JsonProperty("isNew")]
    public bool IsNew { get; set; } = true;

    /// <summary>
    /// The timestamp when the aura was applied - Unix timestamp in milliseconds.
    /// </summary>
    [JsonProperty("ts")]
    public long UnixTimeStamp { get; set; } = 0;

    /// <summary>
    /// If the aura is a passive or not.
    /// </summary>
    [JsonProperty("passive")]
    public bool Passive { get; set; } = false;

    /// <summary>
    /// The potion type of aura if it's a potion.
    /// </summary>
    [JsonProperty("potionType")]
    public string? PotionType { get; set; } = string.Empty;

    /// <summary>
    /// DateTime timestamp (computed from Unix timestamp).
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset TimeStamp => UnixTimeStamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(UnixTimeStamp) : DateTimeOffset.MinValue;

    /// <summary>
    /// The expiration time of the aura.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset ExpiresAt => TimeStamp.AddSeconds(Duration);

    /// <summary>
    /// Gets the remaining time of the aura (int).
    /// </summary>
    [JsonIgnore]
    public int RemainingTime => Math.Max(0, (int)(ExpiresAt - DateTimeOffset.Now).TotalSeconds);

    /// <summary>
    /// The debuff type of aura. (e.g. stun, stone, disable)
    /// </summary>
    [JsonProperty("cat")]
    public string Category { get; set; } = string.Empty;

    [JsonProperty("fx")]
    public string Fx { get; set; } = string.Empty;

    [JsonProperty("msgOn")]
    public string MsgOn { get; set; } = string.Empty;

    [JsonProperty("animOn")]
    public string AnimationOn { get; set; } = string.Empty;

    [JsonProperty("animOff")]
    public string AnimationOff { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{Name} (x{Value})";
    }
    public string ToJson(Formatting formatting = Formatting.None)
    {
        return JsonConvert.SerializeObject(this, formatting);
    }
}