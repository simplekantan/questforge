using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Recording;

/// <summary>
/// RED PHASE: Tests for RecordingQuestState — the recording proxy over IQuestState.
/// All tests fail until Builder implements RecordingQuestState in QuestForge.Adapters.Fakes.
///
/// Covers §6.3 (QuestState proxy portion, 7 tests) of PHASE_5_PLAN.md.
/// </summary>
public sealed class RecordingQuestStateTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    private static RecordingQuestState BuildProxy(
        FakeQuestState inner,
        FakeTraceWriter trace,
        Func<string?> runIdAccessor) =>
        new RecordingQuestState(inner, trace, runIdAccessor);

    // -------------------------------------------------------------------------
    // Pass-through + argument capture for GetQuestSequence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_PassesThroughAndCapturesArgument()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given FakeQuestState with sequence 3 for QuestId(66130),
         *           When proxy.GetQuestSequence(QuestId(66130), ct) is called,
         *           Then:
         *             1. The returned result is Result<int>.Success(3) — pass-through.
         *             2. The emitted ObservationEvent.Argument is non-null and JSON-encodes QuestId(66130).
         *
         * BUILDER GUIDANCE: Record(nameof(GetQuestSequence), argument: quest, result)
         *   where argument is serialized via SerializableValue to JsonElement?.
         */

        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestSequence(new QuestId(66130), 3);
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-qs");

        // Act
        var result = await proxy.GetQuestSequence(new QuestId(66130), CancellationToken.None);

        // Assert — pass-through
        var success = Assert.IsType<Result<int>.Success>(result);
        Assert.Equal(3, success.Value);

        // Assert — argument captured
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("GetQuestSequence", evt.Method);
        Assert.NotNull(evt.Argument);
        // The argument JSON should contain 66130
        Assert.Contains("66130", evt.Argument!.Value.GetRawText(), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // IsQuestComplete — correct Method name recorded
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsQuestComplete_EmitsObservationWithCorrectMethod()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given proxy, when IsQuestComplete is called,
         *           Then ObservationEvent.Method == "IsQuestComplete".
         */

        // Arrange
        var inner = new FakeQuestState();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-ic");

        // Act
        await proxy.IsQuestComplete(new QuestId(66130), CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("IsQuestComplete", evt.Method);
    }

    // -------------------------------------------------------------------------
    // Failure result → failure marker in Value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_InnerReturnsFailure_ValueContainsFailureMarker()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given an inner IQuestState that returns Failure for GetQuestSequence,
         *           When proxy.GetQuestSequence is called,
         *           Then the emitted ObservationEvent.Value is non-null and contains
         *           a "failure" key.
         */

        // Arrange
        var failingInner = new FailingQuestState("db unavailable");
        var trace = new FakeTraceWriter();
        var proxy = new RecordingQuestState(failingInner, trace, () => "run-fail");

        // Act
        await proxy.GetQuestSequence(new QuestId(66130), CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.NotNull(evt.Value);
        Assert.Contains("failure", evt.Value!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("db unavailable", evt.Value.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // RunId propagated from accessor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnyMethod_RunIdAccessorValue_PropagatedToEvent()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given proxy with runIdAccessor = () => "quest-run-42",
         *           When any method is called,
         *           Then ObservationEvent.RunId == "quest-run-42".
         */

        // Arrange
        var inner = new FakeQuestState();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "quest-run-42");

        // Act
        await proxy.IsQuestComplete(new QuestId(66130), CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.Equal("quest-run-42", evt.RunId);
    }

    // -------------------------------------------------------------------------
    // Inner called exactly once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_InnerCalledExactlyOnce()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: When proxy.GetQuestSequence is called once,
         *           Then FakeQuestState.RecordedReads.Count == 1 — inner not double-called.
         */

        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestSequence(new QuestId(66130), 0);
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-once");

        // Act
        await proxy.GetQuestSequence(new QuestId(66130), CancellationToken.None);

        // Assert
        Assert.Equal(1, inner.RecordedReads.Count,
            "Inner adapter must be invoked exactly once — no double-calls in the proxy");
    }

    // -------------------------------------------------------------------------
    // Two different methods → two observations in order
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TwoDifferentMethods_ProduceTwoObservationsInOrder()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given proxy, when GetQuestSequence and then IsQuestComplete are called,
         *           Then trace.RecordedEvents contains 2 ObservationEvents in that order,
         *           with Methods ["GetQuestSequence", "IsQuestComplete"].
         */

        // Arrange
        var inner = new FakeQuestState();
        inner.SetQuestSequence(new QuestId(66130), 0);
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-two");

        // Act
        await proxy.GetQuestSequence(new QuestId(66130), CancellationToken.None);
        await proxy.IsQuestComplete(new QuestId(66130), CancellationToken.None);

        // Assert
        Assert.Equal(2, trace.Count);
        var first = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        var second = Assert.IsType<ObservationEvent>(trace.RecordedEvents[1]);
        Assert.Equal("GetQuestSequence", first.Method);
        Assert.Equal("IsQuestComplete", second.Method);
    }

    // -------------------------------------------------------------------------
    // Pre-cancelled token propagates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_PreCancelledToken_ThrowsOperationCanceledException()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given a pre-cancelled CancellationToken,
         *           When GetQuestSequence is called via the proxy,
         *           Then OperationCanceledException is thrown.
         *
         * BUILDER GUIDANCE: The inner FakeQuestState.GetQuestSequence calls
         *   ct.ThrowIfCancellationRequested() — the proxy must allow this to propagate.
         */

        // Arrange
        var inner = new FakeQuestState();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => proxy.GetQuestSequence(new QuestId(66130), cts.Token));
    }

    // -------------------------------------------------------------------------
    // GetAcceptedQuests — Value is JSON array
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAcceptedQuests_EmitsObservationWithArrayValue()
    {
        /*
         * RED: Will fail until RecordingQuestState is implemented.
         *
         * CONTRACT: Given a FakeQuestState with no accepted quests,
         *           When proxy.GetAcceptedQuests is called,
         *           Then the ObservationEvent.Value is a JSON array (possibly empty).
         *
         * BUILDER GUIDANCE: IReadOnlyList<QuestId> serializes as a JSON array.
         *   Even an empty list serializes as [].
         */

        // Arrange
        var inner = new FakeQuestState();
        var trace = new FakeTraceWriter();
        var proxy = BuildProxy(inner, trace, () => "run-aq");

        // Act
        await proxy.GetAcceptedQuests(CancellationToken.None);

        // Assert
        var evt = Assert.IsType<ObservationEvent>(trace.RecordedEvents[0]);
        Assert.NotNull(evt.Value);
        Assert.Equal(JsonValueKind.Array, evt.Value!.Value.ValueKind);
    }
}

// ---------------------------------------------------------------------------
// Minimal inline fake for failure-path testing
// ---------------------------------------------------------------------------

file sealed class FailingQuestState : IQuestState
{
    private readonly string _reason;

    public FailingQuestState(string reason) => _reason = reason;

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<int>>(Result.Fail<int>(_reason));

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<QuestStatus>>(Result.Fail<QuestStatus>(_reason));

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<uint>>(Result.Fail<uint>(_reason));

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<bool>>(Result.Fail<bool>(_reason));

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct) =>
        Task.FromResult<Result<QuestUnlockReason?>>(Result.Fail<QuestUnlockReason?>(_reason));

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<QuestId>>>(Result.Fail<IReadOnlyList<QuestId>>(_reason));

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<QuestReward>>>(Result.Fail<IReadOnlyList<QuestReward>>(_reason));
}