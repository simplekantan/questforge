using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

public sealed class SnapshotAggregator
{
    public SnapshotAggregator(QuestId? activeQuest)
    {
        throw new NotImplementedException();
    }

    public GameStateSnapshot Current
    {
        get => throw new NotImplementedException();
    }

    public void OnZoneChanged(ZoneId zone, WorldPosition position)
    {
        throw new NotImplementedException();
    }

    public void OnPlayerMoved(WorldPosition position)
    {
        throw new NotImplementedException();
    }

    public void OnQuestAccepted(QuestId quest)
    {
        throw new NotImplementedException();
    }

    public void OnQuestCompleted(QuestId quest)
    {
        throw new NotImplementedException();
    }

    public void OnQuestSequenceChanged(QuestId quest, int newSequence)
    {
        throw new NotImplementedException();
    }

    public void OnQuestFlagsChanged(QuestId quest, uint newFlags)
    {
        throw new NotImplementedException();
    }

    public void OnInteraction(NpcId npc, WorldPosition npcPosition)
    {
        throw new NotImplementedException();
    }

    public void OnDialogueChoice(string promptSheetRef, string answerSheetRef)
    {
        throw new NotImplementedException();
    }

    public void OnInventoryChanged(uint inventoryHash)
    {
        throw new NotImplementedException();
    }
}
