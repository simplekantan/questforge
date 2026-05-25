using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeQuestStateTests
{
    private static readonly QuestId TestQuest = new QuestId(1001);

    // -------------------------------------------------------------------------
    // Group FAKE — FakeQuestState.IsAcceptableNow semantics
    // -------------------------------------------------------------------------

    // FAKE1: default mirrors available — available case
    [Fact]
    public async Task IsAcceptableNow_DefaultWhenAvailable_ReturnsTrueMatchingAvailability()
    {
        // Given a quest scripted Available with no IsAcceptableNow override,
        // IsAcceptableNow should mirror IsQuestAvailable == Ok(true).
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Available);

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var success = Assert.IsType<Result<bool>.Success>(result);
        Assert.True(success.Value,
            $"Expected Ok(true) for Available quest with no override. Got: {result}");
    }

    // FAKE2: default mirrors available — unknown case
    [Fact]
    public async Task IsAcceptableNow_DefaultWhenUnknown_ReturnsFalseMatchingAvailability()
    {
        // Given a quest with no scripting (status Unknown),
        // IsAcceptableNow should mirror IsQuestAvailable == Ok(false).
        var state = new FakeQuestState();

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var success = Assert.IsType<Result<bool>.Success>(result);
        Assert.False(success.Value,
            $"Expected Ok(false) for Unknown quest with no override. Got: {result}");
    }

    // FAKE3: explicit setter narrows — available + false override → not-acceptable
    [Fact]
    public async Task IsAcceptableNow_ExplicitFalseOnAvailableQuest_ReturnsFalseWhileAvailableRemainsTrue()
    {
        // Given quest Available and SetIsAcceptableNow(q, false),
        // IsQuestAvailable == Ok(true) but IsAcceptableNow == Ok(false).
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Available);
        state.SetIsAcceptableNow(TestQuest, false);

        var availResult = await state.IsQuestAvailable(TestQuest, CancellationToken.None);
        var acceptResult = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        Assert.True(availResult.ValueOrThrow, "IsQuestAvailable should remain true");
        Assert.False(acceptResult.ValueOrThrow, "IsAcceptableNow should be false after SetIsAcceptableNow(q, false)");
    }

    // FAKE4: explicit setter with true on available quest → true (invariant preserved)
    [Fact]
    public async Task IsAcceptableNow_ExplicitTrueOnAvailableQuest_ReturnsTrue()
    {
        // Given quest Available and SetIsAcceptableNow(q, true),
        // IsAcceptableNow == Ok(true).
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Available);
        state.SetIsAcceptableNow(TestQuest, true);

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        Assert.True(result.ValueOrThrow, "IsAcceptableNow with explicit true on available quest should be Ok(true)");
    }

    // FAKE5: failure scripting
    [Fact]
    public async Task IsAcceptableNow_WhenFailureScripted_ReturnsFailureWithReason()
    {
        // Given SetIsAcceptableNowFail(q, "gcRead", "PlayerState null"),
        // IsAcceptableNow returns Failure with that reason and detail.
        var state = new FakeQuestState();
        state.SetIsAcceptableNowFail(TestQuest, "gcRead", "PlayerState null");

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var failure = Assert.IsType<Result<bool>.Failure>(result);
        Assert.Equal("gcRead", failure.Reason);
        Assert.Equal("PlayerState null", failure.Detail);
    }

    // FAKE6: records the read
    [Fact]
    public async Task IsAcceptableNow_AfterOneCall_RecordedReadsContainsIsAcceptableNow()
    {
        // After one IsAcceptableNow call, RecordedReads must contain a read with
        // Method == "IsAcceptableNow".
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Available);

        await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var reads = state.RecordedReads.Snapshot();
        Assert.Contains(reads, r => r.Method == "IsAcceptableNow");
    }

    // FAKE7: pre-cancelled token throws
    [Fact]
    public async Task IsAcceptableNow_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var state = new FakeQuestState();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => state.IsAcceptableNow(TestQuest, ct));
    }

    // -------------------------------------------------------------------------
    // Group INV — invariant: acceptable-now ⊆ available
    // -------------------------------------------------------------------------

    // INV1: implication holds across all scripted states x all overrides
    [Theory]
    // State=Unknown (IsQuestAvailable==false), no override → both false, invariant holds vacuously
    [InlineData(QuestStatus.Unknown,   false, false, false, false)] // unset override
    [InlineData(QuestStatus.Unknown,   true,  false, false, false)] // true override, clamped to false
    [InlineData(QuestStatus.Unknown,   true,  true,  false, false)] // false override
    // State=Available (IsQuestAvailable==true), no override → both true
    [InlineData(QuestStatus.Available, false, false, true,  true)]  // unset override
    [InlineData(QuestStatus.Available, true,  true,  true,  true)]  // true override
    [InlineData(QuestStatus.Available, true,  false, true,  false)] // false override narrows
    // State=Accepted (IsQuestAvailable==false), no override
    [InlineData(QuestStatus.Accepted,  false, false, false, false)]
    [InlineData(QuestStatus.Accepted,  true,  true,  false, false)] // clamped
    // State=Complete (IsQuestAvailable==false)
    [InlineData(QuestStatus.Complete,  false, false, false, false)]
    [InlineData(QuestStatus.Complete,  true,  true,  false, false)] // clamped
    public async Task INV1_WhenAcceptableNowTrue_ThenAvailableMustAlsoBeTrue(
        QuestStatus status, bool hasOverride, bool overrideValue,
        bool expectedAvailable, bool expectedAcceptable)
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, status);
        if (hasOverride) state.SetIsAcceptableNow(TestQuest, overrideValue);

        var availResult = await state.IsQuestAvailable(TestQuest, CancellationToken.None);
        var acceptResult = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var isAvailable = availResult is Result<bool>.Success { Value: true };
        var isAcceptable = acceptResult is Result<bool>.Success { Value: true };

        Assert.Equal(expectedAvailable, isAvailable);
        Assert.Equal(expectedAcceptable, isAcceptable);

        // The core invariant: acceptable-now ⊆ available
        if (isAcceptable)
            Assert.True(isAvailable,
                $"INV1 violation: IsAcceptableNow==true but IsQuestAvailable==false " +
                $"(status={status}, hasOverride={hasOverride}, overrideValue={overrideValue})");
    }

    // INV1 special case: SetIsAcceptableNow(true) on a NON-available quest → clamped to Ok(false)
    [Fact]
    public async Task INV1_SetAcceptableNowTrue_OnNonAvailableQuest_ClampedToFalse()
    {
        // A SetIsAcceptableNow(q, true) on a Unknown quest must produce Ok(false), not Ok(true).
        var state = new FakeQuestState();
        // Quest stays Unknown (not Available)
        state.SetIsAcceptableNow(TestQuest, true);

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        var success = Assert.IsType<Result<bool>.Success>(result);
        Assert.False(success.Value,
            "SetIsAcceptableNow(q, true) on a non-available quest must be clamped to Ok(false)");
    }

    // INV2: scripted failure stays Failure — not coerced to truthy success
    [Fact]
    public async Task INV2_ScriptedFailure_IsFailure_NotCoercedToTruthySuccess()
    {
        var state = new FakeQuestState();
        state.SetIsAcceptableNowFail(TestQuest, "gcRead", null);

        var result = await state.IsAcceptableNow(TestQuest, CancellationToken.None);

        // Must be a Failure, not an Ok(true) that would satisfy the implication vacuously
        Assert.IsType<Result<bool>.Failure>(result);
        Assert.False(result is Result<bool>.Success { Value: true },
            "A scripted Failure must not be coerced to Ok(true)");
    }

    [Fact]
    public async Task FreshState_GetQuestStatus_ReturnsUnknown()
    {
        var state = new FakeQuestState();
        var result = await state.GetQuestStatus(TestQuest, CancellationToken.None);
        Assert.Equal(QuestStatus.Unknown, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestStatus_GetQuestStatus_RoundTrips()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Accepted);
        var result = await state.GetQuestStatus(TestQuest, CancellationToken.None);
        Assert.Equal(QuestStatus.Accepted, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestSequence_GetQuestSequence_RoundTrips()
    {
        var state = new FakeQuestState();
        state.SetQuestSequence(TestQuest, 3);
        var result = await state.GetQuestSequence(TestQuest, CancellationToken.None);
        Assert.Equal(3, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestFlagBit_Bit2_IsQuestFlagSet_ReturnsTrueForBit2FalseForBit1()
    {
        var state = new FakeQuestState();
        state.SetQuestFlagBit(TestQuest, 2, true);

        var bit2 = await state.IsQuestFlagSet(TestQuest, 2, CancellationToken.None);
        var bit1 = await state.IsQuestFlagSet(TestQuest, 1, CancellationToken.None);

        Assert.True(bit2.ValueOrThrow);
        Assert.False(bit1.ValueOrThrow);
    }

    [Fact]
    public async Task AddAcceptedQuest_IsQuestAccepted_ReturnsTrue()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        var result = await state.IsQuestAccepted(TestQuest, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task AddAcceptedQuest_AppearsInGetAcceptedQuests()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        var result = await state.GetAcceptedQuests(CancellationToken.None);
        Assert.Contains(TestQuest, result.ValueOrThrow);
    }

    [Fact]
    public async Task RemoveAcceptedQuest_IsQuestAccepted_ReturnsFalse()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        state.RemoveAcceptedQuest(TestQuest);
        var result = await state.IsQuestAccepted(TestQuest, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task IsQuestComplete_ReflectsStatusComplete()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Complete);
        var result = await state.IsQuestComplete(TestQuest, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task IsQuestComplete_StatusNotComplete_ReturnsFalse()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Accepted);
        var result = await state.IsQuestComplete(TestQuest, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_AndNoReadsRecorded()
    {
        var state = new FakeQuestState();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => state.GetQuestStatus(TestQuest, ct));
        Assert.Equal(0, state.RecordedReads.Count);
    }
}