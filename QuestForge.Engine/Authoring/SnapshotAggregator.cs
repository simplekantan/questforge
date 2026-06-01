using QuestForge.Adapters.Types;
using System.Linq;

namespace QuestForge.Engine.Authoring;

/// <remarks>Not thread-safe. Expected to be invoked from the Dalamud framework thread only.</remarks>
public sealed class SnapshotAggregator
{
    private readonly QuestId? _activeQuest;
    private readonly IClock _clock;
    private ZoneId _zone = new(0);
    private WorldPosition _position = new(0, 0, 0);
    private int _questSequence;
    private uint _questFlags;
    private bool _questAccepted;
    private bool _questCompleted;
    private NpcId? _lastNpcInteracted;
    private WorldPosition? _lastNpcPosition;
    private string? _lastDialoguePrompt;
    private string? _lastDialogueAnswer;
    private uint _inventoryHash;
    private AetheryteId? _lastAttuned;
    private IReadOnlyList<uint>? _keyItemsAdded;
    private IReadOnlyList<uint>? _keyItemsRemoved;
    private AetheryteId? _lastAethernetShardInteracted;
    private AethernetId? _aethernetDestinationSelected;
    private AethernetHop? _aethernetTeleportCompleted;
    private IReadOnlyDictionary<uint, int>? _keyItems;
    private int? _dialogueOptionSelected;
    private QuestForge.Schema.NpcLocation? _dialogueNpcSource;
    private QuestId? _foreignQuestAccepted;
    private bool _selectYesnoConfirmed;
    private IReadOnlyList<byte>? _questVariables;

    private AetheryteId? _teleportCompleted;
    private ActionCompletedSignal? _actionCompleted;
    private EmoteCompletedSignal? _emoteCompleted;
    private SayChatMessageSentSignal? _sayChatMessageSent;
    private ItemUsedSignal? _itemUsed;
    private EquipmentChangedSignal? _equipmentChanged;
    private JobChangedSignal? _jobChanged;
    private GearsetRegisteredSignal? _gearsetRegistered;
    private ObjectInteractedSignal? _objectInteracted;

    // ── purchase span state ────────────────────────────────────────────────
    private bool _shopOpen;
    private bool _purchaseSpanStarted;
    private long? _purchaseBaselineGil;
    private int? _purchaseBaselineSeals;
    private long _currentGil;
    private int _currentSeals;
    private readonly Dictionary<uint, int> _spanItemDeltas = new();

    // ── purchase span state (additive: GC axes) ────────────────────────────
    private int? _spanActiveGcCategory;   // last non-null value observed while span is open (§14.9.2)
    private int? _spanActiveGcRankTier;   // same lifecycle as _spanActiveGcCategory

    // ── combat span state ──────────────────────────────────────────────────
    private bool _inCombat;
    private WorldPosition? _combatStartPosition;
    private int _combatStartZone;

    // Tracks the most-recently targeted BattleNpc regardless of combat state.
    // Persists across ResetDeltas so a pre-combat target is still available to seed the
    // next span even if ResetDeltas runs between targeting and InCombatChanged(true).
    private uint? _lastBattleNpcTarget;

    // Distinct hostile targets acquired while _inCombat. Cleared on each false→true InCombat transition.
    private readonly HashSet<uint> _spanBattleNpcTargets = new();

    // Nibble bumps that occurred while _inCombat. Key → latest (highest) value reached. Cleared on each false→true InCombat transition.
    private readonly Dictionary<NibbleKey, int> _spanNibbleBumps = new();

    // Previous quest-variable values baseline for delta detection.
    // Survives ResetDeltas so cross-window deltas are computed correctly.
    private IReadOnlyList<byte>? _prevQuestVariables;

    // clock defaults to SystemClock so production callers need only pass activeQuest;
    // tests inject FakeClock for deterministic CapturedAt values.
    public SnapshotAggregator(QuestId? activeQuest, IClock? clock = null)
    {
        _activeQuest = activeQuest;
        _clock = clock ?? SystemClock.Instance;
    }

