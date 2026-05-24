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
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public string? RequiredZone { get; init; }
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
}

public class TalkStep : Step
{
    public NpcLocation? Target { get; init; }
    public NpcLocation[]? Targets { get; init; }
    public string? TargetOrder { get; init; }   // "sequential" | "any" | "nearest-first"
    public DialogueChoice[] DialogueChoices { get; init; } = [];
}

public class InteractObjectStep : Step
{
    public InteractableTarget Target { get; init; } = default!;
}

public class PickupItemStep : Step
{
    public InteractableTarget Target { get; init; } = default!;
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
    public CombatTarget Target { get; init; } = default!;
}

public class DutyStep : Step
{
    public string Kind { get; init; } = default!;   // "regular" | "spd"
    public uint? DutyId { get; init; }
    public NpcLocation? EntryNpc { get; init; }
    public DutyTrigger? Trigger { get; init; }
    public string? FallbackOverride { get; init; }
}

public class CutsceneStep : Step
{
    public string Skip { get; init; } = "ifAllowed";   // "never" | "ifAllowed"
}

public class SayChatMessageStep : Step
{
    public string Channel { get; init; } = default!;  // "say" | "yell" | "shout"
    public string Message { get; init; } = default!;
    public NpcLocation? Target { get; init; }
}

public class UseEmoteStep : Step
{
    public uint EmoteId { get; init; }
    public NpcLocation? Target { get; init; }
}

public class UseItemStep : Step
{
    public uint ItemId { get; init; }
    public UseItemTarget? Target { get; init; }
}

public class UseActionStep : Step
{
    public uint ActionId { get; init; }
    public ActionTarget Target { get; init; } = default!;
    public bool RepeatUntilExpect { get; init; }
}

public class EquipGearForQuestStep : Step
{
    public GearItem[] Items { get; init; } = [];
}

public class EquipBestGearStep : Step
{
    public GearConstraints? Constraints { get; init; }
}

public class ChangeJobStep : Step
{
    public string Job { get; init; } = default!;
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