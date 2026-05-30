using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Schema;

[JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
public enum ActionType
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("action")]
    Action,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("generalAction")]
    GeneralAction,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("keyItem")]
    KeyItem
}

[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("keyItem")]
    KeyItem,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("inventoryItem")]
    InventoryItem
}

// ---------------------------------------------------------------------------
// Shared value types — immutable records used across quest and fragment types.
// ---------------------------------------------------------------------------

// Schema-side AetheryteId alias. Lives here (not in Adapters) to keep Schema as a leaf
// with no upward dependency. The engine converts Schema.AetheryteId → Adapters.Types.AetheryteId
// via .Value when dispatching AttunementStep.
//
// The converter writes/reads as a plain uint (e.g. 8) for clean JSON authoring.
// For backward compatibility with older JSON that used the object form {"value": N},
// the reader also accepts that format so existing quest files remain valid.
[JsonConverter(typeof(AetheryteIdConverter))]
public readonly record struct AetheryteId(uint Value);

/// <summary>
/// Reads/writes <see cref="AetheryteId"/> as a plain uint in JSON (e.g. <c>8</c>).
/// Also accepts the legacy object form <c>{"value": 8}</c> on read for backward
/// compatibility with older quest files authored before this converter existed.
/// </summary>
public sealed class AetheryteIdConverter : JsonConverter<AetheryteId>
{
    public override AetheryteId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new AetheryteId(reader.GetUInt32());

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            uint value = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType == JsonTokenType.PropertyName
                    && string.Equals(reader.GetString(), "value", StringComparison.OrdinalIgnoreCase))
                {
                    reader.Read();
                    value = reader.GetUInt32();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    reader.Read(); // skip unknown property value
                }
            }
            return new AetheryteId(value);
        }

        throw new JsonException($"Expected a number or object for AetheryteId, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, AetheryteId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}

public record Position3(float X, float Y, float Z);

public record NpcLocation(uint NpcId, int Zone, Position3 Position);

public record TravelDestination(int Zone, Position3? Position = null, uint? AetheryteId = null);

public record AethernetRouteHint(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? From,
    uint To);

/// <summary>
/// Describes NPC-mediated zone travel (e.g. a Lift Attendant). Target.Zone is the SOURCE
/// zone where the NPC resides; TravelStep.Destination.Zone is the DESTINATION zone.
/// </summary>
public record NpcDialogueHint
{
    [JsonConstructor]
    public NpcDialogueHint(NpcLocation Target)
    {
        this.Target = Target;
    }

    public NpcLocation Target { get; init; }
    /// <summary>
    /// Ordered list of list-type dialogue choices to drive the NPC menu.
    /// Empty array when not supplied at construction.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DialogueChoice[] DialogueChoices { get; init; } = [];

    /// <summary>
    /// Convenience constructor that also accepts dialogue choices.
    /// </summary>
    public NpcDialogueHint(NpcLocation target, DialogueChoice[]? dialogueChoices)
        : this(target)
    {
        DialogueChoices = dialogueChoices ?? [];
    }
}

public record RouteHint(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Aetheryte = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AethernetRouteHint? Aethernet = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpcDialogueHint? NpcDialogue = null);

/// <summary>A single dialogue interaction. Type: "list" | "yesno" | "talk".</summary>
public record DialogueChoice(string Type, string? Prompt = null, string? Answer = null);

public record DutyTrigger(
    string Kind,        // "npc" | "object"
    int Zone,
    Position3 Position,
    uint? NpcId = null,
    uint? InteractableId = null);

public record BranchCase(string When, Step[] Steps = default!);

public record ChainNext(string When, uint? QuestId);

public record PrerequisiteRef(uint QuestId, string State);  // "complete" | "accepted"

public record FragmentParameter(string Name, string Type, bool Required = true);
// Type: "position" | "npcId" | "itemId" | "string"

public record RetryConfig(int? MaxAttempts = null, int? Timeout = null, string? Backoff = null);

public record Preconditions(int? MinGearCondition = null);

public record RewardOverride(string Strategy, uint? ItemId = null);

/// <summary>Target for interact-object and pickup-item steps.</summary>
public record InteractableTarget(uint InteractableId, int Zone, Position3 Position);

// CombatTarget (was: nearestHostile / specificNpc / wave) — retired in part A.
// Combat step completion is now modelled via Step.Expect + CombatStep.KillEnemyDataIds + CombatSpawn.
// No schema-version bump — early dev, no existing authored quests used this type.