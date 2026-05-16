namespace QuestForge.Engine.Scheduling;

public enum PrerequisiteJoin
{
    All,         // every non-zero PreviousQuest must be complete (Lumina value 0)
    AtLeastOne   // any one non-zero PreviousQuest must be complete (Lumina value 1)
}
