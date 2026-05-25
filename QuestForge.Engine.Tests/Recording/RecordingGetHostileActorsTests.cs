using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Recording;

/// <summary>
/// RED PHASE — G-RH: GetHostileActors record/scan flip + round-trip tests.
///
/// Current behavior (part A, no flip):
///   RecordingGameStateProvider.GetHostileActors delegates through WITHOUT calling Record().
///   ReplayGameStateProvider.GetHostileActors returns empty WITHOUT scanning.
///
/// After the builder's flip (part B):
///   RecordingGameStateProvider.GetHostileActors calls Record(nameof(GetHostileActors), radius, result).
///   ReplayGameStateProvider.GetHostileActors calls ScanNext(nameof(GetHostileActors), radius).
///
/// These tests fail RED against the current no-record/no-scan behavior.
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §G-RH, §A5
/// </summary>
public sealed class RecordingGetHostileActorsTests
{
    private static readonly DateTimeOffset At = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    private static RecordingGameStateProvider BuildRecordingProxy(
        FakeGameStateProvider inner,
        FakeTraceWriter trace,
        string runId = "run-rh")
        => new RecordingGameStateProvider(inner, trace, () => runId);

    private static HostileActor MakeHostile(ulong id, uint dataId, float dist = 5f,
        bool isTargetable = true, bool isDead = false, bool isTargetingPlayer = false)
        => new HostileActor(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            dist,
            isTargetable,
            isDead,
            isTargetingPlayer,
            OnPlayerEnmityList: false,
            HasQuestMarker: false);

    private static string FormatObs(FakeTraceWriter trace)
        => string.Join(", ", trace.RecordedEvents.OfType<ObservationEvent>().Select(e => e.Method));

    // -------------------------------------------------------------------------
    // RH-record-emits — flip: no-record → record
    // -------------------------------------------------------------------------

    /// <summary>
    /// RH-record-emits: After the flip, RecordingGameStateProvider.GetHostileActors records
    /// a GetHostileActors ObservationEvent (arg=30, value=array with id.value==9, dataId==100).
    ///
    /// RED against current no-Record behavior: zero observations are written today.
    /// </summary>
    [Fact]
    public async Task RH_RecordEmits_GetHostileActors_EmitsObservationWithRadiusAndActorArray()
    {
        // RED: RecordingGameStateProvider.GetHostileActors currently has no Record() call.
        // Today this test fails: Assert.Equal(1, obs.Count) → actual count is 0.

        var inner = new FakeGameStateProvider();
        inner.AddHostileActor(MakeHostile(9, 100, dist: 5f));
        var trace = new FakeTraceWriter();
        var proxy = BuildRecordingProxy(inner, trace);

        await proxy.GetHostileActors(30f, CancellationToken.None);

        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Equal(1, obs.Count);
        Assert.Equal("GetHostileActors", obs[0].Method);

        // Argument must encode the radius (30 → serialized as 30)
        Assert.NotNull(obs[0].Argument);
        var argText = obs[0].Argument!.Value.GetRawText();
        Assert.Equal("30", argText); // float 30f → camelCase opts → "30"

        // Value must be a JSON array with one element whose id.value == 9 and dataId == 100
        Assert.NotNull(obs[0].Value);
        var val = obs[0].Value!.Value;
        Assert.Equal(JsonValueKind.Array, val.ValueKind);
        Assert.Equal(1, val.GetArrayLength());
        var actor0 = val[0];
        // id.value == 9
        Assert.Equal(9uL, actor0.GetProperty("id").GetProperty("value").GetUInt64());
        // dataId == 100
        Assert.Equal(100u, actor0.GetProperty("dataId").GetUInt32());
        // isTargetable == true
        Assert.True(actor0.GetProperty("isTargetable").GetBoolean());
    }

    // -------------------------------------------------------------------------
    // RH-record-dedup — value-change dedup applies (like GetNearbyNpcs)
    // -------------------------------------------------------------------------

    /// <summary>
    /// RH-record-dedup: calling GetHostileActors twice with the same actors emits ONE observation
    /// (existing Record&lt;T&gt; value-change dedup). When actors change, a second is emitted.
    /// </summary>
    [Fact]
    public async Task RH_RecordDedup_SameActorsTwice_OnlyOneObservation_ThenChangeEmitsSecond()
    {
        // RED: today zero observations are emitted because Record() is never called.
        // After flip: first call emits one; second identical → suppressed; actor change → second.

        var inner = new FakeGameStateProvider();
        inner.AddHostileActor(MakeHostile(9, 100, dist: 5f));
        var trace = new FakeTraceWriter();
        var proxy = BuildRecordingProxy(inner, trace);

        await proxy.GetHostileActors(30f, CancellationToken.None); // first call → emits
        await proxy.GetHostileActors(30f, CancellationToken.None); // identical → deduped, no emit

        var obsAfterTwo = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetHostileActors")
            .ToList();
        Assert.Equal(1, obsAfterTwo.Count);

        // Change the world: actor dies
        inner.ClearHostileActors();
        inner.AddHostileActor(MakeHostile(9, 100, dist: 5f, isDead: true, isTargetable: false));

        await proxy.GetHostileActors(30f, CancellationToken.None); // changed → second emits

        var obsAfterChange = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetHostileActors")
            .ToList();
        Assert.Equal(2, obsAfterChange.Count);
    }

    // -------------------------------------------------------------------------
    // RH-replay-scans — flip: no-scan → scan
    // -------------------------------------------------------------------------

