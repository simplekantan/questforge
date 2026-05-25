# Acceptable-Now Quest Eligibility — Implementation Plan

**Status:** ready for test creation
**Input docs:** docs/ADAPTERS.md (IQuestState), docs/SCHEMA.md, docs/QUEST_VARIABLES_TRACE_PLAN.md (the trace-starvation precedent this plan is engineered to avoid), `QuestForge.Adapters/State/IQuestState.cs`, `QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs`, `QuestForge.Plugin/Authoring/AuthoringHost.cs`, `QuestForge.Plugin/UI/Authoring/InteractionPanel.cs`, `QuestForge.Plugin/PluginConfig.cs`, `QuestForge.Plugin/Commands/QfCommand.cs`
**Output (behavior change):** In authoring mode, the Interaction panel's "Available quests from this NPC" list defaults to showing only quests the player can accept *right now* (acceptable-now), with a new checkbox to reveal locked/future quests. `IQuestState` gains a new `IsAcceptableNow` signal; `IsQuestAvailable`/`WhyUnavailable` are unchanged, so `QuestScheduler` is unaffected.
**Scope:** one PR in `questforge`. No `questforge-tools` change, no `questforge-data` change, no schema change, no trace event change. Branch `feat/acceptable-now-quests`.

---

## 1. Problem statement

In authoring mode the Interaction panel lists quests offered by the targeted NPC and filters them with `IQuestState.IsQuestAvailable`. Its Dalamud implementation (`DalamudQuestState.IsQuestAvailable` → `WhyUnavailable`) checks only:

- quest already complete,
- quest already accepted,
- wrong job/class (`ClassJobCategory0`),
- level too low (`ClassJobLevel[0]`),
- prerequisite quests (`PreviousQuest[0..2]` + `PreviousQuestJoin`).

That answers "*is this quest in this NPC's chain and not yet blocked by level/job/prior-quest?*" — **not** "*can the player accept this quest right now?*". It misses real-time gates the game enforces at the accept prompt: Grand Company membership/rank, beast-tribe reputation rank, required key items, repeatable/daily cooldowns, and `QuestLock`/unlock-link conditions. The result: the panel offers quests the game would refuse, which is misleading for an author trying to record exactly the quest that is acceptable in front of them.

We add a **new, distinct, strictly-stronger signal** `IsAcceptableNow` and make the panel default to it, without touching `IsQuestAvailable`/`WhyUnavailable`.

---

## 2. Decisions already fixed by the user (do not relitigate)

1. **New distinct signal `IsAcceptableNow`.** Do not change `IsQuestAvailable` or `WhyUnavailable`. Rationale: `QuestForge.Engine/Scheduling/QuestScheduler.cs` consumes `IsQuestAvailable`/`WhyUnavailable` for tiering (confirmed: lines 75, 82, 177, 192, 244, 252). Tightening them in place would change scheduler inputs. Keeping them untouched gives **zero scheduler blast radius**.
2. **Panel defaults to acceptable-now**, with a toggle that reveals locked/future quests — mirroring the existing `ShowCompletedQuestsInAuthorPanel` config-flag + checkbox pattern in `InteractionPanel.Draw`.

---

## 3. Architectural decisions (read before coding)

### 3.1 `IsAcceptableNow` is an authoring-panel read only — it must NEVER enter an engine-tick path or emit a trace observation

This is the load-bearing constraint of the whole PR. The recent quest-variables work (docs/QUEST_VARIABLES_TRACE_PLAN.md) established a painful precedent: adding a single new `GetQuestVariables` read to `QuestEngine.ResolveAction` *starved every existing replay fixture* (the `SegmentedObservationScanner` throws `ReplayObservationStarvationException` for a `(method, arg)` pair that never appears in the recorded trace), forcing an in-game re-record. We must not repeat that here.

Therefore:

- `QuestEngine.ResolveAction` (and every per-tick engine path) **must not** call `IsAcceptableNow`. The engine does not consume this signal at all. Grep-gate: `QuestForge.Engine` must contain **zero** references to `IsAcceptableNow` after this PR.
- The only production caller is `AuthoringHost.UpdateNpcQuestCache` (Dalamud-layer, fired on NPC retarget — not on the engine tick).
- `RecordingQuestState.IsAcceptableNow` **must not** call `Record(...)`. The recording proxy emits observations only via explicit per-method `Record(...)` calls (confirmed: each method body in `RecordingQuestState.cs` calls `Record(nameof(...), ...)`; there is **no** auto-emit interceptor). A delegate-through with no `Record` call emits nothing. This means a trace recorded with this PR is byte-for-byte identical to one recorded without it — no fixture re-record, no starvation.