    public GameStateSnapshot Current => new(
        CapturedAt: _clock.UtcNow,
        Zone: _zone,
        Position: _position,
        ActiveQuest: _activeQuest,
        QuestSequence: _questSequence,
        QuestFlags: _questFlags,
        QuestAccepted: _questAccepted,
        QuestCompleted: _questCompleted,
        LastNpcInteracted: _lastNpcInteracted,
        LastNpcPosition: _lastNpcPosition,
        LastDialoguePrompt: _lastDialoguePrompt,
        LastDialogueAnswer: _lastDialogueAnswer,
        InventoryHash: _inventoryHash,
        LastAttuned: _lastAttuned)
    {
        KeyItems = _keyItems,
        KeyItemsAdded = _keyItemsAdded,
        KeyItemsRemoved = _keyItemsRemoved,
        LastAethernetShardInteracted = _lastAethernetShardInteracted,
        AethernetDestinationSelected = _aethernetDestinationSelected,
        AethernetTeleportCompleted   = _aethernetTeleportCompleted,
        DialogueOptionSelected       = _dialogueOptionSelected,
        DialogueNpcSource            = _dialogueNpcSource,
        ForeignQuestAccepted         = _foreignQuestAccepted,
        SelectYesnoConfirmed         = _selectYesnoConfirmed,
        QuestVariables               = _questVariables,
        InCombat                     = _inCombat,
        KillCorrelatedTargets        = BuildKillCorrelatedTargets(),
        CombatStartPosition          = _combatStartPosition,
        CombatStartZone              = _combatStartZone,
        PurchaseDetected             = BuildPurchaseDetected(),
        TeleportCompleted            = _teleportCompleted,
        ActionCompleted              = _actionCompleted,
        EmoteCompleted               = _emoteCompleted,
        SayChatMessageSent           = _sayChatMessageSent,
        ItemUsed                     = _itemUsed,
        EquipmentChanged             = _equipmentChanged,
        JobChanged                   = _jobChanged,
        GearsetRegistered            = _gearsetRegistered,
        ObjectInteracted             = _objectInteracted
    };

    public void OnZoneChanged(ZoneId zone, WorldPosition position)
    {
        _zone = zone;
        _position = position;
    }

    public void OnPlayerMoved(WorldPosition position)
    {
        _position = position;
    }

    public void OnQuestAccepted(QuestId quest)
    {
        if (_activeQuest == quest || _activeQuest is null)
            _questAccepted = true;
        else
            _foreignQuestAccepted = quest;
    }

    public void OnQuestCompleted(QuestId quest)
    {
        if (_activeQuest == quest || _activeQuest is null)
            _questCompleted = true;
    }

    public void OnQuestSequenceChanged(QuestId quest, int newSequence)
    {
        if (_activeQuest == quest)
            _questSequence = newSequence;
    }

    public void OnQuestFlagsChanged(QuestId quest, uint newFlags)
    {
        if (_activeQuest == quest)
            _questFlags = newFlags;
    }

    /// <summary>
    /// Called when the active quest's work bytes (V0–V5) are observed.
    /// Stores the latest values; ignored when the quest does not match the authored quest.
    /// Computes per-nibble deltas vs the baseline and correlates bidirectionally.
    /// The FIRST observation (no prior baseline) only establishes the baseline: no nibble
    /// deltas are computed, no bumps are buffered, and no correlation occurs. In production
    /// UIObserver polls quest variables every heartbeat from author-mode start, so the first
    /// observation is always the pre-fight state; an objective bump cannot land on it.
    /// </summary>
    public void OnQuestVariablesUpdated(QuestId quest, IReadOnlyList<byte> variables)
    {
        if (_activeQuest != quest) return;

        // First observation: establish the baseline only. No delta, no bump, no correlation.
        // WHY: the first poll is always the pre-fight state in production, so any non-zero
        // value here is resumed-quest progress, not an objective bump — it must not correlate.
        if (_prevQuestVariables is null)
        {
            _questVariables = variables;
            _prevQuestVariables = variables;
            return;
        }

        var prev = _prevQuestVariables;
        _questVariables = variables;
        _prevQuestVariables = variables;

        var len = variables.Count;
        for (var i = 0; i < len; i++)
        {
            var n = variables[i];
            var p = i < prev.Count ? prev[i] : (byte)0;

            var lowUp  = (n & 0x0F) > (p & 0x0F);
            var highUp = (n >> 4)   > (p >> 4);

            if (lowUp && _inCombat)
            {
                var key = new NibbleKey(i, NibbleHalf.Low);
                _spanNibbleBumps[key] = n & 0x0F;
            }

            if (highUp && _inCombat)
            {
                var key = new NibbleKey(i, NibbleHalf.High);
                _spanNibbleBumps[key] = n >> 4;
            }
        }
    }

