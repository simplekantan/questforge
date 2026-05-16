# Phase 8 — `QuestScheduler` Testable Specification

**Status:** implemented — see amendment below
**Stage in TDD workflow:** Architect deliverable (input to Tester)
**Project scope:** `QuestForge.Engine.Scheduling` namespace; pure C#; no Dalamud reference
**Related:** `docs/NEXT_STEPS.md` §Phase 8, `QuestForge.Adapters/State/IQuestState.cs`, `QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs`

> **Amendment (post-implementation):** Tier 4 was redesigned after this spec was written.
> The original spec describes Tier 4 as DoH/DoL quests (`EnableCraftGatherQuests` gate, job-range 9–19 check).
> The implemented Tier 4 is **blue feature-unlock quests** (`EnableBlueQuests` gate, no job filter).
> DoH/DoL class quests are now Tier 1 via `category="class"` in quest files.
> `EnableCraftGatherQuests` is retained in `SchedulerOptions` but not currently used by the scheduler.
> Quest categories `"blue"`, `"blue-urgent"`, `"role"`, `"side"` were added post-spec.
> Tier 1 now also includes quests with `classJobCatId==0` (blue-urgent, no class restriction).
> Scenarios 7, 8a, 8b were updated accordingly; Scenarios 26–28 were added. All other scenarios are accurate.

---

## 1. Purpose

The `QuestScheduler` answers a single question after each quest run completes: **"What quest should the engine run next?"** It returns either a `QuestId` to run or `null` to stop. The scheduler does not own a run loop; the `EngineHost` calls it once per cycle.

The scheduler must:

- Honour a user-pinned manual chain (Tier 0) that never yields to anything else
- Drive class/job quests for the player's currently-equipped job (Tier 1)
- Dynamically lift unmet prerequisites into the queue (Tier 2)
- Continue automated chains (MSQ, etc.) as the default work (Tier 3)
- Honour opt-in DoH/DoL quests (Tier 4) and side quests (Tier 5)
- Emit `SchedulerStatus` for UI consumption
- Refuse to run anything when the user-pinned chain is blocked

The scheduler must NOT own the engine run loop, mutate engine state, handle in-quest failures, manage death/recovery, or change jobs/gear.

---

## 2. Interfaces

All interfaces live in `QuestForge.Engine.Scheduling`.

### 2.1 `IQuestScheduler`

```csharp
namespace QuestForge.Engine.Scheduling;

public interface IQuestScheduler
{
    // Returns the QuestId the engine should run next, or null if no quest should run.
    // null means: "either everything in scope is complete, or Tier-0 is blocked."
    // Callers MUST consult CurrentStatus afterwards to distinguish Idle from AwaitingUser.
    Task<Result<QuestId?>> NextQuestToRun(CancellationToken ct);

    // The most recent status emitted by the scheduler.
    // Updated synchronously inside NextQuestToRun. Never null after the first call.
    SchedulerStatus CurrentStatus { get; }

    // Replace the in-effect scheduler options. Affects the NEXT call to NextQuestToRun.
    // Does not retroactively change CurrentStatus.
    void UpdateOptions(SchedulerOptions options);
}
```

### 2.2 `IQuestDataProvider`

Abstracts Lumina reads so the scheduler stays in `QuestForge.Engine`. The Dalamud-backed implementation lives in `QuestForge.Adapters.Dalamud`; tests use a hand-rolled fake.

