namespace QuestForge.Adapters.State;

using System.Text.Json.Serialization;

/// <summary>
/// Priority tiers for quest reward selection. Evaluated in order; first tier
/// that produces a winner wins. Used by both the global config priority list
/// and quest-level RewardOverride single-tier evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RewardPriority>))]
public enum RewardPriority
{
    BiggestUpgrade,
    HighestGilValue,
    GearCoffer,
    GilSack,
    EquippableGear,
    UnequippableGear,
    AnythingElse
}