> **Why this matters:** If `IsAcceptableNow` were recorded, then `ReplayQuestState` would have to scan for it, and any fixture recorded before this PR (all of them) would starve. Because it is never recorded, `ReplayQuestState.IsAcceptableNow` never needs a recorded observation (see §3.4).

### 3.2 Method signature — mirror `IsQuestAvailable`, return a plain `Task<Result<bool>>`

```csharp
// QuestForge.Adapters/State/IQuestState.cs — add to the "Lifecycle queries" group,
// immediately after IsQuestAvailable.

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
```

**Rejected:** returning a reason object (`Task<Result<QuestUnacceptableReason?>>`) for the reveal view. The reveal view simply shows the *existing* list (available + completed), so no per-quest reason is needed in v1. A plain bool is cheapest and keeps the panel wiring trivial. (A future follow-up may add a reason; out of scope here.)

**Rejected:** a separate non-`IQuestState` service. Putting the read on `IQuestState` keeps the engine-purity boundary intact (the Dalamud read lives in `DalamudQuestState`, fakes script values) and matches the existing `IsQuestAvailable` shape exactly.

### 3.3 The invariant: acceptable-now ⊆ available

`IsAcceptableNow` must be **strictly stronger** than `IsQuestAvailable`. Concretely, the real `DalamudQuestState.IsAcceptableNow` is implemented as:

```
IsAcceptableNow(q) = IsQuestAvailable(q)  AND  all in-scope real-time gates pass
```

so `IsAcceptableNow == true ⇒ IsQuestAvailable == true` by construction. This is asserted directly in CI against `FakeQuestState` (§7, group INV) — the fake enforces the same implication so a test can pin it without a game.

**Failure propagation:** if `IsQuestAvailable` returns `Result.Fail`, `IsAcceptableNow` returns the **same** `Failure` (reason + detail) — it does not swallow the failure into `false`. This mirrors how `DalamudQuestState.IsQuestAvailable` propagates a `WhyUnavailable` failure.

### 3.4 Non-Dalamud implementers

- **`FakeQuestState`** — settable, defaulting to the available-implied value so tests get the invariant for free unless they override. Default semantics (see §3.5): if not explicitly scripted, `IsAcceptableNow(q)` returns the *same* value `IsQuestAvailable(q)` returns. A new `SetIsAcceptableNow(QuestId, bool)` setter overrides it. A new `SetIsAcceptableNowFail(QuestId, reason, detail)` (mirroring `SetIsQuestAvailableFail`) scripts a `Failure`.
- **`RecordingQuestState`** — delegate-through, **no `Record` call**:
  ```csharp
  public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
      => _inner.IsAcceptableNow(quest, ct);   // NO Record(...) — authoring-only, never traced (§3.1)
  ```
  (Note: returns the inner Task directly; `async`/`await` is unnecessary because there is nothing to do after the inner call. Either form is acceptable as long as no `Record` is called.)
- **`ReplayQuestState`** — **no `ScanNext` call.** Replay fixtures never contain an `IsAcceptableNow` observation (because the recording proxy never emits one, §3.1), and replay never exercises the authoring panel. Return a fixed benign default **without scanning**:
  ```csharp
  public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
      // Authoring-only signal; never recorded, never replayed. Do NOT ScanNext — that
      // would starve every fixture. Return a benign default.
      => Task.FromResult<Result<bool>>(Result.Ok(false));
  ```
  Returning `false` (not `true`) is the safe default for a never-replayed path: it cannot mask a missing observation as a spurious "acceptable".

No other implementers exist. Grep `: IQuestState` returns exactly: `DalamudQuestState`, `FakeQuestState`, `RecordingQuestState`, `ReplayQuestState`, plus one test-local `FailingQuestState` in `RecordingQuestStateTests.cs` (file-scoped; must also implement the new member — see §6).

