namespace QuestForge.Schema;

public class QuestDefinition
{
    public string SchemaVersion { get; init; } = default!;
    public uint Id { get; init; }
    public string Name { get; init; } = default!;
    public string Expansion { get; init; } = default!;   // "arr"|"heavensward"|"stormblood"|"shadowbringers"|"endwalker"|"dawntrail"
    public string Category { get; init; } = default!;    // "msq"|"class"|"job"
    public bool Enabled { get; init; } = true;
    public SupportStatus? SupportStatus { get; init; }   // required — validator checks non-null
    public string? LastVerifiedPatch { get; init; }      // required — validator checks non-null
    public Requirements? Requirements { get; init; }     // required — validator checks non-null
    public NpcLocation? AcceptFrom { get; init; }        // required — validator checks non-null
    public Chain? Chain { get; init; }
    public RewardOverride? RewardOverride { get; init; }
    public string[]? Contributors { get; init; }
    public string? Notes { get; init; }
    public QuestSequence[] Sequences { get; init; } = [];
}

public class SupportStatus
{
    public string Implementation { get; init; } = default!;  // "complete"|"partial"|"none"
    public string[] KnownIssues { get; init; } = [];
    /// <summary>
    /// Computed by the validator from step contents. Authors may set or omit — validator always overwrites.
    /// </summary>
    public bool? MinigameSkippable { get; init; }
}

public class Requirements
{
    public int? MinLevel { get; init; }
    public int? MaxLevel { get; init; }
    public string? RequiredJob { get; init; }
    public string? RequiredStartingClass { get; init; }
    public PrerequisiteRef[] Prereqs { get; init; } = [];
}

public class Chain
{
    public uint[] Previous { get; init; } = [];
    public ChainNext[] Next { get; init; } = [];
}

public class QuestSequence
{
    public int Sequence { get; init; }
    public string? SkipIf { get; init; }
    public Step[] Steps { get; init; } = [];
}