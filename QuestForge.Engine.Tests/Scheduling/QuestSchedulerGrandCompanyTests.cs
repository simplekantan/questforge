using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Scheduling;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;
using Xunit;

namespace QuestForge.Engine.Tests.Scheduling;

public sealed class QuestSchedulerGrandCompanyTests
{
    private static QuestId Q(uint n) => new(n);

    private static readonly QuestId TwinAdderQuest = Q(66216);
    private static readonly QuestId MaelstromQuest = Q(66217);
    private static readonly QuestId FlamesQuest = Q(66218);

    private static QuestScheduler BuildScheduler(
        FakeQuestState questState,
        FakeGameStateProvider gameState,
        FakeQuestDataProvider questData,
        SchedulerOptions? options = null)
    {
        return new QuestScheduler(
            questState,
            gameState,
            questData,
            options ?? SchedulerOptions.Default,
            NullLogger<QuestScheduler>.Instance);
    }

    private static (FakeQuestState, FakeGameStateProvider, FakeQuestDataProvider) SetupGcQuests()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(TwinAdderQuest, tier: 3, sortKey: 10)
            .WithQuest(MaelstromQuest, tier: 3, sortKey: 11)
            .WithQuest(FlamesQuest, tier: 3, sortKey: 12);

        questState.SetQuestStatus(TwinAdderQuest, QuestStatus.Available);
        questState.SetQuestStatus(MaelstromQuest, QuestStatus.Available);
        questState.SetQuestStatus(FlamesQuest, QuestStatus.Available);

        return (questState, gameState, questData);
    }

    // =========================================================================
    // GC1 — UserDecides halts automation with AwaitingUser
    // =========================================================================
    [Fact]
    public async Task UserDecides_HaltsWithAwaitingUser()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.UserDecides
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.AwaitingUser>(scheduler.CurrentStatus);
        Assert.Contains("Grand Company", status.Reason.Detail);
    }

    // =========================================================================
    // GC2 — TwinAdder preference selects quest 66216
    // =========================================================================
    [Fact]
    public async Task TwinAdder_SelectsQuest66216()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.TwinAdder
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(TwinAdderQuest, result.ValueOrThrow);
    }

    // =========================================================================
    // GC3 — Maelstrom preference selects quest 66217
    // =========================================================================
    [Fact]
    public async Task Maelstrom_SelectsQuest66217()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.Maelstrom
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(MaelstromQuest, result.ValueOrThrow);
    }

    // =========================================================================
    // GC4 — ImmortalFlames preference selects quest 66218
    // =========================================================================
    [Fact]
    public async Task ImmortalFlames_SelectsQuest66218()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.ImmortalFlames
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(FlamesQuest, result.ValueOrThrow);
    }

    // =========================================================================
    // GC5 — Random picks one of the three GC quests
    // =========================================================================
    [Fact]
    public async Task Random_PicksOneOfThreeGcQuests()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.Random
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        var picked = result.ValueOrThrow;
        Assert.NotNull(picked);
        Assert.Contains(picked.Value, new[] { TwinAdderQuest, MaelstromQuest, FlamesQuest });
    }

    // =========================================================================
    // GC6 — Non-GC quests are unaffected by the filter
    // =========================================================================
    [Fact]
    public async Task NonGcQuest_UnaffectedByFilter()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.UserDecides
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // GC7 — UserDecides resumes after player manually accepts a GC quest
    // =========================================================================
    [Fact]
    public async Task UserDecides_ResumesAfterPlayerAcceptsGcQuest()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.UserDecides
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);
        Assert.Null(result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.AwaitingUser>(scheduler.CurrentStatus);

        questState.SetQuestStatus(MaelstromQuest, QuestStatus.Accepted);
        questState.AddAcceptedQuest(MaelstromQuest);

        result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(MaelstromQuest, result.ValueOrThrow);
    }

    // =========================================================================
    // GC8 — GC quests already complete are not affected by the filter
    // =========================================================================
    [Fact]
    public async Task GcQuestsComplete_FilterDoesNotBlock()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(TwinAdderQuest, tier: 3, sortKey: 10)
            .WithQuest(Q(300), tier: 3, sortKey: 20);

        questState.SetQuestStatus(TwinAdderQuest, QuestStatus.Complete);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.Maelstrom
        };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // GC9 — GrandCompanyQuests static map has correct entries
    // =========================================================================
    [Fact]
    public void GrandCompanyQuests_ContainsAllThreeEntries()
    {
        Assert.Equal(3, QuestScheduler.GrandCompanyQuests.Count);
        Assert.Equal(GrandCompanyPreference.TwinAdder, QuestScheduler.GrandCompanyQuests[TwinAdderQuest]);
        Assert.Equal(GrandCompanyPreference.Maelstrom, QuestScheduler.GrandCompanyQuests[MaelstromQuest]);
        Assert.Equal(GrandCompanyPreference.ImmortalFlames, QuestScheduler.GrandCompanyQuests[FlamesQuest]);
    }

    // =========================================================================
    // GC10 — Random is stable within a single evaluation
    // =========================================================================
    [Fact]
    public async Task Random_StableWithinSingleEvaluation()
    {
        var (questState, gameState, questData) = SetupGcQuests();
        var options = SchedulerOptions.Default with
        {
            GrandCompanyChoice = GrandCompanyPreference.Random
        };

        var picked = new HashSet<QuestId>();
        for (var i = 0; i < 50; i++)
        {
            var scheduler = BuildScheduler(questState, gameState, questData, options);
            var result = await scheduler.NextQuestToRun(CancellationToken.None);
            Assert.NotNull(result.ValueOrThrow);
            picked.Add(result.ValueOrThrow.Value);
        }

        Assert.True(picked.Count >= 1, "Random should pick at least one GC quest");
        Assert.All(picked, q => Assert.Contains(q, new[] { TwinAdderQuest, MaelstromQuest, FlamesQuest }));
    }
}
