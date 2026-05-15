using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Movement;
using QuestForge.Engine.Tests.Fakes;

namespace QuestForge.Engine.Tests.Helpers;

/// <summary>
/// Creates a fully-wired engine instance backed by all fakes.
/// Use this in flow tests: configure state, wire callbacks, call RunToCompletion.
/// </summary>
public sealed class EngineTestHarness
{
    public FakeGameStateProvider GameState { get; } = new FakeGameStateProvider();
    public FakeQuestState QuestState { get; }
    public FakeNavigator Navigator { get; }
    public FakeTeleporter Teleporter { get; }
    public FakeInteractor Interactor { get; }
    public FakeCombat Combat { get; } = new FakeCombat();
    public FakeGearManager GearManager { get; } = new FakeGearManager();
    public FakeMinigameSkipper MinigameSkipper { get; } = new FakeMinigameSkipper();
    public FakeDialogueResolver DialogueResolver { get; }
    public FakeTimingProfile TimingProfile { get; } = new FakeTimingProfile();
    public NullTraceWriter TraceWriter { get; } = new NullTraceWriter();
    public QuestEngine Engine { get; }

    public EngineTestHarness()
    {
        QuestState = new FakeQuestState();
        Navigator = new FakeNavigator(GameState);
        Teleporter = new FakeTeleporter(GameState);
        Interactor = new FakeInteractor(GameState, QuestState);
        DialogueResolver = new FakeDialogueResolver();

        Engine = new QuestEngine(
            GameState,
            QuestState,
            Navigator,
            Teleporter,
            Interactor,
            Combat,
            GearManager,
            MinigameSkipper,
            DialogueResolver,
            TimingProfile,
            TraceWriter,
            NullLogger<QuestEngine>.Instance);
    }

    /// <summary>
    /// Ticks the engine and executes actions against fakes until Done is returned.
    /// Records every action emitted (not including Done itself).
    /// Throws if AwaitUser is returned or maxTicks is exceeded.
    /// </summary>
    public async Task<List<EngineAction>> RunToCompletion(int maxTicks = 10)
    {
        var actions = new List<EngineAction>();
        var ct = CancellationToken.None;

        for (var i = 0; i < maxTicks; i++)
        {
            var action = await Engine.Tick(ct);

            switch (action)
            {
                case EngineAction.Done:
                    return actions;

                case EngineAction.AwaitUser au:
                    throw new InvalidOperationException(
                        $"Engine returned AwaitUser unexpectedly at tick {i + 1}: {au.Reason}");

                case EngineAction.Navigate nav:
                    actions.Add(action);
                    await Navigator.NavigateTo(nav.Destination, nav.Options, ct);
                    break;

                case EngineAction.Interact interact:
                    actions.Add(action);
                    await Interactor.InteractWith(interact.Target, ct);
                    break;

                case EngineAction.Wait:
                    // Wait actions are not recorded — just loop again
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled EngineAction subtype: {action.GetType().Name}");
            }
        }

        throw new InvalidOperationException(
            $"Engine did not reach Done within {maxTicks} ticks. Actions so far: [{string.Join(", ", actions)}]");
    }
}