    /// <summary>
    /// Called when the player targets a BattleNpc (ObjectKind == BattleNpc).
    /// Always updates <c>_lastBattleNpcTarget</c> so a pre-combat target can be seeded
    /// into the span when InCombatChanged(true) fires. Also adds to the span directly
    /// when already in combat (the in-combat target-swap case).
    /// </summary>
    public void OnBattleNpcTargeted(uint dataId)
    {
        _lastBattleNpcTarget = dataId;
        if (_inCombat)
            _spanBattleNpcTargets.Add(dataId);
    }

    /// <summary>
    /// Called when the player's in-combat state changes.
    /// On a false→true transition, captures the current position and zone as the combat-start
    /// location, clears the span sets to begin a fresh span, then seeds the span with
    /// <c>_lastBattleNpcTarget</c> (the mob targeted right before engaging — the real observer
    /// deduplicates so it will not re-forward the same target once combat starts).
    /// On true→false, the span is retained so the record taken after the fight still sees it.
    /// </summary>
    public void OnInCombatChanged(bool inCombat)
    {
        if (inCombat && !_inCombat)
        {
            _combatStartPosition = _position;
            _combatStartZone = (int)_zone.Value;
            _spanBattleNpcTargets.Clear();
            _spanNibbleBumps.Clear();
            if (_lastBattleNpcTarget is { } t)
                _spanBattleNpcTargets.Add(t);
        }
        _inCombat = inCombat;
    }

    /// <summary>
    /// Called when the player's currency balances are observed.
    /// Always updates the current balances. If a purchase span is active (or not yet started),
    /// the values are available as baselines when <see cref="OnShopOpened"/> fires.
    /// </summary>
    public void OnCurrencyChanged(long gil, int seals)
    {
        _currentGil   = gil;
        _currentSeals = seals;
    }

    /// <summary>
    /// Called when the shop-open state transitions.
    /// On false→true: captures the current balance as baseline and starts a new span.
    /// On true→false: the span is retained (mirror of combat span retention).
    /// G5: optional axes forwarded on the transition so the span baselines with the initial tab state.
    /// </summary>
    public void OnShopOpened(bool open, int? activeGcCategory = null, int? activeGcRankTier = null)
    {
        if (open && !_shopOpen)
        {
            _purchaseSpanStarted    = true;
            _purchaseBaselineGil    = _currentGil;
            _purchaseBaselineSeals  = _currentSeals;
            _spanItemDeltas.Clear();
            // New span: clear GC axes before applying the transition's axes (§14.9.2 lifecycle).
            _spanActiveGcCategory = null;
            _spanActiveGcRankTier = null;
            // Apply "last seen non-null wins" from the transition's axes.
            if (activeGcCategory is not null) _spanActiveGcCategory = activeGcCategory;
            if (activeGcRankTier is not null) _spanActiveGcRankTier = activeGcRankTier;
        }
        _shopOpen = open;
    }

    /// <summary>
    /// G5 (§14.9.2): Called every heartbeat while the vendor probe is active.
    /// Forwards mid-span GC axis changes to the aggregator WITHOUT emitting a new trace event.
    /// No-op when no purchase span is active (mirrors OnVendorItemCountChanged's guard).
    /// </summary>
    public void OnActiveGcAxesObserved(int? activeGcCategory, int? activeGcRankTier)
    {
        if (!_shopOpen) return;
        if (activeGcCategory is not null) _spanActiveGcCategory = activeGcCategory;
        if (activeGcRankTier is not null) _spanActiveGcRankTier = activeGcRankTier;
    }

    /// <summary>
    /// Called when an item count change is observed in the vendor window.
    /// Accumulates deltas while a purchase span is active (open or retained-after-close).
    /// </summary>
    public void OnVendorItemCountChanged(uint itemId, int count)
    {
        if (!_purchaseSpanStarted) return;
        if (_spanItemDeltas.TryGetValue(itemId, out var existing))
            _spanItemDeltas[itemId] = existing + count;
        else
            _spanItemDeltas[itemId] = count;
    }

