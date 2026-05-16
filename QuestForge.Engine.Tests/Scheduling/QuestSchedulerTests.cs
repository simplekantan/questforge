using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Scheduling;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;
using Xunit;

namespace QuestForge.Engine.Tests.Scheduling;

public sealed class QuestSchedulerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static QuestId Q(uint n) => new(n);
    private static JobId J(uint n) => new(n);

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

    private static QuestUnlockReason LevelTooLowReason(int requiredLevel) =>
        new(LevelTooLow: true, RequiredLevel: requiredLevel,
            PrerequisiteIncomplete: false, MissingPrereqs: [],
            WrongJob: false, RequiredJob: null,
            AlreadyCompleted: false,
            OtherReason: false, Detail: null);

    private static QuestUnlockReason PrereqIncompleteReason(params QuestId[] missing) =>
        new(LevelTooLow: false, RequiredLevel: 0,
            PrerequisiteIncomplete: true, MissingPrereqs: missing,
            WrongJob: false, RequiredJob: null,
            AlreadyCompleted: false,
            OtherReason: false, Detail: null);

    private static QuestUnlockReason WrongJobReason() =>
        new(LevelTooLow: false, RequiredLevel: 0,
            PrerequisiteIncomplete: false, MissingPrereqs: [],
            WrongJob: true, RequiredJob: null,
            AlreadyCompleted: false,
            OtherReason: false, Detail: null);

    private static QuestUnlockReason AlreadyCompletedReason() =>
        new(LevelTooLow: false, RequiredLevel: 0,
            PrerequisiteIncomplete: false, MissingPrereqs: [],
            WrongJob: false, RequiredJob: null,
            AlreadyCompleted: true,
            OtherReason: false, Detail: null);

    // =========================================================================
    // Scenario 1 — BasicTierSelection_NoTier0OrTier1_PicksTier3
    // =========================================================================
    [Fact]
    public async Task BasicTierSelection_NoTier0OrTier1_PicksTier3()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0)
            .WithQuest(Q(101), tier: 3, sortKey: 1)
            .WithQuest(Q(102), tier: 3, sortKey: 2);

        questState.SetQuestStatus(Q(100), QuestStatus.Complete);
        questState.SetQuestStatus(Q(101), QuestStatus.Available);
        questState.SetWhyUnavailable(Q(102), PrereqIncompleteReason(Q(101)));

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(101), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(101), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 2 — Tier0Blocker_StopsAllAutomation
    // =========================================================================
    [Fact]
    public async Task Tier0Blocker_StopsAllAutomation()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2))
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(200), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);
        questState.SetWhyUnavailable(Q(50), LevelTooLowReason(30));

        var options = SchedulerOptions.Default with { ManualChain = [Q(50)] };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.AwaitingUser>(scheduler.CurrentStatus);
        Assert.Equal(Q(50), status.BlockedQuest);
        Assert.True(status.Reason.LevelTooLow);
    }

    // =========================================================================
    // Scenario 3 — Tier0CompleteEntriesSkipped_FallsThroughToTier3
    // =========================================================================
    [Fact]
    public async Task Tier0CompleteEntriesSkipped_FallsThroughToTier3()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(50), QuestStatus.Complete);
        questState.SetQuestStatus(Q(51), QuestStatus.Complete);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var options = SchedulerOptions.Default with { ManualChain = [Q(50), Q(51)] };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(300), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 4 — Tier0Available_TakesPriorityOverTier1
    // =========================================================================
    [Fact]
    public async Task Tier0Available_TakesPriorityOverTier1()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2));

        questState.SetQuestStatus(Q(50), QuestStatus.Available);
        questState.SetQuestStatus(Q(200), QuestStatus.Available);

        var options = SchedulerOptions.Default with { ManualChain = [Q(50)] };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(50), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(50), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 5 — Tier2DynamicBlocker_InsertsMissingPrereqAtTier2
    // =========================================================================
    [Fact]
    public async Task Tier2DynamicBlocker_InsertsMissingPrereqAtTier2()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2));

        questState.SetWhyUnavailable(Q(200), PrereqIncompleteReason(Q(199)));
        questState.SetQuestStatus(Q(199), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(199), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(199), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 6 — Tier1ClassQuest_TakesPriorityOverTier3WhenAvailable
    // =========================================================================
    [Fact]
    public async Task Tier1ClassQuest_TakesPriorityOverTier3WhenAvailable()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2), sortKey: 0)
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(200), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(200), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(200), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 7 — Tier4OffByDefault_BlueQuestSkippedEvenWhenAvailable
    // =========================================================================
    [Fact]
    public async Task Tier4OffByDefault_BlueQuestSkippedEvenWhenAvailable()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(400), tier: 4, sortKey: 0)   // no classJob — blue quest
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(400), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        // Default options: EnableBlueQuests = false
        var scheduler = BuildScheduler(questState, gameState, questData);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
    }

    // =========================================================================
    // Scenario 8a — Tier4Enabled_Tier3StillBeats Tier4
    // =========================================================================
    [Fact]
    public async Task Tier4EnabledExplicitly_Tier3StillWins_WhenTier3Available()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(400), tier: 4, sortKey: 0)   // blue quest, no classJob
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(400), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var options = SchedulerOptions.Default with { EnableBlueQuests = true };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // Scenario 8b — Tier4SelectedWhenTier3Exhausted
    // =========================================================================
    [Fact]
    public async Task Tier4EnabledExplicitly_Tier4SelectedWhenTier3Exhausted()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(400), tier: 4, sortKey: 0)   // blue quest, no classJob
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(300), QuestStatus.Complete);
        questState.SetQuestStatus(Q(400), QuestStatus.Available);

        var options = SchedulerOptions.Default with { EnableBlueQuests = true };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(400), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(400), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 9 — Tier5OffByDefault_SideQuestSkipped
    // =========================================================================
    [Fact]
    public async Task Tier5OffByDefault_SideQuestSkipped()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(500), tier: 5, sortKey: 0);

        questState.SetQuestStatus(Q(500), QuestStatus.Available);

        // Default options: EnableSideQuests = false
        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.Idle>(scheduler.CurrentStatus);
    }

    // =========================================================================
    // Scenario 10 — Tier5EnabledExplicitly_SideQuestSelected
    // =========================================================================
    [Fact]
    public async Task Tier5EnabledExplicitly_SideQuestSelected()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(500), tier: 5, sortKey: 0);

        questState.SetQuestStatus(Q(500), QuestStatus.Available);

        var options = SchedulerOptions.Default with { EnableSideQuests = true };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(500), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(500), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 11 — ResumeFromPause_ReevaluatesFromScratch
    // =========================================================================
    [Fact]
    public async Task ResumeFromPause_ReevaluatesFromScratch()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(101), tier: 3, sortKey: 0)
            .WithQuest(Q(200), tier: 1, classJob: J(2), sortKey: 0);

        questState.SetQuestStatus(Q(101), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // First call: Q(101) is the Tier-3 quest returned.
        var firstResult = await scheduler.NextQuestToRun(CancellationToken.None);
        Assert.Equal(Q(101), firstResult.ValueOrThrow);

        // Now Q(101) is completed; Tier-1 quest Q(200) becomes available.
        questState.SetQuestStatus(Q(101), QuestStatus.Complete);
        questState.SetQuestStatus(Q(200), QuestStatus.Available);

        // Act — second call must re-evaluate from scratch.
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Tier 1 wins over any remaining Tier 3.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(200), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(200), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 12 — Idle_AllQuestsComplete_ReturnsNullAndIdleStatus
    // =========================================================================
    [Fact]
    public async Task Idle_AllQuestsComplete_ReturnsNullAndIdleStatus()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0)
            .WithQuest(Q(101), tier: 3, sortKey: 1);

        questState.SetQuestStatus(Q(100), QuestStatus.Complete);
        questState.SetQuestStatus(Q(101), QuestStatus.Complete);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.Idle>(scheduler.CurrentStatus);
    }

    // =========================================================================
    // Scenario 13 — WrongJobQuestSkipped_PlayerChangedJob
    // =========================================================================
    [Fact]
    public async Task WrongJobQuestSkipped_PlayerChangedJob()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA

        // Q(200) is CNJ-only: IsClassQuestForJob(Q(200), J(2)) == false
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(6)) // CNJ
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(200), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(200) filtered before availability check; Q(300) wins.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(300), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 14 — Tier1QuestUnavailableDueToWrongJob_NotElevatedToTier2
    // =========================================================================
    [Fact]
    public async Task Tier1QuestUnavailableDueToWrongJob_NotElevatedToTier2()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA

        // Data provider says GLA, but game-state says WrongJob (player swapped mid-tick).
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2))
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetWhyUnavailable(Q(200), WrongJobReason());
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(200) skipped without recursive resolution; Q(300) wins.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(300), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 15 — Tier2RecursiveResolution_PrereqOfPrereq
    // =========================================================================
    [Fact]
    public async Task Tier2RecursiveResolution_PrereqOfPrereq()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetWhyUnavailable(Q(300), PrereqIncompleteReason(Q(299)));
        questState.SetWhyUnavailable(Q(299), PrereqIncompleteReason(Q(298)));
        questState.SetQuestStatus(Q(298), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — recursion reaches Q(298).
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(298), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(298), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 16 — Tier2CycleProtection_NoInfiniteLoop
    // =========================================================================
    [Fact]
    public async Task Tier2CycleProtection_NoInfiniteLoop()
    {
        // Q(300) requires Q(299); Q(299) requires Q(300) — a data cycle.
        // The visited-set guard must terminate the recursion.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetWhyUnavailable(Q(300), PrereqIncompleteReason(Q(299)));
        questState.SetWhyUnavailable(Q(299), PrereqIncompleteReason(Q(300)));

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act — must terminate without stack overflow and within bounded time.
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await scheduler.NextQuestToRun(cts.Token);

        // Assert — cycle detected; no quest selected; scheduler goes Idle.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.Idle>(scheduler.CurrentStatus);
    }

    // =========================================================================
    // Scenario 17 — Tier2DeadEnd_NoUnlockerFound_SkipsToNextTier3Candidate
    // =========================================================================
    [Fact]
    public async Task Tier2DeadEnd_NoUnlockerFound_SkipsToNextTier3Candidate()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0)
            .WithQuest(Q(301), tier: 3, sortKey: 1);

        // Q(300) is blocked by Q(299) which has no available unlocker (dead-end).
        questState.SetWhyUnavailable(Q(300), PrereqIncompleteReason(Q(299)));
        // Q(299) is also unavailable with no scripted reason (returns null → dead-end).
        questState.SetQuestStatus(Q(301), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(300) dead-ended; Q(301) is selected.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(301), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(301), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 18 — SortKeyOrderingWithinTier
    // =========================================================================
    [Fact]
    public async Task SortKeyOrderingWithinTier_LowestSortKeyWins()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 20)
            .WithQuest(Q(301), tier: 3, sortKey: 10)
            .WithQuest(Q(302), tier: 3, sortKey: 30);

        questState.SetQuestStatus(Q(300), QuestStatus.Available);
        questState.SetQuestStatus(Q(301), QuestStatus.Available);
        questState.SetQuestStatus(Q(302), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(301) has the lowest sort key.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(301), result.ValueOrThrow);
    }

    // =========================================================================
    // Scenario 18 (sibling) — SortKeyTiebreaker_LowerQuestIdValueWins
    // =========================================================================
    [Fact]
    public async Task SortKeyTiebreaker_LowerQuestIdValueWins()
    {
        // Arrange — two quests share the same SortKey; tiebreak by raw QuestId.Value ascending.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(302), tier: 3, sortKey: 10)
            .WithQuest(Q(300), tier: 3, sortKey: 10);

        questState.SetQuestStatus(Q(300), QuestStatus.Available);
        questState.SetQuestStatus(Q(302), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(300) wins on tiebreak (lower raw Value).
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // Scenario 19 — AdapterFailureOnIsQuestAvailable_SkipsCandidate
    // =========================================================================
    [Fact]
    public async Task AdapterFailureOnIsQuestAvailable_SkipsCandidate()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0)
            .WithQuest(Q(301), tier: 3, sortKey: 1);

        questState.SetIsQuestAvailableFail(Q(300), "queryFailed", "transient");
        questState.SetQuestStatus(Q(301), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(300) skipped on failure; Q(301) returned.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(301), result.ValueOrThrow);
    }

    // =========================================================================
    // Scenario 20 — AdapterFailureOnGetCurrentJob_SkipsTier1ButContinuesToTier3
    // =========================================================================
    [Fact]
    public async Task AdapterFailureOnGetCurrentJob_SkipsTier1ButContinuesToTier3()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetCurrentJobFail("playerNull");

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(2))
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(200), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Tier 1 skipped (no job); Tier 3 selected.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(300), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 21 — UpdateOptions_AffectsNextCallNotCurrent
    // =========================================================================
    [Fact]
    public async Task UpdateOptions_AffectsNextCallNotCurrent()
    {
        // Arrange
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0)
            .WithQuest(Q(500), tier: 5, sortKey: 0);

        questState.SetQuestStatus(Q(300), QuestStatus.Available);
        questState.SetQuestStatus(Q(500), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // First call — side quests are off; only Q(300) selected.
        var firstResult = await scheduler.NextQuestToRun(CancellationToken.None);
        Assert.Equal(Q(300), firstResult.ValueOrThrow);

        // Now mark Q(300) complete and enable side quests.
        questState.SetQuestStatus(Q(300), QuestStatus.Complete);
        scheduler.UpdateOptions(SchedulerOptions.Default with { EnableSideQuests = true });

        // Act — second call uses new options.
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(500), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(500), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 22 — NullArgumentsInConstructor_Throw (Theory × 5)
    // =========================================================================
    [Theory]
    [InlineData(0)] // questState
    [InlineData(1)] // gameState
    [InlineData(2)] // questData
    [InlineData(3)] // initialOptions
    [InlineData(4)] // logger
    public void NullArgumentsInConstructor_Throw(int nullArgIndex)
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider();
        var options = SchedulerOptions.Default;
        var logger = NullLogger<QuestScheduler>.Instance;

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = nullArgIndex switch
            {
                0 => new QuestScheduler(null!, gameState, questData, options, logger),
                1 => new QuestScheduler(questState, null!, questData, options, logger),
                2 => new QuestScheduler(questState, gameState, null!, options, logger),
                3 => new QuestScheduler(questState, gameState, questData, null!, logger),
                4 => new QuestScheduler(questState, gameState, questData, options, null!),
                _ => throw new InvalidOperationException("unexpected index")
            };
        });
    }

    // =========================================================================
    // Scenario 23 — EmptyKnownQuestCorpus_ReturnsIdle
    // =========================================================================
    [Fact]
    public async Task EmptyKnownQuestCorpus_ReturnsIdle()
    {
        // Arrange — data provider knows no quests.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider();

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        Assert.IsType<SchedulerStatus.Idle>(scheduler.CurrentStatus);
    }

    // =========================================================================
    // Scenario 24 — Tier0AlreadyCompletedReason_TreatedAsBlockerNotComplete
    // =========================================================================
    [Fact]
    public async Task Tier0AlreadyCompletedReason_TreatedAsBlockerNotComplete()
    {
        // Arrange — IsQuestComplete says false; WhyUnavailable says AlreadyCompleted.
        // The scheduler uses IsQuestComplete as authority for the skip check.
        // Since IsQuestComplete == false, the entry is not skipped.
        // WhyUnavailable returns non-null → AwaitingUser.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider();

        // Q(50) is NOT complete per game state (not set to Complete status).
        questState.SetWhyUnavailable(Q(50), AlreadyCompletedReason());

        var options = SchedulerOptions.Default with { ManualChain = [Q(50)] };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — scheduler stops and surfaces AwaitingUser; does not loop or skip.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.AwaitingUser>(scheduler.CurrentStatus);
        Assert.Equal(Q(50), status.BlockedQuest);
        Assert.True(status.Reason.AlreadyCompleted);
    }

    // =========================================================================
    // Scenario 25 — Tier1NullTierFromDataProvider_QuestIgnored
    // =========================================================================
    [Fact]
    public async Task Tier1NullTierFromDataProvider_QuestIgnored()
    {
        // Arrange — Q(200) has tier == null (uncategorized); must not be selected.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1);

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: null, classJob: J(2), sortKey: 0) // null tier
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(200), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert — Q(200) ignored; falls through to Tier 3.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(300), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 26 — BlueUrgentQuest_Tier1_SelectedBeforeTier3
    // blue-urgent (Tier 1, no class restriction) beats Tier 3
    // =========================================================================
    [Fact]
    public async Task BlueUrgentQuest_Tier1_SelectedBeforeTier3()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(201), tier: 1, sortKey: 0)  // blue-urgent: Tier 1, no classJob
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(201), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(201), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(201), status.CurrentQuest);
    }

    // =========================================================================
    // Scenario 27 — BlueUrgentQuest_Tier1_RunsWhenNoJobMatchForClassQuests
    // blue-urgent runs even when current job matches no class quest in Tier 1
    // =========================================================================
    [Fact]
    public async Task BlueUrgentQuest_Tier1_RunsWhenNoJobMatchForClassQuests()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(200), tier: 1, classJob: J(6), sortKey: 0) // CNJ-only class quest — wrong job
            .WithQuest(Q(201), tier: 1, sortKey: 0)                  // blue-urgent: no class restriction
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        // Q(200) is wrong job — not available
        questState.SetQuestStatus(Q(201), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Q(201) wins — blue-urgent Tier 1 beats Tier 3
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(201), result.ValueOrThrow);
    }

    // =========================================================================
    // Scenario 28 — BlueQuest_Tier4_RunsOnCombatJob_WhenEnabled
    // blue quests have no job restriction — runs even when player is on combat job
    // =========================================================================
    [Fact]
    public async Task BlueQuest_Tier4_RunsOnCombatJob_WhenEnabled()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA — a combat job, not DoH/DoL

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(400), tier: 4, sortKey: 0)  // blue quest, no class restriction
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(300), QuestStatus.Complete);
        questState.SetQuestStatus(Q(400), QuestStatus.Available);

        var options = SchedulerOptions.Default with { EnableBlueQuests = true };
        var scheduler = BuildScheduler(questState, gameState, questData, options);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Q(400) wins — blue quest runs regardless of active job
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(400), result.ValueOrThrow);
        var status = Assert.IsType<SchedulerStatus.Running>(scheduler.CurrentStatus);
        Assert.Equal(Q(400), status.CurrentQuest);
    }
}
