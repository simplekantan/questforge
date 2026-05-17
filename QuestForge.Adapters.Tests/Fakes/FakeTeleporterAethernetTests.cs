using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Pinning tests for FakeTeleporter.TeleportToAethernet.
///
/// FakeTeleporter.TeleportToAethernet already exists — these tests are regression
/// guards that should PASS immediately. If any test fails, the Builder has broken
/// existing fake behavior.
///
/// Spec: issue #24, File 3 — FakeTeleporter Aethernet pinning tests F1-F4.
/// </summary>
public sealed class FakeTeleporterAethernetTests
{
    private static FakeTeleporter CreateTeleporter(out FakeGameStateProvider state)
    {
        state = new FakeGameStateProvider();
        return new FakeTeleporter(state);
    }

    // =========================================================================
    // F1 — Default call returns Arrived and records the call
    // =========================================================================

    [Fact]
    public async Task TeleportToAethernet_DefaultBehavior_ReturnsArrived_AndRecordsCall()
    {
        /*
         * PINNING (should PASS immediately — no implementation change needed).
         *
         * CONTRACT: Given a fresh FakeTeleporter with no scripted result,
         *           When TeleportToAethernet(AethernetId(33)) is called,
         *           Then:
         *             - Result is Ok(TeleportOutcome.Arrived)
         *             - RecordedAethernetTeleports.Count == 1
         *             - RecordedAethernetTeleports[0].Destination == AethernetId(33)
         *
         * If this test fails the fake is broken — do NOT change this test; fix the fake.
         */

        // Arrange
        var teleporter = CreateTeleporter(out _);
        var destination = new AethernetId(33);

        // Act
        var result = await teleporter.TeleportToAethernet(destination, CancellationToken.None);

        // Assert
        Assert.Equal(TeleportOutcome.Arrived, result.ValueOrThrow);
        Assert.Equal(1, teleporter.RecordedAethernetTeleports.Count);
        Assert.Equal(destination, teleporter.RecordedAethernetTeleports[0].Destination);
    }

    // =========================================================================
    // F2 — Scripted sequence drains correctly, then defaults to Arrived
    // =========================================================================

    [Fact]
    public async Task TeleportToAethernet_ScriptedResult_DrainsOnce_ThenDefaultsToArrived()
    {
        /*
         * PINNING (should PASS immediately).
         *
         * CONTRACT: Given ScriptNextTeleportResult(TeleportOutcome.NotAttuned) called once,
         *           When TeleportToAethernet is called twice,
         *           Then:
         *             - First call returns Ok(NotAttuned) (scripted value consumed)
         *             - Second call returns Ok(Arrived) (back to default)
         *             - RecordedAethernetTeleports.Count == 2
         *
         * BUILDER GUIDANCE (if failing): ConsumeNextResult() is shared across all
         *   teleport methods (Aetheryte, Aethernet, Return). The scripted result is
         *   consumed by whichever method is called first.
         */

        // Arrange
        var teleporter = CreateTeleporter(out _);
        teleporter.ScriptNextTeleportResult(TeleportOutcome.NotAttuned);

        // Act
        var result1 = await teleporter.TeleportToAethernet(new AethernetId(33), CancellationToken.None);
        var result2 = await teleporter.TeleportToAethernet(new AethernetId(33), CancellationToken.None);

        // Assert — scripted result consumed on first call; default on second
        Assert.Equal(TeleportOutcome.NotAttuned, result1.ValueOrThrow);
        Assert.Equal(TeleportOutcome.Arrived, result2.ValueOrThrow);
        Assert.Equal(2, teleporter.RecordedAethernetTeleports.Count);
    }

    // =========================================================================
    // F3 — Pre-cancelled CancellationToken throws OperationCanceledException
    //       and the call is NOT recorded
    // =========================================================================

    [Fact]
    public async Task TeleportToAethernet_CancelledToken_ThrowsAndDoesNotRecord()
    {
        /*
         * PINNING (should PASS immediately).
         *
         * CONTRACT: Given a CancellationToken that is already cancelled,
         *           When TeleportToAethernet is called,
         *           Then:
         *             - OperationCanceledException is thrown
         *             - RecordedAethernetTeleports.Count == 0 (call not recorded)
         *
         * BUILDER GUIDANCE: The fake calls ct.ThrowIfCancellationRequested() BEFORE
         *   recording. This is the correct ordering — a cancelled operation should
         *   not appear in the call log.
         */

        // Arrange
        var teleporter = CreateTeleporter(out _);
        var cancelledToken = new CancellationToken(canceled: true);

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => teleporter.TeleportToAethernet(new AethernetId(33), cancelledToken));

        // Assert — no call recorded because cancellation check fires first
        Assert.Equal(0, teleporter.RecordedAethernetTeleports.Count);
    }

    // =========================================================================
    // F4 — Reset clears RecordedAethernetTeleports
    // =========================================================================

    [Fact]
    public async Task TeleportToAethernet_AfterReset_RecordedCallsCleared()
    {
        /*
         * PINNING (should PASS immediately).
         *
         * CONTRACT: Given TeleportToAethernet has been called once successfully,
         *           AND Reset() is then called,
         *           Then RecordedAethernetTeleports.Count == 0 (log cleared).
         *
         * BUILDER GUIDANCE: FakeTeleporter.Reset() calls RecordedAethernetTeleports.Clear().
         *   This test locks that Reset affects the Aethernet log, not just the Aetheryte log.
         */

        // Arrange
        var teleporter = CreateTeleporter(out _);
        await teleporter.TeleportToAethernet(new AethernetId(33), CancellationToken.None);
        Assert.Equal(1, teleporter.RecordedAethernetTeleports.Count); // pre-condition

        // Act
        teleporter.Reset();

        // Assert — log cleared
        Assert.Equal(0, teleporter.RecordedAethernetTeleports.Count);
    }
}
