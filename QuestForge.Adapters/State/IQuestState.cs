using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.State;

public interface IQuestState
{
    // Status queries
    Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct);
    Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct);
    Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct);
    Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct);

    // Lifecycle queries
    Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct);
    Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct);
    Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct);
    Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct);

    /// <summary>
    /// Stronger eligibility than <see cref="IsQuestAvailable"/>: true only when the player
    /// can accept this quest RIGHT NOW. In addition to everything IsQuestAvailable checks
    /// (complete / accepted / job / level / prerequisites), this also evaluates the real-time
    /// gates listed in §4 (v1: Grand Company rank). The invariant acceptable-now ⊆ available
    /// holds: IsAcceptableNow == true implies IsQuestAvailable == true.
    ///
    /// Authoring-panel read ONLY. The engine does not consume this signal and the recording
    /// proxy does not emit an observation for it (see ACCEPTABLE_NOW_PLAN §3.1).
    /// </summary>
    Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct);

    // Collections
    Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct);

    // Work bytes (V0–V5)
    Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct);

    // Rewards
    Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct);
}

public enum QuestStatus
{
    Unknown,
    Locked,
    Available,
    Accepted,
    Complete,
    Failed
}

public record QuestUnlockReason(
    bool LevelTooLow, int RequiredLevel,
    bool PrerequisiteIncomplete, IReadOnlyList<QuestId> MissingPrereqs,
    bool WrongJob, JobId? RequiredJob,
    bool AlreadyCompleted,
    bool OtherReason, string? Detail
);

public record QuestReward(
    int Index,
    ItemId Item,
    int Quantity,
    int ItemLevel,
    long VendorPrice,
    IReadOnlyList<JobId> RestrictedJobs,
    bool IsUntradable,
    uint EquipSlotCategory = 0,
    uint ItemActionId = 0,
    uint ItemUiCategoryId = 0
);