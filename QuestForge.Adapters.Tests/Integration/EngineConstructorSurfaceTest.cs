using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;

namespace QuestForge.Adapters.Tests.Integration;

public class EngineConstructorSurfaceTest
{
    [Fact]
    public void AllFakesImplementAdapterInterfaces()
    {
        // Verify all 10 fakes + FakeTraceWriter satisfy their interface contracts.
        // When QuestEngine exists (Phase 4), its constructor takes all of these.
        IGameStateProvider gameState  = new FakeGameStateProvider();
        IQuestState        questState = new FakeQuestState();
        INavigator         navigator  = new FakeNavigator((FakeGameStateProvider)gameState);
        ITeleporter        teleporter = new FakeTeleporter((FakeGameStateProvider)gameState);
        IInteractor        interactor = new FakeInteractor(
                                           (FakeGameStateProvider)gameState,
                                           (FakeQuestState)questState);
        IDialogueResolver  dialogue   = new FakeDialogueResolver();
        ICombat            combat     = new FakeCombat();
        IGearManager       gear       = new FakeGearManager();
        ITimingProfile     timing     = new FakeTimingProfile();
        IMinigameSkipper   minigames  = new FakeMinigameSkipper();
        ITraceWriter       trace      = new FakeTraceWriter();

        // All 10 + trace should be non-null; this also verifies the constructors compile.
        Assert.NotNull(gameState);
        Assert.NotNull(questState);
        Assert.NotNull(navigator);
        Assert.NotNull(teleporter);
        Assert.NotNull(interactor);
        Assert.NotNull(dialogue);
        Assert.NotNull(combat);
        Assert.NotNull(gear);
        Assert.NotNull(timing);
        Assert.NotNull(minigames);
        Assert.NotNull(trace);
    }
}