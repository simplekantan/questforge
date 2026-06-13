using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Duty;

public interface IDutyRunner
{
    /// <summary>
    /// Configure AutoDuty for Support mode and start the duty.
    /// </summary>
    /// <param name="territoryType">
    /// Territory type ID derived from ContentFinderConditionId via Lumina.
    /// AutoDuty IPC accepts territory type, not CFC ID.
    /// </param>
    Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct);

    /// <summary>
    /// Stop AutoDuty's current run. Idempotent -- safe to call when not started.
    /// </summary>
    Task<Result<bool>> StopDuty(CancellationToken ct);

    /// <summary>
    /// Check whether AutoDuty is installed and its IPC is responsive.
    /// </summary>
    Task<Result<bool>> IsAvailable(CancellationToken ct);

    /// <summary>
    /// Check whether AutoDuty has a navigation path for the given territory.
    /// </summary>
    Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct);

    /// <summary>
    /// Check whether AutoDuty is currently running (looping/navigating).
    /// Returns false when stopped or on read failure.
    /// </summary>
    Task<Result<bool>> IsRunning(CancellationToken ct);
}
