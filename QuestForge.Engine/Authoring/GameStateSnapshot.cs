using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

/// <summary>Captures the departure and destination shard of a completed aethernet teleport.</summary>
public sealed record AethernetHop(AetheryteId? From, AethernetId To);

/// <summary>
/// Records that the player used a game action (combat ability, general action, or quest key item)
/// during this recording window. Set by SnapshotAggregator.OnActionCompleted, which is driven by
/// UIObserver.PollPlayerActionEffect reading LocalPlayer.CastInfo.Response* fields. Cleared by
/// OnActionConsumed (called from RecordStep) so it does not bleed into the next window.
///
/// TargetBaseId is the BNpcBase/ENpcBase row id of the action's target (null = self-cast / no target).
/// ActionType is the SCHEMA-side enum, NOT FFXIVClientStructs (testability boundary).
/// </summary>
public sealed record ActionCompletedSignal(
    QuestForge.Schema.ActionType ActionType,
    uint ActionId,
    uint? TargetBaseId,
    float? TargetX = null,
    float? TargetY = null,
    float? TargetZ = null,
    int? TargetZone = null);

/// <summary>
/// Records that the player triggered an emote during this recording window. Set by
/// SnapshotAggregator.OnEmoteCompleted, which is driven by UIObserver.PollPlayerEmote
/// reading LocalPlayer.EmoteController.EmoteId. Cleared by OnEmoteConsumed (called from
/// AuthoringHost.RecordStep) so it does not bleed into the next recording window.
///
/// EmoteId is the Lumina Emote sheet RowId (matches UseEmoteStep.EmoteId).
/// TargetBaseId is the BNpcBase / ENpcBase row id of the emote's target
/// (null = self-cast / no target / target not in ObjectTable).
/// </summary>
public sealed record EmoteCompletedSignal(
    uint EmoteId,
    uint? TargetBaseId,
    float? TargetX = null,
    float? TargetY = null,
    float? TargetZ = null,
    int? TargetZone = null);

/// <summary>
/// Records that the player typed /say &lt;message&gt; during this recording window. Set by
/// SnapshotAggregator.OnSayChatMessageSent, driven by UIObserver.PollPlayerChatMessage reading
/// RaptureLogModule.MsgSourceArrayLength + GetLogMessageDetail for matching entries.
/// Cleared by OnSayChatMessageConsumed (called from AuthoringHost.RecordStep) so it does not
/// bleed into the next recording window.
///
/// Message is the literal text the player typed (no "/say " prefix).
/// TargetBaseId is the BNpcBase / ENpcBase row id of the currently-targeted NPC at the moment
/// the chat-log entry was observed (null = no hard target / self-cast).
/// </summary>
public sealed record SayChatMessageSentSignal(
    string Message,
    uint? TargetBaseId);

/// <summary>
/// Records that the player used an item (key item or inventory item) during this recording
/// window. Set by SnapshotAggregator.OnItemUsed, which is driven by
/// UIObserver.PollPlayerActionEffect reading CastInfo.Response* fields and routing
/// ActionType.Item (2) and ActionType.EventItem (3) to this signal. Cleared by
/// OnItemUsedConsumed (called from RecordStep) so it does not bleed into the next window.
///
/// Kind discriminates KeyItem vs InventoryItem.
/// ItemId is the EventItem or Item row id (Lumina sheet row).
/// TargetBaseId is the BNpcBase / ENpcBase row id of the item's target
/// (null = self-cast / no target / target not in ObjectTable).
/// TargetPosition is the ground-target world position for AoE-targeted item use
/// (null = non-ground-targeted / all CastInfo.TargetLocation components are zero).
/// </summary>
public sealed record ItemUsedSignal(
    QuestForge.Schema.ItemKind Kind,
    uint ItemId,
    uint? TargetBaseId,
    QuestForge.Schema.Position3? TargetPosition);

