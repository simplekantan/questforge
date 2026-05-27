namespace QuestForge.Engine.Dialogue;

public readonly record struct YesNoContext(bool RunActive, YesNoAnswer? AuthoredAnswer);
