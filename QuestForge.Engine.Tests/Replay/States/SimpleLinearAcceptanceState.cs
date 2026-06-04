using QuestForge.Adapters.Combat;
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
using QuestForge.Adapters.Types;
using QuestForge.Engine;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Scripted state machine for the simple-linear-acceptance fixture.
///
/// The engine ticks but DispatchAction is not called (we inspect actions, not execute them).
/// This state machine manually advances game state after each tick so predicates evaluate
/// in the right order to produce the expected transitions.
///
/// State progression:
/// 1. Navigate(travel-to-wymond) emitted → set position to Wymond → predicate will flip next tick
/// 2. Interact(talk-to-wymond) emitted  → set questSeq=255 → engine moves to sequence 255
/// 3. Navigate(travel-to-momodi) emitted → set position to Momodi → predicate will flip next tick
/// 4. Interact(talk-to-momodi) emitted  → set isComplete=true → Done on next tick
/// </summary>
internal sealed class SimpleLinearAcceptanceState : IFixtureState
{
    private static readonly QuestId Quest66130 = new(66130);
    private static readonly WorldPosition WymondPos = new(35.56f, 4.0f, -151.18f);
    private static readonly WorldPosition MomodiPos = new(21.84f, 7.0f, -81.13f);

    private enum Phase { ToWymond, AtWymond, ToMomodi, AtMomodi, Complete }
    private Phase _phase = Phase.ToWymond;

    public FakeGameStateProvider GameState { get; } = new();
    public FakeQuestState QuestState { get; } = new();
    public FakeNavigator Navigator { get; }
    public ITeleporter Teleporter { get; }
    public IInteractor Interactor { get; }
    public ICombat Combat { get; } = new FakeCombat();
    public IGearEquipper GearEquipper { get; } = new FakeGearEquipper();
    public IBestGearEquipper BestGearEquipper { get; } = new FakeBestGearEquipper();
    public IJobChanger JobChanger { get; } = new FakeJobChanger();
    public IMinigameSkipper Minigames { get; } = new FakeMinigameSkipper();
    public IDialogueResolver Dialogue { get; } = new FakeDialogueResolver();
    public ITimingProfile Timing { get; } = new FakeTimingProfile();
    public TimeProvider? Clock => null;

    IGameStateProvider IFixtureState.GameState => GameState;
    IQuestState IFixtureState.QuestState => QuestState;
    INavigator IFixtureState.Navigator => Navigator;

    public SimpleLinearAcceptanceState()
    {
        Navigator  = new FakeNavigator(GameState);
        Teleporter = new FakeTeleporter(GameState);
        Interactor = new FakeInteractor(GameState, QuestState);

        // Initial state: zone 182, away from Wymond, quest not started
        GameState.SetZone(new ZoneId(182));
        GameState.SetPosition(new WorldPosition(44.7f, 4.0f, -148.7f));
        QuestState.SetQuestSequence(Quest66130, 0);
        QuestState.SetQuestStatus(Quest66130, QuestStatus.Available);
    }

    public void OnTransitionRecorded(EngineAction action, int tick) { }

    public void OnTick(EngineAction action, int tick)
    {
        switch (_phase)
        {
            case Phase.ToWymond when action is EngineAction.Navigate:
                GameState.SetPosition(WymondPos);
                _phase = Phase.AtWymond;
                break;

            case Phase.AtWymond when action is EngineAction.Interact:
                QuestState.SetQuestSequence(Quest66130, 255);
                QuestState.SetQuestStatus(Quest66130, QuestStatus.Accepted);
                GameState.SetPosition(new WorldPosition(44.7f, 4.0f, -80.0f));
                _phase = Phase.ToMomodi;
                break;

            case Phase.ToMomodi when action is EngineAction.Navigate:
                GameState.SetPosition(MomodiPos);
                _phase = Phase.AtMomodi;
                break;

            case Phase.AtMomodi when action is EngineAction.Interact:
                QuestState.SetQuestStatus(Quest66130, QuestStatus.Complete);
                _phase = Phase.Complete;
                break;
        }
    }
}