```csharp
namespace QuestForge.Engine.Scheduling;

public interface IQuestDataProvider
{
    // Lumina ClassJobCategory rowId for the quest, or 0 when the quest has no job restriction.
    // Returns 0 for unknown quest IDs (treat unknown as "no restriction").
    uint GetClassJobCategoryId(QuestId quest);

    // Minimum level required for the quest. Returns 0 for unknown quest IDs.
    int GetRequiredLevel(QuestId quest);

    // Lumina JournalGenre.SortKey for the quest's genre, used to order quests within a tier.
    // Returns int.MaxValue for unknown quest IDs (sorts to the end).
    int GetJournalSortKey(QuestId quest);

    // Up to 3 prerequisite QuestIds defined in Lumina Quest.PreviousQuest[0..2].
    // Zero-row entries are filtered out. Returns an empty list for unknown quest IDs.
    IReadOnlyList<QuestId> GetPrerequisites(QuestId quest);

    // Lumina Quest.PreviousQuestJoin: how prerequisites combine.
    // Returns PrerequisiteJoin.All for unknown quest IDs (safer default).
    PrerequisiteJoin GetPrerequisiteJoin(QuestId quest);

    // Maps a quest to a scheduler tier (1, 3, 4, or 5) based on its category and content type.
    // Tier 0 is user-assigned (not data-driven); Tier 2 is computed from blockers.
    // Returns null for quests that are not categorized — the scheduler MUST ignore null-tier quests.
    int? GetQuestTier(QuestId quest);

    // True when the quest's ClassJobCategory matches the given job.
    // Returns false for unknown quest IDs.
    bool IsClassQuestForJob(QuestId quest, JobId job);

    // All known quest IDs the data provider can answer about.
    // Used by the scheduler to enumerate candidates for each tier.
    IReadOnlyCollection<QuestId> EnumerateKnownQuests();
}

public enum PrerequisiteJoin
{
    All,         // every non-zero PreviousQuest must be complete (Lumina value 0)
    AtLeastOne   // any one non-zero PreviousQuest must be complete (Lumina value 1)
}
```

### 2.3 `SchedulerOptions`

Configuration record. Constructed by the plugin from `PluginConfig`, passed to the scheduler ctor or via `UpdateOptions`.

```csharp
namespace QuestForge.Engine.Scheduling;

public sealed record SchedulerOptions(
    // Tier-0 manual chain. If non-empty, the scheduler tries this list FIRST in order.
    // Completed entries are skipped. Blockers in this list stop ALL automation.
    IReadOnlyList<QuestId> ManualChain,

    // Tier-4 gate. Default false. When false, DoH/DoL quests are never selected.
    bool EnableCraftGatherQuests,

    // Tier-5 gate. Default false. When false, side quests are never selected.
    bool EnableSideQuests
)
{
    public static SchedulerOptions Default { get; } = new(
        ManualChain: [],
        EnableCraftGatherQuests: false,
        EnableSideQuests: false);
}
```

### 2.4 `SchedulerStatus`

Discriminated record for UI display. Mirrors the `EngineAction` discriminated-union pattern already used in the engine.

```csharp
namespace QuestForge.Engine.Scheduling;

public abstract record SchedulerStatus
{
    // The engine is currently running this quest (set by EngineHost — scheduler emits
    // this when NextQuestToRun returns a non-null QuestId).
    public sealed record Running(QuestId CurrentQuest) : SchedulerStatus;

    // The scheduler is mid-evaluation. Transient; only observable in concurrent reads.
    public sealed record SelectingNext : SchedulerStatus;

    // Tier-0 (manual chain) has hit a blocker. Automation stops entirely.
    public sealed record AwaitingUser(QuestId BlockedQuest, QuestUnlockReason Reason)
        : SchedulerStatus;

    // No quest meets the criteria of any active tier.
    public sealed record Idle : SchedulerStatus;

    // The user has paused automation. Set externally (not by NextQuestToRun).
    public sealed record Paused : SchedulerStatus;
}
```

---

## 3. Priority algorithm

`NextQuestToRun` executes the following ordered rules. The first rule that produces a `QuestId` returns it; otherwise the next rule is tried. If no rule fires, the scheduler returns `null` with `CurrentStatus = Idle`.

> **Notation:** "available" means `IQuestState.IsQuestAvailable(q, ct)` returned `Result.Ok(true)`. "Complete" means `IQuestState.IsQuestComplete(q, ct)` returned `Result.Ok(true)`. Adapter `Result.Failure` from any of these calls during selection is treated as: the candidate is skipped (do not assume availability), and the failure detail is logged.

### Rule 0 — Tier 0: Manual chain (highest priority)

For each `QuestId q` in `Options.ManualChain`, in list order:

