using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Scheduling;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;
using Xunit;

namespace QuestForge.Engine.Tests.Scheduling;

/// <summary>
/// Tests for quest-level SkipIf predicate evaluation in the scheduler.
/// Scenarios S1-S10 from docs/QUEST_SKIPIF_PLAN.md.
///
/// RED PHASE: All tests will fail because the scheduler's TrySelectCandidate
/// does not yet evaluate the skipIf predicate. The evaluateSkipIf delegate is
/// stored on the scheduler but never called.
/// </summary>
public sealed class QuestSchedulerSkipIfTests
{
    // -------------------------------------------------------------------------
    // Helpers (mirrors QuestSchedulerTests patterns)
    // -------------------------------------------------------------------------

    private static QuestId Q(uint n) => new(n);
    private static JobId J(uint n) => new(n);

    private static QuestScheduler BuildScheduler(
        FakeQuestState questState,
        FakeGameStateProvider gameState,
        FakeQuestDataProvider questData,
        SchedulerOptions? options = null,
        Func<string, CancellationToken, Task<bool>>? evaluateSkipIf = null)
    {
        return new QuestScheduler(
            questState,
            gameState,
            questData,
            options ?? SchedulerOptions.Default,
            NullLogger<QuestScheduler>.Instance,
            evaluateSkipIf);
    }

    // =========================================================================
    // S1 -- SkipIf true: quest is skipped entirely
    // =========================================================================
    [Fact]
    public async Task S1_SkipIfTrue_QuestIsSkipped()
    {
        // Arrange: three Tier 3 quests; Q(100) has skipIf containing "200".
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0, skipIf: "isQuestComplete(200)")
            .WithQuest(Q(101), tier: 3, sortKey: 1)
            .WithQuest(Q(102), tier: 3, sortKey: 2);

        questState.SetQuestStatus(Q(100), QuestStatus.Available);
        questState.SetQuestStatus(Q(101), QuestStatus.Available);
        questState.SetQuestStatus(Q(102), QuestStatus.Available);