    public void OnInteraction(NpcId npc, WorldPosition npcPosition)
    {
        _lastNpcInteracted = npc;
        _lastNpcPosition = npcPosition;
    }

    public void OnDialogueChoice(string promptSheetRef, string answerSheetRef)
    {
        _lastDialoguePrompt = promptSheetRef;
        _lastDialogueAnswer = answerSheetRef;
    }

    public void OnInventoryChanged(uint inventoryHash)
    {
        _inventoryHash = inventoryHash;
    }

    /// <summary>
    /// Called when a full key item snapshot is available (authoring mode).
    /// Stores the full map for diff-based inference and the pre-computed hash.
    /// Does NOT affect delta lists (KeyItemsAdded / KeyItemsRemoved).
    /// </summary>
    public void OnKeyItemsSnapshot(IReadOnlyDictionary<uint, int> items, uint hash)
    {
        _keyItems = items.Count > 0 ? items : null;
        _inventoryHash = hash;
    }

    /// <summary>
    /// Called in production mode when only the hash is available (no full map).
    /// Updates InventoryHash without touching KeyItems.
    /// </summary>
    public void OnInventoryHashChanged(uint hash)
    {
        _inventoryHash = hash;
    }

    /// <summary>
    /// Called when an aetheryte or aethernet shard attunement state change is observed.
    /// Tracks the most recently observed attunement event (matches LastNpcInteracted semantics).
    /// </summary>
    public void OnAttunementChanged(AetheryteId aetheryte)
    {
        _lastAttuned = aetheryte;
    }

    /// <summary>
    /// Called when the player targets an aethernet shard (sub-aetheryte object).
    /// Used to correlate with a subsequent zone change to infer an aethernet travel step.
    /// NOT cleared by ResetDeltas — persists across the before/after inference window.
    /// </summary>
    public void OnAethernetShardTargeted(AetheryteId shardId)
    {
        _lastAethernetShardInteracted = shardId;
    }

    /// <summary>
    /// Called when the player selects a destination shard in the aethernet menu.
    /// Preferred over LastAethernetShardInteracted for the "to" field in aethernet step inference.
    /// NOT cleared by ResetDeltas — only cleared by OnAethernetMenuClosed.
    /// </summary>
    public void OnAethernetDestinationSelected(AethernetId destination)
        => _aethernetDestinationSelected = destination;

    /// <summary>
    /// Called when the aethernet menu is closed without a selection (or after teleport completes).
    /// Clears AethernetDestinationSelected so it does not bleed into subsequent inference windows.
    /// </summary>
    public void OnAethernetMenuClosed()
        => _aethernetDestinationSelected = null;

    /// <summary>
    /// Called by AuthoringHost when the TelepotTown menu closes after a selection was made.
    /// Signals that an aethernet teleport completed this recording window.
    /// NOT cleared by ResetDeltas — only cleared by OnAethernetTeleportConsumed after RecordStep.
    /// </summary>
    public void OnAethernetTeleportCompleted(AetheryteId? from, AethernetId to)
    {
        _aethernetTeleportCompleted = new AethernetHop(from, to);
        // WHY: LastAethernetShardInteracted is only updated when the player explicitly targets a shard.
        // After an aethernet teleport the player may not target the destination shard before clicking
        // Record. Updating here ensures Rule 2.5 / Rule 2.7 can see the shard changed even when the
        // player just arrives and waits without re-targeting.
        _lastAethernetShardInteracted = new AetheryteId(to.Value);
    }

    /// <summary>
    /// Called at the end of RecordStep to consume the completed teleport event so it does not
    /// bleed into the next recording window.
    /// </summary>
    public void OnAethernetTeleportConsumed()
        => _aethernetTeleportCompleted = null;

    /// <summary>
    /// Called when the player selects an option from a SelectIconString or SelectString menu.
    /// NOT cleared by ResetDeltas — only cleared by OnDialogueOptionConsumed after RecordStep.
    /// </summary>
    public void OnDialogueOptionSelected(int index) => _dialogueOptionSelected = index;

    /// <summary>
    /// Called when SelectIconString first opens, capturing the NPC that triggered it
    /// (e.g., a Lift Attendant). Read from live TargetManager so no pre-targeting is required.
    /// NOT cleared by ResetDeltas — cleared alongside DialogueOptionSelected by OnDialogueOptionConsumed.
    /// </summary>
    public void OnDialogueNpcCaptured(QuestForge.Schema.NpcLocation npc) => _dialogueNpcSource = npc;