1. If `IsQuestComplete(q)` → skip to next entry.
2. If `IsQuestAvailable(q)` → return `Result.Ok(q)`; set `CurrentStatus = Running(q)`.
3. Otherwise call `WhyUnavailable(q)`:
   - If reason is non-null → set `CurrentStatus = AwaitingUser(q, reason)`; return `Result.Ok<QuestId?>(null)`. **Stop all evaluation.**
   - If reason is null but the quest is also not available (race / adapter inconsistency) → log a warning, treat as blocker with `QuestUnlockReason(false, 0, false, [], false, null, false, OtherReason: true, Detail: "WhyUnavailable returned null but quest is not available")`; set `AwaitingUser`; return null.

If every entry in the manual chain is `Complete`, fall through to Rule 1.

### Rule 1 — Tier 1: Active-job class quests

1. Read current job via `IGameStateProvider.GetCurrentJob(ct)`.
   - On `Result.Failure`: skip Tier 1 entirely (do not crash the scheduler); proceed to Rule 2 evaluation against Tier 3.
2. Enumerate all `q ∈ IQuestDataProvider.EnumerateKnownQuests()` where `GetQuestTier(q) == 1` AND `IsClassQuestForJob(q, currentJob) == true`.
3. Filter to those that are not `Complete`.
4. For each candidate (sorted by `GetJournalSortKey(q)` ascending, then by raw `q.Value` ascending as tiebreaker):
   - If `IsQuestAvailable(q)` → return `Result.Ok(q)`; `CurrentStatus = Running(q)`.
   - Else compute `WhyUnavailable(q)`:
     - If `PrerequisiteIncomplete == true` → call `ResolveBlocker(q, ct, visited)`. If it returns a non-null `b`, return `b`; `CurrentStatus = Running(b)`. (Tier-2 elevation — see Rule 2 for the full algorithm.)
     - If `WrongJob == true` → skip this candidate (player changed jobs between the data-provider read and now, or `IsClassQuestForJob` lied).
     - If `LevelTooLow == true` → skip; the player will reach level later.
     - If `AlreadyCompleted == true` → skip (data inconsistency between `IsQuestComplete` and `WhyUnavailable`; log it).
     - Otherwise (`OtherReason`) → skip.

Tier 1 falls through to Rule 2 only if no Tier-1 candidate is selectable.

### Rule 2 — Tier 2: Dynamic blocker resolution

Tier 2 is **never enumerated directly**. It is computed on demand:

- A quest `b` is elevated to Tier 2 iff some Tier-1 or Tier-3 candidate `c` was found whose `WhyUnavailable(c)` reported `PrerequisiteIncomplete` with `MissingPrereqs` containing `b`, AND `b` itself is `IsQuestAvailable(b) == true`.

**Resolution algorithm (recursive):**

```
ResolveBlocker(c, ct, visited):
    if visited.Contains(c): return null                                  // cycle detected
    visited.Add(c)
    reason ← WhyUnavailable(c)
    if reason is null OR not reason.PrerequisiteIncomplete: return null  // shouldn't have been called
    for each missing in reason.MissingPrereqs:
        if IsQuestComplete(missing): continue                            // stale; ignore
        if IsQuestAvailable(missing): return missing                     // direct unblocker
        // Recursively descend
        sub ← ResolveBlocker(missing, ct, visited)
        if sub is not null: return sub
    return null                                                          // dead-end (cycle / unknown)
```

**Recursion guard:** `visited` is a `HashSet<QuestId>` allocated once per `NextQuestToRun` call and threaded through all `ResolveBlocker` invocations. Re-entering a visited node returns `null` immediately. This protects against Lumina data cycles (which should not exist but defensive code must assume they might). The same `visited` set is shared across all Tier-1/3/4/5 candidates within one `NextQuestToRun` call — a blocker that dead-ended for Tier-1 candidate A will not be re-explored for Tier-3 candidate B if both share the same blocked chain.

When `ResolveBlocker` returns a non-null `b`:

- Return `Result.Ok(b)`; `CurrentStatus = Running(b)`.

When `ResolveBlocker` returns `null` for every Tier-1 candidate, fall through to Tier 3 with the same dynamic-blocker behaviour applied there.

### Rule 3 — Tier 3: Auto chain continuation

