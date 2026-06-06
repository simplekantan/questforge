using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Schema;

// ---------------------------------------------------------------------------
// Step base class — all 20 step types inherit from this.
// Uses [JsonPolymorphic] with "type" as the discriminator.
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TravelStep),            "travel")]
[JsonDerivedType(typeof(TalkStep),              "talk")]
[JsonDerivedType(typeof(InteractObjectStep),    "interact-object")]
[JsonDerivedType(typeof(PickupItemStep),        "pickup-item")]
[JsonDerivedType(typeof(AcceptStep),            "accept")]
[JsonDerivedType(typeof(TurnInStep),            "turn-in")]
[JsonDerivedType(typeof(CombatStep),            "combat")]
[JsonDerivedType(typeof(DutyStep),              "duty")]
[JsonDerivedType(typeof(CutsceneStep),          "cutscene")]
[JsonDerivedType(typeof(SayChatMessageStep),    "say-chat-message")]
[JsonDerivedType(typeof(UseEmoteStep),          "use-emote")]
[JsonDerivedType(typeof(UseItemStep),           "use-item")]
[JsonDerivedType(typeof(UseActionStep),         "use-action")]
[JsonDerivedType(typeof(EquipGearForQuestStep), "equip-gear-for-quest")]
[JsonDerivedType(typeof(EquipBestGearStep),     "equip-best-gear")]
[JsonDerivedType(typeof(ChangeJobStep),         "change-job")]
[JsonDerivedType(typeof(MinigameStep),          "minigame")]
[JsonDerivedType(typeof(AwaitUserStep),         "await-user")]
[JsonDerivedType(typeof(BranchStep),            "branch")]
[JsonDerivedType(typeof(FragmentStep),          "fragment")]
[JsonDerivedType(typeof(AttunementStep),        "attune")]
[JsonDerivedType(typeof(HandOverItemStep),      "hand-over-item")]
[JsonDerivedType(typeof(WaitStep),              "wait")]
[JsonDerivedType(typeof(PurchaseItemStep),      "purchase-item")]
[JsonDerivedType(typeof(TeleportStep),          "teleport")]
[JsonDerivedType(typeof(RegisterGearsetStep),   "register-gearset")]
[JsonDerivedType(typeof(OpenCoffersStep),       "open-coffers")]
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public string? RequiredZone { get; init; }
    public string? ResumePointFragmentId { get; init; }
    public ExpectValue? Expect { get; init; }
    public ExpectValue? SkipIf { get; init; }
    public float? StopDistance { get; init; }
    public RecoverConfig? Recover { get; init; }
    public RetryConfig? Retry { get; init; }
    public Preconditions? Preconditions { get; init; }
    public string? Notes { get; init; }
}

// ---------------------------------------------------------------------------
// Concrete step types
// ---------------------------------------------------------------------------

public class TravelStep : Step
{
    public TravelDestination Destination { get; init; } = default!;
    public RouteHint? RouteHint { get; init; }

    /// <summary>
    /// When false, the engine must not attempt Mount Roulette before navigating this step.
    /// Use for steps that traverse known mount-prohibited sub-areas.
    /// Null / true (default) → engine applies its standard mount predicate.
    /// </summary>
    public bool? UseMount { get; init; }
}

public class TalkStep : Step
{
    public NpcLocation? Target { get; init; }
    public NpcLocation[]? Targets { get; init; }
    public string? TargetOrder { get; init; }   // "sequential" | "any" | "nearest-first"
    public DialogueChoice[] DialogueChoices { get; init; } = [];
}

public sealed class InteractObjectStep : Step
{
    public uint InteractableId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? Position { get; init; }
}

public sealed class PickupItemStep : Step
{
    public uint InteractableId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? Position { get; init; }
}

public class AcceptStep : Step
{
    public NpcLocation Target { get; init; } = default!;
}

public class TurnInStep : Step
{
    public NpcLocation Target { get; init; } = default!;
    public DialogueChoice[] DialogueChoices { get; init; } = [];
}

public class CombatStep : Step
{
    /// <summary>
    /// Set of BNpc base data-ids to defeat.
    /// Empty + <see cref="CombatSpawn.AutoOnEnterArea"/> means "all hostiles in the arena".
    /// </summary>
    public uint[] KillEnemyDataIds { get; init; } = [];

    /// <summary>
    /// v1 spawn type.
    /// autoOnEnterArea: a fixed set already present on arena entry.
    /// overworldEnemies: kill N of possibly-many free-roaming mobs.
    /// </summary>
    public CombatSpawn Spawn { get; init; } = CombatSpawn.AutoOnEnterArea;

    /// <summary>
    /// Coarse "get to the arena" navigation — mirrors AttunementStep.Location.
    /// When present and the player is beyond StopDistance, the engine emits Navigate first.
    /// </summary>
    public NpcLocation? Location { get; init; }