    /// <summary>
    /// Called at the end of RecordStep to consume the dialogue option and captured NPC so they
    /// do not bleed into the next recording window.
    /// </summary>
    public void OnSelectYesnoConfirmed() => _selectYesnoConfirmed = true;

    public void OnDialogueOptionConsumed()
    {
        _dialogueOptionSelected = null;
        _dialogueNpcSource = null;
        _selectYesnoConfirmed = false;
    }

    /// <summary>
    /// Called when the key items container changes. <paramref name="added"/> contains item IDs
    /// newly detected since the last observation. An empty list clears the field.
    /// </summary>
    public void OnKeyItemsChanged(IReadOnlyList<uint> added)
    {
        _keyItemsAdded = added.Count > 0 ? added : null;
    }

    /// <summary>
    /// Called when key items are removed from the key items container.
    /// <paramref name="removed"/> contains item IDs removed since the last observation.
    /// An empty list clears the field.
    /// </summary>
    public void OnKeyItemsRemoved(IReadOnlyList<uint> removed)
    {
        _keyItemsRemoved = removed.Count > 0 ? removed : null;
    }

    /// <summary>
    /// Clears per-action delta signals (KeyItemsAdded, KeyItemsRemoved) so the next
    /// "before" snapshot starts clean. Call this when capturing the before snapshot
    /// at the start of each Record cycle to prevent stale deltas from bleeding into
    /// subsequent inference windows.
    /// Also clears the kill-correlation buffer and accumulated correlations, but PRESERVES
    /// _prevQuestVariables so the next bump's delta is computed against the correct baseline.
    /// </summary>
    public void ResetDeltas()
    {
        _keyItemsAdded = null;
        _keyItemsRemoved = null;
        _foreignQuestAccepted = null;
        // WHY: _lastAethernetShardInteracted persists across windows (never auto-cleared). If left
        // set from a previous aethernet step it makes isAethernet=true in the new before-snapshot,
        // causing the isAethernet fallback in Rule 4 to fire when it shouldn't (e.g. after aethernet
        // travel → Lift Attendant: the aethernet step "resurfaces" in the next recording window).
        // Clearing here ensures isAethernet only considers shards targeted within this window.
        _lastAethernetShardInteracted = null;
        // WHY: clear the per-span target/nibble sets so they do not bleed into the next recording window.
        // _prevQuestVariables is intentionally preserved — the next bump's delta must be computed
        // against the last known values, not from zero. (GWT-T7)
        _spanBattleNpcTargets.Clear();
        _spanNibbleBumps.Clear();
        // Clear purchase span so Current.PurchaseDetected is null for the next window.
        _spanItemDeltas.Clear();
        _purchaseSpanStarted    = false;
        _purchaseBaselineGil    = null;
        _purchaseBaselineSeals  = null;
        _spanActiveGcCategory   = null;    // G5 (§14.9.2)
        _spanActiveGcRankTier   = null;    // G5 (§14.9.2)

        // Surfaced by G6 in-game smoke (2026-05-28): if the shop addon is currently open at the
        // moment ResetDeltas runs (e.g. the author clicked "Open" on the Record Step modal while a
        // vendor window was already up), restart the span at the current balances. Otherwise the
        // next UIObserver poll sees no transition (open is unchanged), no OnShopOpened fires, and
        // every subsequent OnVendorItemCountChanged hits the `!_purchaseSpanStarted` early-return —
        // silently disabling purchase detection for that recording window. _shopOpen reflects the
        // addon's true state, not the span lifecycle, so it is NOT forced to false here.
        if (_shopOpen)
        {
            _purchaseSpanStarted   = true;
            _purchaseBaselineGil   = _currentGil;
            _purchaseBaselineSeals = _currentSeals;
            // GC axes are left null; the next PollVendor heartbeat re-fills them via
            // OnActiveGcAxesObserved per the "last-seen non-null wins" rule.
        }
    }

    /// <summary>
    /// Called by UIObserver.PollTeleportDestination when the Teleport menu closes after a
    /// destination was selected. Records the destination aetheryte for inference. Survives
    /// ResetDeltas; cleared by OnTeleportConsumed (called from AuthoringHost.RecordStep).
    /// Does NOT update LastAttuned or LastAethernetShardInteracted (Decision I11 / I2).
    /// </summary>
    public void OnTeleportCompleted(AetheryteId destination) => _teleportCompleted = destination;

