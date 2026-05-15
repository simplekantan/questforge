using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Minigames;

public sealed class FakeMinigameSkipper : IMinigameSkipper
{
    private readonly Dictionary<MinigameKind, bool> _skippable = new();
    private readonly Dictionary<MinigameKind, SkipOutcome> _skipResults = new();

    public record SkipAttemptCall(MinigameKind Kind, DateTimeOffset At) : AdapterCall(At);
    public CallLog<SkipAttemptCall> RecordedSkipAttempts { get; } = new();

    // ----- Scripting -----
    public void SetKindSkippable(MinigameKind kind, bool skippable) => _skippable[kind] = skippable;
    public void ScriptSkipResult(MinigameKind kind, SkipOutcome outcome) => _skipResults[kind] = outcome;

    // ----- Reset -----
    public void Reset()
    {
        RecordedSkipAttempts.Clear();
        _skippable.Clear();
        _skipResults.Clear();
    }

    // ----- IMinigameSkipper implementation -----

    public Task<Result<bool>> IsKindSkippable(MinigameKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var skippable = _skippable.TryGetValue(kind, out var val) && val;
        return Task.FromResult<Result<bool>>(Result.Ok(skippable));
    }

    public Task<Result<SkipOutcome>> SkipMinigame(MinigameKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedSkipAttempts.Add(new SkipAttemptCall(kind, DateTimeOffset.UtcNow));
        var outcome = _skipResults.TryGetValue(kind, out var scripted)
            ? scripted
            : SkipOutcome.Unsupported;
        return Task.FromResult<Result<SkipOutcome>>(Result.Ok(outcome));
    }
}