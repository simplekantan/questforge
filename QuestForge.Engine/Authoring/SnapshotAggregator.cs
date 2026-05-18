using QuestForge.Adapters.Types;

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
    private IReadOnlyDictionary<uint, int>? _keyItems;

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
        AethernetDestinationSelected = _aethernetDestinationSelected
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
    /// </summary>
    public void ResetDeltas()
    {
        _keyItemsAdded = null;
        _keyItemsRemoved = null;
    }
}
