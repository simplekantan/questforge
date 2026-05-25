using QuestForge.Adapters.Combat;
using QuestForge.Engine;

namespace QuestForge.Engine.Combat;

/// <summary>
/// CI-testable seam: manages the rotation-lease lifecycle for the active combat step.
/// Drives ICombat.StartRotation / StopRotation based on the dispatched EngineAction.
///
/// Lives in QuestForge.Engine (pure C#, CI-buildable) — NOT in QuestForge.Plugin
/// (which is Dalamud-coupled and does not build in CI).
///
/// EngineHost owns a RotationLeaseLatch, calls OnAction at the top of DispatchAction,
/// and calls Release in EndRun to ensure the WrathCombo lease is never leaked.
///
/// STUB — Builder implements the transition logic; tests in QuestForge.Engine.Tests (G-LL).
/// </summary>
public sealed class RotationLeaseLatch
{
    private bool _inLease;

    /// <summary>True when we are currently holding a rotation lease (StartRotation was called).</summary>
    public bool InLease => _inLease;

    /// <summary>
    /// Drives Start/Stop on the given ICombat based on the dispatched action.
    /// Engage → ensure Start (idempotent: no-op if already latched).
    /// Non-Engage → ensure Stop if currently latched, else no-op.
    /// Returns the act taken for test assertions.
    /// </summary>
    public async Task<LeaseAct> OnAction(EngineAction action, ICombat combat, CancellationToken ct)
    {
        if (action is EngineAction.Engage)
        {
            if (_inLease) return LeaseAct.None;
            await combat.StartRotation(ct);
            _inLease = true;
            return LeaseAct.Started;
        }
        if (_inLease)
        {
            await combat.StopRotation(ct);
            _inLease = false;
            return LeaseAct.Stopped;
        }
        return LeaseAct.None;
    }

    /// <summary>
    /// Forced release on run teardown / interruption.
    /// Stops the rotation if currently latched; no-op otherwise.
    /// </summary>
    public async Task<LeaseAct> Release(ICombat combat, CancellationToken ct)
    {
        if (!_inLease) return LeaseAct.None;
        await combat.StopRotation(ct);
        _inLease = false;
        return LeaseAct.Stopped;
    }
}

/// <summary>The action taken by the latch on a given call.</summary>
public enum LeaseAct
{
    /// <summary>Nothing changed — either already in the correct state or no-op.</summary>
    None,
    /// <summary>StartRotation was called and the latch is now held.</summary>
    Started,
    /// <summary>StopRotation was called and the latch is now released.</summary>
    Stopped,
}
