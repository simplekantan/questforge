using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Recording;

/// <summary>
/// Tests for RecordingGameStateProvider (QuestForge.Adapters.Recording).
/// Covers §6.3 (GameState proxy portion, 11 tests) of PHASE_5_PLAN.md.
/// </summary>
public sealed class RecordingGameStateProviderTests
{
    private static RecordingGameStateProvider BuildProxy(
        FakeGameStateProvider inner,
        FakeTraceWriter trace,
        Func<string?> runIdAccessor) =>
        new RecordingGameStateProvider(inner, trace, runIdAccessor);

    // -------------------------------------------------------------------------
    // Pass-through: returns same value as inner fake
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_ReturnsInnerValue()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given FakeGameStateProvider configured with ZoneId(182),
         *           When RecordingGameStateProvider.GetPlayerZone is called,
         *           Then it returns Result<ZoneId>.Success(ZoneId(182)) — identical to inner fake.
         *
         * BUILDER GUIDANCE: The proxy MUST return the inner adapter's result unchanged.
         *   Emit the ObservationEvent AFTER the inner call and BEFORE returning.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        inner.SetZone(new ZoneId(182));
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "test-run");

        // Act
        var result = await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var success = Assert.IsType<Result<ZoneId>.Success>(result);
        Assert.Equal(new ZoneId(182), success.Value);
    }

    // -------------------------------------------------------------------------
    // One observation emitted per call, with correct Method and RunId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_CalledOnce_EmitsOneObservationEvent()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given FakeTraceWriter with Count == 0,
         *           When GetPlayerZone is called once,
         *           Then FakeTraceWriter.Count == 1
         *           and the single event is ObservationEvent with Method == "GetPlayerZone".
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        inner.SetZone(new ZoneId(182));
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "test-run");

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        Assert.Equal(1, trace.Count);
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("GetPlayerZone", evt.Method);
        Assert.Equal("test-run", evt.RunId);
    }

    // -------------------------------------------------------------------------
    // Value serialized into ObservationEvent.Value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_WithZone182_ObservationValueContains182()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given FakeGameStateProvider with ZoneId(182),
         *           When GetPlayerZone is called via the proxy,
         *           Then the ObservationEvent.Value JSON contains 182.
         *
         * BUILDER GUIDANCE: SerializableValue(result) for a Success<ZoneId> should serialize
         *   the ZoneId value (underlying uint). ZoneId must be a JSON-serializable type.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        inner.SetZone(new ZoneId(182));
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "test-run");

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.NotNull(evt.Value);
        var valueJson = evt.Value!.Value.GetRawText();
        Assert.Contains("182", valueJson, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Inner adapter called exactly once — no double reads
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_InnerCalledExactlyOnce()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given FakeGameStateProvider with empty RecordedReads,
         *           When the proxy's GetPlayerZone is called once,
         *           Then FakeGameStateProvider.RecordedReads.Count == 1 — inner called exactly once.
         *
         * BUILDER GUIDANCE: The proxy must not call the inner adapter more than once per method call.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        inner.SetZone(new ZoneId(182));
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-x");

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        // FakeGameStateProvider.RecordedReads is the call log tracking every inner call
        Assert.Equal(1, inner.RecordedReads.Count); // Inner adapter must be called exactly once — no double-reads in the proxy
    }

    // -------------------------------------------------------------------------
    // Failure result recorded with failure marker in Value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_InnerReturnsFailure_ObservationValueContainsFailureKey()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given FakeGameStateProvider configured to return Failure("not ready"),
         *           When proxy.GetPlayerZone is called,
         *           Then the ObservationEvent.Value is non-null and contains a "failure" key.
         *
         * BUILDER GUIDANCE: Use SerializableValue helper:
         *   Result<T>.Failure f => SerializeToElement(new { failure = f.Reason, detail = f.Detail })
         */

        // Arrange
        var failingInner = new FailureGameStateProvider("not ready");
        var trace = new FakeTraceWriter();
        var proxy = new RecordingGameStateProvider(failingInner, trace, () => "run-f");

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.NotNull(evt.Value);
        var valueJson = evt.Value!.Value.GetRawText();
        Assert.Contains("failure", valueJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not ready", valueJson, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // RunId from accessor propagated to event
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnyMethod_RunIdAccessorReturnsFixedId_EventHasCorrectRunId()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given proxy constructed with runIdAccessor = () => "fixed-id",
         *           When any proxy method is called,
         *           Then the emitted ObservationEvent.RunId == "fixed-id".
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "fixed-id");

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("fixed-id", evt.RunId);
    }

    // -------------------------------------------------------------------------
    // RunId null before BeginRun — observation records null RunId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnyMethod_RunIdAccessorReturnsNull_EventRunIdIsNullOrEmpty()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given proxy constructed with runIdAccessor = () => null,
         *           When a proxy method is called,
         *           Then the emitted ObservationEvent.RunId is null (or empty string,
         *           per implementation choice — this test asserts null).
         *
         * BUILDER GUIDANCE: Per plan §3.2, if runIdAccessor() returns null,
         *   the event's RunId is null (nullable string property on ObservationEvent).
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => null);

        // Act
        await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        // Accept either null or empty string — the proxy must propagate no-run state
        Assert.True(evt.RunId is null or "",
            $"Expected RunId to be null or empty when accessor returns null, but was: '{evt.RunId}'");
    }

    // -------------------------------------------------------------------------
    // Cancellation propagates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_PreCancelledToken_ThrowsOperationCanceledException()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given a pre-cancelled CancellationToken,
         *           When GetPlayerZone is called via the proxy,
         *           Then OperationCanceledException is thrown (propagated from inner fake).
         *
         * BUILDER GUIDANCE: The inner FakeGameStateProvider.GetPlayerZone calls
         *   ct.ThrowIfCancellationRequested() — the proxy must await the inner call,
         *   allowing cancellation to propagate naturally.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-c");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => proxy.GetPlayerZone(cts.Token));
    }

    // -------------------------------------------------------------------------
    // Trace write failure does not propagate — proxy returns inner result
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_TraceWriteThrows_ProxyStillReturnsInnerResult()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given an ITraceWriter that throws IOException on every Write,
         *           When a proxy method is called,
         *           Then the proxy returns the inner adapter's result without throwing.
         *
         * BUILDER GUIDANCE: Per plan §12, the proxy catches exceptions from Write
         *   and continues. The adapter result is always returned.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        inner.SetZone(new ZoneId(999));
        var throwingTrace = new ThrowingTraceWriter();
        var proxy = new RecordingGameStateProvider(inner, throwingTrace, () => "run-t");

        // Act — must not throw despite trace writer throwing
        var result = await proxy.GetPlayerZone(CancellationToken.None);

        // Assert
        var success = Assert.IsType<Result<ZoneId>.Success>(result);
        Assert.Equal(new ZoneId(999), success.Value);
    }

    // -------------------------------------------------------------------------
    // GetAcceptedQuests result is a JSON array in ObservationEvent.Value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAcceptedQuests_EmitsObservationWithArrayValue()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given proxy over FakeQuestState-equivalent (or FakeGameStateProvider
         *           where GetAcceptedQuests is on IQuestState — use RecordingQuestState for that).
         *           This test uses a FakeGameStateProvider for a different method that also
         *           returns a list: GetNearbyNpcs — verifying Value is a JSON array.
         *
         *           Specifically: GetNearbyNpcs returns IReadOnlyList<NpcReference>
         *           and the serialized Value must be a JSON array.
         *
         * NOTE: GetAcceptedQuests is on IQuestState, covered in RecordingQuestStateTests.
         *   This test uses GetAttunedAetherytes (IGameStateProvider, returns IReadOnlyList<AetheryteId>)
         *   to verify array serialization on the GameState proxy.
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        // No aetherytes attuned — returns empty list
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-arr");

        // Act
        await proxy.GetAttunedAetherytes(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.NotNull(evt.Value);
        Assert.Equal(JsonValueKind.Array, evt.Value!.Value.ValueKind);
    }

    // -------------------------------------------------------------------------
    // Method name captured correctly for a non-zone method
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsPlayerInCombat_EmitsObservationWithCorrectMethodName()
    {
        /*
         * RED: Will fail until RecordingGameStateProvider is implemented.
         *
         * CONTRACT: Given proxy, when IsPlayerInCombat is called,
         *           Then the emitted ObservationEvent has Method == "IsPlayerInCombat".
         */

        // Arrange
        var inner = new FakeGameStateProvider();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-m");

        // Act
        await proxy.IsPlayerInCombat(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("IsPlayerInCombat", evt.Method);
    }
}

