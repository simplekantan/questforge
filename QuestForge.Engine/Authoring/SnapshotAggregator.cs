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
        InventoryHash: _inventoryHash);

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
}