1. Enumerate all `q ∈ EnumerateKnownQuests()` where `GetQuestTier(q) == 3`.
2. Filter out completed quests.
3. Sort by `GetJournalSortKey(q)` ascending; tiebreak by raw `q.Value` ascending.
4. For each candidate:
   - If `IsQuestAvailable(q)` → return `q`; `CurrentStatus = Running(q)`.
   - Else if `WhyUnavailable(q).PrerequisiteIncomplete` and `ResolveBlocker(q, ct, visited)` returns non-null `b` → return `b`; `CurrentStatus = Running(b)`.
   - Otherwise skip.

### Rule 4 — Tier 4: Crafter/Gatherer (opt-in)

Only evaluated if `Options.EnableCraftGatherQuests == true`. Otherwise this rule is a no-op.

1. Read current job.
2. If current job is not in the DoH (jobs 9–16) or DoL (jobs 17–19) range → skip Tier 4.
3. Enumerate `q` where `GetQuestTier(q) == 4` AND `IsClassQuestForJob(q, currentJob) == true`.
4. Same selection logic as Tier 1 (filter complete, sort, pick first available, elevate blockers to Tier 2).

### Rule 5 — Tier 5: Side quests (opt-in)

Only evaluated if `Options.EnableSideQuests == true`. Otherwise this rule is a no-op.

1. Enumerate `q` where `GetQuestTier(q) == 5`.
2. Same selection logic as Tier 3 (no job filter; sort; pick first available; elevate blockers to Tier 2).

### Rule 6 — No selection

If every rule fell through without returning, set `CurrentStatus = Idle` and return `Result.Ok<QuestId?>(null)`.

---

## 4. Tier-0 blocking behaviour (canonical contract)

When the user pins a chain via `Options.ManualChain`, the scheduler treats that chain as the **only** legitimate work.

**Required invariants:**

- If the current first-incomplete manual entry is `Available`, return it. No other tier runs.
- If the current first-incomplete manual entry is `Unavailable`, return `null` and set `AwaitingUser`. **No fallback to Tier 1/2/3/4/5 occurs.** This is the entire point of Tier 0.
- The scheduler does not attempt to auto-resolve manual-chain blockers via Tier-2 elevation. The user opted into a specific chain and is expected to handle blockers themselves; auto-running prereqs would defeat the "manual chain" semantics.
- Re-evaluation occurs only when the host calls `NextQuestToRun` again (after the user completes the blocker manually and presses Play, or after pause/resume).
- Empty `ManualChain` means "no Tier-0 in effect" — proceed directly to Rule 1.

---

## 5. `SchedulerStatus` state transitions

The status is a derived value; the scheduler computes it inside `NextQuestToRun`.

| From → To | Trigger |
|---|---|
| (initial) → `Idle` | Scheduler constructed; `NextQuestToRun` not yet called. |
| (any) → `SelectingNext` | Entry into `NextQuestToRun`; held until a Rule returns or the method exits. |
| `SelectingNext` → `Running(q)` | Any rule returned a non-null `QuestId q`. |
| `SelectingNext` → `AwaitingUser(q, reason)` | Rule 0 hit a blocked manual entry. |
| `SelectingNext` → `Idle` | No rule fired; nothing to run. |
| `Running(q)` → `SelectingNext` | Caller invokes `NextQuestToRun` again after the quest completes. |
| (any) → `Paused` | Caller (e.g. EngineHost on user Stop) sets `Paused` externally — NOT inside the scheduler. The scheduler exposes a `SetPaused()` method only if explicitly added; otherwise `Paused` is set by the EngineHost wrapper. **For Phase 8, the scheduler itself does not enter `Paused` — the EngineHost owns pause state.** This contract is included for completeness; tests on `Paused` are out of scope. |
| `Paused` → `SelectingNext` | Resume — EngineHost calls `NextQuestToRun`, which re-evaluates from scratch (no memoized prior selection). |

**Atomic update:** `CurrentStatus` MUST be set before `NextQuestToRun` returns. Concurrent readers of `CurrentStatus` between calls observe the last-returned status.

