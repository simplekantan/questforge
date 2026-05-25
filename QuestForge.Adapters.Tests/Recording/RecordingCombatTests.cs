using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Adapters.Tests.Recording;

/// <summary>
/// RED PHASE — G-RC: RecordingCombat proxy contract tests.
///
/// All tests fail RED because RecordingCombat.SetTarget/ClearTarget/StartRotation/StopRotation
/// throw NotImplementedException (stubs). The assertions about emitted observations
/// therefore never reach the Assert — the test fails on the NotImplementedException first.
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §G-RC
/// </summary>
public sealed class RecordingCombatTests
{
    private static string FormatEvents(FakeTraceWriter trace)
        => string.Join(", ", trace.RecordedEvents.OfType<ObservationEvent>().Select(e => e.Method));

    private static RecordingCombat BuildProxy(
        FakeCombat inner,
        FakeTraceWriter trace,
        Func<string?> runId,
        bool skipIfNoRunId = false)
        => new RecordingCombat(inner, trace, runId, clock: null, skipIfNoRunId: skipIfNoRunId);

    // -------------------------------------------------------------------------
    // RC-settarget-records
    // -------------------------------------------------------------------------

    /// <summary>
    /// RC-settarget-records: SetTarget emits exactly one ObservationEvent with Method "SetTarget",
    /// its Argument encodes actorId.Value == 9, AND the inner FakeCombat records the SetTarget.
    /// </summary>
    [Fact]
    public async Task RC_SetTargetRecords_EmitsOneObservation_AndDelegatesToInner()
    {
        // RED: RecordingCombat.SetTarget throws NotImplementedException.
        //
        // Given RecordingCombat over FakeCombat, non-empty runId.
        // When SetTarget(new ActorId(9)).
        // Then exactly one ObservationEvent with Method=="SetTarget" is written.
        //      Its Argument JSON encodes actorId value 9.
        //      Inner FakeCombat.RecordedTargets has exactly one SetTarget(ActorId(9)).

        var inner = new FakeCombat();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-rc");

        await proxy.SetTarget(new ActorId(9), CancellationToken.None);

        // Exactly one observation written
        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Equal(1, obs.Count);
        Assert.Equal("SetTarget", obs[0].Method);

        // Argument encodes actorId = 9 (as {"actorId":9} or {"value":9} — builder decides shape;
        // the test checks it is non-null and contains "9")
        Assert.NotNull(obs[0].Argument);
        var argText = obs[0].Argument!.Value.GetRawText();
        Assert.Contains("9", argText, StringComparison.Ordinal);

        // Inner received the call
        var targets = inner.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
        Assert.False(targets[0].IsClear);
        Assert.Equal(new ActorId(9), targets[0].Target!.Value);
    }

    // -------------------------------------------------------------------------
    // RC-clear-start-stop-record
    // -------------------------------------------------------------------------

    /// <summary>
    /// RC-clear-start-stop-record: ClearTarget, StartRotation, StopRotation each emit
    /// one ObservationEvent with their distinct method names, in order, and delegate to inner.
    /// </summary>
    [Fact]
    public async Task RC_ClearStartStopRecord_ThreeMethods_ThreeObservationsInOrder()
    {
        // RED: all three methods throw NotImplementedException.
        //
        // Given the proxy. When ClearTarget, StartRotation, StopRotation each called once.
        // Then three ObservationEvents with Method in order:
        //   "ClearTarget", "StartRotation", "StopRotation".
        // Inner FakeCombat.RecordedTargets has one clear; RecordedRotation has Start then Stop.

        var inner = new FakeCombat();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-rc2");

        await proxy.ClearTarget(CancellationToken.None);
        await proxy.StartRotation(CancellationToken.None);
        await proxy.StopRotation(CancellationToken.None);

        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Equal(3, obs.Count);
        Assert.Equal("ClearTarget",    obs[0].Method);
        Assert.Equal("StartRotation",  obs[1].Method);
        Assert.Equal("StopRotation",   obs[2].Method);

        // Inner delegate-through: one clear target
        var clears = inner.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.Equal(1, clears.Count);

        // Inner: Start then Stop
        var rot = inner.RecordedRotation.Snapshot();
        Assert.Equal(2, rot.Count);
        Assert.Equal("StartRotation", rot[0].Method);
        Assert.Equal("StopRotation",  rot[1].Method);
    }

