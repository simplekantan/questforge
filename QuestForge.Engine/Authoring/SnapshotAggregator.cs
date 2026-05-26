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

    // ── kill-correlation state ─────────────────────────────────────────────
    private bool _inCombat;
    private WorldPosition? _combatStartPosition;
    private int _combatStartZone;

    // Recent kills buffer: (dataId, timestamp) ordered by time ascending.
    private readonly List<(uint DataId, DateTimeOffset At)> _recentKills = new();

    // Recent nibble bumps buffer: (key, nibble value, timestamp), ordered by time ascending.
    private readonly List<(NibbleKey Key, int FinalValue, DateTimeOffset At)> _recentNibbleBumps = new();

    // Per-nibble accumulated correlations for the current recording window.
    private readonly Dictionary<NibbleKey, (HashSet<uint> DataIds, int FinalValue)> _killCorrelatedTargets = new();

    // Previous quest-variable values baseline for delta detection.
    // Survives ResetDeltas so cross-window deltas are computed correctly.
    private IReadOnlyList<byte>? _prevQuestVariables;

    /// <summary>
    /// Correlation window: kills and nibble bumps within this span are bidirectionally correlated.
    /// Must match the offline SnapshotState constant.
    /// </summary>
    public static readonly TimeSpan CombatCorrelationWindow = TimeSpan.FromMilliseconds(500);

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
        CombatStartZone              = _combatStartZone
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

        var now = _clock.UtcNow;
        EvictStale(now);

        var len = variables.Count;
        for (var i = 0; i < len; i++)
        {
            var n = variables[i];
            var p = i < prev.Count ? prev[i] : (byte)0;

            var lowUp  = (n & 0x0F) > (p & 0x0F);
            var highUp = (n >> 4)   > (p >> 4);

            if (lowUp)
            {
                var key = new NibbleKey(i, NibbleHalf.Low);
                var nibbleValue = n & 0x0F;
                _recentNibbleBumps.Add((key, nibbleValue, now));
                CorrelateKillsToBump(key, nibbleValue, now);
            }

            if (highUp)
            {
                var key = new NibbleKey(i, NibbleHalf.High);
                var nibbleValue = n >> 4;
                _recentNibbleBumps.Add((key, nibbleValue, now));
                CorrelateKillsToBump(key, nibbleValue, now);
            }
        }
    }

    /// <summary>
    /// Called when a hostile enemy is detected as killed (tracked-then-gone while in combat).
    /// Pushes the kill into the recent-kills buffer, evicts stale entries, and correlates
    /// bidirectionally against any buffered nibble bumps within the symmetric window.
    /// </summary>
    public void OnEnemyKilled(uint dataId)
    {
        var now = _clock.UtcNow;
        _recentKills.Add((dataId, now));
        EvictStale(now);

        // Correlate-on-arrival: look back at buffered nibble bumps within the symmetric window.
        foreach (var (key, finalValue, bumpAt) in _recentNibbleBumps)
        {
            if (AbsDelta(now, bumpAt) <= CombatCorrelationWindow)
                AddToCorrelation(key, finalValue, dataId);
        }
    }

    /// <summary>
    /// Called when the player's in-combat state changes.
    /// On a false→true transition, captures the current position and zone as the combat-start location.
    /// </summary>
    public void OnInCombatChanged(bool inCombat)
    {
        if (inCombat && !_inCombat)
        {
            _combatStartPosition = _position;
            _combatStartZone = (int)_zone.Value;
        }
        _inCombat = inCombat;
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
        // WHY: clear the per-window kill signals so they do not bleed into the next recording window.
        // _prevQuestVariables is intentionally preserved — the next bump's delta must be computed
        // against the last known values, not from zero. (GWT-L6')
        _recentKills.Clear();
        _recentNibbleBumps.Clear();
        _killCorrelatedTargets.Clear();
    }

    // ── Private kill-correlation helpers ─────────────────────────────────────

    private void EvictStale(DateTimeOffset now)
    {
        var cutoff = now - CombatCorrelationWindow;
        _recentKills.RemoveAll(k => k.At < cutoff);
        _recentNibbleBumps.RemoveAll(b => b.At < cutoff);
    }

    private static TimeSpan AbsDelta(DateTimeOffset a, DateTimeOffset b)
    {
        var d = a - b;
        return d < TimeSpan.Zero ? -d : d;
    }

    private void CorrelateKillsToBump(NibbleKey key, int finalValue, DateTimeOffset bumpAt)
    {
        foreach (var (dataId, killAt) in _recentKills)
        {
            if (AbsDelta(bumpAt, killAt) <= CombatCorrelationWindow)
                AddToCorrelation(key, finalValue, dataId);
        }

        // Ensure the bucket exists even when no kills matched (FinalValue is updated).
        if (!_killCorrelatedTargets.TryGetValue(key, out var bucket))
            _killCorrelatedTargets[key] = (new HashSet<uint>(), finalValue);
        else
            _killCorrelatedTargets[key] = (bucket.DataIds, finalValue);
    }

    private void AddToCorrelation(NibbleKey key, int finalValue, uint dataId)
    {
        if (!_killCorrelatedTargets.TryGetValue(key, out var bucket))
            bucket = (new HashSet<uint>(), finalValue);
        bucket.DataIds.Add(dataId);
        _killCorrelatedTargets[key] = (bucket.DataIds, finalValue);
    }

    private IReadOnlyDictionary<NibbleKey, KillCorrelation>? BuildKillCorrelatedTargets()
    {
        if (_killCorrelatedTargets.Count == 0) return null;
        var result = new Dictionary<NibbleKey, KillCorrelation>(_killCorrelatedTargets.Count);
        foreach (var (key, (dataIds, finalValue)) in _killCorrelatedTargets)
        {
            if (dataIds.Count == 0) continue;
            var sorted = dataIds.OrderBy(id => id).ToList();
            result[key] = new KillCorrelation(sorted, finalValue);
        }
        return result.Count > 0 ? result : null;
    }
}
