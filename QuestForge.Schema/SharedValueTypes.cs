using System.Text.Json.Serialization;

namespace QuestForge.Schema;

// ---------------------------------------------------------------------------
// Shared value types — immutable records used across quest and fragment types.
// ---------------------------------------------------------------------------

// Schema-side AetheryteId alias. Lives here (not in Adapters) to keep Schema as a leaf
// with no upward dependency. The engine converts Schema.AetheryteId → Adapters.Types.AetheryteId
// via .Value when dispatching AttunementStep.
public readonly record struct AetheryteId(uint Value);

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

public record GearItem(string Slot, uint ItemId);
// Slot: "mainhand" | "offhand" | "head" | "body" | "hands" | "legs" | "feet" |
//       "earrings" | "necklace" | "bracelets" | "ringR" | "ringL" | "soul"

public record GearConstraints(int? MinItemLevel = null);

/// <summary>
/// Flat target for use-item and use-action steps.
/// Kind discriminates which optional fields are required (validated structurally).
/// </summary>
public class UseItemTarget
{
    public string Kind { get; init; } = default!;   // "npc" | "object" | "position"
    public uint? NpcId { get; init; }
    public uint? InteractableId { get; init; }
    public int? Zone { get; init; }
    public Position3? Position { get; init; }
    public float? Tolerance { get; init; }
}

/// <summary>
/// Flat target for use-action steps.
/// Kind discriminates which optional fields are required.
/// </summary>
public class ActionTarget
{
    public string Kind { get; init; } = default!;   // "npc" | "object"
    public uint? NpcId { get; init; }
    public uint? InteractableId { get; init; }
    public int? Zone { get; init; }
    public Position3? Position { get; init; }
}

/// <summary>Target for interact-object and pickup-item steps.</summary>
public record InteractableTarget(uint InteractableId, int Zone, Position3 Position);

/// <summary>Target for combat steps.</summary>
public record CombatTarget(
    string Kind,        // "nearestHostile" | "specificNpc" | "wave"
    float? Radius = null,
    uint? NpcId = null);