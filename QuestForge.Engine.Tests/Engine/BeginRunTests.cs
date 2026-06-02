using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for QuestEngine.BeginRun and trace event emission.
/// All tests fail until Builder implements:
///   - QuestEngine.BeginRun(string runId) — throws ArgumentException on null/empty
///   - QuestEngine.CurrentRunId property
///   - run.start emission on first Tick after BeginRun
///   - decision emission on every Tick that produces an action
///   - run.end emission when Done or AwaitUser is returned
///
/// Covers §6.4 of PHASE_5_PLAN.md — 10 tests total.
///
/// NOTE: These tests use EngineTestHarness which, after Phase A, will use
///   RecordingGameStateProvider/RecordingQuestState wired to a FakeTraceWriter.
///   Until Phase A + Phase C implementation, all tests fail.
/// </summary>
public sealed class BeginRunTests
{
    private static QuestDefinition LoadFixture()
    {
        var json = File.ReadAllText("Fixtures/66130.json");
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }

    // -------------------------------------------------------------------------
    // BeginRun with empty string → ArgumentException
    // -------------------------------------------------------------------------

    [Fact]
    public void BeginRun_EmptyString_ThrowsArgumentException()
    {
        /*
         * RED: Will fail until QuestEngine.BeginRun is implemented.
         *
         * CONTRACT: Given an engine,
         *           When BeginRun("") is called,
         *           Then ArgumentException is thrown.
         *
         * BUILDER GUIDANCE: if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException(...)
         */

        // Arrange
        var harness = new EngineTestHarness();

        // Act + Assert
        Assert.Throws<ArgumentException>(() => harness.Engine.BeginRun(""));
    }

    // -------------------------------------------------------------------------
    // BeginRun with null → ArgumentException
    // -------------------------------------------------------------------------

    [Fact]
    public void BeginRun_Null_ThrowsArgumentException()
    {
        /*
         * RED: Will fail until QuestEngine.BeginRun is implemented.
         *
         * CONTRACT: Given an engine,
         *           When BeginRun(null!) is called,
         *           Then ArgumentException is thrown.
         */

        // Arrange
        var harness = new EngineTestHarness();

        // Act + Assert
        Assert.Throws<ArgumentException>(() => harness.Engine.BeginRun(null!));
    }

    // -------------------------------------------------------------------------
    // BeginRun succeeds → CurrentRunId set
    // -------------------------------------------------------------------------

    [Fact]
    public void BeginRun_ValidId_SetsCurrentRunId()
    {
        /*
         * RED: Will fail until QuestEngine.BeginRun and CurrentRunId are implemented.
         *
         * CONTRACT: Given an engine,
         *           When BeginRun("rid") is called,
         *           Then Engine.CurrentRunId == "rid".
         *
         * BUILDER GUIDANCE: public string? CurrentRunId => _runId;
         */

        // Arrange
        var harness = new EngineTestHarness();

        // Act
        harness.Engine.BeginRun("rid");

        // Assert
        Assert.Equal("rid", harness.Engine.CurrentRunId);
    }

