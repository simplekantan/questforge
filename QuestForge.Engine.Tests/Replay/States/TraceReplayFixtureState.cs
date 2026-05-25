using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Generic trace-replay fixture state. Built from a source JSONL trace via
/// ReplayGameStateProvider / ReplayQuestState + inert no-op action adapters.
/// OnTick is a no-op: the ObservationScanner advances as the engine reads recorded observations.
/// </summary>
internal sealed class TraceReplayFixtureState : IFixtureState
{
    public IGameStateProvider GameState  { get; }
    public IQuestState        QuestState { get; }
    public INavigator         Navigator  { get; } = new InertNavigator();
    public ITeleporter        Teleporter { get; } = new InertTeleporter();
    public IInteractor        Interactor { get; } = new InertInteractor();
    public ICombat            Combat     { get; } = new FakeCombat();
    public IGearManager       Gear       { get; } = new FakeGearManager();
    public IMinigameSkipper   Minigames  { get; } = new FakeMinigameSkipper();
    public IDialogueResolver  Dialogue   { get; } = new FakeDialogueResolver();
    public ITimingProfile     Timing     { get; } = new FakeTimingProfile();

    private TraceReplayFixtureState(IReadOnlyList<ObservationEvent> observations)
    {
        GameState  = new ReplayGameStateProvider(observations);
        QuestState = new ReplayQuestState(observations);
    }

    public static TraceReplayFixtureState FromTraceFile(string tracePath)
    {
        var observations = TraceReader.ReadFile<ObservationEvent>(tracePath);
        if (observations.Count == 0)
            throw new InvalidDataException(
                $"Trace '{Path.GetFileName(tracePath)}' contains no observation events; " +
                $"a trace-backed fixture requires recorded engine inputs.");
        return new TraceReplayFixtureState(observations);
    }

    public void OnTick(EngineAction action, int tick) { }
}
