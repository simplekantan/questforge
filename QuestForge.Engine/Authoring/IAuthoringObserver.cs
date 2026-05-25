using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

public interface IAuthoringObserver
{
    void OnZoneChanged(ZoneId zone, WorldPosition position);
    void OnPlayerMoved(WorldPosition position);
    void OnQuestAccepted(QuestId quest);
    void OnQuestCompleted(QuestId quest);
    void OnQuestSequenceChanged(QuestId quest, int newSequence);
    void OnQuestFlagsChanged(QuestId quest, uint newFlags);
    void OnQuestVariablesUpdated(QuestId quest, IReadOnlyList<byte> variables);
    void OnInteraction(NpcId npc, WorldPosition npcPosition);
    void OnDialogueChoice(string promptSheetRef, string answerSheetRef);
    void OnInventoryChanged(uint inventoryHash);
}