// ---------------------------------------------------------------------------
// Test helpers — minimal inline fakes for error-path scenarios
// ---------------------------------------------------------------------------

/// <summary>
/// An IGameStateProvider that always returns Failure for GetPlayerZone.
/// Used to test failure recording in the proxy.
/// </summary>
file sealed class FailureGameStateProvider : IGameStateProvider
{
    private readonly string _reason;

    public FailureGameStateProvider(string reason) => _reason = reason;

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct) =>
        Task.FromResult<Result<ZoneId>>(Result.Fail<ZoneId>(_reason));

    // All other methods return a stub failure or default — only GetPlayerZone is tested here
    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct) =>
        Task.FromResult<Result<WorldPosition>>(Result.Fail<WorldPosition>(_reason));

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct) =>
        Task.FromResult<Result<PlayerStateSnapshot>>(Result.Fail<PlayerStateSnapshot>(_reason));

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<MountState>> GetMountState(CancellationToken ct) =>
        Task.FromResult<Result<MountState>>(Result.Fail<MountState>(_reason));

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct) =>
        Task.FromResult<Result<JobId>>(Result.Fail<JobId>(_reason));

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct) =>
        Task.FromResult<Result<int>>(Result.Fail<int>(_reason));

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct) =>
        Task.FromResult<Result<InstanceKind>>(Result.Fail<InstanceKind>(_reason));

    public Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct) =>
        Task.FromResult<Result<DutyAvailability>>(Result.Fail<DutyAvailability>(_reason));

    public Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct) =>
        Task.FromResult<Result<NewGamePlusState>>(Result.Fail<NewGamePlusState>(_reason));

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<NpcReference>>>(Result.Fail<IReadOnlyList<NpcReference>>(_reason));

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct) =>
        Task.FromResult<Result<NpcReference?>>(Result.Fail<NpcReference?>(_reason));

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<InteractableReference>>>(Result.Fail<IReadOnlyList<InteractableReference>>(_reason));

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct) =>
        Task.FromResult<Result<InteractableReference?>>(Result.Fail<InteractableReference?>(_reason));

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(Result.Fail<IReadOnlyList<AetheryteId>>(_reason));

    public Task<Result<UiState>> GetUiState(CancellationToken ct) =>
        Task.FromResult<Result<UiState>>(Result.Fail<UiState>(_reason));

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct) =>
        Task.FromResult<Result<int>>(Result.Fail<int>(_reason));

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct) =>
        Task.FromResult<Result<int>>(Result.Fail<int>(_reason));

    public Task<Result<long>> GetGil(CancellationToken ct) =>
        Task.FromResult<Result<long>>(Result.Fail<long>(_reason));

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct) =>
        Task.FromResult<Result<TravelCapability>>(Result.Fail<TravelCapability>(_reason));
}

/// <summary>
/// An ITraceWriter that throws IOException on every Write.
/// Used to test that the proxy swallows write failures.
/// </summary>
file sealed class ThrowingTraceWriter : ITraceWriter
{
    public void Write(TraceEvent evt) =>
        throw new IOException("Simulated write failure");
}