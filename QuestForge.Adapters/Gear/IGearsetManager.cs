using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IGearsetManager
{
    Task<Result<RegisterOutcome>> RegisterGearset(CancellationToken ct);
}

public enum RegisterOutcome
{
    Registered,
    Updated,
    MaxGearsetsReached,
    Failed
}