/// <summary>
/// Signals that a vendor shop was opened and purchases were detected during an authoring window.
/// </summary>
public sealed record PurchaseDetection(
    bool ShopWasOpen,
    IReadOnlyDictionary<uint, int> ItemDeltas,
    long GilDropped,
    int SealsDropped,
    int? ActiveGcCategory = null,    // G5: last-seen GC category radio (0..3). Null if not a GC vendor or probe failed.
    int? ActiveGcRankTier = null,    // G5: last-seen GC rank-tier radio (0..2). Null if not a GC vendor or probe failed.
    int? ActiveVendorCategory = null); // VC8: last-seen SelectIconString category index. Null until inference slice lands.

/// <summary>
/// Associates a set of enemy data-ids with the quest variable (or sequence) value that was
/// reached after those kills were observed within the correlation window.
/// </summary>
public sealed record KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue);

/// <summary>
/// Records that the player's equipment changed during this recording window. Set by
/// SnapshotAggregator.OnEquipmentChanged, which is driven by UIObserver.PollEquipmentChange
/// comparing 14-slot snapshots of InventoryType.EquippedItems each frame. Cleared by
/// OnEquipmentChangedConsumed (called from AuthoringHost.RecordStep) so it does not bleed
/// into the next window.
///
/// NewItemIds contains the item IDs that appeared in equipment slots where they were absent
/// (or zero) in the previous snapshot. For a single-item equip this is typically one item.
/// </summary>
public sealed record EquipmentChangedSignal(IReadOnlyList<uint> NewItemIds);

/// <summary>
/// Records that the player's job (ClassJob) changed during this recording window. Set by
/// SnapshotAggregator.OnJobChanged, which is driven by UIObserver.PollJobChange polling
/// PlayerState.CurrentClassJobId each frame. Cleared by OnJobChangedConsumed (called from
/// AuthoringHost.RecordStep and UIObserver.ResetWindowState) so it does not bleed into the
/// next window. Baseline is NOT reset in ResetWindowState (job state persists across windows).
/// </summary>
public sealed record JobChangedSignal(uint OldJobId, uint NewJobId);

/// <summary>
/// Records that the player created a new gearset during this recording window. Set by
/// SnapshotAggregator.OnGearsetRegistered, which is driven by UIObserver.PollGearsetCount
/// detecting that RaptureGearsetModule.NumGearsets increased. Cleared by
/// OnGearsetRegisteredConsumed (called from AuthoringHost.RecordStep and
/// UIObserver.ResetWindowState) so it does not bleed into the next window.
/// Baseline is NOT reset in ResetWindowState (gearset count persists across recording windows).
/// </summary>
public sealed record GearsetRegisteredSignal(byte OldCount, byte NewCount);

/// <summary>
/// Records that the player interacted with an EventObj (quest sparkle, aether current, door lever,
/// treasure chest, etc.) during this recording window. Set by SnapshotAggregator.OnObjectInteracted,
/// which is driven by UIObserver.PollEventObjInteraction detecting that the player had an EventObj
/// (or Treasure) hard-targeted AND Conditions.OccupiedInEvent transitioned false→true. Cleared by
/// OnObjectInteractedConsumed (called from AuthoringHost.RecordStep and UIObserver.ResetWindowState)
/// so it does not bleed into the next recording window.
///
/// InteractableId is the EventObj's BaseId (matches InteractObjectStep.InteractableId).
/// X, Y, Z are the EventObj's world position at the moment of interaction detection.
/// </summary>
public sealed record ObjectInteractedSignal(uint InteractableId, float X, float Y, float Z);

