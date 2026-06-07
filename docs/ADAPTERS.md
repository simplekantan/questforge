# Adapter Interfaces Specification

**Status:** v1 — implemented through Phase 8; revise alongside code changes
**Owners:** QuestForge maintainers
**Related:** [DESIGN.md](./DESIGN.md), [TRACE_FORMAT.md](./TRACE_FORMAT.md)

---

## 1. Purpose

Adapter interfaces define the contract between the engine and the rest of the world. The engine references only these interfaces; concrete implementations live in `QuestForge.Plugin` and depend on Dalamud, vnavmesh, Lifestream, TextAdvance, and combat plugins.

This separation is what makes the engine testable in CI without a running game.

---

## 2. Design principles

These apply to every interface defined below.

### 2.1 Interfaces describe contracts, not implementations

Adapter interfaces are pure C# definitions. They do not import `Dalamud.*` or any plugin-specific namespace. Game concepts are expressed as adapter-layer types (`ZoneId`, `WorldPosition`, `NpcId`). The plugin layer translates between adapter types and Dalamud types.

### 2.2 All async, all cancellable

Every method returns `Task<T>` or `Task<Result<T>>` and takes a `CancellationToken`. FFXIV operations are inherently asynchronous; making the contracts async from day one avoids painful retrofits. Even apparently-synchronous reads are async because the implementation may need to marshal to the framework thread.

### 2.3 Failure is a return value, not an exception

Operations return `Result<T>`:

- `Result<T>.Success` carries a payload
- `Result<T>.Failure` carries a structured reason

Exceptions are reserved for genuine bugs (null arguments, contract violations). Routine failures the engine handles (target not found, in combat, insufficient gil) flow through `Result<T>` so they participate in engine state-machine logic and become trace-recordable.

### 2.4 No interface depends on Dalamud types

The adapter layer is Dalamud-free. The engine references the adapter layer. The plugin references both and bridges them. This is the seam that lets the engine run in tests without a game.

### 2.5 Every read is recordable

Methods returning game state are designed to flow cleanly through the recording proxy. Granularity matters — the engine requests each piece of state separately rather than asking for bundled "convenience" reads, so recordings remain granular.

The single exception is composite reads (`PlayerStateSnapshot`, `UiState`, `TravelCapability`) where atomicity matters more than granularity. These are recorded as one observation key with structured value.

### 2.6 Time abstractions go through `ITimingProfile`

No interface takes a `TimeSpan delay` parameter. Delays are derived from `ITimingProfile`. Adapters may accept *timeouts* (failure thresholds), which are different — timeouts express how long the engine is willing to wait before treating the operation as failed.

### 2.7 Platform compatibility

Adapters are pure .NET. They work wherever Dalamud works, including XIVLauncher.Core on Linux and Steam Deck. File paths use `Path.Combine`. No Windows-specific APIs in adapter code.

### 2.8 Adapter substitution is a design goal

Specific plugins named here (vnavmesh, Lifestream, TextAdvance, BossMod, WrathCombo, RotationSolverReborn, AutoDuty, Stylist) are the **current** implementations behind their respective adapter interfaces. The adapter abstraction exists precisely so they can be swapped when the ecosystem changes.

Concretely:

- The engine references only interface types (`INavigator`, `ICombat`, etc.) — never `vnavmesh.IPC` or `BossMod.AutoRotation` directly
- When a new plugin emerges (or a current one dies), writing a new adapter implementation is sufficient — engine code and quest data remain unchanged
- The plugin's settings UI is allowed to know specific plugin names (the user picks "BossMod" vs "WrathCombo" in a dropdown); the engine doesn't see those names

Adapter interfaces themselves may be revised if a better abstraction emerges. Such revisions should not require quest authors to update quest files — that's what schema versioning is for. Minor interface refactoring shouldn't bump the schema major version unless it actually breaks authoring.

### 2.9 Adapter methods are our abstraction, not Dalamud's

Method names on adapter interfaces (`GetCurrentInstanceKind`, `EnterSinglePlayerDuty`, etc.) are *our* contract. Implementations translate from Dalamud's lower-level primitives (`IClientState.TerritoryType`, `ICondition[BoundByDuty]`, Lumina sheet rows, etc.) into our domain abstractions.

For example, `GetCurrentInstanceKind()` doesn't exist in Dalamud's API — its implementation reads `TerritoryType`, cross-references against the Lumina `TerritoryType` and `ContentFinderCondition` sheets, combines with `Condition` flags, and returns our `InstanceKind` enum value. The adapter layer is where Dalamud-shape becomes engine-shape.

---

## 3. Cross-cutting types

Value types referenced across multiple interfaces. They live in `QuestForge.Adapters.Types`.

### 3.1 Strong-typed identifiers

```csharp
public readonly record struct ZoneId(uint Value);
public readonly record struct NpcId(uint Value);
public readonly record struct QuestId(uint Value);
public readonly record struct AetheryteId(uint Value);
public readonly record struct AethernetId(uint Value);
public readonly record struct ItemId(uint Value);
public readonly record struct JobId(uint Value);
public readonly record struct DialogueOptionId(uint Value);
public readonly record struct InteractableId(uint Value);
public readonly record struct DutyId(uint Value);
```

Rationale: `uint` everywhere would let `NpcId` be passed where `ZoneId` is expected. With `readonly record struct`, the compiler catches that. The cost — occasional `.Value` extraction — is worth the type safety.

### 3.2 Geometry

```csharp
public readonly record struct WorldPosition(float X, float Y, float Z);
```

Coordinates are zone-local. Cross-zone reasoning is the engine's job, not the type's.

### 3.3 References

```csharp
public record NpcReference(NpcId Id, WorldPosition Position, float DistanceToPlayer);

public record InteractableReference(
    InteractableId Id,
    WorldPosition Position,
    float DistanceToPlayer,
    InteractableKind Kind
);

public enum InteractableKind
{
    ItemPickup,
    EventTrigger,
    Aetheryte,
    AetherCurrent,
    DutyTrigger,
    Other
}
```

### 3.4 `Result<T>`

```csharp
// Zero-sized value type for void-returning operations.
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public abstract record Result<T>
{
    public sealed record Success(T Value) : Result<T>;
    public sealed record Failure(string Reason, string? Detail = null) : Result<T>;

    public bool IsSuccess => this is Success;
    public T? ValueOrDefault => this is Success s ? s.Value : default;

    // ValueOrThrow: throws on Failure; use after checking IsSuccess.
    // Not named Value to avoid shadowing Result<T>.Success.Value.
    public T ValueOrThrow => this is Success s
        ? s.Value
        : throw new InvalidOperationException($"Result is Failure: {((Failure)this).Reason}");
}

// Static helpers — the non-generic Result abstract record is replaced by Result<Unit>.
// Void-returning adapter methods return Task<Result<Unit>>.
public static class Result
{
    public static Result<T>.Success  Ok<T>(T value)                              => new(value);
    public static Result<T>.Failure  Fail<T>(string reason, string? detail = null) => new(reason, detail);
    public static Result<Unit>.Success  Ok()                                     => new(Unit.Value);
    public static Result<Unit>.Failure  Fail(string reason, string? detail = null)  => new(reason, detail);
}
```

`Reason` is a stable machine-readable string. `Detail` is human-readable context. The engine matches on `Reason`; the UI shows `Detail`.

**Note on void returns:** Throughout this document, any method that previously returned `Task<Result>` (non-generic) now returns `Task<Result<Unit>>`. The non-generic `Result` abstract record is removed to avoid a C# naming conflict with the `public static class Result` helper. The semantics are identical.

---

## 4. `IGameStateProvider` — read-only view of game state

The single most consequential interface. The recording proxy wraps it. Everything the engine "knows" about the game comes through here.

