using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Engine;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Common abstraction for scripted and trace-replay fixture state machines.
/// Exposes the full adapter set required by QuestEngine and an OnTick callback.
/// </summary>
internal interface IFixtureState
{
    IGameStateProvider GameState  { get; }
    IQuestState        QuestState { get; }
    INavigator         Navigator  { get; }
    ITeleporter        Teleporter { get; }
    IInteractor        Interactor { get; }
    ICombat            Combat     { get; }
    IGearManager       Gear       { get; }
    IMinigameSkipper   Minigames  { get; }
    IDialogueResolver  Dialogue   { get; }
    ITimingProfile     Timing     { get; }

    /// <summary>
    /// Called after the engine produces <paramref name="action"/> on tick <paramref name="tick"/>.
    /// Scripted states mutate their fakes here; the trace-replay state is a no-op.
    /// </summary>
    void OnTick(EngineAction action, int tick);

    /// <summary>
    /// Called by the harness exactly once per new distinct (stepId, actionType) transition.
    /// Trace-replay states advance the segment cursor here; scripted states are no-ops.
    /// </summary>
    void OnTransitionRecorded(EngineAction action, int tick);
}
