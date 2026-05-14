namespace QuestForge.Schema;

public class FragmentDefinition
{
    public string SchemaVersion { get; init; } = default!;
    public string FragmentId { get; init; } = default!;
    public FragmentParameter[] Parameters { get; init; } = [];
    public Step[] Steps { get; init; } = [];
}