### 3.5 `FakeQuestState` default = mirror `IsQuestAvailable`

The fake's default keeps the invariant true with no extra scripting: `IsAcceptableNow(q)` returns whatever `IsQuestAvailable(q)` would return (including its scripted `Failure`), **unless** a test explicitly calls `SetIsAcceptableNow(q, …)` to narrow it. This lets a test model "available but not acceptable-now" by setting `QuestStatus.Available` (→ `IsQuestAvailable == true`) and then `SetIsAcceptableNow(q, false)`. For an unknown/not-accepted quest with no scripting, both return `false` (status defaults to `Unknown`, so `IsQuestAvailable == false` and `IsAcceptableNow` mirrors it).

`FakeQuestState.IsAcceptableNow` records `nameof(IsAcceptableNow)` into `RecordedReads` like every other fake method (so the panel-wiring tests can assert it was consulted).

### 3.6 Panel filtering composition with the existing completed-quests toggle

Two independent boolean config flags drive three list states:

| `ShowUnacceptableQuestsInAuthorPanel` (new) | `ShowCompletedQuestsInAuthorPanel` (existing) | List shows |
|---|---|---|
| `false` (default) | `false` (default) | only acceptable-now, not-complete quests |
| `true` | `false` | available + locked/future not-complete quests (the current behavior) |
| `false` | `true` | acceptable-now quests **and** completed quests |
| `true` | `true` | everything (available/locked/future + completed) |

Filtering lives in `UpdateNpcQuestCache`. The cache stores **all** candidate quests' computed flags (`IsAvailable`, `IsComplete`, `IsAcceptableNow`) and the **filtering by acceptable-now happens in the cache build** (consistent with how completed quests are already filtered there at line 334). When either flag changes, `InteractionPanel.Draw` calls `_host.InvalidateNpcCache()` so the list refreshes on the next heartbeat — exactly the existing pattern.

**Decision — where the acceptable-now filter is applied:** in `UpdateNpcQuestCache`, after computing `isAcceptableNow`, `continue` (skip) a quest when `!isAcceptableNow && !_config.ShowUnacceptableQuestsInAuthorPanel`, evaluated **after** the existing completed-quest skip. Completed quests are governed solely by `ShowCompletedQuestsInAuthorPanel` (a completed quest is never "acceptable-now", so without this ordering it would be hidden by the acceptable-now filter even when "show completed" is on). Ordering: (1) completed-skip, then (2) acceptable-now-skip — so "show completed" still reveals completed quests regardless of the acceptable-now toggle.

```csharp
// inside the foreach in UpdateNpcQuestCache, after isComplete is computed:

// (1) completed-quest gate — unchanged
if (isComplete && !_config.ShowCompletedQuestsInAuthorPanel) continue;

var acceptableResult = _questState.IsAcceptableNow(publicId, ct).GetAwaiter().GetResult();
var isAcceptableNow = acceptableResult is Result<bool>.Success { Value: true };

// (2) acceptable-now gate — NEW. Completed quests already passed (1), so this only
// filters not-complete, not-acceptable quests. A completed quest shown via (1) is not
// dropped here.
if (!isComplete && !isAcceptableNow && !_config.ShowUnacceptableQuestsInAuthorPanel) continue;

results.Add(new NpcQuestInfo(publicId.Value, name, isAvailable, isComplete, isAcceptableNow));
```

### 3.7 `NpcQuestInfo` gains `IsAcceptableNow`

```csharp
// QuestForge.Plugin/Authoring/AuthoringHost.cs
public sealed record NpcQuestInfo(
    uint QuestId, string QuestName, bool IsAvailable, bool IsComplete, bool IsAcceptableNow);
```

It is a `record` (positional) — adding a parameter is a one-line change and all construction sites are in `UpdateNpcQuestCache`. The panel does not currently render anything keyed on this field (the list shows a uniform label), so v1 does not add a per-row badge; the field exists so the cache carries the decision and tests can assert it.

---

## 4. Gate inventory

`IsAcceptableNow = IsQuestAvailable AND (all IN-SCOPE gates pass)`. Each gate must be honestly readable; DEFERRED gates simply are not evaluated (so a deferred-gated quest may still report acceptable-now and be caught at the in-game prompt — acceptable for v1 because the invariant acceptable-now ⊆ available is preserved and the author still sees a strictly tighter list than today).

