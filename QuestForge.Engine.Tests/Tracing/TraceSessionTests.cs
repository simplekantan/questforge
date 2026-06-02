using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for TraceSession with the new event shapes (envelope fields v/seq/ts,
/// nested Data records, RunId on base, seq/ts stamping).
///
/// Covers TS11-TS17, TS22-TS24 from TRACE_SPEC_COMPLIANCE_PLAN.md.
///
/// All tests WILL FAIL because the new TraceEvent types and TraceSession stamping
/// do not exist yet. The builder must implement the type refactoring AND the
/// seq/ts stamping logic.
///
/// Total: 40 tests (existing lifecycle + dedup tests updated for new shape,
/// plus new TS11-TS17, TS22-TS24 stamping tests).
/// </summary>
public sealed class TraceSessionTests
{
    private static readonly string _tracesDir = Path.Combine(Path.GetTempPath(), "qf-trace-tests");

    // ── Helpers: new-shape event constructors ──────────────────────────────

    private static (Func<string, ITraceWriter> factory, Func<FakeTraceWriter?> getWriter) MakeCaptureFactory()
    {
        FakeTraceWriter? captured = null;
        ITraceWriter Factory(string _)
        {
            captured = new FakeTraceWriter();
            return captured;
        }
        return (Factory, () => captured);
    }

    private static RunStartEvent MakeRunStart(string runId = "run-001") =>
        new RunStartEvent
        {
            RunId = runId,
            Data = new RunStartEvent.RunStartData { QuestId = 66130u, SchemaVer = "1.0" }
        };

    private static RunEndEvent MakeRunEnd(string runId = "run-001") =>
        new RunEndEvent
        {
            RunId = runId,
            Data = new RunEndEvent.RunEndData { Outcome = "done" }
        };

    private static ObservationEvent MakeObservation(
        string method = "GetPlayerZone",
        int value = 182,
        string runId = "run-001") =>
        new ObservationEvent
        {
            RunId = runId,
            Data = new ObservationEvent.ObservationData
            {
                Method = method,
                Argument = null,
                Value = JsonSerializer.SerializeToElement(value)
            }
        };

    private static DecisionEvent MakeDecision(
        string stepId,
        string actionType = "navigate",
        string runId = "run-001") =>
        new DecisionEvent
        {
            RunId = runId,
            Data = new DecisionEvent.DecisionData
            {
                StepId = stepId,
                ActionType = actionType
            }
        };

    private static ActionSubmittedEvent MakeActionSubmitted(string runId = "run-001") =>
        new ActionSubmittedEvent
        {
            RunId = runId,
            Data = new ActionSubmittedEvent.ActionSubmittedData { ActionType = "Navigate" }
        };

    private static ActionCompletedEvent MakeActionCompleted(string runId = "run-001") =>
        new ActionCompletedEvent
        {
            RunId = runId,
            Data = new ActionCompletedEvent.ActionCompletedData
            {
                ActionType = "Navigate",
                Outcome = "Arrived"
            }
        };

    private static string FormatEvents(FakeTraceWriter w) =>
        string.Join("; ", w.RecordedEvents.Select(e => $"[{e.Type}] seq={e.Seq} ts={e.Ts}"));

    // =========================================================================
    // TS11: TraceSession stamps seq monotonically
    // =========================================================================

    [Fact]
    public void TS11_TraceSession_SeqMonotonicity()
    {
        /*
         * CONTRACT: Given a TraceSession in QuestRun mode with OnQuestRunStart called
         *           When 5 events are written (RunStart, Observation, Decision,
         *           ActionSubmitted, ActionCompleted)
         *           Then the inner FakeTraceWriter receives 5 events with Seq values
         *           0, 1, 2, 3, 4 respectively
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart());
        session.Write(MakeObservation());
        session.Write(MakeDecision("step-a"));
        session.Write(MakeActionSubmitted());
        session.Write(MakeActionCompleted());

        // Assert
        var inner = getWriter()!;
        Assert.Equal(5, inner.Count);
        var seqs = inner.RecordedEvents.Select(e => e.Seq).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seqs);
    }

    // =========================================================================
    // TS12: TraceSession stamps ts non-negatively and non-decreasingly
    // =========================================================================

    [Fact]
    public void TS12_TraceSession_TsNonNegativeAndNonDecreasing()
    {
        /*
         * CONTRACT: Given a TraceSession in QuestRun mode with OnQuestRunStart called
         *           When 3 events are written
         *           Then all events have Ts >= 0 and Ts values are non-decreasing
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart());
        session.Write(MakeObservation());
        session.Write(MakeDecision("step-a"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(3, inner.Count);
        var timestamps = inner.RecordedEvents.Select(e => e.Ts).ToList();
        for (var i = 0; i < timestamps.Count; i++)
        {
            Assert.True(timestamps[i] >= 0, $"Ts[{i}] = {timestamps[i]} must be >= 0");
            if (i > 0)
                Assert.True(timestamps[i] >= timestamps[i - 1],
                    $"Ts[{i}] = {timestamps[i]} must be >= Ts[{i - 1}] = {timestamps[i - 1]}");
        }
    }

    // =========================================================================
    // TS13: TraceSession resets seq on file reopen
    // =========================================================================

    [Fact]
    public void TS13_TraceSession_SeqResetsOnFileReopen()
    {
        /*
         * CONTRACT: Given a TraceSession in QuestRun mode:
         *           - OnQuestRunStart -> write 3 events (seq 0,1,2) -> OnQuestRunEnd
         *           - OnQuestRunStart -> write 2 events
         *           Then the second file's events have Seq values 0, 1 (reset on new file)
         */

        // Arrange
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, Factory);

        // First file: 3 events
        session.OnQuestRunStart(66130u);
        session.Write(MakeRunStart());
        session.Write(MakeObservation());
        session.Write(MakeDecision("step-a"));
        session.OnQuestRunEnd();

        // Act: second file: 2 events
        session.OnQuestRunStart(66130u);
        session.Write(MakeRunStart("run-002"));
        session.Write(MakeObservation());

        // Assert
        Assert.Equal(2, writerList.Count);

        // First file had seq 0,1,2
        var firstSeqs = writerList[0].RecordedEvents.Select(e => e.Seq).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, firstSeqs);

        // Second file reset to seq 0,1
        var secondSeqs = writerList[1].RecordedEvents.Select(e => e.Seq).ToList();
        Assert.Equal(new[] { 0, 1 }, secondSeqs);
    }

    // =========================================================================
    // TS14: TraceSession stamps events passed via WriteObservation
    // =========================================================================

    [Fact]
    public void TS14_WriteObservation_GetsStamped()
    {
        /*
         * CONTRACT: Given a TraceSession with file open
         *           When WriteObservation is called after one prior event
         *           Then the inner writer receives an ObservationEvent with Seq > 0
         *           and Ts >= 0
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Write a prior event to consume seq=0
        session.Write(MakeRunStart());

        // Act
        session.WriteObservation(
            "GetPlayerZone",
            JsonSerializer.SerializeToElement((object?)null),
            JsonSerializer.SerializeToElement(182),
            "run-001",
            DateTimeOffset.UtcNow);

        // Assert
        var inner = getWriter()!;
        Assert.Equal(2, inner.Count);
        var obs = Assert.IsType<ObservationEvent>(inner.RecordedEvents[1]);
        Assert.True(obs.Seq > 0, $"WriteObservation event Seq must be > 0, got {obs.Seq}");
        Assert.True(obs.Ts >= 0, $"WriteObservation event Ts must be >= 0, got {obs.Ts}");
    }

    // =========================================================================
    // TS15: Decision debounce with new Data shape
    // =========================================================================

    [Fact]
    public void TS15_DecisionDebounce_IdenticalDataShape_CollapseToOne()
    {
        /*
         * CONTRACT: Given a TraceSession with file open
         *           When two identical DecisionEvents are written
         *           (same RunId, same Data.StepId, same Data.ActionType)
         *           Then only one reaches the inner writer
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("travel-to-x", "navigate"));
        session.Write(MakeDecision("travel-to-x", "navigate"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count);
        Assert.IsType<DecisionEvent>(inner.RecordedEvents[0]);
    }

    // =========================================================================
    // TS16: Decision debounce -- different StepId in Data
    // =========================================================================

    [Fact]
    public void TS16_DecisionDebounce_DifferentStepIdInData_BothWritten()
    {
        /*
         * CONTRACT: Given a TraceSession with file open
         *           When DecisionEvent with Data.StepId="a" then
         *           DecisionEvent with Data.StepId="b" (same ActionType)
         *           Then both reach the inner writer
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("b", "navigate"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(2, inner.Count);
    }

    // =========================================================================
    // TS17: RunStartEvent dedup with base RunId
    // =========================================================================

    [Fact]
    public void TS17_RunStartDedup_SameRunId_OnlyOneWritten()
    {
        /*
         * CONTRACT: Given a TraceSession with file open
         *           When two RunStartEvents with the same RunId are written
         *           Then only one reaches the inner writer
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart("run-001"));
        session.Write(MakeRunStart("run-001")); // duplicate

        // Assert
        var inner = getWriter()!;
        var runStarts = inner.RecordedEvents.OfType<RunStartEvent>().ToList();
        Assert.Single(runStarts);
    }

    // =========================================================================
    // TS22: Concurrent writes produce unique seq values
    // =========================================================================

    [Fact]
    public async Task TS22_ConcurrentWrites_ProduceUniqueSeqValues()
    {
        /*
         * CONTRACT: Given a TraceSession in Always mode with 50 concurrent Write calls
         *           When all complete
         *           Then the inner writer received 50 events, and Seq values are a
         *           permutation of 0..49 (no duplicates, no gaps)
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        const int count = 50;

        // Act
        var tasks = Enumerable
            .Range(0, count)
            .Select(i => Task.Run(() => session.Write(MakeRunStart($"run-{i:D3}"))))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert
        var inner = getWriter()!;
        Assert.Equal(count, inner.Count);

        var seqValues = inner.RecordedEvents.Select(e => e.Seq).OrderBy(s => s).ToList();
        var expected = Enumerable.Range(0, count).ToList();
        Assert.Equal(expected, seqValues);
    }

    // =========================================================================
    // TS23: RunEndEvent gate bypass with new shape
    // =========================================================================

    [Fact]
    public void TS23_RunEndEvent_BypassesGate_WithNewShape()
    {
        /*
         * CONTRACT: Given a TraceSession in Recording mode with gate closed
         *           (after OnConfirmRecordStep) but file open
         *           When Write(new RunEndEvent { RunId = "r", Data = new() { Outcome = "done" } })
         *           is called
         *           Then the event reaches the inner writer (bypass still works)
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep(); // gate now closed, file still open

        // Act
        session.Write(MakeRunEnd());

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count);
        var evt = Assert.IsType<RunEndEvent>(inner.RecordedEvents[0]);
        Assert.Equal("done", evt.Data.Outcome);

        // Also verify it got stamped with seq/ts
        Assert.Equal(0, evt.Seq); // first event in file
        Assert.True(evt.Ts >= 0, $"Ts must be >= 0, got {evt.Ts}");
    }

    // =========================================================================
    // TS24: Observation dedup works with nested Data
    // =========================================================================

    [Fact]
    public void TS24_ObservationDedup_WithNestedData()
    {
        /*
         * CONTRACT: Given a TraceSession with file open
         *           When WriteObservation is called twice with the same
         *           (method, argument, value)
         *           Then only one ObservationEvent reaches the inner writer
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var argElement = JsonSerializer.SerializeToElement((object?)null);
        var valueElement = JsonSerializer.SerializeToElement(182);

        // Act
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", DateTimeOffset.UtcNow);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", DateTimeOffset.UtcNow);

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count); // second call suppressed by dedup

        // Verify the written event has nested Data shape
        var obs = Assert.IsType<ObservationEvent>(inner.RecordedEvents[0]);
        Assert.Equal("GetPlayerZone", obs.Data.Method);
    }

    // =========================================================================
    // Existing lifecycle tests (updated for new shapes)
    // These are regression tests that verify lifecycle behavior still works
    // after the type refactoring.
    // =========================================================================

    // ── Off mode ───────────────────────────────────────────────────────────

    [Fact]
    public void Off_WriteBeforeLifecycle_Dropped()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Off, _tracesDir, factory);
        session.Write(MakeRunStart());
        Assert.Null(getWriter());
    }

    [Fact]
    public void Off_OnPluginStart_FileRemainsClosedIsFileOpenFalse()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Off, _tracesDir, factory);
        session.OnPluginStart();
        Assert.False(session.IsFileOpen);
        Assert.Null(session.CurrentFilePath);
    }

    // ── Always mode ────────────────────────────────────────────────────────

    [Fact]
    public void Always_OnPluginStart_FileOpenGateOpen()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        Assert.True(session.IsFileOpen);
        Assert.True(session.IsWriteGateOpen);
    }

    [Fact]
    public void Always_WriteAfterOnPluginStart_ReachesInnerWriter()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        session.Write(MakeRunStart());
        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Always_OnPluginStop_FileClosesSubsequentWriteDropped()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        session.OnPluginStop();
        var countAtStop = getWriter()?.Count ?? 0;
        session.Write(MakeRunStart());
        Assert.False(session.IsFileOpen);
        Assert.Equal(countAtStop, getWriter()?.Count ?? 0);
    }

    // ── Authoring mode ─────────────────────────────────────────────────────

    [Fact]
    public void Authoring_OnEnterAuthorMode_FileOpenGateOpen()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        Assert.True(session.IsFileOpen);
        Assert.True(session.IsWriteGateOpen);
    }

    [Fact]
    public void Authoring_OnExitAuthoring_FileCloses()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnExitAuthoring();
        Assert.False(session.IsFileOpen);
        Assert.Null(session.CurrentFilePath);
    }

    // ── Recording mode ─────────────────────────────────────────────────────

    [Fact]
    public void Recording_OnEnterAuthorMode_NoFileYetGateClosed()
    {
        using var session = new TraceSession(TraceMode.Recording, _tracesDir);
        session.OnEnterAuthorMode(66130u);
        Assert.False(session.IsFileOpen);
        Assert.False(session.IsWriteGateOpen);
    }

    [Fact]
    public void Recording_OnOpenRecordModal_FileOpenGateOpen()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        Assert.True(session.IsFileOpen);
        Assert.True(session.IsWriteGateOpen);
    }

    [Fact]
    public void Recording_OnConfirmRecordStep_GateClosesFileStaysOpen()
    {
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep();
        Assert.False(session.IsWriteGateOpen);
        Assert.True(session.IsFileOpen);
    }

    [Fact]
    public void Recording_WriteAfterConfirm_Dropped()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep();
        var countAtConfirm = getWriter()!.Count;
        session.Write(MakeObservation());
        Assert.Equal(countAtConfirm, getWriter()!.Count);
    }

    // ── Decision debounce (updated for Data shape) ─────────────────────────

    [Fact]
    public void Debounce_ConsecutiveIdenticalDecisions_CollapseToOne()
    {
        /*
         * CONTRACT: Three consecutive identical DecisionEvent (same RunId,
         *           same Data.StepId, same Data.ActionType) -> only one written.
         */

        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeDecision("travel-to-x"));
        session.Write(MakeDecision("travel-to-x"));
        session.Write(MakeDecision("travel-to-x"));

        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Debounce_DifferentStepIdInData_BothWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("b", "navigate"));

        Assert.Equal(2, getWriter()!.Count);
    }

    [Fact]
    public void Debounce_SameStepIdDifferentActionType_BothWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("a", "interact"));

        Assert.Equal(2, getWriter()!.Count);
    }

    [Fact]
    public void Debounce_NonDecisionEventBetweenIdenticalDecisions_DoesNotResetDebounce()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeObservation());
        session.Write(MakeDecision("a", "navigate")); // should be suppressed

        var inner = getWriter()!;
        var decisions = inner.RecordedEvents.OfType<DecisionEvent>().ToList();
        Assert.Single(decisions);
        Assert.Equal(2, inner.Count); // 1 decision + 1 observation
    }

    [Fact]
    public void Debounce_DifferentRunId_BothWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        session.Write(MakeDecision("a", "navigate", runId: "run1"));
        session.Write(MakeDecision("a", "navigate", runId: "run2"));

        Assert.Equal(2, getWriter()!.Count);
    }

    [Fact]
    public void Debounce_NullStepIdInData_CollapseToOne()
    {
        /*
         * CONTRACT: Two consecutive DecisionEvents with Data.StepId == null
         *           and same Data.ActionType -> exactly ONE written.
         *           Pins the Data.StepId ?? "" coalesce.
         */

        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        var d1 = new DecisionEvent
        {
            RunId = "run-001",
            Data = new DecisionEvent.DecisionData { StepId = null, ActionType = "navigate" }
        };
        var d2 = new DecisionEvent
        {
            RunId = "run-001",
            Data = new DecisionEvent.DecisionData { StepId = null, ActionType = "navigate" }
        };

        session.Write(d1);
        session.Write(d2);

        Assert.Equal(1, getWriter()!.Count);
    }

    // ── RunStart dedup (updated for base RunId) ────────────────────────────

    [Fact]
    public void RunStartDedup_SameRunId_OnlyOneWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeRunStart("run-001"));
        session.Write(MakeRunStart("run-001"));

        var runStarts = getWriter()!.RecordedEvents.OfType<RunStartEvent>().ToList();
        Assert.Single(runStarts);
    }

    [Fact]
    public void RunStartDedup_DifferentRunIds_BothWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        session.Write(MakeRunStart("run-001"));
        session.Write(MakeRunStart("run-002"));

        var runStarts = getWriter()!.RecordedEvents.OfType<RunStartEvent>().ToList();
        Assert.Equal(2, runStarts.Count);
    }

    // ── Observation dedup (updated for nested Data) ────────────────────────

    [Fact]
    public void ObservationDedup_SameMethodArgValue_SuppressedSecondCall()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var argElement = JsonSerializer.SerializeToElement((object?)null);
        var valueElement = JsonSerializer.SerializeToElement(182);

        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", DateTimeOffset.UtcNow);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", DateTimeOffset.UtcNow);

        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void ObservationDedup_DifferentValue_BothWritten()
    {
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var argElement = JsonSerializer.SerializeToElement((object?)null);

        session.WriteObservation("GetPlayerZone", argElement, JsonSerializer.SerializeToElement(182), "run-001", DateTimeOffset.UtcNow);
        session.WriteObservation("GetPlayerZone", argElement, JsonSerializer.SerializeToElement(183), "run-001", DateTimeOffset.UtcNow);

        Assert.Equal(2, getWriter()!.Count);
    }

    // ── Dedup cleared on file reopen ───────────────────────────────────────

    [Fact]
    public void Dedup_ClearedOnFileReopen_DecisionReEmits()
    {
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, Factory);

        session.OnQuestRunStart(66130u);
        session.Write(MakeDecision("a", "navigate"));
        session.OnQuestRunEnd();

        session.OnQuestRunStart(66130u);
        session.Write(MakeDecision("a", "navigate"));

        Assert.Equal(2, writerList.Count);
        Assert.Equal(1, writerList[0].Count);
        Assert.Equal(1, writerList[1].Count); // not suppressed after reopen
    }

    // ── Seq stamping on events written by Write (verify stamping occurs) ───

    [Fact]
    public void Stamping_EventsWrittenViaWrite_HaveIncrementingSeq()
    {
        /*
         * CONTRACT: Events written via Write() (not just WriteObservation) get
         *           stamped with monotonically increasing Seq values.
         */

        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        session.Write(MakeRunStart());
        session.Write(MakeDecision("step-a"));
        session.Write(MakeRunEnd());

        var inner = getWriter()!;
        Assert.Equal(3, inner.Count);
        Assert.Equal(0, inner.RecordedEvents[0].Seq);
        Assert.Equal(1, inner.RecordedEvents[1].Seq);
        Assert.Equal(2, inner.RecordedEvents[2].Seq);
    }

    [Fact]
    public void Stamping_EventsHaveV1()
    {
        /*
         * CONTRACT: All events stamped by TraceSession retain V=1.
         */

        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        session.Write(MakeRunStart());
        session.Write(MakeDecision("step-a"));

        var inner = getWriter()!;
        foreach (var evt in inner.RecordedEvents)
        {
            Assert.Equal(1, evt.V);
        }
    }

    // ── Error handling ─────────────────────────────────────────────────────

    [Fact]
    public void Error_WriterFactoryThrows_NoCrashIsFileOpenFalse()
    {
        ITraceWriter ThrowingFactory(string _) => throw new IOException("disk full");
        using var session = new TraceSession(TraceMode.Always, _tracesDir, ThrowingFactory);

        var exception = Record.Exception(() => session.OnPluginStart());

        Assert.Null(exception);
        Assert.False(session.IsFileOpen);
    }

    [Fact]
    public void Error_InnerWriterWriteThrows_ExceptionSwallowedSessionUsable()
    {
        var throwingWriter = new ThrowingFakeTraceWriter();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, _ => throwingWriter);
        session.OnPluginStart();

        var ex1 = Record.Exception(() => session.Write(MakeRunStart()));
        var ex2 = Record.Exception(() => session.Write(MakeRunStart("run-002")));

        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    [Fact]
    public void Error_DisposeCalledTwice_NoException()
    {
        using var session = new TraceSession(TraceMode.Always, _tracesDir);
        session.OnPluginStart();
        session.Dispose();
        var ex = Record.Exception(() => session.Dispose());
        Assert.Null(ex);
    }

    // ── Thread safety ──────────────────────────────────────────────────────

    [Fact]
    public async Task ThreadSafety_FiftyConcurrentWrites_AllArrivedWithStampedSeq()
    {
        /*
         * CONTRACT: Given 50 concurrent Write calls,
         *           Then all 50 events arrive with unique Seq values.
         */

        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        const int count = 50;
        var tasks = Enumerable
            .Range(0, count)
            .Select(i => Task.Run(() => session.Write(MakeRunStart($"run-{i:D3}"))))
            .ToArray();
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        Assert.Null(ex);
        var inner = getWriter()!;
        Assert.Equal(count, inner.Count);

        // All seq values must be unique
        var seqValues = inner.RecordedEvents.Select(e => e.Seq).ToHashSet();
        Assert.Equal(count, seqValues.Count);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private sealed class ThrowingFakeTraceWriter : ITraceWriter
    {
        public void Write(TraceEvent evt) =>
            throw new InvalidOperationException("Simulated inner writer failure");
    }
}
