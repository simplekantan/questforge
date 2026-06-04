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
using QuestForge.Engine.Tests.Engine;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Generic trace-replay fixture state. Built from a full JSONL trace (observations + decisions)
/// via a shared SegmentedObservationScanner passed to both ReplayGameStateProvider and
/// ReplayQuestState. Inert no-op action adapters are used.
///
/// OnTick is a no-op (per option A). Segment advancement is driven by OnTransitionRecorded,
/// which is called by the harness exactly once per new distinct (stepId, actionType) transition.
/// </summary>
internal sealed class TraceReplayFixtureState : IFixtureState
{
    private readonly SegmentedObservationScanner _scanner;
    private readonly ManualTimeProvider _clock;

    public IGameStateProvider  GameState       { get; }
    public IQuestState         QuestState      { get; }
    public INavigator          Navigator       { get; } = new InertNavigator();
    public ITeleporter         Teleporter      { get; } = new InertTeleporter();
    public IInteractor         Interactor      { get; } = new InertInteractor();
    public ICombat             Combat          { get; } = new FakeCombat();
    public IGearEquipper       GearEquipper    { get; } = new FakeGearEquipper();
    public IBestGearEquipper   BestGearEquipper{ get; } = new FakeBestGearEquipper();
    public IJobChanger         JobChanger      { get; } = new FakeJobChanger();
    public IMinigameSkipper    Minigames       { get; } = new FakeMinigameSkipper();
    public IDialogueResolver   Dialogue        { get; } = new FakeDialogueResolver();
    public ITimingProfile      Timing          { get; } = new FakeTimingProfile();
    public TimeProvider?       Clock           { get; }

    private TraceReplayFixtureState(IReadOnlyList<TraceEvent> trace)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        Clock      = clock;
        _clock     = clock;
        _scanner   = new SegmentedObservationScanner(trace);
        GameState  = new ReplayGameStateProvider(_scanner);
        QuestState = new ReplayQuestState(_scanner);
    }

    public static TraceReplayFixtureState FromTraceFile(string tracePath)
    {
        var trace = TraceReader.ReadFile(tracePath);
        var obsCount = trace.OfType<ObservationEvent>().Count();
        if (obsCount == 0)
            throw new InvalidDataException(
                $"Trace '{Path.GetFileName(tracePath)}' contains no observation events; " +
                $"a trace-backed fixture requires recorded engine inputs.");
        return new TraceReplayFixtureState(trace);
    }

    /// <summary>Advance the fake clock each tick so WaitSteps complete without real-time delay.</summary>
    public void OnTick(EngineAction action, int tick) => _clock.Advance(TimeSpan.FromSeconds(1));

    /// <summary>Called by the harness once per new distinct transition. Advances the replay segment.</summary>
    public void OnTransitionRecorded(EngineAction action, int tick)
        => _scanner.AdvanceSegment();
}