    /// <summary>
    /// RH-replay-scans: After the flip, ReplayGameStateProvider.GetHostileActors scans
    /// the recorded observation and returns the actor. Today it returns empty (no scan).
    ///
    /// RED against current no-scan behavior:
    ///   Assert.Equal(1, actors.Count) fails → actual count is 0.
    /// </summary>
    [Fact]
    public async Task RH_ReplayScans_LegacyScanner_ReturnsRecordedActor()
    {
        // Build a trace containing one GetHostileActors observation with ActorId 9.
        var actorList = new List<HostileActor> { MakeHostile(9, 100) };
        var actorJson = JsonSerializer.SerializeToElement(actorList, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var obs = new ObservationEvent(
            RunId: "run-rh-replay",
            Method: "GetHostileActors",
            Argument: JsonSerializer.SerializeToElement(30f, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Value: actorJson,
            At: At);

        // Legacy (IReadOnlyList) scanner path
        var replay = new ReplayGameStateProvider(new[] { obs });

        var result = await replay.GetHostileActors(30f, CancellationToken.None);

        Assert.True(result.IsSuccess,
            $"Expected success but got failure: {(result is Result<System.Collections.Generic.IReadOnlyList<HostileActor>>.Failure f ? f.Reason : "?")}");
        var actors = result.ValueOrThrow;
        Assert.Equal(1, actors.Count);
        Assert.Equal(new ActorId(9), actors[0].Id);
        Assert.Equal(100u, actors[0].DataId);
    }

    // -------------------------------------------------------------------------
    // RH-roundtrip-byte-identical — all nine HostileActor fields survive record→replay
    // -------------------------------------------------------------------------

    /// <summary>
    /// RH-roundtrip-byte-identical: a populated HostileActor (all nine fields non-default)
    /// round-trips through RecordingGameStateProvider.Record → ReplayGameStateProvider.ScanNext
    /// byte-identically. Pins A5's no-custom-converter decision.
    ///
    /// RED: today the Recording side emits no observation, so the Replay side cannot find one
    /// and throws ReplayObservationStarvationException.
    /// </summary>
    [Fact]
    public async Task RH_RoundtripByteIdentical_AllNineFields_SurviveRecordReplay()
    {
        // Build a fully-populated actor with every bool distinct.
        var original = new HostileActor(
            Id:                 new ActorId(7),
            DataId:             200u,
            Position:           new WorldPosition(1f, 2f, 3f),
            DistanceToPlayer:   4.5f,
            IsTargetable:       true,
            IsDead:             false,
            IsTargetingPlayer:  true,   // aggro
            OnPlayerEnmityList: false,
            HasQuestMarker:     true);

        // Record it through the recording proxy
        var inner = new FakeGameStateProvider();
        inner.AddHostileActor(original);
        var trace = new FakeTraceWriter();
        var proxy = BuildRecordingProxy(inner, trace);

        await proxy.GetHostileActors(30f, CancellationToken.None);

        // Exactly one observation emitted
        var obs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetHostileActors")
            .ToList();
        Assert.Equal(1, obs.Count);

        // Replay it through the ReplayGameStateProvider (legacy scanner)
        var replay = new ReplayGameStateProvider(new[] { obs[0] });
        var result = await replay.GetHostileActors(30f, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var actors = result.ValueOrThrow;
        Assert.Equal(1, actors.Count);
        var roundTripped = actors[0];

        // All nine fields must be byte-identical
        Assert.Equal(new ActorId(7),       roundTripped.Id);
        Assert.Equal(7uL,                  roundTripped.Id.Value); // ulong round-trip, not base64
        Assert.Equal(200u,                 roundTripped.DataId);
        Assert.Equal(1f,                   roundTripped.Position.X, precision: 3);
        Assert.Equal(2f,                   roundTripped.Position.Y, precision: 3);
        Assert.Equal(3f,                   roundTripped.Position.Z, precision: 3);
        Assert.Equal(4.5f,                 roundTripped.DistanceToPlayer, precision: 3);
        Assert.True(roundTripped.IsTargetable,       "IsTargetable must round-trip as true");
        Assert.False(roundTripped.IsDead,            "IsDead must round-trip as false");
        Assert.True(roundTripped.IsTargetingPlayer,  "IsTargetingPlayer must round-trip as true");
        Assert.False(roundTripped.OnPlayerEnmityList,"OnPlayerEnmityList must round-trip as false");
        Assert.True(roundTripped.HasQuestMarker,     "HasQuestMarker must round-trip as true");
    }

    // -------------------------------------------------------------------------
    // RH-replay-radius-mismatch-starves — mismatched radius → starvation
    // -------------------------------------------------------------------------

    /// <summary>
    /// RH-replay-radius-mismatch-starves: replaying a trace recorded at radius 30 with
    /// radius 99 throws ReplayObservationStarvationException (scanner cannot find a match).
    /// Pins A5: ScanRadius must be a constant so record and replay args align.
    ///
    /// RED: today ReplayGameStateProvider.GetHostileActors returns empty without scanning,
    /// so it never starves — it silently succeeds. After the flip, a radius mismatch starves.
    /// </summary>
    [Fact]
    public async Task RH_ReplayRadiusMismatch_Starves_WithReplayObservationStarvationException()
    {
        // Build one observation at radius 30
        var actorList = new List<HostileActor> { MakeHostile(9, 100) };
        var actorJson = JsonSerializer.SerializeToElement(actorList, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var obs = new ObservationEvent(
            RunId: "run-rh-mismatch",
            Method: "GetHostileActors",
            Argument: JsonSerializer.SerializeToElement(30f, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Value: actorJson,
            At: At);

        var replay = new ReplayGameStateProvider(new[] { obs });

        // Replay with a DIFFERENT radius — scanner must starve
        await Assert.ThrowsAsync<ReplayObservationStarvationException>(
            () => replay.GetHostileActors(99f, CancellationToken.None));
    }
}