**No memoization:** Each `NextQuestToRun` call must re-read game state via the adapters and re-evaluate. The scheduler holds no cached "queue."

---

## 6. `IQuestDataProvider` contract — per-method semantics

| Method | Unknown ID behaviour | Failure mode |
|---|---|---|
| `GetClassJobCategoryId` | Returns `0` (treated as no restriction) | Pure; never throws |
| `GetRequiredLevel` | Returns `0` | Pure; never throws |
| `GetJournalSortKey` | Returns `int.MaxValue` (sorts to end) | Pure; never throws |
| `GetPrerequisites` | Returns `Array.Empty<QuestId>()` | Pure; never throws |
| `GetPrerequisiteJoin` | Returns `PrerequisiteJoin.All` | Pure; never throws |
| `GetQuestTier` | Returns `null` (scheduler ignores) | Pure; never throws |
| `IsClassQuestForJob` | Returns `false` | Pure; never throws |
| `EnumerateKnownQuests` | Returns a snapshot; same call may differ across invocations if data reloads | Pure; never throws |

All methods are **synchronous** by design — Lumina is in-memory and adapter-free at this level. Async wrapping would create needless `Task` allocation on the hot enumeration path.

---

## 7. Construction & dependencies

```csharp
public sealed class QuestScheduler : IQuestScheduler
{
    public QuestScheduler(
        IQuestState questState,
        IGameStateProvider gameState,
        IQuestDataProvider questData,
        SchedulerOptions initialOptions,
        ILogger<QuestScheduler> logger);
}
```

All constructor arguments are required (non-null). `ArgumentNullException` for any null arg.

---

## 8. Test scenarios

Each scenario lists preconditions, the call under test, and expected outcome. The Tester writes one xUnit test per scenario. Use the existing `FakeGameStateProvider` and `FakeQuestState` patterns from `QuestForge.Adapters.Fakes`; add a new `FakeQuestDataProvider` with the same hand-rolled style.

> **Convention for scenarios below:**
> - `Q(n)` = `new QuestId(n)`
> - `J(n)` = `new JobId(n)` (job IDs match Lumina rowIds: 2=GLA, 19=FSH, etc.)
> - "Available" = `FakeQuestState.IsQuestAvailable` scripted to return `Result.Ok(true)`
> - "Complete" = `FakeQuestState.IsQuestComplete` scripted to return `Result.Ok(true)`
> - "Locked by X" = `WhyUnavailable` returns a `QuestUnlockReason` with the named flag set
> - Default `SchedulerOptions` = `SchedulerOptions.Default` unless noted

### Scenario 1 — `BasicTierSelection_NoTier0OrTier1_PicksTier3`

**Pre:** Manual chain empty. Tier-1 corpus empty (no class quests known). Tier-3 corpus = `{Q(100), Q(101), Q(102)}`. `Q(100)` is Complete; `Q(101)` is Available; `Q(102)` is locked by prereq `Q(101)`. SortKeys ascending in numeric order.
**When:** `NextQuestToRun(ct)` is called.
**Then:** Returns `Result.Ok(Q(101))`. `CurrentStatus == Running(Q(101))`.

### Scenario 2 — `Tier0Blocker_StopsAllAutomation`

**Pre:** `ManualChain = [Q(50)]`. `Q(50)` locked by `LevelTooLow=true, RequiredLevel=30`. Tier-1 has an available class quest `Q(200)`; Tier-3 has an available `Q(300)`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok<QuestId?>(null)`. `CurrentStatus is AwaitingUser` with `BlockedQuest == Q(50)` and `Reason.LevelTooLow == true`. **Tier-1 and Tier-3 candidates are NOT returned.**

### Scenario 3 — `Tier0CompleteEntriesSkipped_FallsThroughToTier3`

**Pre:** `ManualChain = [Q(50), Q(51)]`. `Q(50)` Complete; `Q(51)` Complete. Tier-3 has available `Q(300)`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. `CurrentStatus == Running(Q(300))`.

### Scenario 4 — `Tier0Available_TakesPriorityOverTier1`

**Pre:** `ManualChain = [Q(50)]`. `Q(50)` Available. Tier-1 has an available class quest `Q(200)` for the current job.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(50))`. `CurrentStatus == Running(Q(50))`.