```csharp
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
    Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct);
    Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct);
    Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct);
    Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct);
    Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct);
    Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct);
    // Note: DalamudGameStateProvider.IsAetheryteAttuned currently reads attunement state via
    // UIState (ClientStructs). The initial Phase 11B implementation was a stub that returned 0
    // for all aetherytes; the UIState-based implementation was completed as part of Phase 11
    // "Other Phase 11 work." If you observe incorrect attunement values, check the ClientStructs
    // UIState field mapping — the correct API was UIState, not PlayerState.
    Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct);

    // UI state (composite)
    Task<Result<UiState>> GetUiState(CancellationToken ct);

    // Inventory
    Task<Result<int>> GetFreeInventorySlots(CancellationToken ct);
    Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct);
    Task<Result<long>> GetGil(CancellationToken ct);

    // Composite/derived
    Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct);
}
```

### 4.1 Player state

```csharp
public enum MountState
{
    Dismounted,
    Mounted,
    Flying
}
```

`MountState` distinguishes ground mount from flight because vnavmesh routes differently for flight and some quest steps require dismounting before interaction. `IsPlayerDiving` is separate because underwater zones (Heavensward+) are orthogonal to mounting.

`IsPlayerDead` returns true when the player's HP is zero, regardless of whether the death prompt has appeared yet. The death prompt itself is in `UiState.DeathPromptOpen`. Engine recovery on death is documented in §15.

`PlayerStateSnapshot` is a composite read for atomic snapshots:

```csharp
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
```

The recording proxy records snapshots as a single observation key (`playerState: {...}`), not as decomposed individual reads. This avoids inconsistent mid-transition views.

### 4.2 Instanced content

```csharp
public enum InstanceKind
{
    None,                // not in an instance
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
```

`GetCurrentInstanceKind` is the single query for "am I in instanced content, and if so what kind." `InstanceKind.None` means the player is in the open world. Any other value means inside instanced content.

The engine pauses most adapter activity inside instances (AutoDuty handles dungeons, BossMod handles single-player duties, other plugins or the user handle the rest). It observes `InstanceKind == None` as the postcondition for "duty complete."

`DutyAvailability` describes whether and how a duty can currently be entered:

```csharp
public record DutyAvailability(
    bool Unlocked,
    bool SupportAvailable,         // Duty Support / Trust / Squadron
    bool FinderAvailable,          // standard Duty Finder
    bool UndersizedPartyAllowed,
    int MinimumLevel,
    int MinimumIlvl,
    string? UnlockHint             // diagnostic, e.g., "complete quest 1234 first"
);
```

**New Game Plus state:**

```csharp
public record NewGamePlusState(
    bool IsActive,
    NewGamePlusChapter? CurrentChapter,
    bool IsSuspended
);

public record NewGamePlusChapter(int ChapterId, string Name);
```

`GetNewGamePlusState()` is read on engine run start and recorded in trace `run.start.data.newGamePlus`. The engine adjusts behavior in NG+ (skip reward selection, pre-unlocked SPD difficulties, retainer/market board unavailable). Quest data is identical for first playthrough and NG+; the engine handles the differences. See §15.7.

### 4.3 World/NPC state

`GetNearbyNpcs` returns a collection. Per the trace format spec (§5.2), collections record `{queried, accessed}`: the proxy captures the full returned list and tags subsequently-accessed elements.

`InteractableReference` and the related methods exist because quest interactions aren't only with NPCs. Fetch quest items, harvest nodes, event triggers, aether currents, and duty triggers are all `EventObject` entities in game data — a different category from NPCs.

`IsInteractableActive` exists because some interactables are conditionally usable (a chest may be empty after looting, an aether current may already be attuned). Engine checks before attempting interaction.

### 4.4 UI state

UI states are temporally tight; reading them separately risks inconsistent snapshots. `GetUiState` returns one structured observation:

```csharp
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
    string SpeakerKey,           // resolved by IDialogueResolver, never raw text
    int OptionCount,
    IReadOnlyList<DialogueOptionId> AvailableOptions
);
```

Distinctions:

- **Dialogue** — the bottom-screen text box with NPC speech and optional response choices. Player has agency.
- **Cutscene** — a non-interactive cinematic. Player input limited to skip. Dialogue can occur *inside* cutscenes; they're not mutually exclusive.
- **YesNoPrompt** — confirmation dialog (binary choice).
- **SelectString** — multi-option list selection (quest objective choices, etc.).
- **RewardSelection** — quest completion reward picker.
- **Shop** — merchant window.
- **DeathPromptOpen** — the "Return to home point / Wait" dialog after death. Reused `ConfirmYesNoPrompt` confirms it.

`DialogueState.SpeakerKey` is resolved by `IDialogueResolver`. Quest data never references displayed text; it references stable keys.

### 4.5 Inventory and currency

`GetFreeInventorySlots` is the engine's pre-flight check for quests that grant items. Recovery on low inventory is engine-side (retainer offload, vendor sale, user prompt).

`GetGil` is needed for teleport cost evaluation. Failure to teleport due to insufficient gil is a routine `TeleportOutcome.InsufficientGil`, not an error.

### 4.6 Derived state

```csharp
public record TravelCapability(
    bool CanTeleport,
    AetheryteId? NearestAttuned,
    bool RequiresOnFootSegment,
    bool CanFly,
    bool CanMount,
    bool CanDive,
    long EstimatedGilCost
);
```

`TravelCapability` consolidates "can I reasonably get to (destination zone)" into one query. The full implementation reads aether current state, current job level, zone flight unlock data, and zone mounting rules.

**Phase 6 implementation status (partial):**
- `CanFly` — implemented via `PlayerState.Instance()->CanFly`, which the game sets per zone based on aether current completion. Correctly handles all zone types including post-ARR zones with Aether Current unlocks.
- `CanMount` — approximated as `!IsPlayerInCombat`. Zone-specific mount restrictions (some indoor areas, quest-restricted zones) are deferred.
- `CanDive`, `CanTeleport`, `NearestAttuned`, `RequiresOnFootSegment`, `EstimatedGilCost` — all return placeholder values (false/null/0). Full implementation deferred to Phase 7+.

### 4.7 What's deliberately not in this interface

- **No "wait for X" methods.** Waiting is the engine's job via `KeepObserving` return type. The provider answers "what is true now."
- **No mutators.** State changes happen through other adapters.
- **No raw UI text scraping.** Dialogue is accessed by structured ID via `DialogueState`. Localization enforcement (§11.3) lives here.
- **No party, friend, FC, or retainer name data.** The recording proxy allowlist forbids it; including these in the interface would be a footgun.

### 4.8 The recording proxy

`RecordingGameStateProvider` wraps any `IGameStateProvider`:

```csharp
public sealed class RecordingGameStateProvider : IGameStateProvider
{
    private readonly IGameStateProvider _inner;
    private readonly ITraceWriter _trace;
    private readonly TickObservationBuffer _buffer = new();

    public async Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var result = await _inner.GetPlayerZone(ct);
        if (result is Result<ZoneId>.Success s)
            _buffer.Record("playerZone", s.Value);
        return result;
    }

    // ... one method per interface member

    internal void FlushTickObservations(int seq, string node)
    {
        _trace.WriteObservation(seq, node, _buffer.Drain());
    }
}
```

The engine signals tick boundaries; the proxy flushes accumulated reads as one `observation` event. Composite reads (`PlayerStateSnapshot`, `UiState`, `TravelCapability`) are recorded as a single key with structured value.

Replay reverses this: `ReplayGameStateProvider` serves recorded observations and refuses reads for keys not in the recording — this surfaces real regressions where the engine has started reading state it didn't read at recording time.

---

## 5. `IQuestState` — quest progression queries

Split from `IGameStateProvider` because quest state is the project's primary concern and earns its own interface. Other interfaces' state is genuinely cross-cutting; quest state is the engine's central focus.

```csharp
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

    // Rewards
    Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct);
}

public enum QuestStatus
{
    Unknown,
    Locked,            // prerequisites not met
    Available,         // can be accepted
    Accepted,          // currently in progress
    Complete,          // finished
    Failed             // some quests can fail (rare)
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
```

