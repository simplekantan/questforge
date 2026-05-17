using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Tests for FakeInteractor.HandOverItem — the fake implementation of IInteractor.HandOverItem.
///
/// RED PHASE: All tests will fail to compile until Builder implements:
///   - IInteractor.HandOverItem(ItemId, NpcId, CancellationToken) → Task&lt;Result&lt;HandOverOutcome&gt;&gt;
///   - HandOverOutcome enum (at minimum: HandedOver, ItemPlaced)
///   - FakeInteractor.HandOverItem method
///   - FakeInteractor.ScriptHandOverSequence scripting helper
///   - FakeInteractor.RecordedHandOvers call log
///   - FakeInteractor.Reset() extended to clear hand-over state
///
/// Spec: hand-over-item adapter contract (IInteractor extension).
/// Behaviors B9-B9d.
/// </summary>
public sealed class FakeInteractorHandOverTests
{
    private static FakeInteractor CreateInteractor(
        out FakeGameStateProvider gameState,
        out FakeQuestState questState)
    {
        gameState  = new FakeGameStateProvider();
        questState = new FakeQuestState();
        return new FakeInteractor(gameState, questState);
    }

    // =========================================================================
    // B9 — HandOverItem default returns HandedOver, records one call with correct ItemId
    // =========================================================================

    [Fact]
    public async Task HandOverItem_Default_ReturnsHandedOver_RecordsCall()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItem to IInteractor/FakeInteractor.
         *
         * CONTRACT: Given fresh FakeInteractor (no scripting),
         *           When HandOverItem(item: ItemId(2002001), target: NpcId(1003987), ct) is called,
         *           Then:
         *             result.ValueOrThrow == HandOverOutcome.HandedOver (default outcome)
         *             RecordedHandOvers.Count == 1
         *             RecordedHandOvers[0].ItemId == new ItemId(2002001)
         *
         * BUILDER GUIDANCE:
         *   - Add HandOverOutcome enum to IInteractor.cs with at least HandedOver and ItemPlaced values.
         *   - Add Task<Result<HandOverOutcome>> HandOverItem(ItemId item, NpcId target, CancellationToken ct)
         *     to IInteractor interface.
         *   - Implement in FakeInteractor: default returns HandedOver, records call.
         *   - Add RecordedHandOvers call log (CallLog<HandOverCall> where HandOverCall records ItemId).
         */

        // Arrange
        var interactor = CreateInteractor(out _, out _);
        var item   = new ItemId(2002001u);
        var target = new NpcId(1003987u);

        // Act
        var result = await interactor.HandOverItem(item, target, CancellationToken.None); // RED: method does not exist

        // Assert
        Assert.Equal(HandOverOutcome.HandedOver, result.ValueOrThrow); // RED: enum does not exist
        Assert.Equal(1, interactor.RecordedHandOvers.Count);           // RED: property does not exist
        Assert.Equal(item, interactor.RecordedHandOvers[0].ItemId);    // RED: call record does not exist
    }

    // =========================================================================
    // B9b — scripted sequence: ItemPlaced then HandedOver then (post-queue) HandedOver
    // =========================================================================

    [Fact]
    public async Task HandOverItem_ScriptedSequence_ReturnsInOrder_ThirdReturnsDefault()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItem and ScriptHandOverSequence.
         *
         * CONTRACT: Given ScriptHandOverSequence(ItemPlaced, HandedOver),
         *           When HandOverItem is called three times,
         *           Then:
         *             call 1 → HandOverOutcome.ItemPlaced
         *             call 2 → HandOverOutcome.HandedOver
         *             call 3 → HandOverOutcome.HandedOver  (queue exhausted → default)
         *
         * BUILDER GUIDANCE: ScriptHandOverSequence uses the same Queue<T> pattern as
         *   ScriptDialogueSequence. After the queue is exhausted, the default (HandedOver) is returned.
         */

        // Arrange
        var interactor = CreateInteractor(out _, out _);
        interactor.ScriptHandOverSequence(          // RED: method does not exist
            HandOverOutcome.ItemPlaced,
            HandOverOutcome.HandedOver);

        var item   = new ItemId(2002001u);
        var target = new NpcId(1003987u);
        var ct     = CancellationToken.None;

        // Act
        var first  = await interactor.HandOverItem(item, target, ct);
        var second = await interactor.HandOverItem(item, target, ct);
        var third  = await interactor.HandOverItem(item, target, ct);

        // Assert
        Assert.Equal(HandOverOutcome.ItemPlaced,  first.ValueOrThrow);   // RED: enum does not exist
        Assert.Equal(HandOverOutcome.HandedOver,  second.ValueOrThrow);
        Assert.Equal(HandOverOutcome.HandedOver,  third.ValueOrThrow);   // post-queue default
    }

    // =========================================================================
    // B9c — cancelled CancellationToken throws OperationCanceledException
    // =========================================================================

    [Fact]
    public async Task HandOverItem_CancelledToken_ThrowsOperationCanceledException()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItem.
         *
         * CONTRACT: Given a pre-cancelled CancellationToken,
         *           When HandOverItem is called,
         *           Then OperationCanceledException is thrown before any work is done.
         *
         * BUILDER GUIDANCE: Call ct.ThrowIfCancellationRequested() at the top of HandOverItem,
         *   identical to the pattern used by all other FakeInteractor methods.
         */

        // Arrange
        var interactor = CreateInteractor(out _, out _);
        var item   = new ItemId(2002001u);
        var target = new NpcId(1003987u);
        var ct     = new CancellationToken(canceled: true);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => interactor.HandOverItem(item, target, ct)); // RED: method does not exist
    }

    // =========================================================================
    // B9d — Reset() clears hand-over log and scripted queue
    // =========================================================================

    [Fact]
    public async Task Reset_ClearsHandOverLogAndQueue()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItem and ScriptHandOverSequence.
         *
         * CONTRACT: Given one recorded HandOverItem call and a scripted ItemPlaced in the queue,
         *           When Reset() is called,
         *           Then:
         *             RecordedHandOvers.Count == 0      (log is cleared)
         *             A new HandOverItem call returns HandedOver (queue is cleared → default)
         *
         * BUILDER GUIDANCE: Extend the existing Reset() method to:
         *   - Clear RecordedHandOvers
         *   - Clear the hand-over scripted queue (_handOverSequence.Clear())
         */

        // Arrange
        var interactor = CreateInteractor(out _, out _);
        var item   = new ItemId(2002001u);
        var target = new NpcId(1003987u);
        var ct     = CancellationToken.None;

        // Pre-condition: one call recorded, one outcome scripted
        interactor.ScriptHandOverSequence(HandOverOutcome.ItemPlaced); // RED: method does not exist
        await interactor.HandOverItem(item, target, ct);               // RED: method does not exist
        Assert.Equal(1, interactor.RecordedHandOvers.Count);           // RED: property does not exist

        // Act
        interactor.Reset();

        // Assert — log cleared
        Assert.Equal(0, interactor.RecordedHandOvers.Count);

        // Assert — queue also cleared: next call returns default (HandedOver), not ItemPlaced
        var afterReset = await interactor.HandOverItem(item, target, ct);
        Assert.Equal(HandOverOutcome.HandedOver, afterReset.ValueOrThrow);
    }
}