| Gate | Lumina `Quest` field | Player-state source (ClientStructs) | v1 status | Rationale |
|---|---|---|---|---|
| **Grand Company membership + rank** | `Quest.GrandCompany` (RowId; 0 = none) and `Quest.GrandCompanyRank` (RowId into `GrandCompanyRank`; 0 = none) | `PlayerState.Instance()->GrandCompany` (byte: 0 none, 1 Maelstrom, 2 Twin Adder, 3 Immortal Flames) and `PlayerState.Instance()->GetGrandCompanyRank()` | **IN SCOPE** | Single `PlayerState` read (already used in `DalamudGameStateProvider`, confirmed line 339) + two Lumina fields. Reliably readable, high value (gates GC-progression quests the panel currently mis-offers). Logic: if `Quest.GrandCompany != 0`, require player's GC matches and player rank ≥ required rank. |
| Required key items | `Quest.ItemRequired` (array of item RowIds) | `InventoryManager.Instance()->GetInventoryItemCount(itemId, …)` over key-item inventory | **DEFERRED** | Readable but the field is an array with role/optional slots and key-item vs normal-inventory ambiguity; needs careful index semantics. Strongest deferred candidate — promote in a follow-up issue once index semantics are verified in-game. |
| Beast-tribe reputation rank | `Quest.BeastTribe` + `Quest.BeastReputationRank` | `QuestManager.Instance()->BeastReputation` array | DEFERRED | Reputation-rank indexing and the "currently-active tribe" coupling are easy to get wrong; low corpus coverage today. |
| Repeatable / daily cooldown | `Quest.IsRepeatable` (+ daily/weekly reset tracking) | `QuestManager.Instance()` daily-quest accept tracking | DEFERRED | "Already done this cycle" requires reset-window tracking that is opaque/fragile to read reliably. |
| `QuestLock` / unlock-link | `Quest.QuestLock` (array) + `Quest.QuestLockJoin`; unlock-state links | no single reliable runtime flag | DEFERRED | The lock-link conditions are not exposed as one readable boolean; high risk of false negatives. |

**v1 in-scope gate set = { Grand Company rank }.** This is deliberately minimal-but-honest: it is the one gate that is a clean `PlayerState` read plus two well-known Lumina fields, it is strictly additive over `IsQuestAvailable`, and it preserves the acceptable-now ⊆ available invariant. The DEFERRED rows are explicitly listed so the follow-up issue is pre-scoped.

> **In-game verification (user, not CI):** the exact `PlayerState` accessor for GC rank (`GetGrandCompanyRank()` vs a `GrandCompanyRank` field) and the `Quest.GrandCompanyRank` → `GrandCompanyRank` row→rank-number mapping must be confirmed in-game when the real `DalamudQuestState.IsAcceptableNow` is wired. The CI tests in §7 validate the **contract and the panel/proxy wiring against fakes**; they do not validate the live Lumina/ClientStructs reads.

---

## 5. `IsAcceptableNow` contract (semantics summary)

1. **Strictly stronger:** `IsAcceptableNow(q) == true ⇒ IsQuestAvailable(q) == true` (the invariant). Real impl: `IsQuestAvailable(q) && allInScopeGatesPass(q)`.
2. **Failure propagation:** if the underlying availability/state read returns `Result.Fail(reason, detail)`, `IsAcceptableNow` returns the **same** `Failure` — never coerces a failure to `false`.
3. **Default for not-accepted / unknown quest:** `false` (because `IsQuestAvailable` is `false` for unknown/complete/accepted quests, and acceptable-now mirrors that).
4. **Authoring-only:** not read by the engine; not emitted by the recording proxy; returns a benign `Ok(false)` under replay without scanning.

---

## 6. Per-file change list

