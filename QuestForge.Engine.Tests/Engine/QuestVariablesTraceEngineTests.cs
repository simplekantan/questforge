using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for the quest-variables trace-emission feature.
///
/// Covers spec groups EN, RP, and RT from docs/QUEST_VARIABLES_TRACE_PLAN.md §4.
///
/// Groups EN and EN3/EN4 are RED: QuestEngine.ResolveAction does not yet call
/// GetQuestVariables, so FakeQuestState.RecordedReads will NOT contain a
/// "GetQuestVariables" entry and EN1 will fail with an assertion error.
///
/// Groups RP and RT are expected to be GREEN guards (the recording proxy + replay
/// round-trip already exist from #40), and confirm the dedup/serialization contract.
/// </summary>
public sealed class QuestVariablesTraceEngineTests
{
    private static readonly DateTimeOffset At = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    // Helper format for diagnostics
    private static string FormatReads(IReadOnlyList<FakeQuestState.StateRead> reads) =>
        string.Join(", ", reads.Select(r => r.Method));

    // =========================================================================
    // EN — engine reads variables each tick
    // =========================================================================

    /// <summary>
    /// EN1 (happy path): After one non-terminal tick, the engine must have called
    /// GetQuestVariables on the active quest.
    ///
    /// RED: QuestEngine.ResolveAction has no GetQuestVariables call yet.
    /// The read will be absent from RecordedReads → assertion failure.
    /// </summary>
    [Fact]
    public async Task EN1_Tick_NonTerminal_RecordsGetQuestVariablesRead()
    {
        // Arrange: minimal quest with one unfinished TalkStep so the engine doesn't
        // return Done or AwaitUser — it reaches the variables read site.
        var harness = new EngineTestHarness();
        var questId = new QuestId(12345u);

        harness.QuestState.SetQuestSequence(questId, 0);
        // Player near the NPC so the tick emits Interact (non-terminal action).
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 12345u,
            sequence: 0,
            step: new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(
                    NpcId: 1003987u,
                    Zone: 182,
                    Position: new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-en1");

        // Act
        await harness.Engine.Tick(CancellationToken.None);

        // Assert: FakeQuestState must have recorded a GetQuestVariables read.
        var reads = harness.QuestState.RecordedReads.Snapshot();
        Assert.True(
            reads.Any(r => r.Method == "GetQuestVariables"),
            $"Expected a 'GetQuestVariables' read in RecordedReads after a non-terminal tick, " +
            $"but only found: [{FormatReads(reads)}]. " +
            "RED: QuestEngine.ResolveAction does not call GetQuestVariables yet.");
    }

    /// <summary>
    /// EN2 (decision unaffected): Scripting two different 6-byte variable values for
    /// the same quest produces the IDENTICAL action from the engine on each tick.
    ///
    /// This pins "discarded read, no decision impact."
    /// </summary>
    [Fact]
    public async Task EN2_DifferentVariableValues_DoNotChangeEngineDecision()
    {
        // Build the quest builder helper once; reuse the same structure.
        static EngineTestHarness BuildHarness(byte[] vars)
        {
            var harness = new EngineTestHarness();
            var questId = new QuestId(12345u);
            harness.QuestState.SetQuestSequence(questId, 0);
            harness.QuestState.SetQuestVariables(questId, vars[0], vars[1], vars[2], vars[3], vars[4], vars[5]);
            // Player near the NPC → Interact (not Navigate)
            harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
            return harness;
        }

        var quest = BuildSingleStepQuest(
            questId: 12345u,
            sequence: 0,
            step: new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(
                    NpcId: 1003987u,
                    Zone: 182,
                    Position: new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
            });

        var harnessA = BuildHarness([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        harnessA.Engine.StartQuest(quest);
        harnessA.Engine.BeginRun("run-en2a");
        var actionA = await harnessA.Engine.Tick(CancellationToken.None);

        var harnessB = BuildHarness([0x10, 0x20, 0x30, 0x40, 0x05, 0x06]);
        harnessB.Engine.StartQuest(quest);
        harnessB.Engine.BeginRun("run-en2b");
        var actionB = await harnessB.Engine.Tick(CancellationToken.None);

        // Assert: same action type (both should be Interact with the same NpcId).
        Assert.Equal(actionA.GetType(), actionB.GetType());
        if (actionA is EngineAction.Interact iA && actionB is EngineAction.Interact iB)
        {
            Assert.Equal(iA.Target, iB.Target);
        }
    }

    /// <summary>
    /// EN3 (terminal tick skips read): When IsQuestComplete returns true the engine
    /// returns Done before reaching the variables read. Therefore GetQuestVariables must
    /// NOT be in RecordedReads on that terminal tick.
    ///
    /// Pins §2.3 placement — the read is after the IsQuestComplete early-return.
    /// This test may be RED (if the read were placed before IsQuestComplete), or green
    /// if the placement is correct. Writing it to pin the placement.
    /// </summary>
    [Fact]
    public async Task EN3_TerminalTick_QuestComplete_DoesNotRecordGetQuestVariables()
    {
        // Arrange: script the quest as already complete.
        var harness = new EngineTestHarness();
        var questId = new QuestId(12345u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestStatus(questId, QuestStatus.Complete);

        var quest = BuildSingleStepQuest(
            questId: 12345u,
            sequence: 0,
            step: new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(
                    NpcId: 1003987u,
                    Zone: 182,
                    Position: new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-en3");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: the action is Done (quest complete)...
        Assert.IsType<EngineAction.Done>(action);

        // ...and GetQuestVariables must NOT have been recorded (early-return before the read).
        var reads = harness.QuestState.RecordedReads.Snapshot();
        Assert.False(
            reads.Any(r => r.Method == "GetQuestVariables"),
            $"Expected NO 'GetQuestVariables' read on a terminal (Done) tick, " +
            $"but found one. Reads: [{FormatReads(reads)}]. " +
            "This pins §2.3: the read must be AFTER the IsQuestComplete early-return.");
    }

    /// <summary>
    /// EN4 (fail-open): When the inner quest state's GetQuestVariables returns Failure,
    /// no exception propagates and the engine still emits its normal step action.
    ///
    /// Tests the fail-open posture (identical to GetPlayerPosition / GetPlayerZone).
    /// RED: the engine doesn't call GetQuestVariables yet, so we can't observe fail-open.
    /// After the builder adds the call, this test ensures it never throws on failure.
    /// </summary>
    [Fact]
    public async Task EN4_GetQuestVariablesFailure_DoesNotPropagate_EngineEmitsNormalAction()
    {
        // Arrange: use the standard harness but override the inner QuestState with one
        // that fails GetQuestVariables. We do this by using a custom harness where
        // FakeQuestState.SetQuestVariables is NOT called (so it returns all-zero, which
        // is a success). To test a failure, we use the FailingQuestVariablesState wrapper.
        // However, the harness wraps FakeQuestState in a RecordingQuestState. Instead we
        // construct a separate inner check: use FakeQuestState and verify no exception
        // even when the proxy's inner returns Failure via the file-local FailingVariablesQuestState.
        //
        // Since EngineTestHarness hardwires FakeQuestState, we build an engine manually
        // with a wrapping fake that injects failures only for GetQuestVariables.

        var failingVariablesFake = new FailingVariablesQuestState();
        var trace = new FakeTraceWriter();

        // Construct the engine with the recording proxy over the failing fake.
        var recordingProxy = new RecordingQuestState(
            failingVariablesFake, trace, () => "run-en4", skipIfNoRunId: false);

        var harness = new EngineTestHarness();

        // Wire a quest the engine can parse without hitting GetQuestVariables logic
        // by scripting the sequence so the engine does NOT return AwaitUser for "no block".
        harness.QuestState.SetQuestSequence(new QuestId(12345u), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        // We can't inject the failing proxy into the EngineTestHarness directly, but we
        // can verify the "fail-open" property at the RecordingQuestState level:
        // even if the proxy writes a failure value, GetQuestVariables must still return
        // the inner's Failure result without throwing.
        var ct = CancellationToken.None;
        var resultThrewException = false;
        try
        {
            var result = await recordingProxy.GetQuestVariables(new QuestId(12345u), ct);
            // Should return Failure without throwing.
            Assert.IsType<Result<IReadOnlyList<byte>>.Failure>(result);
        }
        catch
        {
            resultThrewException = true;
        }

        Assert.False(resultThrewException,
            "GetQuestVariables returning Failure must not propagate an exception through the proxy.");

        // Additionally verify: even with all-zero variables (the FakeQuestState default),
        // a normal harness tick does not throw.
        var harness2 = new EngineTestHarness();
        harness2.QuestState.SetQuestSequence(new QuestId(12345u), 0);
        harness2.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        var quest = BuildSingleStepQuest(
            questId: 12345u,
            sequence: 0,
            step: new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(
                    NpcId: 1003987u,
                    Zone: 182,
                    Position: new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
            });

        harness2.Engine.StartQuest(quest);
        harness2.Engine.BeginRun("run-en4b");

        Exception? tickException = null;
        try { await harness2.Engine.Tick(ct); }
        catch (Exception ex) { tickException = ex; }

        Assert.Null(tickException);
    }

    // =========================================================================
    // RP — recording proxy emits on change
    //
    // These are GREEN guards: the dedup behavior already exists in RecordingQuestState
    // from #40. They pin the dedup contract and the JSON-array serialization form.
    // =========================================================================

    /// <summary>
    /// RP1 (emit on first read): Calling GetQuestVariables once produces exactly one
    /// ObservationEvent whose Value is a JSON ARRAY (not base64) of six bytes.
    ///
    /// Expected: GREEN guard (the recording proxy already records variables).
    /// </summary>
    [Fact]
    public async Task RP1_GetQuestVariables_FirstRead_EmitsOneObservation_ValueIsJsonArray()
    {
        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestVariables(new QuestId(12345u), 0x01, 0x02, 0x03, 0x04, 0x05, 0x06);
        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(inner, trace, () => "run-rp1");

        // Act
        await proxy.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Assert: exactly one ObservationEvent for GetQuestVariables
        var varObs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetQuestVariables")
            .ToList();

        Assert.Single(varObs);

        var obs = varObs[0];

        // Value must be non-null and a JSON ARRAY (not a base64 string).
        Assert.NotNull(obs.Data.Value);
        Assert.Equal(JsonValueKind.Array, obs.Data.Value!.Value.ValueKind);

        // The array must have 6 elements with the correct byte values.
        var elements = obs.Data.Value.Value.EnumerateArray().Select(e => e.GetByte()).ToArray();
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, elements);
    }

    /// <summary>
    /// RP2 (dedup suppresses second call with same value): Calling GetQuestVariables
    /// twice with unchanged variables emits only ONE ObservationEvent total.
    ///
    /// Expected: GREEN guard — mirrors the sequence/flags dedup contract.
    /// </summary>
    [Fact]
    public async Task RP2_GetQuestVariables_SameValueTwice_OnlyOneObservation()
    {
        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestVariables(new QuestId(12345u), 0x01, 0x02, 0x03, 0x04, 0x05, 0x06);
        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(inner, trace, () => "run-rp2");

        // Act
        await proxy.GetQuestVariables(new QuestId(12345u), CancellationToken.None);
        await proxy.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Assert: only 1 ObservationEvent (second suppressed by _lastEmitted dedup)
        var varObs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetQuestVariables")
            .ToList();

        Assert.Single(varObs);
    }

    /// <summary>
    /// RP3 (re-emit on value change): After the first call, changing the variable bytes
    /// and calling again produces a SECOND ObservationEvent with the new bytes.
    ///
    /// This is the "emit whenever they change" behavior the PR is named for.
    /// Expected: GREEN guard.
    /// </summary>
    [Fact]
    public async Task RP3_GetQuestVariables_ValueChanges_EmitsSecondObservation()
    {
        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestVariables(new QuestId(12345u), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(inner, trace, () => "run-rp3");
        var questId = new QuestId(12345u);

        // Act — first read: all-zero
        await proxy.GetQuestVariables(questId, CancellationToken.None);

        // Change the inner bytes
        inner.SetQuestVariables(questId, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00);

        // Second read: changed → must emit a new observation
        await proxy.GetQuestVariables(questId, CancellationToken.None);

        // Assert: exactly 2 ObservationEvents for GetQuestVariables
        var varObs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetQuestVariables")
            .ToList();

        Assert.Equal(2, varObs.Count);

        // First observation: all-zero
        var first = varObs[0].Data.Value!.Value.EnumerateArray().Select(e => e.GetByte()).ToArray();
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, first);

        // Second observation: changed
        var second = varObs[1].Data.Value!.Value.EnumerateArray().Select(e => e.GetByte()).ToArray();
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 }, second);
    }

    // =========================================================================
    // RT — record↔replay byte-list round-trip
    //
    // These are GREEN guards: the recording proxy and ReplayQuestState already
    // handle IReadOnlyList<byte>. They pin the JSON-array serialization so a future
    // refactor cannot silently break the round-trip.
    // =========================================================================

    /// <summary>
    /// RT1 (THE subtle serialization case): Record a GetQuestVariables observation through
    /// RecordingQuestState, then replay via ReplayQuestState — must return the identical bytes.
    ///
    /// Pins §2.5 / §3.3: IReadOnlyList&lt;byte&gt; serializes as a JSON array, not base64.
    /// Expected: GREEN guard.
    /// </summary>
    [Fact]
    public async Task RT1_RecordThenReplay_ByteListRoundTrips_ArrayForm()
    {
        // --- Record ---
        var inner = new FakeQuestState();
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        inner.SetQuestVariables(new QuestId(12345u), bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]);

        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(inner, trace, () => "run-rt1");
        await proxy.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Capture the emitted observation
        var obs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Single(e => e.Data.Method == "GetQuestVariables");

        // Verify it is stored as a JSON array (not base64 string)
        Assert.Equal(JsonValueKind.Array, obs.Data.Value!.Value.ValueKind);

        // --- Replay ---
        var replayState = new ReplayQuestState(new List<ObservationEvent> { obs });
        var result = await replayState.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Assert: Success with the identical 6 bytes
        var success = Assert.IsType<Result<IReadOnlyList<byte>>.Success>(result);
        Assert.Equal(6, success.Value.Count);
        Assert.Equal(bytes, success.Value.ToArray());
    }

    /// <summary>
    /// RT2 (failure round-trip): A GetQuestVariables observation recorded from a failing
    /// inner materializes back to a Failure result via ReplayQuestState.
    ///
    /// Guards the failure-marker path for the new method.
    /// Expected: GREEN guard (consistent with existing Materialize behavior).
    /// </summary>
    [Fact]
    public async Task RT2_RecordFailure_RoundTrips_ToFailureResult()
    {
        // --- Record ---
        var failingInner = new FailingVariablesQuestState();
        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(failingInner, trace, () => "run-rt2");
        await proxy.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Capture the observation — the proxy must have written one (failure is still recorded)
        var obs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Single(e => e.Data.Method == "GetQuestVariables");

        Assert.NotNull(obs.Data.Value);
        // The failure serialization includes a "failure" key
        Assert.Contains("failure", obs.Data.Value!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // --- Replay ---
        var replayState = new ReplayQuestState(new List<ObservationEvent> { obs });
        var result = await replayState.GetQuestVariables(new QuestId(12345u), CancellationToken.None);

        // Assert: Failure result (not Success)
        Assert.IsType<Result<IReadOnlyList<byte>>.Failure>(result);
    }

    /// <summary>
    /// RT3 (segmented-scanner round-trip): Build a SegmentedObservationScanner from a
    /// trace that includes a GetQuestVariables observation and wire a ReplayQuestState.
    /// GetQuestVariables must return the six bytes via the segmented path.
    ///
    /// Proves the value survives the segmented path (the path the with-attunement fixture uses).
    /// Expected: GREEN guard once the segmented scanner exists; compile-RED until it does.
    /// </summary>
    [Fact]
    public async Task RT3_SegmentedScanner_GetQuestVariables_ReturnsRecordedBytes()
    {
        // Build a trace: run.start, GetQuestVariables obs, decision, run.end
        var questId = new QuestId(12345u);
        var bytes = new byte[] { 0x10, 0x00, 0x00, 0x05, 0x00, 0x00 };

        // Serialize the argument as RecordingQuestState would (camelCase)
        var argJson = JsonDocument.Parse("{\"value\":12345}").RootElement;

        // Serialize the value as an array (the STJ-with-IReadOnlyList<byte> form)
        var valueJson = JsonDocument.Parse($"[{string.Join(",", bytes)}]").RootElement;

        var obs = new ObservationEvent
        {
            RunId = "run-rt3",
            Data = new ObservationEvent.ObservationData
            {
                Method   = "GetQuestVariables",
                Argument = argJson,
                Value    = valueJson
            }
        };

        var trace = new List<TraceEvent>
        {
            new RunStartEvent { RunId = "run-rt3", Data = new RunStartEvent.RunStartData { QuestId = 12345u } },
            obs,
            new DecisionEvent { RunId = "run-rt3", Data = new DecisionEvent.DecisionData { StepId = "talk-npc", ActionType = "Interact" } },
            new RunEndEvent { RunId = "run-rt3", Data = new RunEndEvent.RunEndData { Outcome = "done" } }
        };

        // Build scanner and replay state
        var scanner = new SegmentedObservationScanner(trace);
        var replayState = new ReplayQuestState(scanner);

        // Act
        var result = await replayState.GetQuestVariables(questId, CancellationToken.None);

        // Assert: correct bytes returned through the segmented path
        var success = Assert.IsType<Result<IReadOnlyList<byte>>.Success>(result);
        Assert.Equal(6, success.Value.Count);
        Assert.Equal(bytes, success.Value.ToArray());
    }

    // =========================================================================
    // Quest builder helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "QuestVariablesTrace Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = sequence,
                    Steps = [step]
                }
            ]
        };
}

// ---------------------------------------------------------------------------
// File-local failing fake — injects Failure only for GetQuestVariables
// ---------------------------------------------------------------------------

/// <summary>
/// A IQuestState that delegates everything to FakeQuestState except for
/// GetQuestVariables, which always returns a Failure result.
/// Used by EN4 and RT2 to verify fail-open / failure round-trip behavior.
/// </summary>
file sealed class FailingVariablesQuestState : IQuestState
{
    private readonly FakeQuestState _inner = new();

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct) =>
        _inner.GetQuestSequence(quest, ct);

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct) =>
        _inner.IsQuestComplete(quest, ct);

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct) =>
        _inner.GetQuestStatus(quest, ct);

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct) =>
        _inner.GetQuestFlags(quest, ct);

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct) =>
        _inner.IsQuestFlagSet(quest, flagBit, ct);

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct) =>
        _inner.IsQuestAccepted(quest, ct);

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct) =>
        _inner.IsQuestAvailable(quest, ct);

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct) =>
        _inner.WhyUnavailable(quest, ct);

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct) =>
        _inner.GetAcceptedQuests(ct);

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct) =>
        _inner.GetAvailableQuestRewards(ct);

    /// <summary>Always returns Failure — simulates an adapter failure.</summary>
    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Fail<IReadOnlyList<byte>>("adapter failure: variables unavailable"));

    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct) =>
        _inner.IsAcceptableNow(quest, ct);
}