public sealed record GameStateSnapshot(
    DateTimeOffset CapturedAt,
    ZoneId Zone,
    WorldPosition Position,
    QuestId? ActiveQuest,
    int QuestSequence,
    uint QuestFlags,
    bool QuestAccepted,
    bool QuestCompleted,
    NpcId? LastNpcInteracted,
    WorldPosition? LastNpcPosition,
    string? LastDialoguePrompt,
    string? LastDialogueAnswer,
    uint InventoryHash,
    // WHY: appended last — positional record; inserting mid-record would force churn-only edits
    // across all existing constructor call sites with no semantic benefit.
    AetheryteId? LastAttuned)
{
    // Non-positional: does not affect existing constructor call sites.
    // Full key item map snapshot (authoring mode only — null in production/hash-only mode).
    public IReadOnlyDictionary<uint, int>? KeyItems { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Holds item IDs newly detected in the key items container since the last snapshot.
    public IReadOnlyList<uint>? KeyItemsAdded { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Holds item IDs removed from the key items container since the last snapshot.
    public IReadOnlyList<uint>? KeyItemsRemoved { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The last aethernet shard (sub-aetheryte) the player targeted/interacted with.
    // Used by StepInferenceEngine to detect aethernet travel hops.
    public AetheryteId? LastAethernetShardInteracted { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The aethernet destination shard selected by the player in the aethernet menu.
    // Preferred over LastAethernetShardInteracted for "to" in StepInferenceEngine Rule 4.
    public AethernetId? AethernetDestinationSelected { get; init; }

    // Non-positional: set when an aethernet teleport completes this recording window
    // (TelepotTown menu closed after a selection was made). Cleared by OnAethernetTeleportConsumed
    // in RecordStep so it does not bleed into the next inference window.
    // This is the primary signal for aethernet inference — replaces the fragile snapshot-diff approach.
    public AethernetHop? AethernetTeleportCompleted { get; init; }

    // Non-positional. Set when the player selects an option from SelectIconString/SelectString
    // during an authoring session. Cleared by OnDialogueOptionConsumed after RecordStep.
    // Survives ResetDeltas.
    public int? DialogueOptionSelected { get; init; }

    // Non-positional. The NPC that opened the SelectIconString dialog (e.g., a Lift Attendant),
    // captured from the live TargetManager when SelectIconString first appears.
    // Cleared alongside DialogueOptionSelected by OnDialogueOptionConsumed.
    // WHY: before.LastNpcInteracted is unreliable (may be a shard or prior NPC);
    //      reading TargetManager at the moment the dialog opens gives the correct NPC.
    public QuestForge.Schema.NpcLocation? DialogueNpcSource { get; init; }

    // Non-positional. Set when a quest OTHER than the active quest is accepted during this
    // recording window (e.g. a mandatory sub-quest offered by an NPC mid-sequence).
    // Cleared by ResetDeltas so it is a per-window delta signal.
    public QuestId? ForeignQuestAccepted { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // True when the local player is in combat. Set by OnInCombatChanged.
    public bool InCombat { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Kill-to-nibble correlations accumulated across the current recording window.
    // Key: (VarIndex, NibbleHalf). Null or empty when no correlated kills have been observed.
    public IReadOnlyDictionary<NibbleKey, KillCorrelation>? KillCorrelatedTargets { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Player position captured when the local player entered combat (false→true transition).
    // Null if no combat-start transition was observed in the current session.
    public WorldPosition? CombatStartPosition { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Zone captured when the local player entered combat (false→true transition).
    public int CombatStartZone { get; init; }

    // Non-positional. True when the player confirmed a SelectYesno prompt during this
    // recording window. Distinct from DialogueOptionSelected (SelectIconString list choices)
    // so StepFactory emits {type:"yesno"} rather than a spurious {type:"list"} choice.
    // Cleared alongside DialogueOptionSelected by OnDialogueOptionConsumed.
    public bool SelectYesnoConfirmed { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The six quest work bytes (V0–V5) of the ActiveQuest, captured in Author mode.
    // Null when no variables have been observed (e.g. Inspect mode, or before the first
    // heartbeat poll). Always length 6 when non-null.
    public IReadOnlyList<byte>? QuestVariables { get; init; }

    // Non-positional. Set when a shop was opened and vendor activity was detected
    // during an authoring recording window. Null when no purchase span has occurred.
    // Cleared by ResetDeltas so it is a per-window delta signal.
    public PurchaseDetection? PurchaseDetected { get; init; }

    // Non-positional. Set when a cross-region teleport completes this recording window
    // (Teleport menu closed after a destination was selected). Cleared by OnTeleportConsumed
    // in RecordStep so it does not bleed into the next inference window.
    // Distinct from AethernetTeleportCompleted (which signals an intra-region shard hop).
    public AetheryteId? TeleportCompleted { get; init; }

    // Non-positional. Set when UIObserver.PollPlayerActionEffect observes that the player used a
    // supported game action (Action / GeneralAction / KeyItem) during this recording window.
    // Cleared by OnActionConsumed in RecordStep so it does not bleed into the next window.
    public ActionCompletedSignal? ActionCompleted { get; init; }

    // Non-positional. Set when UIObserver.PollPlayerEmote observes that the player triggered an
    // emote during this recording window (LocalPlayer.EmoteController.EmoteId transitioned to a
    // new non-zero value). Cleared by OnEmoteConsumed in RecordStep so it does not bleed into
    // the next window.
    public EmoteCompletedSignal? EmoteCompleted { get; init; }

    // Non-positional. Set when UIObserver.PollPlayerChatMessage observes that the player typed
    // /say <message> during this recording window (RaptureLogModule observed a new log entry
    // matching ChatLogEntryFilter.IsPlayerSayMessage). Cleared by OnSayChatMessageConsumed
    // in RecordStep so it does not bleed into the next window.
    public SayChatMessageSentSignal? SayChatMessageSent { get; init; }

    // Non-positional. Set when UIObserver.PollPlayerActionEffect observes that the player used an
    // item (ActionType.Item=2 or ActionType.EventItem=3) during this recording window.
    // Cleared by OnItemUsedConsumed (called from AuthoringHost.RecordStep and
    // UIObserver.ResetWindowState) so it does not bleed into the next window.
    public ItemUsedSignal? ItemUsed { get; init; }

    // Non-positional. Set when UIObserver.PollEquipmentChange detects a delta in the 14 equipment
    // slots (InventoryType.EquippedItems) during this recording window. NewItemIds contains the
    // item IDs that appeared in slots where they were absent in the prior snapshot.
    // Cleared by OnEquipmentChangedConsumed (called from AuthoringHost.RecordStep and
    // UIObserver.ResetWindowState) so it does not bleed into the next window.
    // Does NOT clear in ResetDeltas (equipment state persists across recording windows).
    public EquipmentChangedSignal? EquipmentChanged { get; init; }

    // Non-positional. Set when UIObserver.PollJobChange detects that
    // PlayerState.CurrentClassJobId changed during this recording window.
    // Cleared by OnJobChangedConsumed (called from AuthoringHost.RecordStep
    // and UIObserver.ResetWindowState) so it does not bleed into the next window.
    // Baseline is NOT reset in ResetWindowState (job state persists across recording windows).
    public JobChangedSignal? JobChanged { get; init; }

    // Non-positional. Set when UIObserver.PollGearsetCount detects that
    // RaptureGearsetModule.NumGearsets increased during this recording window.
    // Cleared by OnGearsetRegisteredConsumed (called from AuthoringHost.RecordStep
    // and UIObserver.ResetWindowState) so it does not bleed into the next window.
    // Baseline is NOT reset in ResetWindowState (gearset count persists across windows).
    public GearsetRegisteredSignal? GearsetRegistered { get; init; }

    // Non-positional. Set when UIObserver.PollEventObjInteraction detects that the player
    // interacted with an EventObj (or Treasure) during this recording window. Cleared by
    // OnObjectInteractedConsumed (called from AuthoringHost.RecordStep and
    // UIObserver.ResetWindowState) so it does not bleed into the next recording window.
    // Does NOT clear in ResetDeltas — survives per-window.
    public ObjectInteractedSignal? ObjectInteracted { get; init; }
}
