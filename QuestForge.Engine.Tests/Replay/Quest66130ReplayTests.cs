using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: End-to-end replay test for quest 66130 "Coming to Ul'dah".
///
/// This test loads the canonical Phase 6 trace fixture from questforge-data, drives
/// QuestEngine against replay-backed adapters, and asserts the engine emits the same
/// sequence of (stepId, actionType) pairs as the recorded trace.
///
/// SKIP BEHAVIOUR: If the fixture is not present (questforge-data not checked out),
/// the test is skipped via Assert.Skip — it does NOT fail. This keeps the test suite
/// green for developers who have only the questforge repo cloned.
///
/// Covers §8.6 and Task 6 of PHASE_7_PLAN.md.
/// </summary>
public sealed class Quest66130ReplayTests
{
    // -------------------------------------------------------------------------
    // §8.6 End-to-end replay — full decision sequence matches recorded trace
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Quest66130_CanonicalTracePhase6_EngineEmitsSameDecisionSequence()
    {
        /*
         * RED: Will fail until ALL Phase 7 implementation is in place:
         *   - FixtureLocator (for skip-on-missing logic)
         *   - TraceReader.ReadFile (JSONL deserialization)
         *   - ReplayGameStateProvider (observation-backed IGameStateProvider)
         *   - ReplayQuestState (observation-backed IQuestState)
         *   - QuestEngine constructor / Tick / BeginRun (already implemented from Phase 6)
         *
         * CONTRACT: Given the 66130-canonical-trace-phase6.jsonl fixture is present,
         *   When the engine is driven by replay providers for as many ticks as there are
         *   recorded decision events,
         *   Then every (stepId, actionType) pair emitted by the engine matches the
         *   corresponding recorded pair in document order.
         *   After the main loop, one final Tick returns EngineAction.Done,
         *   matching runEnd.Outcome == "done".
         *
         * BUILDER GUIDANCE:
         *   - FixtureLocator.LocateOrSkip causes the test to SKIP (not fail) if the
         *     fixture is absent — so CI without questforge-data stays green.
         *   - ReplayGameStateProvider and ReplayQuestState receive split observation lists
         *     (split by interface method name).
         *   - CapturingTraceWriter buffers DecisionEvents so the test can read stepId
         *     (which QuestEngine.Tick does not expose on the returned EngineAction).
         *   - The "one extra tick" after the loop catches EngineAction.Done, which the
         *     engine emits as a run.end event rather than a decision event.
         */

        // ---- Arrange: locate the fixture (skip if absent) ----
        var tracePath = FixtureLocator.LocateOrSkip(
            "quests/arr/msq/66130-canonical-trace-phase6.jsonl");

        // ---- Arrange: load the trace ----
        var events = TraceReader.ReadFile(tracePath);
        var runStart = events.OfType<RunStartEvent>().Single();
        var observations = events.OfType<ObservationEvent>().ToList();
        var recordedDecisions = events.OfType<DecisionEvent>().ToList();
        var runEnd = events.OfType<RunEndEvent>().SingleOrDefault();

        Assert.Equal(66130u, runStart.QuestId);
        Assert.NotEmpty(observations);
        Assert.NotEmpty(recordedDecisions);

        // ---- Arrange: split observations by interface ----
        var (gameStateObs, questStateObs) = SplitByInterface(observations);

        var replayGameState = new ReplayGameStateProvider(gameStateObs);
        var replayQuestState = new ReplayQuestState(questStateObs);

        // ---- Arrange: non-recording fakes for adapters not covered by observations ----
        // These adapters accept whatever the engine sends and return sensible defaults.
        // Their calls are NOT recorded in the Phase 6 trace (they are fire-and-forget).
        var fakeGameState = new FakeGameStateProvider();
        var fakeQuestState = new FakeQuestState();
        var navigator = new FakeNavigator(fakeGameState);
        var teleporter = new FakeTeleporter(fakeGameState);
        var interactor = new FakeInteractor(fakeGameState, fakeQuestState);
        var combat = new FakeCombat();
        var gear = new FakeGearManager();
        var minigame = new FakeMinigameSkipper();
        var dialogue = new FakeDialogueResolver();
        var timing = new FakeTimingProfile();

        // ---- Arrange: capturing trace writer ----
        // The engine writes a DecisionEvent to the trace on each tick that returns
        // a non-terminal action. We buffer these to read (stepId, actionType) per tick.
        var capturingTrace = new CapturingTraceWriter();

        // ---- Arrange: engine ----
        var engine = new QuestEngine(
            replayGameState,
            replayQuestState,
            navigator,
            teleporter,
            interactor,
            combat,
            gear,
            minigame,
            dialogue,
            timing,
            capturingTrace,
            NullLogger<QuestEngine>.Instance);

        var quest = LoadQuest66130();
        engine.StartQuest(quest);
        engine.BeginRun(runStart.RunId);

        // ---- Act: drive the engine for as many ticks as recorded decisions ----
        var actualDecisions = new List<(string? StepId, string ActionType)>();
        var ct = CancellationToken.None;

        var eventOffset = 0; // tracks how many events were in the list before this tick

        for (var i = 0; i < recordedDecisions.Count; i++)
        {
            var action = await engine.Tick(ct);

            // Scan only the events appended since the last tick (O(1) per tick).
            var newDecision = capturingTrace.Events
                .Skip(eventOffset)
                .OfType<DecisionEvent>()
                .FirstOrDefault();

            eventOffset = capturingTrace.Events.Count;

            // If no new DecisionEvent was emitted, the engine reached a terminal state early.
            // Break and let the post-loop assertion handle it.
            if (newDecision is null) break;

            actualDecisions.Add((newDecision.StepId, newDecision.ActionType));
        }

        // ---- Assert: decision sequence matches recorded sequence ----
        Assert.Equal(recordedDecisions.Count, actualDecisions.Count);

        for (var i = 0; i < recordedDecisions.Count; i++)
        {
            var expected = (recordedDecisions[i].StepId, recordedDecisions[i].ActionType);
            var actual = actualDecisions[i];

            if (expected != actual)
                throw new ReplayDecisionMismatchException(
                    $"Decision divergence at index {i}: " +
                    $"expected ({expected.StepId ?? "<null>"}, {expected.ActionType}), " +
                    $"got ({actual.StepId ?? "<null>"}, {actual.ActionType}). " +
                    $"Trace runId={runStart.RunId}, questId={runStart.QuestId}.");
        }

        // ---- Assert: terminal action matches run.end outcome ----
        // The engine does NOT emit a DecisionEvent for Done — it emits run.end instead.
        // Drive one final tick to observe the terminal action.
        var terminalAction = await engine.Tick(ct);

        if (runEnd is not null)
        {
            switch (runEnd.Outcome)
            {
                case "done":
                    Assert.IsType<EngineAction.Done>(terminalAction);
                    break;
                case "awaitUser":
                    Assert.IsType<EngineAction.AwaitUser>(terminalAction);
                    break;
                default:
                    Assert.Fail($"Unhandled recorded run.end.outcome: '{runEnd.Outcome}'.");
                    break;
            }
        }
        else
        {
            // No recorded run.end — accept any terminal action.
            // In practice every committed canonical trace ends with run.end.
            Assert.True(terminalAction is EngineAction.Done or EngineAction.AwaitUser,
                $"Engine returned non-terminal action {terminalAction.GetType().Name} " +
                $"after consuming all {recordedDecisions.Count} recorded decisions.");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static QuestDefinition LoadQuest66130()
    {
        // Same convention as Phase 4 tests (AwaitUserTests, BeginRunTests, etc.):
        // the fixture is copied next to the test assembly via the csproj's
        // <None Update="Fixtures\66130.json"> CopyToOutputDirectory entry.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }

    /// <summary>
    /// Splits a flat observation list into two lists: one for IGameStateProvider methods,
    /// one for IQuestState methods. Discrimination is by method name — the two interfaces
    /// have non-overlapping method names.
    /// </summary>
    private static (List<ObservationEvent> GameState, List<ObservationEvent> QuestState)
        SplitByInterface(IReadOnlyList<ObservationEvent> observations)
    {
        // All method names defined on IQuestState.
        // Any observation whose method name is in this set belongs to IQuestState;
        // all others belong to IGameStateProvider.
        var questStateMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IQuestState.GetQuestStatus),
            nameof(IQuestState.GetQuestSequence),
            nameof(IQuestState.GetQuestFlags),
            nameof(IQuestState.IsQuestFlagSet),
            nameof(IQuestState.IsQuestAccepted),
            nameof(IQuestState.IsQuestComplete),
            nameof(IQuestState.IsQuestAvailable),
            nameof(IQuestState.WhyUnavailable),
            nameof(IQuestState.GetAcceptedQuests),
            nameof(IQuestState.GetAvailableQuestRewards),
        };

        var gs = new List<ObservationEvent>();
        var qs = new List<ObservationEvent>();

        foreach (var obs in observations)
        {
            if (questStateMethods.Contains(obs.Method))
                qs.Add(obs);
            else
                gs.Add(obs);
        }

        return (gs, qs);
    }
}
