using Newtonsoft.Json;

namespace Skua.Core.Models.Auras;

/// <summary>The game's current HUD aura data, separate from an aura's effect value.</summary>
public class AuraSnapshot
{
    [JsonProperty("nam")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("n")]
    public int Stacks { get; set; }

    /// <summary>The aura duration in seconds. Zero denotes no timed expiry.</summary>
    [JsonProperty("dur")]
    public double Duration { get; set; }

    /// <summary>Seconds remaining at the time this snapshot was read.</summary>
    [JsonProperty("remaining")]
    public double RemainingTime { get; set; }

    [JsonProperty("persist")]
    public bool Persistent { get; set; }

    [JsonProperty("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonProperty("desc")]
    public string Description { get; set; } = string.Empty;
}
