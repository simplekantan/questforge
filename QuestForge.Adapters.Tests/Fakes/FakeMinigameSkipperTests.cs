using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Minigames;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeMinigameSkipperTests
{
    [Fact]
    public async Task Default_IsKindSkippable_ReturnsFalse()
    {
        var skipper = new FakeMinigameSkipper();
        var result = await skipper.IsKindSkippable(MinigameKind.Sniping, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task SetKindSkippable_True_IsKindSkippable_ReturnsTrue()
    {
        var skipper = new FakeMinigameSkipper();
        skipper.SetKindSkippable(MinigameKind.Memory, true);
        var result = await skipper.IsKindSkippable(MinigameKind.Memory, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task Default_SkipMinigame_ReturnsUnsupported_IsRecorded()
    {
        var skipper = new FakeMinigameSkipper();
        var result = await skipper.SkipMinigame(MinigameKind.Rhythm, CancellationToken.None);
        Assert.Equal(SkipOutcome.Unsupported, result.ValueOrThrow);
        Assert.Equal(1, skipper.RecordedSkipAttempts.Count);
    }

    [Fact]
    public async Task ScriptSkipResult_Skipped_ReturnsSkippedForThatKind()
    {
        var skipper = new FakeMinigameSkipper();
        skipper.ScriptSkipResult(MinigameKind.Aiming, SkipOutcome.Skipped);

        var result = await skipper.SkipMinigame(MinigameKind.Aiming, CancellationToken.None);
        Assert.Equal(SkipOutcome.Skipped, result.ValueOrThrow);
    }

    [Fact]
    public async Task ScriptSkipResult_OnlyAppliesToScriptedKind_OtherKindsReturnDefault()
    {
        var skipper = new FakeMinigameSkipper();
        skipper.ScriptSkipResult(MinigameKind.Aiming, SkipOutcome.Skipped);

        var other = await skipper.SkipMinigame(MinigameKind.Sniping, CancellationToken.None);
        Assert.Equal(SkipOutcome.Unsupported, other.ValueOrThrow);
    }
}