### Scenario 5 — `Tier2DynamicBlocker_InsertsMissingPrereqAtTier2`

**Pre:** Tier-1 candidate `Q(200)` (class quest for current job J(2)=GLA) is `Unavailable`; `WhyUnavailable(Q(200))` returns `PrerequisiteIncomplete=true, MissingPrereqs=[Q(199)]`. `Q(199)` is `Available` and `IsQuestComplete(Q(199)) == false`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(199))`. `CurrentStatus == Running(Q(199))`.

### Scenario 6 — `Tier1ClassQuest_TakesPriorityOverTier3WhenAvailable`

**Pre:** Manual chain empty. Current job = `J(2)` (GLA). Tier-1 candidate `Q(200)` is a GLA class quest, `Available`. Tier-3 candidate `Q(300)` is also `Available`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(200))`. `CurrentStatus == Running(Q(200))`. (Class quests beat MSQ.)

### Scenario 7 — `Tier4OffByDefault_DoHQuestSkippedEvenWhenAvailable`

**Pre:** `Options = SchedulerOptions.Default` (so `EnableCraftGatherQuests == false`). Current job = `J(9)` (CRP). Tier-4 candidate `Q(400)` is a CRP class quest, `Available`. No Tier-1 candidates (Tier 1 is for combat jobs in this setup — `Q(400)` is registered as Tier 4 by the data provider). Tier-3 candidate `Q(300)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. `Q(400)` is not selected. `CurrentStatus == Running(Q(300))`.

### Scenario 8 — `Tier4EnabledExplicitly_Tier3StillWins_ButTier4SelectedWhenTier3Empty`

Two sub-cases:

**8a — Tier 3 still beats Tier 4:**
**Pre:** `Options = Default with { EnableCraftGatherQuests = true }`. Current job = `J(9)` (CRP). Tier-4 candidate `Q(400)` (CRP) is Available. Tier-3 candidate `Q(300)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. Tier 4 is strictly lower priority than Tier 3; the crafter's active-job status does not promote it.

**8b — Tier 4 selected when Tier 3 is exhausted:**
**Pre:** Same options. `Q(300)` is Complete. Only `Q(400)` remains.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(400))`. `CurrentStatus == Running(Q(400))`.

### Scenario 9 — `Tier5OffByDefault_SideQuestSkipped`

**Pre:** `Options = SchedulerOptions.Default`. No Tier-1/3 candidates available. Tier-5 candidate `Q(500)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok<QuestId?>(null)`. `CurrentStatus == Idle`. `Q(500)` is not selected.

### Scenario 10 — `Tier5EnabledExplicitly_SideQuestSelected`

**Pre:** `Options = Default with { EnableSideQuests = true }`. No Tier-1/3 candidates available. Tier-5 candidate `Q(500)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(500))`. `CurrentStatus == Running(Q(500))`.

### Scenario 11 — `ResumeFromPause_ReevaluatesFromScratch`

**Pre:** Earlier call to `NextQuestToRun` returned `Q(101)` (Tier 3, Available). Subsequently `Q(101)` is marked Complete in the fake state. A new Tier-1 class quest `Q(200)` has become Available for the current job.
**When:** `NextQuestToRun(ct)` is called again.
**Then:** Returns `Result.Ok(Q(200))` (Tier 1 wins over remaining Tier 3). The scheduler does not return the next Tier-3 quest first — it re-evaluates from scratch.

### Scenario 12 — `Idle_AllQuestsComplete_ReturnsNullAndIdleStatus`

**Pre:** Manual chain empty. Every quest known to the data provider is Complete in `IQuestState`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok<QuestId?>(null)`. `CurrentStatus == Idle`.

### Scenario 13 — `WrongJobQuestSkipped_PlayerChangedJob`