    // Completion is the inherited Step.Expect (questVariable/questFlag/questSequence). REQUIRED
    // for combat in practice; the validator warns when absent.
}

/// <summary>
/// Describes how/when enemies spawn for a combat step.
/// Serialised as camelCase via JsonStringEnumConverter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CombatSpawn>))]
public enum CombatSpawn
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("autoOnEnterArea")]
    AutoOnEnterArea,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("overworldEnemies")]
    OverworldEnemies
}

public sealed class DutyStep : Step
{
    public string Kind { get; init; } = default!;   // "regular" | "spd" | "duty"

    /// <summary>
    /// ContentFinderCondition row ID (Lumina). Required for kind "duty".
    /// The adapter derives territory type from this ID for AutoDuty's IPC.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ContentFinderConditionId { get; init; }

    /// <summary>
    /// DataId of the NPC that triggers SPD entry. Used by EngineHost for autonomous
    /// retry after SPD failure: navigate to EntryPosition, interact with EntryTargetId.
    /// Null when the quest author omits it (legacy quests, or when the preceding TalkStep
    /// already handles the NPC interaction for initial entry).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EntryTargetId { get; init; }

    /// <summary>
    /// World-space position of the entry NPC. Used for implied navigation before re-interact.
    /// Null is valid: when EntryTargetId is set but EntryPosition is null, EngineHost
    /// emits Interact without preceding Navigate (assumes player is already near the NPC).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? EntryPosition { get; init; }
}

public class CutsceneStep : Step
{
    public string Skip { get; init; } = "ifAllowed";   // "never" | "ifAllowed"
}

public sealed class SayChatMessageStep : Step
{
    public string Message { get; init; } = default!;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }
}

public sealed class UseEmoteStep : Step
{
    public uint EmoteId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }
    public bool Motion { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NpcLocation? Location { get; init; }
}

public sealed class UseItemStep : Step
{
    public ItemKind Kind { get; init; }
    public uint ItemId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? TargetPosition { get; init; }
}

public class UseActionStep : Step
{
    public ActionType ActionType { get; init; }
    public uint ActionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NpcLocation? Location { get; init; }
}

public sealed class EquipGearForQuestStep : Step
{
    /// <summary>
    /// Item IDs to equip. The adapter determines the target slot from each item's
    /// EquipSlotCategory (Lumina lookup). For rings, the adapter picks the first
    /// available ring slot.
    /// </summary>
    public uint[] ItemIds { get; init; } = [];
}

public sealed class EquipBestGearStep : Step { }

public sealed class ChangeJobStep : Step
{
    /// <summary>
    /// ClassJob row ID from the game's ClassJob sheet.
    /// Example: 32 = Dark Knight, 19 = Paladin, 24 = White Mage.
    /// </summary>
    public uint JobId { get; init; }
}

public class MinigameStep : Step
{
    public string Kind { get; init; } = default!;   // "sniping" | "memory" | "aiming" | "rhythm" | "selection" | "other"
    public string Skip { get; init; } = "ifAllowed"; // "never" | "ifAllowed" | "always"
}

public class AwaitUserStep : Step
{
    public string Reason { get; init; } = default!;   // ≤200 chars
}

public class BranchStep : Step
{
    public BranchCase[] Branches { get; init; } = [];
}

public class FragmentStep : Step
{
    public string Ref { get; init; } = default!;
    public Dictionary<string, JsonElement>? Params { get; init; }
}

public class AttunementStep : Step
{
    /// <summary>The aetheryte or aethernet shard to attune to.</summary>
    public AetheryteId Target { get; init; }

    /// <summary>
    /// Optional world-space position of the aetheryte or shard.
    /// When present, the engine uses implied navigation (same as talk/accept/turn-in):
    /// if the player is beyond StopDistance, it emits Navigate first.
    /// When absent, the engine emits Interact directly — author a preceding TravelStep
    /// to ensure the player is close enough.
    /// </summary>
    public NpcLocation? Location { get; init; }
}

public class HandOverItemStep : Step
{
    public NpcLocation Target { get; init; } = default!;
    public uint[] Items { get; init; } = [];
}

public class WaitStep : Step
{
    public double Seconds { get; init; }
}

public class PurchaseItemStep : Step
{
    public NpcLocation Target { get; init; } = default!;
    public uint ItemId { get; init; }
    public int Quantity { get; init; } = 1;
    public PurchaseCurrency Currency { get; init; } = PurchaseCurrency.Gil;
    public int? GcCategory { get; init; }
    public int? GcRankTier { get; init; }
}

public class TeleportStep : Step
{
    public AetheryteId AetheryteId { get; init; }
}

public sealed class RegisterGearsetStep : Step { }

public sealed class OpenCoffersStep : Step { }

[JsonConverter(typeof(JsonStringEnumConverter<PurchaseCurrency>))]
public enum PurchaseCurrency
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("gil")]
    Gil,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("gcSeals")]
    GcSeals
}