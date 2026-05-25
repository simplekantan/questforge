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
    bool IsUntradable
);

public enum RewardSelectionStrategy
{
    FirstAvailable,
    SpecificItem,
    HighestIlvlForCurrentJob,
    HighestIlvlForAnyJob,
    HighestVendorValue,
    MatchingCurrentJob,
    AskUser
}