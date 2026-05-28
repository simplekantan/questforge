namespace QuestForge.Adapters.Movement;

/// <summary>
/// Controls player mount state. Both methods are fire-and-forget best-effort:
/// the game may silently reject the action (indoor zone, no mount unlocked, mid-cast).
/// The engine verifies outcome on the next tick via IGameStateProvider.GetPlayerState().MountState.
/// </summary>
public interface IMount
{
    /// <summary>Fires Mount Roulette (GeneralAction 9).</summary>
    Task Mount(CancellationToken ct = default);

    /// <summary>Fires Dismount (GeneralAction 23).</summary>
    Task Dismount(CancellationToken ct = default);
}