    // -------------------------------------------------------------------------
    // RC-no-dedup
    // -------------------------------------------------------------------------

    /// <summary>
    /// RC-no-dedup: calling SetTarget twice with the same ActorId emits TWO observations
    /// (combat acts are an ordered command log, not value-change deduped).
    /// Pins A6's no-dedup decision vs RecordingGameStateProvider's dedup.
    /// </summary>
    [Fact]
    public async Task RC_NoDedup_SetTargetCalledTwiceSameActor_TwoObservationsWritten()
    {
        // RED: SetTarget throws NotImplementedException.
        //
        // Given proxy. When SetTarget(9) called twice with no change.
        // Then TWO SetTarget ObservationEvents written (not one — no dedup on combat acts).

        var inner = new FakeCombat();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-rc3");

        await proxy.SetTarget(new ActorId(9), CancellationToken.None);
        await proxy.SetTarget(new ActorId(9), CancellationToken.None);

        var obs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "SetTarget")
            .ToList();

        Assert.Equal(2, obs.Count);
    }

    // -------------------------------------------------------------------------
    // RC-reads-not-recorded
    // -------------------------------------------------------------------------

    /// <summary>
    /// RC-reads-not-recorded: IsRotationModuleAvailable and GetRotationModule delegate
    /// through unrecorded — no ObservationEvents emitted for them, inner result returned.
    /// </summary>
    [Fact]
    public async Task RC_ReadsNotRecorded_IsRotationModuleAvailableAndGetRotationModule_NoObservations()
    {
        // These two read methods are pure availability queries — no side effects, not logged.
        // They should pass through to inner immediately (stub delegates to _inner.X).
        // This test PASSES already if the stubs correctly delegate reads (they do).

        var inner = new FakeCombat();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-rc4");

        var availResult = await proxy.IsRotationModuleAvailable(CancellationToken.None);
        var moduleResult = await proxy.GetRotationModule(CancellationToken.None);

        // No observations emitted for read-only methods
        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Empty(obs);

        // Inner results returned correctly
        Assert.True(availResult.IsSuccess);
        Assert.True(availResult.ValueOrThrow); // FakeCombat default: true
        Assert.True(moduleResult.IsSuccess);
        Assert.Equal("FakeCombat", moduleResult.ValueOrThrow.Name);
    }

    // -------------------------------------------------------------------------
    // RC-skip-if-no-runid
    // -------------------------------------------------------------------------

    /// <summary>
    /// RC-skip-if-no-runid: when skipIfNoRunId:true and runId returns null,
    /// no observation is written for SetTarget but inner FakeCombat still records the call.
    /// Delegate-through always happens; only the trace write is gated.
    /// Mirrors RecordingGameStateProvider's skipIfNoRunId behavior.
    /// </summary>
    [Fact]
    public async Task RC_SkipIfNoRunId_NullRunId_NoObservationButInnerCalled()
    {
        // RED: RecordingCombat.SetTarget throws NotImplementedException.

        var inner = new FakeCombat();
        var trace = new FakeTraceWriter();
        // skipIfNoRunId: true, runId accessor returns null (simulates pre-BeginRun state)
        var proxy = BuildProxy(inner, trace, () => null, skipIfNoRunId: true);

        await proxy.SetTarget(new ActorId(9), CancellationToken.None);

        // No observation written (gate: skipIfNoRunId && runId is null)
        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Empty(obs);

        // Inner still received the call
        var targets = inner.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
        Assert.Equal(new ActorId(9), targets[0].Target!.Value);
    }
}
