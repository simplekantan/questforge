using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.State;

public interface IGameStateProvider
{
    // Player state
    Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct);
    Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct);
    Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct);
    Task<Result<bool>> IsPlayerInCombat(CancellationToken ct);
    Task<Result<MountState>> GetMountState(CancellationToken ct);
    Task<Result<bool>> IsPlayerDiving(CancellationToken ct);
    Task<Result<bool>> IsPlayerCasting(CancellationToken ct);
    Task<Result<bool>> IsPlayerDead(CancellationToken ct);
    Task<Result<JobId>> GetCurrentJob(CancellationToken ct);
    Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct);

    // Instanced content
    Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct);
    Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct);

    // New Game+
    Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct);

    // World/NPC state
    Task<Result<IReadOnlyList<NpcReference>>>    GetNearbyNpcs(float radius, CancellationToken ct);
    Task<Result<IReadOnlyList<HostileActor>>>    GetHostileActors(float radius, CancellationToken ct);
    Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct);
    Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct);
    Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct);
    Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct);
    Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct);
    Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct);

    // UI state (composite)
    Task<Result<UiState>> GetUiState(CancellationToken ct);

    // Inventory
    Task<Result<int>> GetFreeInventorySlots(CancellationToken ct);
    Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct);
    Task<Result<long>> GetGil(CancellationToken ct);

    // Composite/derived
    Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct);

    // Aethernet
    Task<Result<AethernetId?>> GetLastAethernetDestination(CancellationToken ct);
}

public enum MountState
{
    Dismounted,
    Mounted,
    Flying
}

public record PlayerStateSnapshot(
    WorldPosition Position,
    ZoneId Zone,
    JobId Job,
    int JobLevel,
    bool InCombat,
    MountState MountState,
    bool Diving,
    bool Casting,
    bool Dead
);

public enum InstanceKind
{
    None,
    Dungeon,
    Trial,
    Raid,
    AllianceRaid,
    SinglePlayerDuty,
    PvP,
    DeepDungeon,
    VariantDungeon,
    Other
}

public record DutyAvailability(
    bool Unlocked,
    bool SupportAvailable,
    bool FinderAvailable,
    bool UndersizedPartyAllowed,
    int MinimumLevel,
    int MinimumIlvl,
    string? UnlockHint
);

public record NewGamePlusState(
    bool IsActive,
    NewGamePlusChapter? CurrentChapter,
    bool IsSuspended
);

public record NewGamePlusChapter(int ChapterId, string Name);

public record UiState(
    bool DialogueOpen,
    bool CutscenePlaying,
    bool LoadingZone,
    bool YesNoPromptOpen,
    bool SelectStringOpen,
    bool RewardSelectionOpen,
    bool ShopOpen,
    bool DeathPromptOpen,
    DialogueState? CurrentDialogue
);

public record DialogueState(
    string SpeakerKey,
    int OptionCount,
    IReadOnlyList<DialogueOptionId> AvailableOptions
);

public record TravelCapability(
    bool CanTeleport,
    AetheryteId? NearestAttuned,
    bool RequiresOnFootSegment,
    bool CanFly,
    bool CanMount,
    bool CanDive,
    long EstimatedGilCost
);