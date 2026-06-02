using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Recording;

/// <summary>
/// RED PHASE — S1: NewGamePlusState.ActiveReplayQuestId detection surface.
///
/// These tests ALL fail to compile because NewGamePlusState does not yet have
/// an ActiveReplayQuestId field. The ONLY not-yet-existing member referenced
/// is NewGamePlusState.ActiveReplayQuestId (QuestId?).
///
/// Spec: docs/NEW_GAME_PLUS_PLAN.md §S1, scenarios S1.1–S1.6.
/// </summary>
public sealed class NewGamePlusStateS1Tests
{
    private static readonly DateTimeOffset At = new(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static string FormatResult<T>(Result<T> r) => r switch
    {
        Result<T>.Success { Value: var v } => $"Ok({v})",
        Result<T>.Failure f => $"Fail({f.Reason})",
        _ => "?",
    };

    // -------------------------------------------------------------------------
    // S1.1 — Default 3-arg construction: ActiveReplayQuestId == null
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.1 (record default, back-compat):
    /// Given the existing 3-arg positional call new NewGamePlusState(false, null, false),
    /// Then ActiveReplayQuestId == null and the record still compiles.
    ///
    /// RED: compile error — NewGamePlusState.ActiveReplayQuestId does not exist.
    /// </summary>
    [Fact]
    public void S1_1_ThreeArgConstruction_ActiveReplayQuestId_IsNull()
    {
        // Given: existing 3-arg positional call site — must still compile after the field is added
        var state = new NewGamePlusState(false, null, false);

        // Then: new field defaults to null (the "= null" default keeps existing call sites green)
        Assert.Null(state.ActiveReplayQuestId); // compile-fails: 'ActiveReplayQuestId' does not exist
    }

    // -------------------------------------------------------------------------
    // S1.1 (equality/with) — record equality includes new field
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.1 (record equality):
    /// Two NewGamePlusState values with the same ActiveReplayQuestId are equal;
    /// values differing only in ActiveReplayQuestId are not.
    ///
    /// RED: compile error — ActiveReplayQuestId does not exist.
    /// </summary>
    [Fact]
    public void S1_1_RecordEquality_DifferentActiveReplayQuestId_NotEqual()
    {
        var chapter = new NewGamePlusChapter(2, "Ch2");
        var a = new NewGamePlusState(true, chapter, false, new QuestId(70123)); // compile-fails
        var b = new NewGamePlusState(true, chapter, false, new QuestId(99999)); // compile-fails

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// S1.1 (with-expression):
    /// with { ActiveReplayQuestId = … } produces a new record with only that field changed.
    ///
    /// RED: compile error — ActiveReplayQuestId does not exist.
    /// </summary>
    [Fact]
    public void S1_1_WithExpression_ChangesOnlyActiveReplayQuestId()
    {
        var original = new NewGamePlusState(true, new NewGamePlusChapter(2, "Ch2"), false);

        // with-expression — compile-fails if field absent
        var updated = original with { ActiveReplayQuestId = new QuestId(70123) };

        Assert.Equal(new QuestId(70123), updated.ActiveReplayQuestId); // compile-fails
        Assert.Null(original.ActiveReplayQuestId); // original unchanged, compile-fails
        Assert.Equal(original.IsActive, updated.IsActive);
        Assert.Equal(original.IsSuspended, updated.IsSuspended);
    }

    // -------------------------------------------------------------------------
    // S1.2 — Fake setter carries the new field through GetNewGamePlusState
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.2 (fake setter carries the field):
    /// Given FakeGameStateProvider; When SetNewGamePlusState is called with a state
    /// whose ActiveReplayQuestId == new QuestId(70123), then GetNewGamePlusState returns
    /// Result.Ok with IsActive == true, CurrentChapter.ChapterId == 2,
    /// and ActiveReplayQuestId == new QuestId(70123).
    ///
    /// RED: compile error — NewGamePlusState 4-arg ctor / ActiveReplayQuestId field absent.
    /// </summary>
    [Fact]
    public async Task S1_2_FakeSetter_CarriesActiveReplayQuestId()
    {
        // Given
        var fake = new FakeGameStateProvider();
        var chapter = new NewGamePlusChapter(2, "Ch2");
        var state = new NewGamePlusState(true, chapter, false, new QuestId(70123)); // compile-fails

        // When
        fake.SetNewGamePlusState(state);
        var result = await fake.GetNewGamePlusState(CancellationToken.None);

        // Then
        var success = Assert.IsType<Result<NewGamePlusState>.Success>(result);
        Assert.True(success.Value.IsActive,
            $"Expected IsActive=true; result={FormatResult(result)}");
        Assert.NotNull(success.Value.CurrentChapter);
        Assert.Equal(2, success.Value.CurrentChapter!.ChapterId);
        Assert.Equal(new QuestId(70123), success.Value.ActiveReplayQuestId); // compile-fails
    }

    /// <summary>
    /// S1.2 (default ActiveReplayQuestId):
    /// Given a fake with the default 3-arg state (no ActiveReplayQuestId set),
    /// When GetNewGamePlusState is called, Then ActiveReplayQuestId == null.
    ///
    /// RED: compile error — ActiveReplayQuestId does not exist.
    /// </summary>
    [Fact]
    public async Task S1_2_FakeDefault_ActiveReplayQuestId_IsNull()
    {
        // Given: fresh FakeGameStateProvider — default state is new NewGamePlusState(false, null, false)
        var fake = new FakeGameStateProvider();

        // When
        var result = await fake.GetNewGamePlusState(CancellationToken.None);

        // Then
        var success = Assert.IsType<Result<NewGamePlusState>.Success>(result);
        Assert.Null(success.Value.ActiveReplayQuestId); // compile-fails
    }

    // -------------------------------------------------------------------------
    // S1.3 — Recording proxy emits the new field in the serialized observation
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.3 (recording proxy emits the new field):
    /// Given RecordingGameStateProvider wrapping a fake set to ActiveReplayQuestId == 70123
    /// with a non-null runId; When GetNewGamePlusState is called, Then one ObservationEvent
    /// with Method == "GetNewGamePlusState" is written and its Value JSON contains
    /// "activeReplayQuestId" with value 70123 (camelCase naming policy).
    ///
    /// RED: compile error — NewGamePlusState 4-arg ctor / ActiveReplayQuestId absent.
    /// If the field exists but STJ doesn't serialize QuestId? correctly, assertion fails.
    /// Builder task: verify NewGamePlusState serializes QuestId? null/non-null via ReflectionFallback options.
    /// </summary>
    [Fact]
    public async Task S1_3_RecordingProxy_EmitsObservationWithActiveReplayQuestId()
    {
        // Given
        var inner = new FakeGameStateProvider();
        var chapter = new NewGamePlusChapter(2, "Ch2");
        inner.SetNewGamePlusState(
            new NewGamePlusState(true, chapter, false, new QuestId(70123))); // compile-fails

        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, trace, () => "run-s1");

        // When
        await proxy.GetNewGamePlusState(CancellationToken.None);

        // Then: exactly one observation
        var obs = trace.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Equal(1, obs.Count);
        Assert.Equal("GetNewGamePlusState", obs[0].Data.Method);

        // Value must be present and contain activeReplayQuestId == 70123
        Assert.NotNull(obs[0].Data.Value);
        var valueJson = obs[0].Data.Value!.Value.GetRawText();
        Assert.Contains("activeReplayQuestId", valueJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("70123", valueJson, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // S1.4 — Recording dedup still works with the new field
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.4 (recording dedup unchanged):
    /// Given the same NewGamePlusState (including ActiveReplayQuestId == 70123) read twice
    /// with no change, Then only ONE observation is emitted.
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// </summary>
    [Fact]
    public async Task S1_4_RecordingProxy_SameStateTwice_OnlyOneObservationEmitted()
    {
        // Given
        var inner = new FakeGameStateProvider();
        inner.SetNewGamePlusState(
            new NewGamePlusState(true, new NewGamePlusChapter(1, "Ch1"), false, new QuestId(70123))); // compile-fails

        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, trace, () => "run-s1-dedup");

        // When: call twice with identical state
        await proxy.GetNewGamePlusState(CancellationToken.None);
        await proxy.GetNewGamePlusState(CancellationToken.None);

        // Then: dedup suppresses the second — exactly ONE observation
        var ngpObs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetNewGamePlusState")
            .ToList();
        Assert.Equal(1, ngpObs.Count);
    }

    /// <summary>
    /// S1.4 (dedup on field change emits second):
    /// When ActiveReplayQuestId changes between two calls, a second observation IS emitted.
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// </summary>
    [Fact]
    public async Task S1_4_RecordingProxy_ActiveReplayQuestIdChanges_SecondObservationEmitted()
    {
        // Given
        var inner = new FakeGameStateProvider();
        inner.SetNewGamePlusState(
            new NewGamePlusState(true, new NewGamePlusChapter(1, "Ch1"), false, new QuestId(70123))); // compile-fails

        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, trace, () => "run-s1-dedup2");

        // First call
        await proxy.GetNewGamePlusState(CancellationToken.None);

        // State changes — ActiveReplayQuestId advances to next quest
        inner.SetNewGamePlusState(
            new NewGamePlusState(true, new NewGamePlusChapter(1, "Ch1"), false, new QuestId(70124))); // compile-fails

        // Second call with changed value
        await proxy.GetNewGamePlusState(CancellationToken.None);

        // Then: both observations emitted
        var ngpObs = trace.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetNewGamePlusState")
            .ToList();
        Assert.Equal(2, ngpObs.Count);
    }

    // -------------------------------------------------------------------------
    // S1.5 — Replay materializes ActiveReplayQuestId (non-null round-trip)
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.5 (replay materializes the new field):
    /// Given a trace with a GetNewGamePlusState observation carrying activeReplayQuestId == 70123;
    /// When ReplayGameStateProvider.GetNewGamePlusState is called, Then Result.Ok with
    /// ActiveReplayQuestId == new QuestId(70123).
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// Builder task: confirm QuestId? deserializes from {value:70123} via ReplayJsonOptions.Default
    /// (reflection-based, camelCase). If QuestId struct is not in any [JsonSerializable] source-gen
    /// context used during Materialize<NewGamePlusState>, the builder must verify it falls back to
    /// reflection correctly (no [JsonConverter] on QuestId, so the default ctor+property path applies).
    /// </summary>
    [Fact]
    public async Task S1_5_Replay_MaterializesNonNullActiveReplayQuestId()
    {
        // Given: a recorded observation value that includes activeReplayQuestId
        // Build the value JSON by serializing a real NewGamePlusState so we match the exact shape.
        // This line also compile-fails because of the 4-arg ctor.
        var recordedState = new NewGamePlusState(true, new NewGamePlusChapter(2, "Ch2"), false, new QuestId(70123)); // compile-fails
        var valueJson = JsonSerializer.SerializeToElement(recordedState, CamelCase);

        var obs = new ObservationEvent
        {
            RunId = "run-s1-replay",
            Data = new ObservationEvent.ObservationData { Method = "GetNewGamePlusState", Value = valueJson }
        };

        var replay = new ReplayGameStateProvider(new[] { obs });

        // When
        var result = await replay.GetNewGamePlusState(CancellationToken.None);

        // Then
        Assert.True(result.IsSuccess, $"Expected success; got: {FormatResult(result)}");
        var value = result.ValueOrThrow;
        Assert.True(value.IsActive);
        Assert.Equal(2, value.CurrentChapter!.ChapterId);
        Assert.Equal(new QuestId(70123), value.ActiveReplayQuestId); // compile-fails
    }

    // -------------------------------------------------------------------------
    // S1.5 full round-trip: record → replay (non-null)
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.5 (full record→replay round-trip, non-null ActiveReplayQuestId):
    /// RecordingGameStateProvider records a state with ActiveReplayQuestId==70123;
    /// ReplayGameStateProvider materializes it back equal (byte-identical field values).
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// </summary>
    [Fact]
    public async Task S1_5_FullRoundTrip_NonNullActiveReplayQuestId_RoundTripsEqual()
    {
        // Given
        var inner = new FakeGameStateProvider();
        var original = new NewGamePlusState( // compile-fails
            IsActive: true,
            CurrentChapter: new NewGamePlusChapter(2, "Ch2"),
            IsSuspended: false,
            ActiveReplayQuestId: new QuestId(70123)); // compile-fails
        inner.SetNewGamePlusState(original);

        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, trace, () => "run-s1-rt");

        // Record
        await proxy.GetNewGamePlusState(CancellationToken.None);

        // Retrieve the observation
        var recorded = trace.RecordedEvents.OfType<ObservationEvent>()
            .Single(e => e.Data.Method == "GetNewGamePlusState");

        // Replay
        var replay = new ReplayGameStateProvider(new[] { recorded });
        var result = await replay.GetNewGamePlusState(CancellationToken.None);

        // Then: round-tripped value equals original
        Assert.True(result.IsSuccess, $"Expected success; got: {FormatResult(result)}");
        var rt = result.ValueOrThrow;
        Assert.Equal(original.IsActive, rt.IsActive);
        Assert.Equal(original.IsSuspended, rt.IsSuspended);
        Assert.Equal(original.CurrentChapter!.ChapterId, rt.CurrentChapter!.ChapterId);
        Assert.Equal(original.ActiveReplayQuestId, rt.ActiveReplayQuestId); // compile-fails
    }

    // -------------------------------------------------------------------------
    // S1.6 — Replay: null ActiveReplayQuestId round-trips correctly
    // -------------------------------------------------------------------------

    /// <summary>
    /// S1.6 (replay null round-trips):
    /// Given an observation with activeReplayQuestId: null (not-in-NG+ state),
    /// When ReplayGameStateProvider.GetNewGamePlusState is called,
    /// Then ActiveReplayQuestId == null (not QuestId(0) or any default).
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// </summary>
    [Fact]
    public async Task S1_6_Replay_NullActiveReplayQuestId_RoundTripsAsNull()
    {
        // Given: a state with no active replay quest
        // 3-arg construction still compiles (back-compat per S1.1), but the serialized
        // value must include "activeReplayQuestId": null for the round-trip assertion below.
        var recordedState = new NewGamePlusState(false, null, false); // back-compat: compiles today
        var valueJson = JsonSerializer.SerializeToElement(recordedState, CamelCase);

        // Confirm the serialized form includes activeReplayQuestId null once field exists
        // (this assertion only activates after the builder adds the field)
        var obs = new ObservationEvent
        {
            RunId = "run-s1-null",
            Data = new ObservationEvent.ObservationData { Method = "GetNewGamePlusState", Value = valueJson }
        };

        var replay = new ReplayGameStateProvider(new[] { obs });

        // When
        var result = await replay.GetNewGamePlusState(CancellationToken.None);

        // Then
        Assert.True(result.IsSuccess, $"Expected success; got: {FormatResult(result)}");
        var value = result.ValueOrThrow;
        Assert.False(value.IsActive);
        // Key assertion: null must come back as null, not QuestId(0)
        Assert.Null(value.ActiveReplayQuestId); // compile-fails (field absent)
    }

    /// <summary>
    /// S1.6 (full record→replay round-trip, null ActiveReplayQuestId):
    /// A not-in-NG+ state round-trips through record/replay with ActiveReplayQuestId == null.
    ///
    /// RED: compile error — ActiveReplayQuestId absent.
    /// </summary>
    [Fact]
    public async Task S1_6_FullRoundTrip_NullActiveReplayQuestId_RoundTripsAsNull()
    {
        // Given: default not-in-NG+ state (existing 3-arg, back-compat)
        var inner = new FakeGameStateProvider();
        // Leave default: new NewGamePlusState(false, null, false)

        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, trace, () => "run-s1-null-rt");

        // Record
        await proxy.GetNewGamePlusState(CancellationToken.None);

        var recorded = trace.RecordedEvents.OfType<ObservationEvent>()
            .Single(e => e.Data.Method == "GetNewGamePlusState");

        // Replay
        var replay = new ReplayGameStateProvider(new[] { recorded });
        var result = await replay.GetNewGamePlusState(CancellationToken.None);

        // Then
        Assert.True(result.IsSuccess, $"Expected success; got: {FormatResult(result)}");
        var value = result.ValueOrThrow;
        Assert.False(value.IsActive);
        Assert.Null(value.ActiveReplayQuestId); // compile-fails (field absent)
    }
}