### 5.1 Sequence vs flags

FFXIV quests track progress on two axes:

- **Sequence** — current major step number (0, 1, 2, ...). Monotonic, mostly.
- **Quest flags** — per-quest bit array tracking sub-step progress (e.g., "talked to NPC A but not NPC B" within sequence 3).

Quest definitions declare postconditions in terms of one or both. The engine reads both because some steps only update flags, not sequence.

### 5.2 Availability and unlock reasons

`IsQuestAvailable` returns true when the quest can be accepted now. `WhyUnavailable` is its diagnostic counterpart, returning structured reasons. Without `WhyUnavailable`, the engine's failure messages are useless ("Quest 1234 unavailable"); with it, the support-status UI can show "Quest 1234 requires Gladiator level 15; current level is 12."

### 5.3 Reward selection

Quest definitions declare a `RewardSelectionStrategy`:

```csharp
public enum RewardSelectionStrategy
{
    FirstAvailable,             // default fallback
    SpecificItem,               // requires ItemId parameter
    HighestIlvlForCurrentJob,
    HighestIlvlForAnyJob,
    HighestVendorValue,
    MatchingCurrentJob,         // any reward usable by current job
    AskUser
}
```

`VendorPrice` is read from Lumina game data (static, no network lookups). Market board pricing is intentionally not supported in v1 — it would require network dependencies and complicate replay determinism. Users wanting market-optimized choices use third-party tools for those specific quests.

The engine reads `GetAvailableQuestRewards`, applies the configured strategy, calls `IInteractor.SelectQuestReward(index)`. A `reward.selected` trace event records both the available rewards at decision time and the chosen index — diagnostic for "wrong reward chosen" bug reports.

---

## 6. `INavigator` — in-zone movement

Wraps vnavmesh. The engine asks "get me to (position)"; the navigator handles pathing, mount management, obstacles, dynamic re-routing.

```csharp
public interface INavigator
{
    Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct);

    Task<Result<Unit>> Stop(CancellationToken ct);
    Task<Result<Unit>> Jump(CancellationToken ct);
    Task<Result<bool>> IsNavigating(CancellationToken ct);
    Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct);
}

public record NavigationOptions(
    float StoppingDistance = 3.0f,
    bool UseMount = true,
    bool UseFlight = true,
    TimeSpan? Timeout = null
);

public enum NavigationOutcome
{
    Arrived,
    StoppedByObstacle,
    NavmeshUnavailable,
    Interrupted,                   // combat, cutscene, zone change
    TimedOut,
    PlayerDied
}
```

### 6.1 Same-zone-plus-adjacent contract

`NavigateTo` is contractually guaranteed for destinations within the player's current zone. Adjacent-zone destinations *may* work if the navmesh data supports it (vnavmesh does support cross-zone routing in some cases), but the engine should not rely on this.

For inter-zone travel beyond walking distance, the engine composes: use `ITeleporter` to hop to an aetheryte near the destination, then `NavigateTo` from there.

