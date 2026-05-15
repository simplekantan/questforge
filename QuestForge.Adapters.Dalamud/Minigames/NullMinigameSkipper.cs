using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Minigames;

// Returns Unsupported for every minigame kind in v1.
public sealed class NullMinigameSkipper : IMinigameSkipper
{

    public Task<Result<bool>> IsKindSkippable(MinigameKind kind, CancellationToken ct)
        => Task.FromResult<Result<bool>>(new Result<bool>.Success(false));

    public Task<Result<SkipOutcome>> SkipMinigame(MinigameKind kind, CancellationToken ct)
        => Task.FromResult<Result<SkipOutcome>>(
            new Result<SkipOutcome>.Failure(
                "unsupported",
                "NullMinigameSkipper does not support any minigame kind"));
}