    // -------------------------------------------------------------------------
    // Tick without BeginRun → no events emitted (Phase 4 backward compat)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tick_WithoutBeginRun_EmitsNoEvents()
    {
        /*
         * RED: Will fail until harness is updated to use FakeTraceWriter.
         *
         * CONTRACT: Given a harness with quest loaded and BeginRun NOT called,
         *           When Tick is called,
         *           Then harness.TraceWriter.Count == 0 — no trace events.
         *
         * BUILDER GUIDANCE: The engine emits events only when _runId is set.
         *   Phase 4 tests call StartQuest + Tick without BeginRun → zero events.
         *   This test verifies backward compatibility.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(LoadFixture());
        // NOTE: BeginRun is NOT called

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert — the harness.TraceWriter is FakeTraceWriter (after Phase A update)
        Assert.Equal(0, harness.TraceWriter.Count); // No trace events should be emitted when BeginRun has not been called
    }

    // -------------------------------------------------------------------------
    // Tick without StartQuest → no events, even after BeginRun
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tick_WithBeginRunButNoQuest_EmitsNoEvents()
    {
        /*
         * RED: Will fail until QuestEngine.BeginRun is implemented.
         *
         * CONTRACT: Given an engine with BeginRun called but NO quest loaded (no StartQuest),
         *           When Tick is called,
         *           Then no trace events are emitted (engine returns AwaitUser but has no questId
         *           to include in run.start, so emits nothing).
         *
         * BUILDER GUIDANCE: EmitRunStartIfNeeded() checks _quest is not null before emitting.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.Engine.BeginRun("rid");
        // NOTE: StartQuest is NOT called

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.Equal(0, harness.TraceWriter.Count); // No trace events emitted when _quest is null even after BeginRun
    }

    // -------------------------------------------------------------------------
    // First tick after BeginRun: run.start is first event, decision is last
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BeginRun_ThenTick_RunStartIsFirstEventDecisionIsLast()
    {
        /*
         * RED: Will fail until engine trace emission is implemented.
         *
         * CONTRACT: Given quest 66130 loaded, BeginRun("test-run-1") called,
         *           sequence = 0, player far from Wymond,
         *           When Tick is called once,
         *           Then:
         *             1. RecordedEvents[0] is RunStartEvent with RunId == "test-run-1", QuestId == 66130
         *             2. Last event in RecordedEvents is DecisionEvent
         *             3. There is at least one ObservationEvent between them
         *
         * BUILDER GUIDANCE: EmitRunStartIfNeeded() is called first in Tick.
         *   Then adapter reads (via recording proxy) emit ObservationEvents.
         *   Finally, TraceSafe(DecisionEvent) is called after ResolveAction.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("test-run-1");

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var events = harness.TraceWriter.RecordedEvents;
        Assert.True(events.Count >= 3,
            $"Expected at least 3 events (run.start, >=1 observation, decision) but got {events.Count}");

        // run.start must be first
        var first = Assert.IsType<RunStartEvent>(events[0]);
        Assert.Equal("test-run-1", first.RunId);
        Assert.Equal(66130u, first.Data.QuestId);

        // last event must be decision
        Assert.IsType<DecisionEvent>(events[events.Count - 1]);

        // at least one observation in between
        Assert.Contains(events, e => e is ObservationEvent);
    }

    // -------------------------------------------------------------------------
    // DecisionEvent has correct ActionType and StepId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BeginRun_ThenTick_DecisionEventHasCorrectActionTypeAndStepId()
    {
        /*
         * RED: Will fail until engine trace emission is implemented.
         *
         * CONTRACT: Given sequence 0, player far from Wymond, BeginRun("rid"),
         *           When Tick is called,
         *           Then the DecisionEvent has ActionType == "Navigate"
         *           and StepId == "travel-to-wymond".
         *
         * BUILDER GUIDANCE: The step.Id from the quest fixture for the first travel step
         *   is "travel-to-wymond". Capture it at the ResolveAction return site.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("rid");

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var events = harness.TraceWriter.RecordedEvents;
        var decision = events.OfType<DecisionEvent>().Single();
        Assert.Equal("Navigate", decision.Data.ActionType);
        Assert.Equal("travel-to-wymond", decision.Data.StepId);
    }

    // -------------------------------------------------------------------------
    // Done → RunEndEvent with Outcome == "done" is last event
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tick_QuestComplete_RunEndEventWithDoneIsLastEvent()
    {
        /*
         * RED: Will fail until engine trace emission is implemented.
         *
         * CONTRACT: Given quest complete and BeginRun("end-run") called,
         *           When Tick is called,
         *           Then the last event in RecordedEvents is RunEndEvent with Outcome == "done".
         *
         * BUILDER GUIDANCE: When action is EngineAction.Done, emit RunEndEvent after decision.
         *   run.end must be the absolute last event in the list.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("end-run");

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var events = harness.TraceWriter.RecordedEvents;
        Assert.True(events.Count >= 1, "Expected at least run.start event");
        var last = events[events.Count - 1];
        var runEnd = Assert.IsType<RunEndEvent>(last);
        Assert.Equal("done", runEnd.Data.Outcome);
    }

    // -------------------------------------------------------------------------
    // AwaitUser → RunEndEvent with Outcome == "awaitUser"
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Tick_UnknownSequence_RunEndEventWithAwaitUser()
    {
        /*
         * RED: Will fail until engine trace emission is implemented.
         *
         * CONTRACT: Given an unknown sequence number (no matching block), BeginRun called,
         *           When Tick is called,
         *           Then a RunEndEvent with Outcome == "awaitUser" is emitted.
         *
         * BUILDER GUIDANCE: When engine returns EngineAction.AwaitUser (e.g. no matching sequence block),
         *   emit RunEndEvent("awaitUser") as the last trace event before returning the action.
         */

        // Arrange
        var harness = new EngineTestHarness();
        // Sequence 99 has no matching block in quest 66130
        harness.QuestState.SetQuestSequence(new QuestId(66130), 99);
        harness.GameState.SetZone(new ZoneId(182));
        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("await-run");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — engine returns AwaitUser
        Assert.IsType<EngineAction.AwaitUser>(action);

        // Assert — RunEndEvent with awaitUser
        var events = harness.TraceWriter.RecordedEvents;
        Assert.True(events.Count >= 1);
        var last = events[events.Count - 1];
        var runEnd = Assert.IsType<RunEndEvent>(last);
        Assert.Equal("awaitUser", runEnd.Data.Outcome);
    }

    // -------------------------------------------------------------------------
    // Second BeginRun after done → fresh RunStartEvent on next Tick
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SecondBeginRun_AfterRunCompletes_FreshRunStartOnNextTick()
    {
        /*
         * RED: Will fail until engine trace emission is implemented.
         *
         * CONTRACT: Given a run that completed (Done emitted), when BeginRun("rid-2") is called
         *           and Tick runs again,
         *           Then a fresh RunStartEvent with RunId == "rid-2" is emitted.
         *
         * BUILDER GUIDANCE: After Done, set _runStartEmitted = false so next BeginRun
         *   triggers a fresh run.start on the next Tick.
         */

        // Arrange — run to completion to get first run.end
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("rid-1");
        await harness.Engine.Tick(CancellationToken.None); // completes the run

        // Clear trace so we only see fresh events
        harness.TraceWriter.Reset();

        // Set up for a new run — same quest, now accepted again
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.GameState.SetPosition(new WorldPosition(21.84f, 7.0f, -81.13f));

        // Act — begin second run
        harness.Engine.BeginRun("rid-2");
        await harness.Engine.Tick(CancellationToken.None);

        // Assert — fresh run.start with rid-2 is the first event
        var events = harness.TraceWriter.RecordedEvents;
        var runStart = events.OfType<RunStartEvent>().FirstOrDefault();
        Assert.NotNull(runStart);
        Assert.Equal("rid-2", runStart!.RunId);
    }
}