| # | File | Change |
|---|---|---|
| 1 | `QuestForge.Adapters/State/IQuestState.cs` | Add `Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct);` after `IsQuestAvailable`, with the doc comment from §3.2. |
| 2 | `QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs` | Implement `IsAcceptableNow`: compute `IsQuestAvailable` first; if `false` or `Failure`, return it directly. Else evaluate the GC gate (§4) via `PlayerState.Instance()` + `Quest.GrandCompany`/`Quest.GrandCompanyRank`; return `Ok(true)` only if the gate passes, else `Ok(false)`. **(In-game-verified, not CI-tested.)** |
| 3 | `QuestForge.Adapters.Fakes/State/FakeQuestState.cs` | Add backing `Dictionary<QuestId,bool> _acceptableNow` + `Dictionary<QuestId,(string,string?)> _acceptableNowFailures`; setters `SetIsAcceptableNow(QuestId,bool)`, `SetIsAcceptableNowFail(QuestId,string,string?)`, `ClearIsAcceptableNowFail(QuestId)`; implement `IsAcceptableNow` per §3.5 (default = mirror `IsQuestAvailable`'s result, including its failure), recording `nameof(IsAcceptableNow)`. |
| 4 | `QuestForge.Adapters/Recording/RecordingQuestState.cs` | Implement `IsAcceptableNow` as a pure delegate-through with **no `Record(...)` call** (§3.4). |
| 5 | `QuestForge.Adapters.Fakes/Replay/ReplayQuestState.cs` | Implement `IsAcceptableNow` returning `Task.FromResult(Result.Ok(false))` with **no `ScanNext`** (§3.4). |
| 6 | `QuestForge.Engine.Tests/Recording/RecordingQuestStateTests.cs` | The file-scoped `FailingQuestState` must implement the new member (return `Result.Fail<bool>(_reason)`) so the test project compiles. |
| 7 | `QuestForge.Plugin/Authoring/AuthoringHost.cs` | Add `IsAcceptableNow` to the `NpcQuestInfo` record (§3.7); in `UpdateNpcQuestCache` compute `isAcceptableNow` and apply the new filter (§3.6), populate the field. |
| 8 | `QuestForge.Plugin/PluginConfig.cs` | Add `public bool ShowUnacceptableQuestsInAuthorPanel { get; set; } = false;` with a doc comment. |
| 9 | `QuestForge.Plugin/UI/Authoring/InteractionPanel.cs` | Add a "Show locked/future quests" checkbox bound to `ShowUnacceptableQuestsInAuthorPanel`, mirroring the existing "Show completed quests" checkbox (save config + `InvalidateNpcCache()` on toggle). Update the empty-list hint to mention this toggle. |
| 10 | `QuestForge.Plugin/Commands/QfCommand.cs` | In `HandleDebugQuest`, print an `acceptableNow: <bool|failure>` line (low-cost: one `IsAcceptableNow` call via `_host.QuestState`, same pattern as the existing `variables:` line). |

> **Compile-gate:** adding a member to `IQuestState` breaks the build until all five production/test implementers (#2–#6) are updated. The Builder must update all of them in the same commit. Any additional `: IQuestState` implementer introduced after this plan was written must also be updated (re-grep before building).

---

## 7. Acceptance criteria (Tester writes failing tests from these)

Grouped by area. Each is independently testable against fakes unless marked **(in-game-only)**. CI-testable criteria do not require a running game.

### Group IFACE — interface & contract (CI-testable)

- **IFACE1** — `typeof(IQuestState).GetMethod("IsAcceptableNow")` exists and returns `Task<Result<bool>>` taking `(QuestId, CancellationToken)`. (Reflection or simple call compiles + runs.)
- **IFACE2** — All production implementers are assignable to `IQuestState` and instances of `DalamudQuestState` need not be constructed; `FakeQuestState`, `RecordingQuestState`, `ReplayQuestState` each expose a callable `IsAcceptableNow` (covered implicitly by the groups below; this is the "all implementers implement it" smoke).

### Group FAKE — `FakeQuestState` semantics (CI-testable)

- **FAKE1 (default mirrors available — available case)** — Given a quest scripted `QuestStatus.Available` and nothing else, `IsAcceptableNow(q)` returns `Ok(true)` (mirrors `IsQuestAvailable == true`).
- **FAKE2 (default mirrors available — unknown case)** — Given a quest with no scripting (status `Unknown`), `IsAcceptableNow(q)` returns `Ok(false)` (mirrors `IsQuestAvailable == false`).
- **FAKE3 (explicit setter narrows)** — Given a quest scripted `QuestStatus.Available` AND `SetIsAcceptableNow(q, false)`, then `IsQuestAvailable(q) == Ok(true)` but `IsAcceptableNow(q) == Ok(false)`. Models "available but not acceptable-now".
- **FAKE4 (explicit setter widens to true only when available)** — Given `SetIsAcceptableNow(q, true)` on a quest also scripted `QuestStatus.Available`, `IsAcceptableNow(q) == Ok(true)`. (The fake honors the explicit value; the invariant test INV1 guards against widening above availability.)
- **FAKE5 (failure scripting)** — Given `SetIsAcceptableNowFail(q, "gcRead", "PlayerState null")`, `IsAcceptableNow(q)` returns `Result<bool>.Failure` with `Reason == "gcRead"` and `Detail == "PlayerState null"`.
- **FAKE6 (records the read)** — After one `IsAcceptableNow(q)` call, `RecordedReads` contains a `StateRead` with `Method == "IsAcceptableNow"`.
- **FAKE7 (pre-cancelled token throws)** — Given a pre-cancelled `CancellationToken`, `IsAcceptableNow(q)` throws `OperationCanceledException` (mirrors the other fake methods' `ct.ThrowIfCancellationRequested()`).

### Group INV — the invariant acceptable-now ⊆ available (CI-testable)

- **INV1 (implication holds across scripted states)** — For a quest in each scripted state {Unknown, Available, Accepted, Complete} and for each `SetIsAcceptableNow` override in {unset, true, false}, assert: whenever `IsAcceptableNow(q) == Ok(true)`, then `IsQuestAvailable(q) == Ok(true)`. (The fake's default + the "true only when available" rule must make this hold; a `SetIsAcceptableNow(q, true)` on a non-available quest is the case the fake must clamp — see Tester note.) 
- **INV2 (failure does not satisfy the implication vacuously)** — Given `SetIsAcceptableNowFail`, `IsAcceptableNow(q)` is a `Failure` (not `Ok(true)`), so the implication is not triggered; assert the result is a `Failure`, confirming failures are propagated, not coerced to a truthy success.

> **Tester note (INV1):** the fake must not allow `IsAcceptableNow` to report `Ok(true)` when `IsQuestAvailable` is `false`. Specify the fake so that `SetIsAcceptableNow(q, true)` is **clamped** to the availability result: the effective value is `explicitValue && (IsQuestAvailable == Ok(true))`. This keeps the invariant unbreakable in tests and mirrors the real implementation's `available && gates` construction. FAKE4 (available + true → true) and INV1 (non-available + true → false) together pin this clamp.

### Group REC — recording proxy emits NO observation (CI-testable) — the critical no-starvation guard

- **REC1 (no observation written)** — Given `RecordingQuestState` over a `FakeQuestState`, when `IsAcceptableNow(q)` is called, then `trace.RecordedEvents` contains **zero** `ObservationEvent` with `Method == "IsAcceptableNow"` (assert the count of such events is 0). This is the guard that prevents fixture starvation.
- **REC2 (pass-through value)** — The proxy returns exactly the inner result: given the inner scripted `Ok(true)`, the proxy returns `Ok(true)`; given the inner a `Failure`, the proxy returns the same `Failure`.
- **REC3 (inner called exactly once, no double-call)** — After one proxy `IsAcceptableNow`, the inner `FakeQuestState.RecordedReads` count for `IsAcceptableNow` is exactly 1.
- **REC4 (other methods still emit — proxy not broken)** — In the same proxy, calling `IsQuestComplete` still writes exactly one `ObservationEvent` (regression guard that adding the silent method didn't disturb the emit path). Total `ObservationEvent` count after one `IsAcceptableNow` + one `IsQuestComplete` is exactly 1.

### Group REP — replay does not scan (CI-testable) — the other half of the no-starvation guard

- **REP1 (returns default without scanning)** — Given a `ReplayQuestState` built from a trace that contains **no** `IsAcceptableNow` observation (e.g. only a `GetQuestSequence` observation), calling `IsAcceptableNow(q)` returns `Ok(false)` and does **not** throw `ReplayObservationStarvationException`.
- **REP2 (does not consume an unrelated observation)** — Given a `ReplayQuestState` whose next scanner observation is for `IsQuestComplete`, calling `IsAcceptableNow(q)` first and then `IsQuestComplete(q)` still returns the `IsQuestComplete` observation correctly — i.e. `IsAcceptableNow` did not consume/advance the scanner. (Pins "no `ScanNext`".)

### Group PANEL — cache filtering default + toggle (CI-testable via `AuthoringHost`/cache logic against `FakeQuestState`)

> **Tester note (PANEL group):** these target the filtering logic in `UpdateNpcQuestCache` against `FakeQuestState`. If `UpdateNpcQuestCache` is not directly unit-testable (it reads `_services.TargetManager` / Lumina), the Builder should extract the per-quest filter decision into a testable pure helper (e.g. `static bool ShouldShow(bool isComplete, bool isAcceptableNow, bool showCompleted, bool showUnacceptable)`) and the PANEL criteria assert that helper. Specify the helper's truth table to match §3.6.

- **PANEL1 (default hides not-acceptable)** — With both flags `false`: a quest that is available-but-not-acceptable (`isComplete=false, isAcceptableNow=false`) is **hidden** (`ShouldShow == false`).
- **PANEL2 (default shows acceptable)** — With both flags `false`: a quest with `isAcceptableNow=true` is **shown**.
- **PANEL3 (reveal toggle shows locked/future)** — With `ShowUnacceptableQuestsInAuthorPanel=true`, `ShowCompletedQuestsInAuthorPanel=false`: a not-complete, not-acceptable quest is **shown**.
- **PANEL4 (completed still governed by its own flag, independent of acceptable-now)** — With `ShowCompletedQuestsInAuthorPanel=true`, `ShowUnacceptableQuestsInAuthorPanel=false`: a completed quest (`isComplete=true`, `isAcceptableNow=false`) is **shown** (not dropped by the acceptable-now filter). Pins the ordering decision in §3.6.
- **PANEL5 (completed hidden when its flag is off, regardless of reveal toggle)** — With `ShowCompletedQuestsInAuthorPanel=false`, `ShowUnacceptableQuestsInAuthorPanel=true`: a completed quest is **hidden** (the reveal toggle reveals locked/future, not completed).
- **PANEL6 (cache populates IsAcceptableNow)** — If testing `UpdateNpcQuestCache` end-to-end is feasible: a produced `NpcQuestInfo` carries `IsAcceptableNow` equal to the `FakeQuestState` scripted value for that quest.

### Group CMD — `/qf debug quest` line (optional, CI-testable only if the formatter is extracted)

- **CMD1** — If the `acceptableNow:` formatting is extracted to a pure helper, given a `Result<bool>.Success(true)` it formats `acceptableNow: true`; given a `Failure("gcRead", …)` it formats `acceptableNow: (failure: gcRead)`. (If not extracted, this is verified in-game; mark **(in-game-only)**.)

### Group GAME — real Dalamud read (in-game-only, NOT CI)

- **GAME1 (in-game-only)** — On a character below the required GC rank for a GC-progression quest offered by the targeted NPC, the quest is excluded from the default panel list and appears only when "Show locked/future quests" is enabled. Validated by the user in-game.
- **GAME2 (in-game-only)** — On a character at/above the required GC rank (and otherwise available), the same quest appears in the default list. Validated by the user in-game.
- **GAME3 (in-game-only)** — `/qf debug quest <gcQuestId>` prints an `acceptableNow:` line consistent with GAME1/GAME2.

---

## 8. Implementation order

**Phase A — Interface + implementers (compile-gate), 0.25 day.**
1. Add `IsAcceptableNow` to `IQuestState` (#1).
2. Update all implementers in the same commit so the solution compiles: `FakeQuestState` (#3), `RecordingQuestState` (#4), `ReplayQuestState` (#5), the test-local `FailingQuestState` (#6), and a stub-then-real `DalamudQuestState` (#2). The build is red until all are done.
3. Make groups FAKE, INV, REC, REP pass. **Done before B.**

**Phase B — Panel + config + command (`questforge`), 0.25 day.**
4. `NpcQuestInfo` field + `UpdateNpcQuestCache` filter (#7); extract the `ShouldShow` helper for testability.
5. `PluginConfig` flag (#8); `InteractionPanel` checkbox (#9); `/qf debug quest` line (#10).
6. Make group PANEL (and CMD1 if extracted) pass. **Done before C.**

**Phase C — In-game validation (user, operational).**
7. Build the plugin; the user wires/verifies the real `DalamudQuestState` GC gate against a live character (GAME1–GAME3). Adjust the `PlayerState` rank accessor / `GrandCompanyRank` mapping if in-game behavior differs. Not part of the CI suite.

---

## 9. Done criteria

1. `IQuestState` exposes `Task<Result<bool>> IsAcceptableNow(QuestId, CancellationToken)`; all five implementers compile and implement it (IFACE1/IFACE2 green).
2. `FakeQuestState.IsAcceptableNow` defaults to mirroring `IsQuestAvailable`, is overridable via `SetIsAcceptableNow`/`SetIsAcceptableNowFail`, clamps `true` to availability, and records the read (FAKE1–FAKE7 green).
3. The invariant **acceptable-now ⊆ available** holds for every scripted combination; failures propagate rather than coercing to `false` (INV1, INV2 green).
4. `RecordingQuestState.IsAcceptableNow` writes **no** `ObservationEvent` while passing the value through and calling the inner exactly once, leaving the emit path for other methods intact (REC1–REC4 green) — **no fixture starvation, no re-record**.
5. `ReplayQuestState.IsAcceptableNow` returns `Ok(false)` without scanning and without consuming an unrelated observation (REP1, REP2 green).
6. The Interaction panel defaults to acceptable-now; the new `ShowUnacceptableQuestsInAuthorPanel` checkbox reveals locked/future quests; the existing `ShowCompletedQuestsInAuthorPanel` continues to govern completed quests independently per the §3.6 truth table (PANEL1–PANEL5 green; PANEL6 if end-to-end).
7. `QuestForge.Engine` contains **zero** references to `IsAcceptableNow` (grep-verified) — the engine never consumes the signal.
8. `IsQuestAvailable` and `WhyUnavailable` are byte-for-byte unchanged; `QuestScheduler` inputs and behavior are unaffected (no scheduler test changes).
9. **(in-game)** A GC-progression quest below the player's GC rank is hidden by default and revealed by the toggle; `/qf debug quest` reports a consistent `acceptableNow:` line (GAME1–GAME3, validated by the user).

---

## 10. Exclusions

This PR does **NOT** include:

- **Any change to `IsQuestAvailable` / `WhyUnavailable` / `QuestUnlockReason`** or any `QuestScheduler` logic (§2.1).
- **Any engine consumption of `IsAcceptableNow`.** It is authoring-panel-only; the engine does not read it and the recording proxy does not emit it (§3.1). No `QuestEngine.ResolveAction` change, no new trace observation, no fixture re-record.
- **The DEFERRED gates** (key items, beast-tribe reputation, repeatable/daily cooldown, QuestLock/unlock-link). v1 evaluates only the Grand Company rank gate (§4). Promoting key-items is the pre-scoped follow-up.
- **A reason object for the reveal view.** `IsAcceptableNow` returns a plain bool; the reveal view shows the existing list (§3.2).
- **A per-row panel badge** distinguishing acceptable-now from available. The field is carried in `NpcQuestInfo` but the v1 list label is unchanged (§3.7).
- **Schema, trace-format, or `TraceMode` changes.** None are touched.
- **CI validation of the live Dalamud GC read.** The Lumina/ClientStructs reads are validated in-game by the user (§4, group GAME); CI validates the contract and wiring against fakes.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §7.
- Happy paths: 9 scenarios (IFACE1, FAKE1, FAKE2, FAKE4, REC2, REP1, PANEL2, PANEL3, PANEL4)
- Edge cases: 9 scenarios (IFACE2, FAKE3, FAKE6, FAKE7, INV1, REC3, REC4, REP2, PANEL1, PANEL5, PANEL6 — count flexible by extraction)
- Error cases: 3 scenarios (FAKE5, INV2, CMD1 — CMD1 in-game-only unless formatter extracted)
- In-game-only (not CI): 3 scenarios (GAME1, GAME2, GAME3)
- Expected total: ~22 CI tests — ~7 in `FakeQuestState` tests (group FAKE), ~2 invariant tests (group INV), ~4 in `RecordingQuestStateTests` additions (group REC), ~2 in a `ReplayQuestState` test (group REP), ~6 in a panel-filter helper test (group PANEL), plus ~1 optional command-formatter test (CMD).