**Pre:** Current job = `J(2)` (GLA). Tier-1 corpus contains `Q(200)` (CNJ-only, `IsClassQuestForJob(Q(200), J(2)) == false`). No other Tier-1 candidates. Tier-3 candidate `Q(300)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. `Q(200)` is filtered out before availability is even queried.

### Scenario 14 — `Tier1QuestUnavailableDueToWrongJob_NotElevatedToTier2`

**Pre:** Current job = `J(2)`. Tier-1 candidate `Q(200)` has `IsClassQuestForJob(Q(200), J(2)) == true` (data provider says GLA), but `WhyUnavailable(Q(200))` returns `WrongJob=true` (game-state disagrees — perhaps the player just swapped jobs). Tier-3 candidate `Q(300)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. `Q(200)` is skipped without recursive blocker resolution. The log should record the inconsistency.

### Scenario 15 — `Tier2RecursiveResolution_PrereqOfPrereq`

**Pre:** Tier-3 candidate `Q(300)`: `WhyUnavailable` → `PrerequisiteIncomplete, MissingPrereqs=[Q(299)]`. `Q(299)`: `WhyUnavailable` → `PrerequisiteIncomplete, MissingPrereqs=[Q(298)]`, not available. `Q(298)`: `IsQuestAvailable == true`, not complete.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(298))`. `CurrentStatus == Running(Q(298))`.

### Scenario 16 — `Tier2CycleProtection_NoInfiniteLoop`

**Pre:** Tier-3 `Q(300)` requires `Q(299)`; `Q(299)` requires `Q(300)` (data cycle). Neither is Available, neither is Complete.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok<QuestId?>(null)` after `Q(300)` is exhausted. `CurrentStatus == Idle` (or proceeds to Tier-4/5 if enabled). The test asserts that the call terminates within a small bounded time (< 100ms) and does not stack-overflow.

### Scenario 17 — `Tier2DeadEnd_NoUnlockerFound_SkipsToNextTier3Candidate`

**Pre:** Tier-3 corpus = `{Q(300), Q(301)}`. `Q(300)` blocked by `Q(299)`, which is itself blocked by unknown/missing prereq (recursion returns null). `Q(301)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(301))`. `CurrentStatus == Running(Q(301))`.

### Scenario 18 — `SortKeyOrderingWithinTier`

**Pre:** Tier-3 corpus = `{Q(300), Q(301), Q(302)}`, all Available. `GetJournalSortKey` returns: `Q(300)=20, Q(301)=10, Q(302)=30`.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(301))` (lowest SortKey). Tiebreaker by raw `QuestId.Value` is exercised in a sibling test with two quests sharing a SortKey.

### Scenario 19 — `AdapterFailureOnIsQuestAvailable_SkipsCandidate`

**Pre:** Tier-3 corpus = `{Q(300), Q(301)}`. `IsQuestAvailable(Q(300))` returns `Result.Fail<bool>("queryFailed", "transient")`. `Q(301)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(301))`. `Q(300)` is skipped (not retried, not promoted to blocker).

### Scenario 20 — `AdapterFailureOnGetCurrentJob_SkipsTier1ButContinuesToTier3`

**Pre:** `IGameStateProvider.GetCurrentJob` returns `Result.Fail<JobId>("playerNull", null)`. Tier-3 candidate `Q(300)` is Available.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(300))`. Tier-1 evaluation is skipped (cannot determine "active job"). Tier-4 evaluation is also skipped for the same reason if `EnableCraftGatherQuests` is true.

### Scenario 21 — `UpdateOptions_AffectsNextCallNotCurrent`

**Pre:** Scheduler constructed with `Options.Default`. First call returns some quest. Caller invokes `UpdateOptions(Default with { EnableSideQuests = true })`. Tier-5 candidate `Q(500)` is Available; no other quests are available.
**When:** Second call to `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok(Q(500))`. The first call's status is unaffected by `UpdateOptions`.

### Scenario 22 — `NullArgumentsInConstructor_Throw`

**Pre:** Construct `QuestScheduler` with one of the five constructor arguments set to null.
**When:** Constructor invoked.
**Then:** `ArgumentNullException` is thrown, naming the null parameter.

### Scenario 23 — `EmptyKnownQuestCorpus_ReturnsIdle`

