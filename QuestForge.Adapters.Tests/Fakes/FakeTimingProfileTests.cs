using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Timing;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeTimingProfileTests
{
    [Fact]
    public void FreshProfile_ReactionDelay_ReturnsZero()
    {
        var profile = new FakeTimingProfile();
        Assert.Equal(TimeSpan.Zero, profile.ReactionDelay(StimulusType.Dialogue));
        Assert.Equal(TimeSpan.Zero, profile.ReactionDelay(StimulusType.Generic));
    }

    [Fact]
    public void FreshProfile_InterActionGap_Returns50Ms()
    {
        var profile = new FakeTimingProfile();
        Assert.Equal(TimeSpan.FromMilliseconds(50), profile.InterActionGap());
    }

    [Fact]
    public void SetDelayFor_Dialogue_ReactionDelay_ReturnsThatDelay()
    {
        var profile = new FakeTimingProfile();
        profile.SetDelayFor(StimulusType.Dialogue, TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), profile.ReactionDelay(StimulusType.Dialogue));
    }

    [Fact]
    public void SetDelayFor_Dialogue_OtherStimuli_StillReturnZero()
    {
        var profile = new FakeTimingProfile();
        profile.SetDelayFor(StimulusType.Dialogue, TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.Zero, profile.ReactionDelay(StimulusType.Generic));
        Assert.Equal(TimeSpan.Zero, profile.ReactionDelay(StimulusType.NewWindow));
    }

    [Fact]
    public void SetDecisionDelay_DecisionDelay_ReturnsSetValue()
    {
        var profile = new FakeTimingProfile();
        profile.SetDecisionDelay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), profile.DecisionDelay(3));
    }

    [Fact]
    public void SetDecisionDelay_DecisionDelay_IgnoresChoiceCount()
    {
        var profile = new FakeTimingProfile();
        profile.SetDecisionDelay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), profile.DecisionDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), profile.DecisionDelay(5));
        Assert.Equal(TimeSpan.FromMilliseconds(100), profile.DecisionDelay(10));
    }

    [Fact]
    public void SetShouldTakeBreak_True_ShouldTakeBreak_ReturnsTrue()
    {
        var profile = new FakeTimingProfile();
        profile.SetShouldTakeBreak(true);
        var ctx = new SessionContext(TimeSpan.FromMinutes(30), 50, TimeSpan.FromMinutes(30));
        Assert.True(profile.ShouldTakeBreak(ctx));
    }

    [Fact]
    public void Reset_RestoresAllDelaysToZeroAndGapTo50Ms()
    {
        var profile = new FakeTimingProfile();
        profile.SetDelayFor(StimulusType.Dialogue, TimeSpan.FromSeconds(1));
        profile.SetDecisionDelay(TimeSpan.FromSeconds(2));
        profile.SetInterActionGap(TimeSpan.FromSeconds(3));
        profile.SetShouldTakeBreak(true);

        profile.Reset();

        Assert.Equal(TimeSpan.Zero, profile.ReactionDelay(StimulusType.Dialogue));
        Assert.Equal(TimeSpan.Zero, profile.DecisionDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(50), profile.InterActionGap());
        var ctx = new SessionContext(TimeSpan.Zero, 0, TimeSpan.Zero);
        Assert.False(profile.ShouldTakeBreak(ctx));
    }
}