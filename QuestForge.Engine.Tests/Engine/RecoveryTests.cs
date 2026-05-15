using System.Text.Json;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for stateless retry behaviour (§5.8).
///
/// RED PHASE: Tests throw NotImplementedException from QuestEngine.Tick().
/// </summary>
public sealed class RecoveryTests
{
    // -------------------------------------------------------------------------
    // §5.8.1 — Navigation failure → engine returns Navigate again on next tick
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NavigationFailure_NextTickReturnsNavigateAgain()
    {
        /*
         * RED: NotImplementedException from QuestEngine.StartQuest / Tick
         *
         * CONTRACT: Given cold-start setup with player at origin (far from Wymond),
         *   AND FakeNavigator.ScriptNextResult(NavigationOutcome.StoppedByObstacle) for tick 1,
         *   When tick 1 runs → engine returns Navigate(Wymond) → harness calls NavigateTo
         *       → outcome is StoppedByObstacle → player position is NOT updated,
         *   Then tick 2 → engine still returns Navigate(Wymond) (step expect still false),
         *   And tick 3 (after harness allows arrival) → engine returns Interact(Wymond).
         *
         * BUILDER GUIDANCE:
         *   The engine is STATELESS per tick — it never tracks "I already issued this navigation."
         *   On tick 2 it simply reads state again: player still at origin, playerNear → false,
         *   → returns Navigate again. This is the fundamental "stateless retry" model.
         *
         *   FakeNavigator.ScriptNextResult(StoppedByObstacle): on the first NavigateTo call,
         *   returns StoppedByObstacle and does NOT update GameState.SetPosition.
         *   Subsequent calls (without scripting) return Arrived and DO update position.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Script the first NavigateTo to fail with StoppedByObstacle
        harness.Navigator.ScriptNextResult(NavigationOutcome.StoppedByObstacle);

        // Act — Tick 1: engine should return Navigate
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var navigate1 = Assert.IsType<EngineAction.Navigate>(tick1);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate1.Destination);

        // Execute tick 1's action against the fake: outcome is StoppedByObstacle,
        // so position is NOT updated (FakeNavigator only updates on Arrived).
        await harness.Navigator.NavigateTo(navigate1.Destination, navigate1.Options, CancellationToken.None);

        // Player is still at origin — the engine should retry Navigate on the next tick.

        // Act — Tick 2: engine sees playerNear still false → returns Navigate again
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var navigate2 = Assert.IsType<EngineAction.Navigate>(tick2);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate2.Destination);

        // Execute tick 2's action: this time FakeNavigator uses default Arrived → updates position
        await harness.Navigator.NavigateTo(navigate2.Destination, navigate2.Options, CancellationToken.None);
        // Player is now at Wymond's position

        // Act — Tick 3: playerNear is now true → talk step expect (questSequence >= 255) is false
        // → engine returns Interact(Wymond)
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        var interact3 = Assert.IsType<EngineAction.Interact>(tick3);
        Assert.Equal(new NpcId(1003987), interact3.Target);
    }

    // -------------------------------------------------------------------------
    // Fixture loader
    // -------------------------------------------------------------------------

    private static QuestDefinition LoadFixture()
    {
        var json = File.ReadAllText("Fixtures/66130.json");
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }
}