Example:
- Same zone (Ul'dah → another point in Ul'dah): always supported
- Adjacent zone walking (Ul'dah → Western Thanalan via the gate): typically supported
- Inter-zone hop (Ul'dah → Limsa Lominsa): engine teleports first, then navigates

### 6.2 Navmesh generation status

```csharp
public record NavmeshInfo(
    NavmeshStatus Status,
    float? GenerationProgress,     // 0.0–1.0 if Generating
    TimeSpan? EstimatedTimeRemaining
);

public enum NavmeshStatus
{
    Ready,
    Generating,
    NotStarted,
    Failed,
    Unsupported
}
```

vnavmesh generates per-zone navmeshes on demand. First entry into a zone may require 10-60 seconds of generation. Without tracking this, the engine would incorrectly conclude the zone is unsupported.

Engine behavior on `Generating`: emit `navmesh.wait` trace event, surface UI status to user, `KeepObserving` until status becomes `Ready` (or fails). The wait has no hard timeout — generation takes as long as it takes — but the engine surfaces progress to the user.

### 6.3 What `NavigateTo` does and doesn't do

**Does:** pathfind, mount when allowed and useful, fly when allowed and useful, handle minor obstacles, retry around dynamic obstructions.

**Does not:** teleport (that's `ITeleporter`), engage combat (that's `ICombat`), interact with NPCs or objects (that's `IInteractor`).

### 6.4 Engine recovery on navigation failure

Recovery is engine-side, not in this interface. The interface returns what happened; the engine decides what to do next. Default recovery ladders for each `NavigationOutcome` are documented in §15.

---

## 7. `ITeleporter` — aetheryte travel and Return

Wraps Lifestream and native teleport.

```csharp
public interface ITeleporter
{
    Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct);
    Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct);
    Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct);

    Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct);
    Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct);
    Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct);
    Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct);
    Task<Result<bool>> IsTeleportAvailable(CancellationToken ct);
}

public enum TeleportOutcome
{
    Arrived,
    InsufficientGil,
    NotAttuned,
    OnCooldown,
    Interrupted,
    InCombat,
    InInstance,
    Failed
}
```

### 7.1 Aetheryte choice is explicit

There is deliberately no `TeleportToZone(ZoneId)`. Zones have multiple aetherytes; choosing among them is a strategy concern (proximity to destination, attunement status, cost) and belongs to the engine. The interface forces the caller to pick the specific aetheryte.

### 7.2 Return as recovery primitive

`UseReturn` invokes the `/return` command: teleport to home aetheryte, free of cost, but with a long cooldown (typically 15 minutes). The engine uses Return as a recovery destination when:

- Navigation is irrecoverably stuck
- Player is in an unfamiliar location after an unexpected event
- Other teleport options are unavailable

`GetReturnCooldown` lets the engine avoid trying Return when it's on cooldown. `GetTeleportCooldown` does the same for standard teleport.

### 7.3 HomeAetheryte is read-only

The engine reads `GetHomeAetheryte` to know where Return will deposit the player. The engine **never changes** the user's home aetheryte automatically — that's a user-visible game setting they may care about for reasons unrelated to questing.

### 7.4 What's not covered

DC travel is not in this interface. MSQ + class/job quests don't require it (per `DESIGN.md` §5.5). If that changes, this is where it goes.

---

## 8. `IInteractor` — NPCs, objects, dialogue, duty entry, chat, emotes

Wraps TextAdvance plus direct interaction APIs, duty entry, and non-standard interactions (chat messages, emotes, quest abandonment).

```csharp
public interface IInteractor
{
    // NPC and object interaction
    Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct);
    Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct);

    // Dialogue
    Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOption(
        DialogueOptionId option,
        CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(
        int zeroBasedIndex,
        CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(
        string sheetReference,
        CancellationToken ct);

    // Prompts
    Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct);
    Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct);
    Task<Result<Unit>> CloseDialogue(CancellationToken ct);

    // Quest lifecycle
    Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct);
    Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct);
    Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct);
    Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct);

    // Duty entry — full duties (dungeons, trials, raids)
    Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct);
    Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct);

    // SPD entry — Quest Battles via NPC or object trigger
    Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct);

    // Item use
    Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct);

    // Chat and emotes (non-standard interactions, often NPC-targeted)
    Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct);
    Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct);
}

public enum InteractOutcome
{
    DialogueOpened,
    AlreadyInteracted,
    OutOfRange,
    NpcNotFound,
    ObjectNotFound,
    Blocked
}

public enum DialogueOutcome
{
    Advanced,
    DialogueClosed,
    OptionUnavailable,
    NoActiveDialogue
}

public enum DutyEntryOutcome
{
    Entered,
    LevelTooLow,
    NotUnlocked,
    SupportUnavailable,
    FinderUnavailable,
    AlreadyQueued,
    InCombat,
    Failed
}

public enum SpdEntryOutcome
{
    Entered,                       // SPD started, regardless of difficulty
    AlreadyOnPreferredDifficulty,  // YesNo only — Normal entered (no failure history)
    DifficultyDowngraded,          // SelectString offered, picked easier than preferred
    NoSuitableDifficulty,          // dialog UI didn't appear or no valid options
    NotAtTrigger,                  // out of range to the trigger
    Failed
}

public enum DutyDifficulty { Normal, Easy, VeryEasy }

public enum UseItemOutcome
{
    Used,
    ItemNotInInventory,
    ItemNotUsable,
    TargetOutOfRange,
    TargetInvalid,
    OnCooldown,
    Failed
}

public abstract record InteractableOrNpc;
public sealed record AsNpc(NpcId Id) : InteractableOrNpc;
public sealed record AsObject(InteractableId Id) : InteractableOrNpc;

public enum ChatChannel { Say, Yell, Shout }
```

### 8.1 Dialogue selection by sheet reference

The primary dialogue path in v1 is `SelectDialogueOptionBySheetReference(string)`. The sheet reference is a stable identifier into FFXIV's text data sheets (e.g., `"TEXT_JOBDRK301_02054_A1_000_116"`), defined by Square Enix and read via Lumina.

The adapter implementation:
1. Resolves the sheet reference to displayed text in the current language via `IDialogueResolver`
2. Finds the matching option in the currently-open dialogue UI
3. Selects it

`SelectDialogueOption(DialogueOptionId)` remains for cases where the option ID is directly known. `SelectDialogueOptionByIndex(int)` is the fallback for cases where neither is available.

Selecting by raw text is deliberately absent — that's what localization breaks.

`ConfirmYesNoPrompt` confirms the binary prompt. It is also reused for the death prompt: `ConfirmYesNoPrompt(true)` accepts return to home aetheryte when `UiState.DeathPromptOpen == true`.

### 8.2 Quest lifecycle methods

`AcceptQuest`, `CompleteQuest`, `SelectQuestReward` are explicit methods rather than dialogue sequences because they're load-bearing for engine postconditions. The implementation may compose multiple dialogue advances internally; the contract is "the quest is accepted/completed when this returns success."

**`AbandonQuest` is special.** The engine never calls it automatically. It exists in the interface to support user-initiated abandonment through the plugin UI's explicit "Abandon this quest" action. See `DESIGN.md` and `AUTHORING.md` for the abandonment policy.

The three run-outcome distinctions per `TRACE_FORMAT.md`:
- **`Success`** — quest completed normally
- **`CompletionFailure`** — engine triggered `MaxConsecutiveQuestFailures`; quest left in journal
- **`StopQuestAutomation`** — user clicked stop; quest left in journal
- **`Abandon`** — user explicitly abandoned via UI; quest removed from journal (only outcome where `AbandonQuest` is called)

### 8.3 Object interaction

`InteractWithObject(InteractableId)` handles fetch items, harvest nodes, event triggers, aether currents, and duty triggers. The semantics match `InteractWith` for NPCs but operate on `EventObject` entities.

### 8.4 Duty entry — full duties

`EnterDutyWithSupport` requests entry via Duty Support / Trust / Adventurer Squadron — solo with NPCs, no queue, deterministic behavior. This is the default for MSQ duty steps that are full duties (dungeons, trials).

`EnterDutyWithFinder` requests standard Duty Finder entry. The engine uses this only when configured to fall back, and only when Duty Support is unavailable.

The engine reads `DutyAvailability` (§4.2) before choosing which method to call. The configured `DutyFallbackPolicy` determines what happens when Support is unavailable:

```csharp
public enum DutyFallbackPolicy
{
    SupportOnly,                   // default — fail if Support unavailable
    SupportThenFinder,             // try Support, fall back to Finder
    FinderOnly,                    // skip Support entirely
    AlwaysPromptUser               // surface to user via AwaitUserCompletion
}
```

After duty entry, AutoDuty (for dungeons) or the configured combat plugin (for trials and similar) takes over execution. These are independent plugins the user configures; the engine just observes `InstanceKind` transitions.

If those plugins aren't configured or fail, the engine's `AwaitUserCompletion` mechanism (§15) waits for the player to complete the duty manually.

### 8.5 SPD entry — Quest Battles

Single-Player Duties (SPDs, also called Quest Battles) use a different entry mechanism than full duties. They don't have queues; the player interacts with an NPC or object trigger, confirms entry, and is taken directly into the encounter.

`EnterSinglePlayerDuty(trigger, preferredDifficulty, ct)` encapsulates the entry flow as a single adapter operation. Internally:

1. Interact with the trigger (NPC or object)
2. Observe which UI appeared:
   - `SelectYesno`: first attempt (only Normal is offered). Adapter confirms Yes.
   - `SelectString` with difficulty options: previous failure of this SPD has unlocked Easy/Very Easy. Adapter picks per `preferredDifficulty` and `DutyFailurePolicy`, falling back to the easiest available.
3. Wait for `InstanceKind` transition to `SinglePlayerDuty`
4. Return outcome

This is one of the few places where an adapter does multi-step UI orchestration internally rather than expecting the engine to drive each step. The rationale: the SPD entry sequence is a tightly-coupled UI flow, not a quest-level decision point. Exposing it as separate engine-level steps invites racing the UI.

**Difficulty unlock scope:** Easy/Very Easy options are unlocked **per-specific-SPD** within a session, not globally. The engine maintains a per-session memory keyed by `(QuestId, stepId)` tracking which SPDs have failed this session. Restarting the client clears this; in NG+, all difficulties are available from the start without needing failure.

**SPD failure detection:** the engine observes `InstanceKind` transitioning from `SinglePlayerDuty` back to `None`. Success vs failure is then discriminated by the step's `expect` postcondition:
- Postcondition satisfied → success, advance
- Postcondition not satisfied → failure, retry SPD entry

The transition is required to be sustained (~2-3 seconds) before considered a real transition; momentary blips during instanced content (some encounters teleport players in/out mid-fight) do not trigger failure logic.

**Configuration:**

```csharp
public DutyDifficulty PreferredDutyDifficulty { get; init; } = DutyDifficulty.Normal;
public DutyFailurePolicy DutyFailurePolicy { get; init; } = DutyFailurePolicy.RetryAtEasierIfAvailable;
public int MaxDutyRetries { get; init; } = 3;

public enum DutyFailurePolicy
{
    RetryAtSameDifficulty,
    RetryAtEasierIfAvailable,        // drop one level on each failure
    AlwaysUseEasiestAvailable,       // pick the easiest the dialog offers
    AwaitUser                        // pause and let the user choose
}
```

After `MaxDutyRetries` consecutive failures of the same SPD, the engine surfaces via `AwaitUserCompletion`.

**Quest schema integration:** SPDs use `duty` step type with `kind: "spd"` and a `trigger` field instead of `dutyId`. See `SCHEMA.md` §4.9.

**SPD postconditions:** A successful SPD completion does **not** necessarily complete the quest. Most often it advances the quest sequence; sometimes it sets a flag; rarely it completes the quest. Quest authors write whatever `expect` postcondition actually proves the SPD outcome. The engine doesn't assume.

### 8.6 Item use

Four overloads of `UseItem*` cover the patterns FFXIV quests use:

- **`UseItem(item)`** — use a consumable from inventory with no target (summoning bells, telephones, etc.)
- **`UseItemOnTarget(item, npcId)`** — use a key item on an NPC (delivering a letter, etc.)
- **`UseItemOnObject(item, interactableId)`** — use a key item on a world object (key on a door, etc.)
- **`UseItemOnPosition(item, position, tolerance)`** — use an AoE-placement item at a ground position (throwing dynamite on rocks, dropping flares at markers, etc.). `tolerance` is the navigation radius — engine arrives within `tolerance` units before placing.

All return `UseItemOutcome`. Precondition `playerHasItem(item, 1)` is implicit; the engine checks before invoking. Quest schema's `use-item` step type maps to one of these based on the `target.kind` field. See `SCHEMA.md` §4.12.

### 8.7 Chat messages and emotes

`SendChatMessage(channel, messageSheetReference, ct)` sends a chat message in one of the public channels (`Say`, `Yell`, `Shout`). The message text is resolved from a game-data sheet reference, matching the dialogue model — quest data references sheets, not literal strings.

**The adapter rejects party/FC/linkshell channels at the API boundary.** This is enforced in code, not by convention, to prevent unintended spam from buggy quest data or malicious PRs.

`UseEmote(emoteId, target?, ct)` performs an emote. Optional NPC target — some emotes are directional. Both chat messages and emotes are typically targeted at quest NPCs; the engine's step expansion navigates to the NPC and targets it before invoking these methods.

---

## 9. `ICombat` — combat delegation and direct action use

The engine doesn't choose which skills to use in normal combat. It asks a configured combat plugin to fight. But for quest steps that require executing a *specific* action against a *specific* target (e.g., "use Heavy Swing on these rocks"), the engine invokes the action directly.

```csharp
public interface ICombat
{
    // Delegated combat — combat plugin chooses rotation
    Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct);
    Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct);
    Task<Result<Unit>> Disengage(CancellationToken ct);
    Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct);
    Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct);

    // Direct action use — engine specifies action and target
    Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct);
    Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct);
    Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct);
}

public record CombatPluginInfo(
    string Name,                   // "WrathCombo", "BossMod", "RotationSolverReborn"
    string Version,
    bool SupportsTargetEngagement,
    bool SupportsAutoTargeting
);

public enum CombatOutcome
{
    TargetDefeated,
    PlayerDefeated,
    Disengaged,
    TargetNotFound,
    NoCombatPlugin,
    Interrupted
}

public enum UseActionOutcome
{
    Executed,
    ActionNotLearned,
    ActionNotUsable,        // wrong context (e.g., not in combat for a combat-only action)
    OnCooldown,
    TargetOutOfRange,
    TargetInvalid,
    InsufficientMP,
    Failed
}
```

### 9.1 No rotation control for delegated combat

For `EngageTarget` and `EngageNearestHostile`, the engine never tells the combat plugin which skills to use. The combat plugin makes those decisions. This decoupling means new combat plugins can integrate without engine changes.

### 9.2 Plugin selection

Configuration selects which combat plugin to bind. The choice is recorded in `EngineDecisionConfig` (per `TRACE_FORMAT.md` §8) so replay reproduces it.

Switching combat plugins mid-session requires plugin restart in v1. Reactive rebinding (subscribing to Dalamud's plugin-loaded/unloaded events) is a future enhancement if users request it.

### 9.3 Direct action use

`UseAction` and `UseActionOnObject` are for quest steps that require executing a specific player action against a specific target. The canonical example is the Marauder class quest "Axe in the Stone" — the player must use "Heavy Swing" on interactive rock objects three times. The combat plugin would have no reason to pick those targets autonomously; the engine drives it directly.

These methods are on `ICombat` rather than `IInteractor` because they share combat infrastructure: action cooldowns, MP costs, casting interruption, target validation. The combat plugin doesn't need to "rotate" for this use — the engine asks for a specific action against a specific target, and the underlying combat infrastructure executes it once.

`IsActionUsable(actionId)` checks both "is this action learned" and "is it usable right now" (off cooldown, sufficient MP, valid context). The engine calls this before invocation when failure would be expected.

Quest schema's `use-action` step maps to one of these methods. See `SCHEMA.md` §4.13.

---

## 10. `ITimingProfile` — input timing

The seam where non-determinism enters the engine.

```csharp
public interface ITimingProfile
{
    TimeSpan ReactionDelay(StimulusType stimulus);
    TimeSpan DecisionDelay(int choiceCount);
    TimeSpan InterActionGap();
    bool ShouldTakeBreak(SessionContext context);
    TimeSpan BreakDuration();
}

public enum StimulusType
{
    Dialogue,
    NewWindow,
    SceneTransition,
    QuestStart,
    CombatStart,
    Generic
}

public record SessionContext(
    TimeSpan TotalSessionDuration,
    int ActionsSinceLastBreak,
    TimeSpan TimeSinceLastBreak
);
```

### 10.1 Why this is synchronous

Unlike every other adapter, `ITimingProfile` methods are synchronous. They're pure functions of their inputs plus the seeded RNG. They don't read game state, don't perform IO, and are called on every action (so async overhead would matter).

### 10.2 Determinism contract

The profile is seeded at run start from the engine seed (derived from `runId` per `TRACE_FORMAT.md` §6.1). All randomness flows from that seed. Same `runId` → same timing sequence → same trace.

### 10.3 Implementations

- **`HumanLikeProfile`** — log-normal sampling with empirically reasonable parameters. The default.
- **`FastProfile`** — minimal but nonzero floors. For users who opt in to fast execution.
- **`RecordedProfile`** — replays distributions captured from real human play sessions.

The selected profile name is recorded in `EngineDecisionConfig`. Switching profiles between recordings invalidates previously-recorded traces for replay purposes; the engine logs a warning if it detects a mismatch.

### 10.4 Distribution guidance

Reference ranges for `HumanLikeProfile`:

| Stimulus / context | Expected range |
|-------------------|---------------|
| `ReactionDelay(Dialogue)` | 300-800ms |
| `ReactionDelay(NewWindow)` | 300-800ms |
| `ReactionDelay(SceneTransition)` | 500-2000ms |
| `ReactionDelay(QuestStart)` | 600-2000ms |
| `ReactionDelay(CombatStart)` | 200-600ms |
| `DecisionDelay(2 choices)` | 200-500ms |
| `DecisionDelay(5+ choices)` | 600-1500ms |
| `InterActionGap()` floor | ≥50ms always |
| Break frequency | ~1 per 30-50 actions |
| Break duration | 30s-5min log-normal |

These are guidance, not contract. The profile implementation chooses concrete distributions.

### 10.5 No two action timestamps may be identical

The `InterActionGap()` floor guarantees this at the engine level. The engine validates it in trace post-processing.

---

## 11. `IDialogueResolver` — localization seam

```csharp
public interface IDialogueResolver
{
    Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct);

    Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct);

    Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct);

    Task<Result<GameLanguage>> GetActiveLanguage(CancellationToken ct);
}

public enum GameLanguage { English, German, French, Japanese }
```

### 11.1 The localization model

Quest definitions reference dialogue by **sheet references** — direct identifiers into FFXIV's text data sheets, defined by Square Enix and read via Lumina. Example: `"TEXT_JOBDRK301_02054_Q1_000_115"`.

The resolver:
1. Reads the sheet reference and returns the displayed text for the player's current language
2. Searches the currently-open dialogue UI for the option matching that text
3. Returns the matching `DialogueOptionId` (or null if not found)

This is fundamentally simpler than maintaining a separate key namespace and lookup table:
- No invented keys to maintain or validate across languages
- Translations are SE's responsibility (the sheet rows are already localized)
- Quest data references stable, game-defined identifiers
- Patch-day workflow validates sheet references resolve in all four shipped languages

### 11.2 Convenience helper

`CurrentDialogueMatches(sheetReference)` is a convenience for "is the currently-open dialogue showing the prompt I expect?" The engine uses this for postcondition checks like "we're at the expected dialogue point."

### 11.3 No text in quest data

Quest data files never contain literal displayed text — only sheet references. The semantic validator (tier 2 tests) rejects any literal text fields in dialogue definitions.

### 11.4 Diagnostic tracing

The `dialogue.resolved` trace event records each resolution — diagnostic for "wrong dialogue option chosen" bug reports. The event captures the queried sheet reference and the resolved option ID.

---

## 12. `IGearManager` — gear and job management

Manages equipped gear and job-changing for quest steps that require specific items or job changes.

```csharp
public interface IGearManager
{
    // Inspection
    Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct);
    Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct);
    Task<Result<int>> GetAverageItemLevel(CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);

    // Equipping
    Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct);
    Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct);
    Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);

    // Gearsets and job
    Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct);
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);

    // Condition and repair
    Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct);
    Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct);
    Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct);
    Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct);
    Task<Result<bool>> CanRepairWithSelf(CancellationToken ct);
}

public record EquippedItem(EquipSlot Slot, ItemId Item, int ItemLevel);
public record EquippedItemCondition(EquipSlot Slot, ItemId Item, int ConditionPercent);
public record GearItem(ItemId Item, int ItemLevel, IReadOnlyList<JobId> UsableBy, bool InInventory, bool InArmoury);

public enum EquipSlot {
    MainHand, OffHand, Head, Body, Hands, Legs, Feet,
    Earrings, Necklace, Bracelets, RingRight, RingLeft,
    Soul   // Job crystal
}

public enum EquipOutcome { Equipped, NoChange, InCombat, InInstance, ItemNotFound, Failed }
public enum JobChangeOutcome { Changed, GearsetNotFound, JobNotUnlocked, InCombat, InInstance, Failed }
public enum RepairOutcome { Repaired, NoNpcInRange, InsufficientGil, NothingToRepair, InCombat, InInstance, MissingDarkMatter, Failed }
```

### 12.1 Gear selection strategy

The `equip-best-gear` quest step relies on `EquipRecommendedGear` (game built-in) or `EquipBestGearViaStylist` (third-party plugin). Selection is controlled by plugin configuration:

```csharp
public enum GearSelectionMethod
{
    GameRecommended,             // default — built-in /equiprecommended
    StylistIfAvailable           // try Stylist, fall back to GameRecommended
}
```

`EquipBestGearViaStylist` requires the Stylist plugin. It considers inventory items in addition to armoury chest items. The engine falls back to `EquipRecommendedGear` if Stylist is unavailable.

### 12.2 Job change requirements

`ChangeToJob(job)` requires:
- An existing gearset matching the target job
- Not in combat
- Not in an instance
- No relevant UI windows open (vendor, retainer, etc.)

If a gearset doesn't exist, returns `GearsetNotFound`. The engine surfaces this via `AwaitUserCompletion` — v1 does not auto-create gearsets.

V2 may add auto-create capability. Quest steps that depend on a specific job assume the user has created appropriate gearsets in advance.

### 12.3 Gear condition and auto-repair

The engine proactively monitors equipped gear condition to prevent degradation. When any equipped item's condition falls below a configured threshold, the engine repairs before continuing.

**Configuration:**

```csharp
public int GearConditionThreshold { get; init; } = 30;     // percent
public bool AutoRepair { get; init; } = true;
public RepairPreference RepairPreference { get; init; } = RepairPreference.SelfThenNpc;

public enum RepairPreference
{
    SelfThenNpc,        // use the Repair crafting action if available, else find NPC
    NpcOnly,            // skip self-repair (e.g., to save Dark Matter)
    SelfOnly,           // never use NPC
    AskUser             // pause and surface via AwaitUserCompletion
}
```

**Engine behavior:**

The engine checks `GetLowestEquippedCondition()` at natural pause points:

- Before any `duty` step (full duty or SPD)
- Before any `combat` step
- After completing a quest
- Periodically during long quest chains (every N quests, configurable)

If the lowest equipped condition is below `GearConditionThreshold`:

1. Try `RepairWithSelf` if `CanRepairWithSelf()` returns true and preference allows
2. Otherwise: find the nearest mender NPC, teleport to the closest aetheryte, navigate to the NPC, call `RepairAtNpc`
3. Resume the interrupted quest step

If `AutoRepair: false`, the engine surfaces via `AwaitUserCompletion` instead of repairing autonomously.

**Mender NPC registry:** generated at validator-build time from Lumina's `ENpcResident` sheet filtered to NPCs offering the Repair service. Patch-day workflow regenerates the registry. Engine looks up nearest mender by zone/position from this registry.

**Self-repair:** uses the **Repair** general action — a level-1 ability available to all eight Disciple of the Hand classes (Carpenter, Blacksmith, Armorer, Goldsmith, Leatherworker, Weaver, Alchemist, Culinarian; though Culinarian cannot repair gear since it crafts no equipment). The action consumes Dark Matter of an appropriate grade for the item being repaired.

Since Patch 2.28, no class switching is required to perform self-repair — the player can repair from any class as long as they have the appropriate DoH class leveled. Self-repair also works inside instanced content (dungeons, raids), where NPC repair is unavailable. A side benefit not relevant to engine logic: self-repair can exceed 100% durability (up to 199%), whereas NPC repair caps at 100%.

`CanRepairWithSelf()` returns true when the player has a crafter at sufficient level *and* possesses Dark Matter of the required grade for any of the equipped items below threshold. Implementation checks each item against the requirements; partial coverage (some items repairable by self, others not) falls back to NPC repair for the remainder.

Reference: <https://ffxiv.consolegameswiki.com/wiki/Repair_(action)>

**Repair and spiritbond:** Repairs do not affect spiritbond. Materia farming and gear repair are independent — no special handling needed.

### 12.4 Quest schema integration

The `equip-gear-for-quest` step uses `EquipItem` directly. The `equip-best-gear` step uses `EquipRecommendedGear` or `EquipBestGearViaStylist` per configuration. The `change-job` step uses `ChangeToJob`. Auto-repair happens transparently — no quest schema involvement except for the optional `preconditions.minGearCondition` field on steps that *require* specific condition regardless of user policy. See `SCHEMA.md`.

### 12.5 Dependency

Hard dependency on game data (Lumina) for gear information. Soft dependency on Stylist plugin for `EquipBestGearViaStylist`. The engine works fully without Stylist; only `GearSelectionMethod.StylistIfAvailable` configurations notice the difference.

---

## 13. `IMinigameSkipper` — minigame skip handlers (opt-in)

Handles skipping of quest-embedded minigames that the engine can't realistically play.

```csharp
public interface IMinigameSkipper
{
    Task<Result<bool>> IsKindSkippable(MinigameKind kind, CancellationToken ct);
    Task<Result<SkipOutcome>> SkipMinigame(MinigameKind kind, CancellationToken ct);
}

public enum MinigameKind
{
    Sniping,
    Memory,
    Aiming,
    Rhythm,
    Selection,
    Other
}

public enum SkipOutcome { Skipped, Unsupported, Failed }
```

### 13.1 Implementation scope

**Phase 6:** `NullMinigameSkipper` returns `false` for `IsKindSkippable` and `SkipOutcome.Unsupported` for `SkipMinigame` across all `MinigameKind` values. The engine falls back to `AwaitUserCompletion` for any quest step that requires a minigame. No-arg constructor; no Dalamud dependencies.

**Phase 10+:** Implement skip for specific kinds when quests that require them enter the corpus. Sniping minigames appear in enough early quests to likely be the first target.

### 13.2 User opt-in

Minigame skipping is **off by default**. Users must explicitly enable it:

```csharp
public bool AllowMinigameSkipping { get; init; } = false;
```

Quest steps with `skip: "ifAllowed"` (the typical case) run as `AwaitUserCompletion` unless the user has opted in.

This is the most ToS-adjacent capability in the project. The opt-in model ensures users make an informed decision. The configuration UI surrounds the toggle with clear context about what it does and the risk profile.

### 13.3 Implementation approach

The sniping handler observes the relevant addon (typically `WeeklyBingo` or similar — implementation detail) and triggers the "minigame complete" packet or UI flag. Specifics vary per minigame and change across patches; the implementation lives in the adapter layer where patch-day updates are scoped.

### 13.4 Dependency

No external plugin dependencies. The implementation uses Dalamud's hooks and game-data access directly. Hard dependency on Dalamud; soft conceptual dependency on the user's opt-in choice.

---

## 14. Composition and dependency injection

The plugin registers concrete implementations via `Microsoft.Extensions.DependencyInjection`. The engine constructor takes interfaces only:

```csharp
public sealed class QuestEngine
{
    public QuestEngine(
        IGameStateProvider state,
        IQuestState questState,
        INavigator nav,
        ITeleporter teleport,
        IInteractor interact,
        ICombat combat,
        IGearManager gear,
        IMinigameSkipper minigameSkipper,
        ITimingProfile timing,
        IDialogueResolver dialogue,
        ITraceWriter trace,
        ILogger<QuestEngine> log)
    { ... }
}
```

### 14.1 Binding by environment

| Environment | `IGameStateProvider` | `IQuestState` | Other adapters |
|-------------|---------------------|---------------|----------------|
| Production | `RecordingGameStateProvider` wrapping `DalamudGameStateProvider` | `DalamudQuestState` (also recording-wrapped) | Dalamud-backed |
| Replay tests | `ReplayGameStateProvider` | `ReplayQuestState` | `FakeInteractor`, `FakeNavigator`, etc. |
| Unit tests | `FakeGameStateProvider` | `FakeQuestState` | Configured per test |

The engine code is identical across all three. The recording proxy approach means production runs produce trace data; replay reuses those traces as fixtures.

---

## 15. Engine recovery behaviors

Recovery logic lives in the engine, not in the adapters. The adapters report what happened; the engine decides what to do. This section documents default behaviors that the rest of the system must support.

### 15.1 `Action.AwaitUserCompletion`

A first-class engine action emitted when automation cannot proceed without manual user action.

```csharp
public sealed record AwaitUserCompletion(
    string Reason,
    PostconditionExpression Postcondition
) : EngineAction;
```

Behavior:

1. Engine emits the action with a human-readable `Reason` and an observable `Postcondition`
2. Plugin UI shows a persistent, non-blocking notification: "Waiting for you to complete [Reason]. Quest will resume automatically when done."
3. Engine polls the postcondition on a sensible cadence (every 2-3 seconds)
4. When the postcondition becomes true, the notification clears and the engine resumes
5. The wait is indefinite — no timeout. The user controls the plugin via the always-available stop button.

If the player logs out, the engine's observation pauses entirely and resumes when the player logs back in to the same character. This is consistent with how all engine state-reads behave when there's no active character.

Use cases:
- Single-player duty completion (when BossMod isn't configured)
- Dungeon completion (when AutoDuty isn't configured)
- Manual cutscene wait where the engine can't act
- Quest steps that genuinely require user judgment

### 15.2 Default recovery ladder for navigation failures

```
NavigationOutcome.StoppedByObstacle:
  1. Wait 2s, retry (×2)
  2. Dismount, retry on foot (×1)
  3. Move backward 5m, retry (×1)
  4. Teleport to nearest attuned aetheryte, restart navigation
  5. Surface via AwaitUserCompletion

NavigationOutcome.NavmeshUnavailable:
  1. If status is Generating: wait for completion (no timeout)
  2. Teleport to a known aetheryte in the destination zone
  3. Surface via AwaitUserCompletion

NavigationOutcome.Interrupted:
  1. Wait for the interrupting condition (combat ends, cutscene finishes)
  2. Re-observe, restart navigation from current position

NavigationOutcome.TimedOut:
  1. Stop, log, retry once
  2. UseReturn, teleport to destination aetheryte, restart
  3. Surface via AwaitUserCompletion

NavigationOutcome.PlayerDied:
  Handled by §15.4 (death recovery), not the navigation ladder

Adapter error (vnavmesh throws or returns failure):
  1. Log adapter.error trace event
  2. Wait 5s for vnavmesh recovery
  3. If still failing: UseReturn, teleport to last known good aetheryte, restart
  4. Surface via AwaitUserCompletion
```

Quest authors can override defaults per step in the quest definition (`recover: {...}`).

### 15.3 Default recovery for interaction and combat failures

To be elaborated when the quest schema is designed. Initial defaults:

- `InteractOutcome.OutOfRange` → re-navigate to target position, retry interaction
- `InteractOutcome.Blocked` → wait 2s, retry once, then surface
- `CombatOutcome.PlayerDefeated` → handled by §15.4 (death recovery)
- `CombatOutcome.TargetNotFound` → re-observe nearby NPCs, retry once, then surface

### 15.4 Death recovery

Death can occur during any engine activity. Its handling depends critically on **where** the death occurred — open-world deaths require active recovery; deaths inside instanced content are handled by delegated plugins (BossMod, AutoDuty, etc.) or the game's own mechanics. The engine must not conflate them.

**Detection signals:**
- `IsPlayerDead == true`, or
- `UiState.DeathPromptOpen == true`

**Context routing on death detection:**

The engine reads `GetCurrentInstanceKind()` at the moment of detection and routes accordingly:

| `InstanceKind` | Context | Engine action |
|----------------|---------|---------------|
| `None` | Open-world death | Accept return, re-plan current step |
| `Dungeon` / `Trial` / `Raid` / `AllianceRaid` | Delegated death | No action; party plugin / game handles |
| `SinglePlayerDuty` | SPD death (Quest Battle failure) | No immediate action; SPD retry logic engages when `InstanceKind → None` without postcondition met |
| `PvP` / `DeepDungeon` / `VariantDungeon` / `Other` | Out of scope | No action |

In all cases, a `player.died` trace event is recorded with the context for diagnostics.

**Open-world death recovery (`DeathRecoveryPolicy.ReturnImmediately`, the default):**

1. Cancel the current action
2. Wait for `UiState.DeathPromptOpen == true`
3. `ConfirmYesNoPrompt(true)` to accept return to home aetheryte
4. Wait for `IsPlayerDead == false` and `UiState.LoadingZone == false`
5. Re-observe: query current zone, position, quest state
6. Re-plan the current quest step from the new starting state — usually re-issuing the travel that was in progress

**Delegated death (dungeons, trials, raids):**

The engine takes no action. BossMod, AutoDuty, or whichever combat/duty plugin is configured handles the encounter — including raises, wipes, and recovery. The engine continues observing `InstanceKind` for the eventual transition back to `None`, at which point the step's `expect` postcondition determines whether the duty succeeded.

This routing is essential: a player can die many times in a dungeon without the engine treating it as a per-quest failure. **Dungeon deaths never increment quest-level or step-level failure counters.**

**SPD death (Quest Battle failure):**

The engine notes the death in a trace event but takes no immediate action. The actual failure handling triggers when `InstanceKind` transitions from `SinglePlayerDuty` back to `None` *without* the step's `expect` being satisfied. At that point, SPD retry logic engages (see §15.6 below and §8.5).

**Edge cases:**

- *Death at zone transition:* If `InstanceKind` is mid-transition when death is detected and reads as `None`, treat as open-world. Worst case is the engine accepts return when it could have stayed; acceptable failure mode.
- *Cross-zone death and resurrection:* Engine handles naturally — recovery flow re-observes zone/position after return and re-plans.
- *Brief `InstanceKind` blips during dungeon mechanics:* The "duty complete" detection requires a sustained transition (~2-3 seconds), not momentary. Existing `KeepObserving` patterns handle this without special code.

**Future configurability:**

```csharp
public enum DeathRecoveryPolicy
{
    ReturnImmediately,     // default
    WaitForRaise,          // useful for waiting on raise-capable allies in open world
    AskUser                // surface via AwaitUserCompletion
}
```

`WaitForRaise` and `AskUser` are future enhancements; v1 implements only `ReturnImmediately`.

### 15.5 Quest state preserved across death

Quest sequence and flags persist server-side. Death doesn't lose quest progress. The engine's HSM naturally handles re-planning because every quest step is built around observable postconditions — if a postcondition isn't yet met, the engine re-tries from current state regardless of how it got there.

### 15.6 Failure counters

The engine maintains three distinct failure counters with different scopes and triggers. Critically, **death events do not directly increment any counter** — they're handled by the routing above, with only SPD failures (which involve a duty exit without postcondition met) feeding into the duty counter.

| Counter | Scope | Increments on | Default max | Action on max |
|---------|-------|---------------|-------------|---------------|
| `MaxConsecutiveStepFailures` | Per-step within a quest | Step's full recovery ladder exhausts | 3 | Surface via `AwaitUserCompletion` |
| `MaxDutyRetries` | Per-SPD within a session | SPD failure (entered, didn't complete, exited) | 3 | Surface via `AwaitUserCompletion` |
| `MaxConsecutiveQuestFailures` | Across quests in a chain run | Quest-level recovery exhausts (step counter maxes without resolution) | 3 | Stop quest automation entirely; surface via UI |

**Interaction matrix:**

| Death context | Step counter | Duty counter | Quest counter |
|---------------|-------------|-------------|---------------|
| Open-world (during navigation/interaction) | No change (handled by recovery, may eventually +1 if recovery exhausts) | No change | No change |
| Dungeon / trial / raid / alliance raid | No change | No change | No change |
| SPD failure (the duty exit, not the death itself) | No change | +1 | No change |
| All recovery exhausted, step gives up | resets | resets | +1 |

The intent: dungeon deaths are explicitly free. SPD failures cost a duty retry. Step-level failures cost a step retry. Only when these accumulate without resolution does the quest-level counter advance.

`MaxDutyRetries` and step counters reset when the engine successfully completes the affected step or duty. `MaxConsecutiveQuestFailures` resets on a successful quest completion in the chain.

**Configuration:**

```csharp
public int MaxConsecutiveStepFailures { get; init; } = 3;
public int MaxDutyRetries { get; init; } = 3;
public int MaxConsecutiveQuestFailures { get; init; } = 3;
```

### 15.7 NG+ awareness

The engine reads `IGameStateProvider.GetNewGamePlusState()` on run start and adjusts behavior:

- **Reward selection:** NG+ runs grant no rewards. Engine skips `SelectQuestReward` entirely; `turn-in` steps complete without reward dialog interaction.
- **SPD difficulty:** All three difficulties (Normal, Easy, Very Easy) are pre-unlocked from the start. Engine consults `DifficultyDialogState.AvailableDifficulties` as usual and applies `PreferredDutyDifficulty` accordingly.
- **Inventory and gil:** Retainers and market board are inaccessible in NG+. Engine's recovery options exclude these.
- **Trace recording:** `run.start.data.newGamePlus` captures `{active: bool, chapterId?: int, chapterName?: string}` for replay determinism.

Quest data itself is identical for first playthrough and NG+ — same sequences, same flags, same NPCs. The same quest file works for both modes.

---

## 16. What each adapter wraps (concretely)

For implementation clarity:

| Interface | Wraps | Dependency type |
|-----------|-------|-----------------|
| `IGameStateProvider` | Dalamud Services (ClientState, Conditions, etc.) | Hard |
| `IQuestState` | Dalamud QuestManager + Lumina | Hard |
| `INavigator` | vnavmesh via IPC | Hard |
| `ITeleporter` | Lifestream via IPC + native teleport action | Hard |
| `IInteractor` | Native interaction APIs + TextAdvance via IPC | TextAdvance soft |
| `ICombat` | BossMod / WrathCombo / RSR via IPC | At least one required |
| `IGearManager` | Native gear APIs + Stylist via IPC | Stylist soft |
| `IMinigameSkipper` | Direct Dalamud hooks (no external plugin) | Hard (Dalamud); user opt-in |
| `ITimingProfile` | Nothing — pure logic | None |
| `IDialogueResolver` | Lumina (game data sheets) | None (Lumina is bundled) |

**Hard dependency:** Engine fails-start if missing. Plugin manifest declares it.

**Soft dependency:** Engine starts; affected adapter degrades gracefully. Quests requiring that adapter show as "unsupported" in the §9 UI of `DESIGN.md`.

Combat plugin requirement: at least one of BossMod, WrathCombo, or RSR must be configured. The user picks which.

`IMinigameSkipper` is unusual: hard dependency on Dalamud itself but feature-gated by user opt-in (`AllowMinigameSkipping`). Without opt-in, the adapter is effectively unused and minigame steps run as `AwaitUserCompletion`.

---

## Appendix A: Open questions for revision

These remain unresolved and may be addressed during implementation.

- **Combat plugin reactive rebinding** — currently restart-required; revisit if users complain
- **Auto-create gearsets for `ChangeToJob`** — v1 requires existing gearsets; v2 may add auto-create
- **Minigame skip coverage beyond Sniping** — implementation grows as v1 corpus encounters them
- **Stylist IPC stability** — soft dependency; behavior on Stylist version changes is implementation detail

## Appendix B: Glossary additions beyond `DESIGN.md`

- **Adapter** — interface defining the contract between the engine and game/plugin IO
- **Composite read** — a single adapter method returning structured state from multiple underlying fields, recorded as one observation key
- **`AwaitUserCompletion`** — engine action indicating automation must pause and observe for user-initiated progress
- **Duty Support** — FFXIV's NPC-companion system for soloing dungeons (Trust / Adventurer Squadron)
- **Recovery ladder** — ordered list of remediation attempts the engine applies on adapter failure
- **Return** — FFXIV's `/return` command, teleporting the player to their home aetheryte
- **Strong-typed identifier** — wrapper struct around a primitive ID to prevent cross-type confusion (`NpcId` vs `ZoneId`)

## Appendix C: Design decisions and rationale

| Decision | Alternative considered | Why |
|----------|----------------------|-----|
| `Result<T>` for routine failures | Exceptions | Failures are part of engine logic and must be trace-recordable |
| Nine interfaces | Fewer, merged | Each has one clear responsibility; quest state, gear, minigame skip earn their own |
| Composite reads for atomicity | Only granular reads | Temporally-tight state needs consistent snapshots |
| Strong-typed IDs | `uint` everywhere | Compiler catches cross-type confusion |
| Sheet references for dialogue | Invented namespace + lookup table | Use SE's own data; no translation layer or multi-lang CI |
| `HighestVendorValue` not `HighestMarketValue` | Universalis integration | No network dependency in v1; deterministic for replay |
| `SupportOnly` as default duty policy | `SupportThenFinder` | Less surprising to users; doesn't queue them with random players |
| Duty entry on `IInteractor`, not separate interface | `IDutyFinder` | Duty entry is a UI interaction |
| SPD entry as one adapter call | Engine drives each UI step | Tightly-coupled UI flow not a quest-level decision; avoids racing the UI |
| `UseReturn` as first-class | Compose teleport + cooldown check | Return has unique semantics (free, long cooldown, home destination) |
| HomeAetheryte read-only | Engine could set it | User-visible game setting; not the engine's to change |
| `AwaitUserCompletion` no timeout | 60-minute timeout | Paternalistic; breaks legitimate "do duty later" workflow |
| Death recovery returns to home aetheryte | Wait for raise / ask user | Reliable, free, deterministic; doesn't depend on other players |
| Death context routing via `InstanceKind` | Single death handler | Dungeon deaths must not increment quest failure counters |
| Three separate failure counters | Single counter | Different scopes (step / SPD / quest) need different limits and reset semantics |
| Per-SPD session memory keyed by `(QuestId, stepId)` | Session-global flag | Game's difficulty unlock is per-specific-SPD, not global |
| Adapter substitution as explicit goal | Hard-coded plugin dependencies | Plugins change over time; abstraction insulates engine and quest data |
| Repair does not affect spiritbond | Carve-out config | Repairs and spiritbond are independent game mechanics — no interaction |
| `IsPlayerDead` and `DeathPromptOpen` as separate signals | Single death state | They mean different things (HP zero vs. UI element shown) |
| Combat plugin restart on switch | Reactive rebinding | Simpler for v1; revisit if users complain |
| Direct `UseAction` on `ICombat` | Separate `IActionUser` interface | Shares combat infrastructure (cooldowns, MP, targeting); ninth interface not justified |
| Auto-repair as engine concern, not schema | Per-quest repair steps | Repair is a player-state policy, not a quest-step concern |
