namespace QuestForge.Engine.Dialogue;

public static class SelectYesnoDecider
{
    public static YesNoAnswer? Decide(YesNoContext ctx)
        => ctx.RunActive ? (ctx.AuthoredAnswer ?? YesNoAnswer.Yes) : null;
}
