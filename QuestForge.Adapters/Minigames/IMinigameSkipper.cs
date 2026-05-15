using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Minigames;

public interface IMinigameSkipper
{
    Task<Result<bool>> IsKindSkippable(MinigameKind kind, CancellationToken ct);
    Task<Result<SkipOutcome>> SkipMinigame(MinigameKind kind, CancellationToken ct);
}

public enum MinigameKind
{
    Sniping,
    Memory,
    Aiming,
    Rhythm,
    Selection,
    Other
}

public enum SkipOutcome { Skipped, Unsupported, Failed }