        // evaluateSkipIf returns true for any predicate containing "200"
        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (pred, _) => Task.FromResult(pred.Contains("200")));

        // Act
        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Assert: Q(100) skipped; Q(101) is next.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(101), result.ValueOrThrow);
    }

    // =========================================================================
    // S2 -- SkipIf false: quest is NOT skipped
    // =========================================================================
    [Fact]
    public async Task S2_SkipIfFalse_QuestProceeds()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0, skipIf: "isQuestComplete(200)");

        questState.SetQuestStatus(Q(100), QuestStatus.Available);

        // evaluateSkipIf returns false for all predicates
        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (_, _) => Task.FromResult(false));

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(100), result.ValueOrThrow);
    }

    // =========================================================================
    // S3 -- SkipIf null: no evaluation, quest proceeds
    // =========================================================================
    [Fact]
    public async Task S3_SkipIfNull_DelegateNeverCalled()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0); // no skipIf

        questState.SetQuestStatus(Q(100), QuestStatus.Available);

        int callCount = 0;
        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (_, _) => { callCount++; return Task.FromResult(true); });

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(100), result.ValueOrThrow);
        Assert.Equal(0, callCount);
    }

    // =========================================================================
    // S4 -- SkipIf evaluation throws: quest is NOT skipped (non-fatal)
    // =========================================================================
    [Fact]
    public async Task S4_SkipIfThrows_QuestProceeds()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0, skipIf: "badPredicate()");

        questState.SetQuestStatus(Q(100), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (_, _) => throw new Exception("parse error"));

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Exception caught; quest proceeds as if skipIf were null.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(100), result.ValueOrThrow);
    }

    // =========================================================================
    // S5 -- No evaluateSkipIf delegate: skipIf field ignored
    // =========================================================================
    [Fact]
    public async Task S5_NoDelegateProvided_SkipIfIgnored()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0, skipIf: "isQuestComplete(200)");

        questState.SetQuestStatus(Q(100), QuestStatus.Available);

        // evaluateSkipIf: null (default)
        var scheduler = BuildScheduler(questState, gameState, questData);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(100), result.ValueOrThrow);
    }

    // =========================================================================
    // S6 -- SkipIf prevents prerequisite chain descent
    // =========================================================================
    [Fact]
    public async Task S6_SkipIfShortCircuits_NoPrereqDescentForSkippedQuest()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 3, sortKey: 0, skipIf: "isQuestComplete(200)")
            .WithQuest(Q(101), tier: 3, sortKey: 1);

        // Q(100) is NOT complete and NOT available -- locked behind prereq Q(99).
        // Q(99) IS available. Without skipIf, the scheduler descends into
        // Q(100)'s prereq chain and returns Q(99) as a Tier-2 blocker.
        // With skipIf evaluated to true, Q(100) is skipped entirely and
        // the scheduler never looks at Q(99); it picks Q(101) instead.
        questState.SetWhyUnavailable(Q(100),
            new QuestUnlockReason(
                LevelTooLow: false, RequiredLevel: 0,
                PrerequisiteIncomplete: true, MissingPrereqs: [Q(99)],
                WrongJob: false, RequiredJob: null,
                AlreadyCompleted: false,
                OtherReason: false, Detail: null));
        questState.SetQuestStatus(Q(99), QuestStatus.Available);
        questState.SetQuestStatus(Q(101), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (pred, _) => Task.FromResult(pred.Contains("200")));

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // With skipIf: Q(100) skipped, Q(101) selected.
        // Without skipIf: Q(100) blocked -> Q(99) returned as blocker (WRONG).
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(101), result.ValueOrThrow);
    }

    // =========================================================================
    // S7 -- Mutual exclusion: simulates starting city scenario
    // Uses correct quest IDs: 65643 (Limsa), 66130 (Ul'dah), 65575 (Gridania)
    // =========================================================================
    [Fact]
    public async Task S7_MutualExclusion_StartingCityScenario()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(65643), tier: 3, sortKey: 0,
                skipIf: "isQuestComplete(66130) or isQuestComplete(65575)")
            .WithQuest(Q(66130), tier: 3, sortKey: 1,
                skipIf: "isQuestComplete(65643) or isQuestComplete(65575)")
            .WithQuest(Q(65575), tier: 3, sortKey: 2,
                skipIf: "isQuestComplete(65643) or isQuestComplete(66130)");

        // Q(65643) is complete. Q(66130) and Q(65575) are available.
        questState.SetQuestStatus(Q(65643), QuestStatus.Complete);
        questState.SetQuestStatus(Q(66130), QuestStatus.Available);
        questState.SetQuestStatus(Q(65575), QuestStatus.Available);

        // Simulate predicate evaluation: isQuestComplete(N) checks if N is in the completed set.
        var completedQuests = new HashSet<uint> { 65643 };
        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (pred, _) =>
            {
                // Simple predicate simulation: check if any completed quest ID appears in the predicate
                foreach (var id in completedQuests)
                {
                    if (pred.Contains(id.ToString()))
                        return Task.FromResult(true);
                }
                return Task.FromResult(false);
            });

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Q(65643) is complete (skipped by IsQuestComplete check).
        // Q(66130) and Q(65575) are skipped by skipIf (because 65643 is complete).
        // No quest to run.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Null(result.ValueOrThrow);
    }

    // =========================================================================
    // S8 -- Existing tests still pass with null evaluateSkipIf
    // (This is implicitly verified by the existing QuestSchedulerTests
    //  continuing to pass. This test explicitly constructs without the param.)
    // =========================================================================
    [Fact]
    public async Task S8_ExistingSchedulerPattern_StillWorks()
    {
        // This test uses the exact same construction pattern as existing tests
        // (no evaluateSkipIf parameter) to verify backward compatibility.
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        // Construct scheduler without evaluateSkipIf -- same as existing tests
        var scheduler = new QuestScheduler(
            questState,
            gameState,
            questData,
            SchedulerOptions.Default,
            NullLogger<QuestScheduler>.Instance);

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // S9 -- SkipIf on Tier 1 quest: falls through to Tier 3
    // =========================================================================
    [Fact]
    public async Task S9_SkipIfOnTier1_FallsThroughToTier3()
    {
        var questState = new FakeQuestState();
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(J(2), 1); // GLA

        var questData = new FakeQuestDataProvider()
            .WithQuest(Q(100), tier: 1, classJob: J(2), sortKey: 0,
                skipIf: "isQuestComplete(200)")
            .WithQuest(Q(300), tier: 3, sortKey: 0);

        questState.SetQuestStatus(Q(100), QuestStatus.Available);
        questState.SetQuestStatus(Q(300), QuestStatus.Available);

        var scheduler = BuildScheduler(questState, gameState, questData,
            evaluateSkipIf: (_, _) => Task.FromResult(true));

        var result = await scheduler.NextQuestToRun(CancellationToken.None);

        // Q(100) skipped by skipIf; Q(300) selected from Tier 3.
        Assert.IsType<Result<QuestId?>.Success>(result);
        Assert.Equal(Q(300), result.ValueOrThrow);
    }

    // =========================================================================
    // S10 -- FakeQuestDataProvider.GetSkipIf returns null for unregistered quest
    // =========================================================================
    [Fact]
    public void S10_FakeProvider_GetSkipIf_ReturnsNullForUnknown()
    {
        var questData = new FakeQuestDataProvider();
        Assert.Null(questData.GetSkipIf(Q(999)));
    }
}