    /// <summary>
    /// Called at the end of RecordStep to consume the completed teleport so it does not bleed
    /// into the next recording window. Mirrors OnAethernetTeleportConsumed exactly.
    /// </summary>
    public void OnTeleportConsumed() => _teleportCompleted = null;

    /// <summary>
    /// Called by UIObserver.PollPlayerActionEffect when a new ResponseGlobalSequence is observed.
    /// Records the action's type, id, and optional target. Survives ResetDeltas; cleared by
    /// OnActionConsumed (called from AuthoringHost.RecordStep).
    /// Does NOT update LastNpcInteracted, LastAttuned, or any other unrelated field (Decision UAI3).
    /// </summary>
    public void OnActionCompleted(
        QuestForge.Schema.ActionType actionType,
        uint actionId,
        uint? targetBaseId)
        => _actionCompleted = new ActionCompletedSignal(actionType, actionId, targetBaseId);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to
    /// consume the action-completed signal so it does not bleed into the next recording window.
    /// Mirrors OnTeleportConsumed exactly.
    /// </summary>
    public void OnActionConsumed() => _actionCompleted = null;

    /// <summary>
    /// Called by UIObserver.PollPlayerEmote when a new non-zero EmoteId is observed on the local
    /// player (the player just started an emote this frame). Records the emote id and optional target
    /// (from TargetManager.Target at the time of the read). Survives ResetDeltas; cleared by
    /// OnEmoteConsumed (called from AuthoringHost.RecordStep).
    /// Does NOT update LastNpcInteracted or any other unrelated field (Decision UEI3).
    /// </summary>
    public void OnEmoteCompleted(uint emoteId, uint? targetBaseId)
        => _emoteCompleted = new EmoteCompletedSignal(emoteId, targetBaseId);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to
    /// consume the emote-completed signal so it does not bleed into the next recording window.
    /// Mirrors OnActionConsumed exactly.
    /// </summary>
    public void OnEmoteConsumed() => _emoteCompleted = null;

    /// <summary>
    /// Called by UIObserver.PollPlayerChatMessage when a new chat-log entry matching
    /// ChatLogEntryFilter.IsPlayerSayMessage is observed. Records the message text and optional
    /// target (from TargetManager.Target at the time of the read). Survives ResetDeltas;
    /// cleared by OnSayChatMessageConsumed (called from AuthoringHost.RecordStep).
    /// Last-write-wins on multiple calls within the same window.
    /// Does NOT update LastNpcInteracted or any other unrelated field (Decision SCI3).
    /// </summary>
    public void OnSayChatMessageSent(string message, uint? targetBaseId)
        => _sayChatMessageSent = new SayChatMessageSentSignal(message, targetBaseId);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to
    /// consume the say-chat-message signal so it does not bleed into the next recording window.
    /// Mirrors OnEmoteConsumed exactly.
    /// </summary>
    public void OnSayChatMessageConsumed() => _sayChatMessageSent = null;

    /// <summary>
    /// Called by UIObserver.PollPlayerActionEffect when a new ResponseGlobalSequence is observed
    /// AND the ResponseActionType is Item (2) or EventItem (3). Records the item kind, id,
    /// optional target, and optional ground-target position. Survives ResetDeltas; cleared by
    /// OnItemUsedConsumed. Does NOT update LastNpcInteracted or any other unrelated field.
    /// </summary>
    public void OnItemUsed(
        QuestForge.Schema.ItemKind kind,
        uint itemId,
        uint? targetBaseId,
        QuestForge.Schema.Position3? targetPosition)
        => _itemUsed = new ItemUsedSignal(kind, itemId, targetBaseId, targetPosition);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
    /// the item-used signal so it does not bleed into the next recording window.
    /// Mirrors OnActionConsumed exactly.
    /// </summary>
    public void OnItemUsedConsumed() => _itemUsed = null;

