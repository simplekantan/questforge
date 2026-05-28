using FFXIVClientStructs.FFXIV.Client.Game;
using QuestForge.Adapters.Movement;

namespace QuestForge.Adapters.Dalamud.Movement;

/// <summary>
/// Dalamud-backed IMount implementation.
/// Fires Mount Roulette (ActionManager.UseAction(GeneralAction, 9)) and
/// Dismount (ActionManager.UseAction(GeneralAction, 23)).
///
/// Both calls are best-effort: UseAction returns false when the game rejects the action
/// (indoor zone, no mount unlocked, mid-cast, mid-cutscene). We discard the return value
/// in v1 — the engine re-reads MountState on the next tick to determine outcome.
///
/// NOTE (Q4): this logic lives in EngineHost.DispatchAction (not in QuestEngine.Tick).
/// Do NOT move the mount-decision predicate into the engine — it belongs on the host so
/// that replay tests against QuestEngine.Tick() don't inadvertently require new reads
/// that would starve existing replay fixtures.
///
/// NOTE (R2 — flying dismount): Dismount on a mid-flight player causes the player to fall.
/// No altitude protection is provided in v1. Quest authors should structure quests so that
/// Navigate steps end at ground-level before a non-Navigate step follows.
///
/// TODO (O1): if UseAction returns false AND !Casting AND !InCombat, consider a single-line
/// warning log so the user can diagnose indoor-zone or missing-mount conditions.
/// Not in v1 scope.
/// </summary>
public sealed class DalamudMount : IMount
{
    private readonly PluginServices _svc;

    public DalamudMount(PluginServices svc) => _svc = svc;

    /// <inheritdoc/>
    public unsafe Task Mount(CancellationToken ct = default)
    {
        // TODO O1: discard the bool return value in v1 (silent best-effort).
        // Future: log a warning when false AND !Casting AND !InCombat (indoor zone / no mount).
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.GeneralAction, 9);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public unsafe Task Dismount(CancellationToken ct = default)
    {
        // TODO O1: discard the bool return value in v1 (silent best-effort).
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.GeneralAction, 23);
        return Task.CompletedTask;
    }
}