**Pre:** `IQuestDataProvider.EnumerateKnownQuests()` returns an empty list. Manual chain empty.
**When:** `NextQuestToRun(ct)`.
**Then:** Returns `Result.Ok<QuestId?>(null)`. `CurrentStatus == Idle`.

### Scenario 24 — `Tier0AlreadyCompletedReason_TreatedAsCompleteNotBlocker`

**Pre:** `ManualChain = [Q(50)]`. `IsQuestComplete(Q(50)) == false` (game-state says not complete) BUT `WhyUnavailable(Q(50))` returns `AlreadyCompleted=true` (Lumina-side says complete). This is a data inconsistency.
**When:** `NextQuestToRun(ct)`.
**Then:** The Rule-0 path prefers `IsQuestComplete` as the authority. Since `IsQuestComplete == false`, the entry is not skipped as complete. Since `WhyUnavailable` returns a non-null reason, `CurrentStatus = AwaitingUser(Q(50), reason with AlreadyCompleted=true)`. **The scheduler does NOT loop forever or silently skip; it stops and lets the user resolve.**

### Scenario 25 — `Tier1NullTierFromDataProvider_QuestIgnored`

**Pre:** `Q(200)` has `GetQuestTier(Q(200)) == null` (uncategorized). It would otherwise match Tier-1 criteria.
**When:** `NextQuestToRun(ct)`.
**Then:** `Q(200)` is not selected as a Tier-1 candidate. Falls through to Tier 3.

---

## 9. Expected test count

| Bucket | Count |
|---|---|
| Happy paths (Scenarios 1, 3, 6, 10, 18) | 5 |
| Tier-0 blocking & manual chain (Scenarios 2, 4, 24) | 3 |
| Tier-2 dynamic resolution (Scenarios 5, 15, 16, 17) | 4 |
| Tier filtering & opt-in gates (Scenarios 7, 8a, 8b, 9, 25) | 5 |
| Class quest & wrong-job (Scenarios 6, 13, 14) | 3 |
| Adapter failure handling (Scenarios 19, 20) | 2 |
| Status transitions & options updates (Scenarios 11, 21) | 2 |
| Idle / empty cases (Scenarios 12, 23) | 2 |
| Sort ordering (Scenario 18 + 1 sibling tiebreak test) | 2 |
| Constructor null checks (Scenario 22, parameterized × 5 args) | 5 |

Total: ~33 tests (Scenarios 8, 22 each produce multiple cases via `[Theory]`).

---

## 10. Mocking targets for tests

The Tester should construct mocks/fakes for:

- `IQuestState` — hand-rolled `FakeQuestState` already exists in `QuestForge.Adapters.Fakes`. Extend it as needed for `WhyUnavailable` scripting (it may not yet support per-quest reason scripting).
- `IGameStateProvider` — existing `FakeGameStateProvider`. Used here only for `GetCurrentJob`.
- `IQuestDataProvider` — **NEW**. Create `FakeQuestDataProvider` in `QuestForge.Adapters.Fakes` with builder-style methods: `WithQuest(QuestId, tier: int?, job: JobId?, sortKey: int, prereqs: QuestId[], join: PrerequisiteJoin)`.
- `ILogger<QuestScheduler>` — use `NullLogger<QuestScheduler>.Instance`.

The scheduler under test is `QuestScheduler` (the production class). No partial mocking. Tests are pure unit tests on the scheduler's `NextQuestToRun` and `CurrentStatus`.

---

## 11. Integration points (out of scope for Phase 8 tests)

- `EngineHost` calling `IQuestScheduler.NextQuestToRun` in a run loop — covered by future integration tests.
- `IQuestDataProvider`'s Dalamud-backed implementation reading Lumina — covered by manual smoke testing in-game (Lumina cannot be loaded in CI).
- UI binding to `CurrentStatus` — Phase 8 UI work, not scheduler-level.
- Persisting `SchedulerOptions` in `PluginConfig` — plugin-layer concern.

---

✅ READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviours.
- Happy paths: 6 scenarios
- Edge cases: 14 scenarios (Tier-2 recursion, sort, adapter quirks, data inconsistencies)
- Error cases: 5 scenarios (adapter failures, null args, cycles)
- Expected total: ~30 tests