using System.Text.Json;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Integration tests for quest 66130 "Coming to Ul'dah" full-flow and resume scenarios.
/// All tests load the fixture from Fixtures/66130.json.
///
/// RED PHASE: All tests will throw NotImplementedException from QuestEngine.Tick()
/// (and StartQuest() for tests that call it).
/// </summary>
public sealed class Quest66130FlowTests
{
    // -------------------------------------------------------------------------
    // §5.4.1 — Cold start, sequence 0, player far from Wymond → Navigate to Wymond
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ColdStart_PlayerFarFromWymond_ReturnsNavigate()
    {
        /*
         * RED: NotImplementedException from QuestEngine.StartQuest / Tick
         *
         * CONTRACT: Given quest is loaded, sequence is 0, player is at origin (far from Wymond),
         *           When Tick is called,
         *           Then EngineAction.Navigate with Destination = Wymond's position is returned.
         *
         * BUILDER GUIDANCE:
         *   1. Tick reads sequence → 0
         *   2. Finds sequence block 0
         *   3. Evaluates travel step's expect: playerNear({35.56,4,-151.18}, 3) → false (player at origin)
         *   4. Travel step not yet satisfied → returns Navigate(Wymond position, default options)
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // §5.4.2 — Player near Wymond → Interact with Wymond
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TravelComplete_PlayerNearWymond_ReturnsInteract()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given sequence is 0 and player is at Wymond's position (distance = 0 ≤ 3),
         *           When Tick is called,
         *           Then EngineAction.Interact(NpcId(1003987)) is returned.
         *
         * BUILDER GUIDANCE:
         *   1. Tick reads sequence → 0
         *   2. Travel step expect: playerNear({35.56,4,-151.18}, 3) → true (player at exact position)
         *      → travel step is satisfied, engine advances to next step
         *   3. Talk step expect: questSequence(66130) >= 255 → false (seq is still 0)
         *      → talk step is not yet satisfied → returns Interact(NpcId(1003987))
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(35.56f, 4.0f, -151.18f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1003987), interact.Target);
    }

    // -------------------------------------------------------------------------
    // §5.4.3 — Sequence advanced to 255, player still near Wymond → Navigate to Momodi
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SequenceAdvancedTo255_ReturnsNavigateToMomodi()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given sequence is 255 and player is near Wymond (far from Momodi),
         *           When Tick is called,
         *           Then EngineAction.Navigate(Destination=(21.84, 7, -81.13)) is returned.
         *
         * BUILDER GUIDANCE:
         *   1. Tick reads sequence → 255
         *   2. Finds sequence block 255
         *   3. Travel step expect: playerNear({21.84,7,-81.13}, 3) → false (player at Wymond)
         *      → returns Navigate(Momodi position)
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(35.56f, 4.0f, -151.18f)); // near Wymond, far from Momodi

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(21.84f, 7.0f, -81.13f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // §5.4.4 — Quest complete → Done
    // -------------------------------------------------------------------------

    [Fact]
    public async Task QuestComplete_ReturnsDone()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given quest status is Complete,
         *           When Tick is called,
         *           Then EngineAction.Done is returned.
         *
         * BUILDER GUIDANCE:
         *   The IsQuestComplete check happens BEFORE sequence walking (§4.2 algorithm step 3).
         *   Even if sequence is at 0, Done is returned immediately.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.Done>(action);
    }

    // -------------------------------------------------------------------------
    // §5.4.5 — End-to-end: full flow yields exactly 4 actions before Done
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_FullFlow_FourActionsBeforeDone()
    {
        /*
         * RED: NotImplementedException (StartQuest + Tick both throw)
         *
         * CONTRACT: Given cold start with callbacks wired to advance quest state,
         *           When the engine runs to completion,
         *           Then exactly 4 actions are returned in order:
         *             Navigate(Wymond), Interact(Wymond), Navigate(Momodi), Interact(Momodi)
         *           and Done terminates the loop within 10 ticks.
         *
         * BUILDER GUIDANCE:
         *   FakeNavigator automatically updates GameState.SetPosition on Arrived outcome.
         *   FakeInteractor.OnInteractWith callbacks advance quest state.
         *   The harness loop (RunToCompletion) drives tick → execute → tick until Done.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        // Wire up callbacks: talking to Wymond advances sequence to 255
        harness.Interactor.OnInteractWith(new NpcId(1003987), () =>
            harness.QuestState.SetQuestSequence(new QuestId(66130), 255));

        // Talking to Momodi completes the quest
        harness.Interactor.OnInteractWith(new NpcId(1003988), () =>
            harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var actions = await harness.RunToCompletion(maxTicks: 10);

        // Assert — exactly 4 actions
        Assert.Equal(4, actions.Count);

        // First action: Navigate to Wymond
        var nav1 = Assert.IsType<EngineAction.Navigate>(actions[0]);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), nav1.Destination);

        // Second action: Interact with Wymond
        var interact1 = Assert.IsType<EngineAction.Interact>(actions[1]);
        Assert.Equal(new NpcId(1003987), interact1.Target);

        // Third action: Navigate to Momodi
        var nav2 = Assert.IsType<EngineAction.Navigate>(actions[2]);
        Assert.Equal(new WorldPosition(21.84f, 7.0f, -81.13f), nav2.Destination);

        // Fourth action: Interact with Momodi
        var interact2 = Assert.IsType<EngineAction.Interact>(actions[3]);
        Assert.Equal(new NpcId(1003988), interact2.Target);
    }

    // -------------------------------------------------------------------------
    // §5.5.1 — Resume at sequence 255: first action is Navigate to Momodi
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Resume_StartAtSeq255_ReturnsNavigateToMomodi()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given quest is loaded with sequence already at 255 and player far from Momodi,
         *           When Tick is called,
         *           Then Navigate(Momodi) is returned immediately — no Wymond actions.
         *
         * BUILDER GUIDANCE:
         *   The engine reads current sequence (255) and walks sequence block 255 directly.
         *   Sequence block 0 is never consulted.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f)); // far from Momodi

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(21.84f, 7.0f, -81.13f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // §5.5.2 — Resume at sequence 255, player already near Momodi → Interact
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Resume_AtMomodiPosition_ReturnsInteract()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given sequence is 255 and player is at Momodi's exact position,
         *           When Tick is called,
         *           Then EngineAction.Interact(NpcId(1003988)) is returned.
         *
         * BUILDER GUIDANCE:
         *   Travel step expect: playerNear({21.84,7,-81.13}, 3) → true (player at exact position)
         *   → travel step satisfied → engine advances to talk step
         *   Talk step expect: isQuestComplete(66130) → false (quest still accepted, not complete)
         *   → returns Interact(NpcId(1003988))
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(21.84f, 7.0f, -81.13f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1003988), interact.Target);
    }

    // -------------------------------------------------------------------------
    // §5.6.1 — Done detection before sequence walk
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Done_CheckedBeforeSequenceWalk()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given quest is complete (isQuestComplete returns true),
         *           When Tick is called,
         *           Then Done is returned WITHOUT consulting any sequence block.
         *
         * BUILDER GUIDANCE:
         *   The algorithm in §4.2 checks IsQuestComplete before finding the matching sequence block.
         *   This test verifies that ordering by using sequence 0 (where the quest would normally
         *   prompt for travel) but quest is already complete → Done is returned.
         */

        // Arrange
        var harness = new EngineTestHarness();
        // Sequence is 0 — would normally return Navigate if quest was not complete
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Done returned even though sequence 0 would normally emit Navigate
        Assert.IsType<EngineAction.Done>(action);
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