    /// <summary>
    /// Called by UIObserver.PollEquipmentChange when a delta is detected in the 14 equipment slots
    /// (InventoryType.EquippedItems). Records the set of item IDs that appeared in slots where they
    /// were absent in the prior snapshot. Survives ResetDeltas; cleared by OnEquipmentChangedConsumed
    /// (called from AuthoringHost.RecordStep and UIObserver.ResetWindowState).
    /// Does NOT clear in ResetDeltas — equipment state persists across recording windows.
    /// </summary>
    public void OnEquipmentChanged(IReadOnlyList<uint> newItemIds)
        => _equipmentChanged = new EquipmentChangedSignal(newItemIds);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
    /// the equipment-changed signal so it does not bleed into the next recording window.
    /// Mirrors OnItemUsedConsumed exactly.
    /// </summary>
    public void OnEquipmentChangedConsumed() => _equipmentChanged = null;

    /// <summary>
    /// Called by UIObserver.PollJobChange when a ClassJob change is detected.
    /// Survives ResetDeltas; cleared by OnJobChangedConsumed (called from
    /// AuthoringHost.RecordStep and UIObserver.ResetWindowState).
    /// Does NOT clear in ResetDeltas — job state persists across recording windows.
    /// </summary>
    public void OnJobChanged(uint oldJobId, uint newJobId)
        => _jobChanged = new JobChangedSignal(oldJobId, newJobId);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
    /// the job-changed signal so it does not bleed into the next recording window.
    /// </summary>
    public void OnJobChangedConsumed() => _jobChanged = null;

    /// <summary>
    /// Called by UIObserver.PollGearsetCount when RaptureGearsetModule.NumGearsets increases.
    /// Survives ResetDeltas; cleared by OnGearsetRegisteredConsumed (called from
    /// AuthoringHost.RecordStep and UIObserver.ResetWindowState).
    /// Does NOT clear in ResetDeltas — gearset count persists across recording windows.
    /// </summary>
    public void OnGearsetRegistered(byte oldCount, byte newCount)
        => _gearsetRegistered = new GearsetRegisteredSignal(oldCount, newCount);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
    /// the gearset-registered signal so it does not bleed into the next recording window.
    /// </summary>
    public void OnGearsetRegisteredConsumed() => _gearsetRegistered = null;

    /// <summary>
    /// Called by UIObserver.PollEventObjInteraction when the player interacts with an EventObj
    /// (or Treasure). Records the EventObj's BaseId and world position. Survives ResetDeltas;
    /// cleared by OnObjectInteractedConsumed (called from AuthoringHost.RecordStep and
    /// UIObserver.ResetWindowState). Last-write-wins on multiple calls within the same window.
    /// </summary>
    public void OnObjectInteracted(uint interactableId, float x, float y, float z)
        => _objectInteracted = new ObjectInteractedSignal(interactableId, x, y, z);

    /// <summary>
    /// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
    /// the object-interacted signal so it does not bleed into the next recording window.
    /// </summary>
    public void OnObjectInteractedConsumed() => _objectInteracted = null;

    // ── Private span-correlation helper ──────────────────────────────────────

    private PurchaseDetection? BuildPurchaseDetected()
    {
        if (!_purchaseSpanStarted) return null;
        var baselineGil   = _purchaseBaselineGil   ?? _currentGil;
        var baselineSeals = _purchaseBaselineSeals  ?? _currentSeals;
        return new PurchaseDetection(
            ShopWasOpen:        true,
            ItemDeltas:         new Dictionary<uint, int>(_spanItemDeltas),
            GilDropped:         Math.Max(0L, baselineGil - _currentGil),
            SealsDropped:       Math.Max(0, baselineSeals - _currentSeals),
            ActiveGcCategory:   _spanActiveGcCategory,    // G5 (§14.9.2) — null until Builder wires setters
            ActiveGcRankTier:   _spanActiveGcRankTier);   // G5 (§14.9.2)
    }

    private IReadOnlyDictionary<NibbleKey, KillCorrelation>? BuildKillCorrelatedTargets()
    {
        if (_spanNibbleBumps.Count == 0 || _spanBattleNpcTargets.Count == 0) return null;
        var dataIds = _spanBattleNpcTargets.OrderBy(id => id).ToList();
        var result = new Dictionary<NibbleKey, KillCorrelation>(_spanNibbleBumps.Count);
        foreach (var (key, value) in _spanNibbleBumps)
            result[key] = new KillCorrelation(dataIds, value);
        return result.Count > 0 ? result : null;
    }
}
