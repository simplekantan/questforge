# GLOBAL_DEFENSE_PLAN

**Status:** spec — not yet implemented
**Branch:** `feat/global-defense-combat` (clean at main = `19ceb35`)
**Author repo:** `questforge` (engine + plugin only; no schema or data-repo changes)

---

## §1 Goals + non-goals

### Goals (v1)

1. **Defend before advancing.** When the player is in combat AND at least one enemy is actively attacking (`IsTargetingPlayer || OnPlayerEnmityList`), the engine must engage that enemy before dispatching the *current step's* action — regardless of whether the current step is `Travel`, `Talk`, `Accept`, `TurnIn`, `InteractObject`, `HandOver`, `Attunement`, `Purchase`, `Wait`, or `Cutscene`.
2. **Single, uniform mechanism.** Replace the per-CombatStep post-objective mop-up with one defense rule that fires uniformly. Eliminate the race in quest 65847 (`IsPlayerInCombat` reads false between mob death and respawn aggro → step confirms → defense never re-fires).
3. **CombatStep-aware.** When the current step *is* a `CombatStep` with unsatisfied `Expect`, the existing `CombatController.Decide` path remains authoritative (it already prioritises kill-set + handles aggro adds). The global defense rule must NOT double-fire in that case.
4. **CI-testable end to end.** A new `DefenseEngineTests` xUnit fixture proves D1–D7 against in-memory fakes.
5. **Zero fixture-cascade fallout.** Existing replay fixtures (`simple-linear-acceptance.trace.jsonl`, `with-attunement.trace.jsonl`) MUST continue to pass without re-recording (verified §3 Q1).

### Non-goals (v1)

- **No new step type.** The defense rule is engine-internal; quest authors do not opt in or out.
- **No author-configurable scan radius / target priority.** Reuses `CombatController.ScanRadius = 50f` and a clear deterministic tie-break (§3 Q3).
- **No interaction with dungeon/trial/raid/SPD contexts.** `InstanceKind` routing already excludes those from open-world combat flow; the defense rule only matters in `InstanceKind.None` and behaves benignly elsewhere because dungeon combat is delegated.
- **No predicate-language change.** No new `inDefense()` / `isUnderAttack()` predicate. Quest authors cannot reference defense state.
- **No schema change.** (§4 confirms.)
- **No defense-only timeout.** v1 does NOT add a `DefenseTimeout`. The engine spins `Engage(null)` indefinitely if a target is unreachable — same failure mode as `Engage(null)` today (§3 Q5).
- **No new adapter method.** `IGameStateProvider.GetHostileActors` is sufficient; filtering happens engine-side (§3 Q7).

---

## §2 User-pinned decisions (verbatim)

> "Rather than being 'clever' trying to have the mop-up behavior trigger some of the time, we just make it a 'global rule' so-to-speak that we always defeat enemies that are attacking us before advancing to the next step."

> "isHostile may not match docile enemies that are attacking us. If there is an enmity list or some kind of 'currently attacking player' list we can use, then we should use that for our list of targets to mop-up."

**Pinned filter:** an enemy is a defense target iff `actor.IsTargetable && !actor.IsDead && (actor.IsTargetingPlayer || actor.OnPlayerEnmityList)`. This is the same filter `KillPriority.SelectAttacker` already uses (`QuestForge.Engine/Combat/KillPriority.cs:93-122`). Pure-faction hostility (e.g. docile mobs of the opposing faction) is NOT a trigger.

**Pinned skip (added 2026-05-28 in-game smoke):** if the player is currently mounted AND vnavmesh is still navigating, defense is SKIPPED — keep moving. The whole point of mounting is to skip overworld trash mobs en route to objectives; stopping to dismount and engage every aggro along the way defeats it. Defense fires only when EITHER (a) the player is on foot, OR (b) the player is mounted but vnavmesh has stopped (we're at the destination). The skip uses one extra `GetPlayerState` read + one `IsNavigating` read, only when `IsPlayerInCombat=true`. See §3 Q3 for the predicate.

---

## §3 Architectural decisions

### Q1 — Where does the defense check live? **Pinned: (c) new seam — `QuestEngine.ResolveDefenseOrNull` at the top of `ResolveAction`. UNIVERSAL — no quest-shape gate.** (Resolved 2026-05-28 per user: defense should fire on every quest, not only combat-bearing ones.)

**Decision:** Add a private async helper `ResolveDefenseOrNull(playerPos, ct)` invoked at the very top of `ResolveAction` (`QuestEngine.cs:349`) for every tick. If the helper returns non-null, the engine returns that action immediately (skipping the cursor walk this tick). If null, the cursor walk proceeds unchanged.

```csharp
// In QuestEngine.cs, near top of ResolveAction (after seq/NGP reads, before UI/pos reads).
var defense = await ResolveDefenseOrNull(playerPos, ct);
if (defense is not null) return (defense.Value.action, defense.Value.stepId);
```

**Why (c) over (a) plain top-of-Tick:**

- **Why not (b) in `EngineHost`.** The defense decision needs the kill-priority logic (`CombatController.DecideClearAggro` → `KillPriority.SelectAttacker`) which lives in `QuestForge.Engine` (pure C#). Moving the decision to `EngineHost` would either (i) drag `CombatController` instantiation into the host (breaking "engine owns the decision") or (ii) duplicate the selection logic. Both are worse than a guarded engine seam.
- **Why (c) over (a) at the TOP of `Tick`.** `Tick` already wraps `ResolveAction` for trace emission. Putting the seam inside `ResolveAction` means defense-emitted `Engage` actions flow through the existing `DecisionEvent` write path uniformly. No special trace wiring.

**Fixture-cascade consequence (accepted, follow-up):** the two existing engine replay fixtures (`simple-linear-acceptance.trace.jsonl`, `with-attunement.trace.jsonl`) carry zero `IsPlayerInCombat` or `GetHostileActors` observations. Once defense runs universally, those fixtures will throw `ReplayObservationStarvationException` on Tick. The test wrapper at `Quest66130ReplayTests.WrapTickForStarvation` (`Quest66130ReplayTests.cs:240-261`) catches that and `Assert.Skip`s with an actionable "re-record needed" message — **tests skip, they do NOT fail**. CI build stays green. Re-recording the 2 fixtures is a tracked follow-up (run the quests in-game with tracing on, then `qf-trace extract-fixture <run>.jsonl`). Until then, the 2 fixture tests provide no regression coverage. User accepted this trade-off (2026-05-28).

**Production behavior is universal:** the live `DalamudGameStateProvider` always returns real values for `IsPlayerInCombat` and `GetHostileActors`, so defense works for every quest in-game from day one.

**Test infrastructure mirror.** `EngineTestHarness.RunToCompletion` (`QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:132-216`) already dispatches `EngineAction.Engage` (line 201). It needs ONE addition: the `HarnessEngine` wrapper (line 249+) mirrors EngineHost's mount/dismount/close-shop pre-switch hooks. The defense rule lives INSIDE `QuestEngine.ResolveAction`, NOT in the host, so the harness needs no additional mirror — the inner `_inner.Tick` already exercises it.

---

### Q2 — What `EngineAction` does defense emit? **Pinned: (b) make `CombatStep` nullable on `EngineAction.Engage`, plus update consumers.**

**Decision:** Change `EngineAction.Engage` from `Engage(CombatStep Step, KillTarget? Target)` to `Engage(CombatStep? Step, KillTarget? Target)`. Defense fires `Engage(Step: null, Target: <attacker>)`. CombatStep dispatch fires `Engage(Step: combatStep, Target: <kill-set or aggro>)` unchanged.

```csharp
// QuestForge.Engine/EngineAction.cs
public sealed record Engage(CombatStep? Step, KillTarget? Target) : EngineAction;
```

**Consumer updates required (all small):**

- `RotationLeaseLatch.OnAction` (`QuestForge.Engine/Combat/RotationLeaseLatch.cs:33`) — already pattern-matches `is EngineAction.Engage` without unpacking `Step`. **Zero change.** Defense and CombatStep both correctly start the rotation lease.
- `EngineHost.DispatchAction` `case EngineAction.Engage:` (`QuestForge.Plugin/EngineHost.cs:316`) — does not unpack `Step`. **Zero change.**
- `EngineTestHarness.RunToCompletion` `case EngineAction.Engage:` (line 201) — does not unpack `Step`. **Zero change.**
- `Quest66130ReplayTests.ActionTypeString` (line 271) — `EngineAction.Engage _ => "engage"` — does not unpack. **Zero change.**
- `CombatFixtureTests.ActionTypeString` (line 482) — same. **Zero change.**
- Test call sites in `CombatClearAggroTests.cs`, `CombatApproachHandoffTests.cs`, `CombatEngineTests.cs`, `CombatFixtureTests.cs`, `RotationLeaseLatchTests.cs` that construct `new EngineAction.Engage(step, target)` — type compiles unchanged (positional record accepts `CombatStep` for `CombatStep?` param).

**Why (b) over the alternatives:**

- **(a) synthesize an ephemeral CombatStep wrapper.** Forced fake `Step.Id`, fake `KillEnemyDataIds`, fake `Spawn`. Risk: future readers of `Engage.Step` start trusting it as real authored data. Rejected.
- **(c) new `EngineAction.Defend`.** Adds a parallel branch in `RotationLeaseLatch`, `EngineHost`, `EngineTestHarness`, `ActionTypeString`. Doubles the surface for the same lease/rotation/dispatch behavior. Rejected.
- **(d) mutate current step context.** Mutation in a value-record world is a smell; would also leak defense state into the (otherwise pure) step graph. Rejected.

**What breaks if `Step` is non-null assumed:** `Engage.Step` is currently only read in tests, and those reads (e.g. `CombatFixtureTests.cs:424` `if (action is EngineAction.Engage engage && engage.Target is { } t)`) ignore `Step`. **No production read of `Step` from `Engage` was found.** Safe change.

**Testability implication:** test helpers may now construct `new EngineAction.Engage(null, target)` to exercise defense scenarios distinct from CombatStep ones — useful for D1–D7 in §5.

---

### Q3 — `CombatController.DecideDefense` contract. **Pinned: reuse `DecideClearAggro` directly, no new method.**

**Decision:** The existing `CombatController.DecideClearAggro(ct)` at `QuestForge.Engine/Combat/CombatController.cs:94-112` is bit-for-bit the right primitive. It already:

- Calls `_gameState.GetHostileActors(ScanRadius, ct)` with `ScanRadius = 50f` (line 25, line 96).
- Filters via `KillPriority.SelectAttacker` (line 109), which uses the user-pinned `IsTargetingPlayer || OnPlayerEnmityList` predicate (`KillPriority.cs:103-104`).
- Tie-breaks: higher score (`IsTargetingPlayer +10`, `OnPlayerEnmityList +5`) → nearest (`DistanceToPlayer asc`) → lowest `ActorId.Value` (`KillPriority.cs:110-114`).
- Issues `SetTarget` / `ClearTarget` and approach-navigation via `ApplyDecision`.
- Returns `CombatDecision { Target, RotationShouldRun, Reason, Approach }`.

**Decision usage in `ResolveDefenseOrNull`:**

```csharp
private async Task<(EngineAction action, string? stepId)?> ResolveDefenseOrNull(
    WorldPosition? playerPos, CancellationToken ct)
{
    var inCombatResult = await _gameState.IsPlayerInCombat(ct);
    // Fail-open: read failure → no defense action this tick. Cursor walk runs normally.
    if (inCombatResult is not Result<bool>.Success { Value: true })
        return null;

    var decision = await _combatController.DecideClearAggro(ct);
    if (decision.Target is null)
        return null; // in combat but no attacker in scan range → cursor walk normally
                     // (e.g. transient between mob death and respawn aggro)

    // Emit defense Engage with Step=null. stepId=null so DecisionEvent.stepId is null
    // (matches "no authored step is currently being executed").
    return (new EngineAction.Engage(Step: null, Target: decision.Target), stepId: null);
}
```

**Tiebreaker:** identical to existing mop-up. `KillPriority.SelectAttacker` is the single source of truth. Documented at `KillPriority.cs:86-91`.

**Return shape:** identical `CombatDecision`. No new `DefenseDecision` record.

**What breaks if a new `DecideDefense` is added:** code duplication with `DecideClearAggro`; two places to keep the radius / range / approach logic in sync. Rejected.

---

### Q4 — Interaction with existing CombatStep mop-up. **Pinned: (a) global defense subsumes mop-up — delete `ResolveMopUp` and `_mopUpStartedAt`.**

**Decision:** The new defense rule fires uniformly for ANY step type (including `CombatStep`). When the cursor is on a `CombatStep` and `Expect` is satisfied AND the player is still in combat:

1. The cursor-walk path at `QuestEngine.cs:461-479` is simplified to: "Expect satisfied → confirm step → `ResetAsync` → continue." The "in-combat → ResolveMopUp" branch is **removed**.
2. On the NEXT tick (or the same tick if defense runs first per Q6), the global defense helper fires `Engage(null, attacker)`, exactly the same Engage shape the mop-up emitted, with `stepId=null` (no authored step is the source).

**Deletions:**

- `ResolveMopUp` method (`QuestEngine.cs:789-801`).
- `MopUpTimeout` const (`QuestEngine.cs:584`).
- `_combatController.StartOrElapsedMopUp` and `ClearMopUpTimer` calls in the engine.
- The post-confirm `_combatController.ClearMopUpTimer();` at `QuestEngine.cs:473`.
- The mop-up timer state itself (`_mopUpStartedAt` field, `StartOrElapsedMopUp`, `ClearMopUpTimer`) in `CombatController` — optional v1 cleanup. Keeping the methods as no-op is acceptable if they have other internal callers; verified only the engine calls them.
- `MopUpTimeout` AwaitUser path (`QuestEngine.cs:793-797`). v1 has NO defense timeout (see Q5).

**Stale assertion fallout (test side):** `CombatClearAggroTests.cs` is the most-affected suite; many tests assert mop-up-specific behaviors (timer, AwaitUser-on-timeout). They will rewrite as defense-rule tests — the user-visible behavior moves to the new mechanism with one semantic change: **the mop-up StepId in DecisionEvent shifts from `cs.Id` to `null`** because defense is no longer step-anchored. Test fixtures need to assert `stepId: null` instead of `stepId: cs.Id`. This change is acceptable because mop-up is, by definition, "after the step's objective was confirmed" — anchoring the trace to a confirmed step always read awkwardly.

**Why (a) over (b) "global defense skips when current step is CombatStep with unsatisfied Expect":**

- **Double-fire prevention.** When `CombatStep.Expect` is unsatisfied, the cursor walk reaches step 5 at `QuestEngine.cs:517` which calls `_combatController.Decide` and emits its own `Engage`. If the global defense helper ALSO emits an Engage on the same tick, the engine returns the *defense* one (since defense fires first in `ResolveAction`). This is fine IF `KillPriority.SelectTarget` and `SelectAttacker` agree on the target in the common case (they do — kill-set membership scores +1000, dominates attacker-only +10). In the rare disagreement (kill-set mob is non-attacking + an attacking add exists), the **defense rule wins** and attacks the add — exactly the global rule the user pinned ("defeat enemies attacking us before advancing"). Acceptable and correct.
- **Telemetry clarity.** `DecisionEvent.stepId == null` clearly marks defense-driven Engages distinct from CombatStep-driven Engages (which carry `cs.Id`). A unified mop-up via defense gives consistent telemetry.

**Why (a) over (c) "both coexist":** keeping `ResolveMopUp` alongside global defense is technically possible but defeats the user's intent ("rather than being 'clever'") — two systems for the same job is the cleverness they want to eliminate. Rejected.

---

### Q5 — Exit condition and timeout. **Pinned: NO defense timeout in v1. Defense exits naturally when `IsPlayerInCombat=false` OR no attacker in scan range.**

**Decision:** The defense helper returns `null` (cursor walk proceeds) when EITHER:

1. `IsPlayerInCombat` reads false, OR
2. `IsPlayerInCombat=true` but `DecideClearAggro` returns `Target=null` (no attacker in scan range; combat tag is stale or attacker is out of range).

No timeout. No `AwaitUser`. If the player is genuinely stuck in combat (e.g. attacker is geographically unreachable and the combat plugin can't kill them), the engine emits `Engage(null, attacker)` every tick — same forward decision shape as `Engage(combatStep, null)` today (verified `QuestEngine.cs:542` precedent calling Engage "a forward decision, never a stall").

**Why no timeout:**

- The pre-existing `MopUpTimeout = 15s` (`QuestEngine.cs:584`) led to `AwaitUser("could not leave combat after objective complete")`, which is hostile to the unattended-bot use case. The user explicitly wants a "global rule" — pausing for the user mid-combat contradicts that.
- 15s is too short for tough mobs (some lvl-15 quest enemies in 65847 take 20s+). Picking any timeout invites tuning hell.
- If a deeper bug exists (combat plugin disabled, attacker untargetable), the `Engage(null)` no-target spin is observable in trace and harmless. The user can override with manual intervention or `/qf stop`.

**What changes if v2 adds a timeout:** add `DefenseTimeout` and `_defenseStartedAt` on `CombatController`. The defense helper would track first-fire instant and emit `AwaitUser` after expiry. Not v1.

---

### Q6 — Engine vs host placement (consolidated rationale). **Pinned: engine, before the cursor walk in `ResolveAction`. See Q1.**

**Trade-offs walked:**

| Concern                               | Engine (Q1 (c))                                                                                              | Host (Q1 (b))                                                                                                  |
| ------------------------------------- | ------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| Where target selection lives          | `CombatController.DecideClearAggro` (already there)                                                          | Would need to duplicate or expose                                                                              |
| Fixture cascade risk                  | Gated on `_questHasAnyCombatStep` → ZERO impact on existing non-combat fixtures                              | Same gate possible, but host doesn't see `QuestDefinition` shape today; new plumbing needed                    |
| Recording proxy load                  | One `IsPlayerInCombat` + one `GetHostileActors` per tick (combat quests only)                                | Same, just at a different layer                                                                                |
| "Engine owns what action to emit"    | Preserved                                                                                                    | Violated — host would synthesize `Engage` actions                                                              |
| Trace anchoring (`DecisionEvent`)    | Natural: defense Engage flows through existing `Tick` decision write                                         | Awkward: host would need to fabricate or skip DecisionEvent                                                    |
| Test infrastructure                  | `EngineTestHarness` already dispatches Engage; no new mirror needed                                          | Host hook would need a `HarnessEngine` mirror                                                                  |

**Placement WITHIN `ResolveAction`:** the helper runs AFTER seq/NGP/UI/pos reads (so `playerPos` is available for any approach-related diagnostics), but BEFORE the resume-fragment processing and cursor walk. Concretely: insert at `QuestEngine.cs:432` (just after the sequence-change clear, before the resume-fragment processing at line 434).

**Why before resume-fragment processing:** if the player is in a resume-fragment situation AND being attacked, defense still wins — the user pinned "always defeat enemies that are attacking us before advancing to the next step" without exception. Resume fragments are a kind of step too.

---

### Q7 — Adapter changes? **Pinned: NONE. Engine-side filter via `KillPriority.SelectAttacker`.**

**Decision:** `IGameStateProvider.GetHostileActors(radius, ct)` (`QuestForge.Adapters/State/IGameStateProvider.cs:28`) already returns `HostileActor` records with `IsTargetingPlayer` and `OnPlayerEnmityList` fields (`QuestForge.Adapters/Types/HostileActor.cs:14-15`). `KillPriority.SelectAttacker` already filters and ranks. No new adapter surface.

**Why not a new `GetAttackingHostiles(ct)`:**

- It would be a one-line LINQ wrapper around `GetHostileActors`. Adding an interface method requires updating `IGameStateProvider`, `FakeGameStateProvider`, `RecordingGameStateProvider`, `ReplayGameStateProvider`, and the Dalamud-backed implementation — five files for zero behavior gain.
- The "what counts as attacking" definition lives in `KillPriority.SelectAttacker` (already documented). Centralising it twice (adapter + selector) is duplication.
- The existing trace replay scanner keys on adapter method names; introducing a new method silently invalidates older traces. Reusing `GetHostileActors` keeps the scanner stable.

---

### Q8 — Test surface. (Full GWT specs in §5.)

**Decision:**

- New test file: `QuestForge.Engine.Tests/Combat/GlobalDefenseTests.cs`.
- Reuses `EngineTestHarness` (already has `FakeGameStateProvider.SetInCombat`, `AddHostileActor`, `ClearHostileActors` — verified at `FakeGameStateProvider.cs:89-99` and `FakeGameStateProvider.cs:266-280`).
- **No new fake** required. The defense fixture builder is just `harness.GameState.SetInCombat(true); harness.GameState.AddHostileActor(...)` plus a non-combat quest (Travel, Talk, etc.) that contains AT LEAST ONE `CombatStep` somewhere in the sequence graph (to trigger `_questHasAnyCombatStep`).
- **`CombatClearAggroTests.cs` rewrites:** the tests that asserted mop-up-on-CombatStep behavior are repurposed as global-defense tests. The CA-1 / CA-2 / CA-3 outcomes carry over with two semantic shifts: (1) `DecisionEvent.stepId = null` instead of `cs.Id`, (2) no `AwaitUser` on timeout (timeout removed entirely).
- **Existing replay fixtures:** `Quest66130ReplayTests` skip-or-pass behavior is **unchanged**, because Quest 66130 does not contain a `CombatStep` (it is a turn-in chain), so `_questHasAnyCombatStep=false` and the defense helper short-circuits. Verified by reading the fixture-bearing quests in `questforge-data/fixtures/engine` — only `simple-linear-acceptance.json` and `with-attunement.json` exist, neither has combat.

---

## §4 Schema impact

**None.** Quest authors have no opt-in or opt-out. The defense rule is engine-internal. JSON schema, validator, and quest data files are unaffected.

The only engine-side type change is `EngineAction.Engage(CombatStep? Step, KillTarget? Target)` — internal to `QuestForge.Engine.dll`, not visible in `QuestForge.Schema`.

---

## §5 Test plan (D1–D8 GWT)

Test file: `QuestForge.Engine.Tests/Combat/GlobalDefenseTests.cs`.
All tests use `EngineTestHarness` with `FakeGameStateProvider`, `FakeCombat`, `FakeNavigator`.

**Helper assumed:** `BuildSingleStepQuestWithCombatPresent(stepKind, questId)` constructs a one-sequence quest with the requested step AND a dummy unreachable `CombatStep` later in the same sequence (so `_questHasAnyCombatStep=true` is triggered without the cursor ever reaching the combat step in the test).

### D1 — Player attacked mid-Travel → engine emits Engage instead of Navigate

**Given** a quest with a `TravelStep` (cursor here) and one downstream dummy `CombatStep`.
And `harness.GameState.SetInCombat(true)`.
And `harness.GameState.AddHostileActor(MakeHostile(id=42, dataId=999, distance=8, isTargetingPlayer=true))`.
And `harness.GameState.SetPlayerPosition((0,0,0))`.

**When** `harness.Engine.Tick(ct)` runs.

**Then** the returned action is `EngineAction.Engage`.
And `engage.Step` is `null` (defense, not CombatStep-anchored).
And `engage.Target` is non-null and `engage.Target.Value.Id == new ActorId(42)`.
And `harness.GameState.RecordedReads` contains exactly one `IsPlayerInCombat` read AND at least one `GetHostileActors` read on this tick.
And NO `Navigate` action was emitted.
And the trace `DecisionEvent.StepId` is `null`.

### D2 — Player attacked mid-Talk → engine emits Engage instead of Interact

**Given** a quest with a `TalkStep` (cursor here) and one downstream dummy `CombatStep`.
And `SetInCombat(true)`; `AddHostileActor(id=7, dataId=500, distance=6, isTargetingPlayer=true)`.

**When** `Tick`.

**Then** action is `Engage` with `Step=null`, `Target.Id == new ActorId(7)`.
And NO `Interact` action was emitted.

### D3 — Defense completes (in-combat=false) → engine resumes Travel

**Given** D1's quest and initial state. Tick 1 returns `Engage` per D1.
**When** the test sets `harness.GameState.SetInCombat(false)` AND `harness.GameState.ClearHostileActors()`, then ticks again.
**Then** action is `EngineAction.Navigate` (the original Travel target).
And on tick 2 `harness.Combat.RecordedTargets` contains a `ClearTarget` call (from `ApplyDecision`'s target-identity-change branch).

### D4 — Multiple attackers → engine picks per `SelectAttacker` tie-break

**Given** a quest with a `TalkStep` (cursor) and a downstream dummy `CombatStep`.
And `SetInCombat(true)`.
And THREE hostiles: `(id=10, dist=5, IsTargetingPlayer=false, OnPlayerEnmityList=true)`,
`(id=20, dist=8, IsTargetingPlayer=true, OnPlayerEnmityList=false)`,
`(id=30, dist=8, IsTargetingPlayer=true, OnPlayerEnmityList=true)`.

**When** `Tick`.

**Then** action is `Engage` with `Target.Id == new ActorId(30)` (highest score: 10+5=15, beats id=20 at 10 and id=10 at 5).

### D5 — Not in combat, no hostiles → defense is no-op

**Given** a quest with a `TravelStep` (cursor) and a downstream dummy `CombatStep`.
And `SetInCombat(false)`. No hostiles.

**When** `Tick`.

**Then** action is `EngineAction.Navigate` (cursor walk proceeds normally).
And `harness.GameState.RecordedReads` contains exactly ONE `IsPlayerInCombat` read.
And `harness.GameState.RecordedReads` contains ZERO `GetHostileActors` reads (defense short-circuits on `IsPlayerInCombat=false` before calling `DecideClearAggro`).

### D5a — In combat but no attacker in scan range → defense is no-op (forward to cursor)

**Given** a quest with a `TravelStep` (cursor) and a downstream dummy `CombatStep`.
And `SetInCombat(true)` but `AddHostileActor(id=99, dist=200, isTargetingPlayer=true)` (BEYOND `ScanRadius=50`).

**When** `Tick`.

**Then** action is `EngineAction.Navigate` (defense returns null because `DecideClearAggro` returns `Target=null` when no in-scan attacker).
And `harness.GameState.RecordedReads` contains one `IsPlayerInCombat` AND one `GetHostileActors` (the `DecideClearAggro` call ran but found nothing).

### D6 — Quest with NO CombatStep + player attacked → defense STILL fires (universal, no gate)

**Given** a one-step Travel quest with NO CombatStep anywhere.
And `SetInCombat(true)`; `AddHostileActor(id=42, dist=5, isTargetingPlayer=true)`.

**When** `Tick`.

**Then** action is `EngineAction.Engage` with `Step=null`, `Target.Id == new ActorId(42)`.
(Gate was dropped per user 2026-05-28 — defense is universal regardless of quest shape. The fixture-cascade trade-off is documented in §3 Q1 + §7.)
And `harness.GameState.RecordedReads` contains exactly one `IsPlayerInCombat` read AND one `GetHostileActors` read.

### D7 — Cursor on CombatStep with unsatisfied Expect + attacker NOT in kill set → defense wins, engages the attacker (not the kill-set member)

**Given** a quest whose cursor is on a `CombatStep` with `KillEnemyDataIds=[100]` and Expect unsatisfied.
And `SetInCombat(true)`.
And TWO hostiles in scan range: `(id=50, dataId=100, dist=20, isTargetingPlayer=false, OnPlayerEnmityList=false)` (kill-set, idle) and `(id=51, dataId=999, dist=10, isTargetingPlayer=true)` (attacker, off kill-set).

**When** `Tick`.

**Then** action is `Engage`.
And `engage.Step` is `null` (defense win — emitted BEFORE the CombatStep cursor branch ran).
And `engage.Target.Value.Id == new ActorId(51)` (attacker, NOT the kill-set member).

**Rationale:** the user pinned "we always defeat enemies that are attacking us before advancing." On this tick, "advancing" means "engaging the kill-set member"; defense pre-empts.

### D7a — Cursor on CombatStep + attacker IS the kill-set member → defense fires, target identical to what CombatStep would have selected

**Given** cursor on `CombatStep` with `KillEnemyDataIds=[100]`, Expect unsatisfied.
And `SetInCombat(true)`.
And ONE hostile: `(id=60, dataId=100, dist=10, isTargetingPlayer=true)`.

**When** `Tick`.

**Then** action is `Engage` with `Step=null`, `Target.Id == new ActorId(60)`.
(The cursor CombatStep branch did NOT execute this tick — defense pre-empted. Functionally identical target.)
And `harness.GameState.RecordedReads` contains exactly ONE `GetHostileActors` read (defense called it; the CombatStep branch never ran).

### D8 — CombatStep Expect satisfied + still in combat → defense engages, step does NOT re-mop-up

**Given** cursor on `CombatStep`, `Expect` satisfied (`var[0]=1`), `SetInCombat(true)`.
And `AddHostileActor(id=70, dataId=999, dist=5, isTargetingPlayer=true)` (an unrelated attacker post-objective).

**When** `Tick`.

**Then** action is `Engage` with `Step=null`, `Target.Id == new ActorId(70)`.
(This is the path that replaces `ResolveMopUp`. The CombatStep cursor branch is NEVER reached on this tick because defense pre-empts.)

**When** test sets `SetInCombat(false)` and `ClearHostileActors`, ticks again.

**Then** action is NOT `Engage`. The CombatStep cursor branch runs, `Expect` is satisfied, step confirms, `ClearTarget` is recorded. Cursor moves past the CombatStep.

**This test replaces the old mop-up race fix (Quest 65847 scenario):** since defense runs every tick uniformly, the brief `IsPlayerInCombat=false` window between mob death and respawn aggro no longer causes premature confirmation — on the NEXT tick when respawn aggros, defense fires again immediately. (Note: this specific race scenario should also have its own dedicated test as D9 below if time permits.)

### D9 (optional) — Race-window regression: brief IsPlayerInCombat=false → respawn aggro → defense re-fires

**Given** cursor on CombatStep, Expect satisfied. Tick 1: `SetInCombat(false)`, no hostiles. **Expected:** step confirms, action is `Wait("all steps satisfied")`.
**When** test ADVANCES to next step (sequence advance simulated), then before next tick sets `SetInCombat(true)` + adds attacker.
**Then** next tick fires `Engage` with `Step=null` BEFORE the next non-combat step's action.

This proves the global rule's correctness for the user's pinned scenario.

### D10 (optional) — `IsPlayerInCombat` adapter failure → defense fails-open, cursor walks normally

**Given** D1's setup, but `harness.GameState.SetIsPlayerInCombatFailure("adapter offline")` (verify FakeGameStateProvider has this setter; add if missing).
**When** `Tick`.
**Then** action is `EngineAction.Navigate` (cursor walk — defense returned null on read failure).

---

## §6 Sub-slicing

**Single PR.** The change set is tight (~6 files, all in one repo, no schema/data-repo coupling):

| File                                                                       | Change                                                                                          |
| -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| `QuestForge.Engine/EngineAction.cs`                                       | `Engage(CombatStep? Step, KillTarget? Target)` (nullable)                                       |
| `QuestForge.Engine/QuestEngine.cs`                                        | Add `_questHasAnyCombatStep`; latch in `StartQuest`; add `ResolveDefenseOrNull`; call from `ResolveAction`; delete `ResolveMopUp`, `MopUpTimeout`, post-confirm `ClearMopUpTimer` |
| `QuestForge.Engine/Combat/CombatController.cs`                            | Optional: remove `_mopUpStartedAt`, `StartOrElapsedMopUp`, `ClearMopUpTimer` (or leave as dead code for v1)                            |
| `QuestForge.Engine.Tests/Combat/GlobalDefenseTests.cs`                    | NEW — D1–D10 fixtures                                                                          |
| `QuestForge.Engine.Tests/Combat/CombatClearAggroTests.cs`                 | Rewrite mop-up-anchored assertions: `stepId=null` instead of `cs.Id`; remove timeout-AwaitUser tests; update `Engage.Step` expectations |
| (optional) `QuestForge.Adapters.Fakes/State/FakeGameStateProvider.cs`     | Add `SetIsPlayerInCombatFailure(reason, detail)` if missing (verify in tester pass)             |

**No questforge-data changes.** No schema changes. No validator changes. No fixture re-records (D6 asserts).

---

## §7 Risks + open questions

### Risks (accepted)

- **R1 — Fixture cascade (ACCEPTED, follow-up).** The 2 existing engine replay fixtures (`simple-linear-acceptance.trace.jsonl`, `with-attunement.trace.jsonl`) will throw `ReplayObservationStarvationException` once defense runs universally. The `WrapTickForStarvation` wrapper translates this into `Assert.Skip` with an actionable message — tests skip, build stays green, but the 2 fixtures lose their regression-coverage value until re-recorded. **Follow-up task:** run `simple-linear-acceptance` and `with-attunement` quests in-game with tracing on, then `qf-trace extract-fixture <run>.jsonl` for each. Not blocking this PR.
- **R2 — Defense-induced rotation thrash.** When defense fires repeatedly, `RotationLeaseLatch.OnAction` sees Engage every tick and remains in `_inLease=true` (line 35: "if already in lease, no-op"). No thrash. Verified against `RotationLeaseLatch.cs:31-47`.
- **R3 — Approach navigation hijack.** `CombatController.DecideClearAggro` issues `NavigateTo` toward the attacker inside `ApplyDecision`. This can override an in-flight cursor navigation (e.g. Navigate toward a quest NPC). **Acceptable per user intent.** We are "defending" — pausing the original navigation to handle combat is exactly the goal. When defense exits (D3), `ClearTarget` fires and the next cursor walk re-issues the original navigation.
- **R4 — Telemetry semantic shift.** `DecisionEvent.stepId` for defense Engages is `null` instead of the previously-confirmed CombatStep's ID. Documented in §5 D1. Trace consumers must tolerate null stepIds (they already do — `DecisionEvent.StepId` is `string?`).

### Open questions (defer or v2)

- **OQ1 — Defense timeout.** §3 Q5 pinned "none in v1." If field-testing shows pathological loops (unreachable attacker), v2 adds `DefenseTimeout` (suggest 30s default, configurable) and `AwaitUser` exit. Tracked as a follow-up issue, not a blocker.
- **OQ2 — Resume-fragment interaction.** Q6 placement runs defense BEFORE resume processing. If a resume fragment has its OWN sub-cursor on a step that would be defense-eligible, defense pre-empts the resume sub-cursor. v1 assumes this is correct (resume is a path-to-zone, not a combat sub-quest). If a resume fragment ever contains a CombatStep, the interaction is identical to D7/D8 — defense pre-empts. Document but no code change.
- **OQ3 — `CombatController` state cleanup on defense->cursor transitions.** When defense exits (target dies, returns null), should the controller eagerly reset `_currentTarget` and `_approachTarget`? `ApplyDecision` already issues `ClearTarget` and `Stop` when `target=null` (verified `CombatController.cs:158-163`). Should be sufficient; tester should add an assertion in D3 to pin.
- **OQ4 — Multiple defense Engages in a single tick.** Not possible — `ResolveDefenseOrNull` is called once per `ResolveAction`, returns at most one action.
- **OQ5 — Stale `_questHasAnyCombatStep` across `StartQuest` chains.** Latched per quest in `StartQuest`. Each new quest re-latches. NG+ replay: verified `StartQuest` runs for each quest in the chain. No staleness risk.

---

✅ READY FOR TEST CREATION

Tester: write failing tests from the GWT specs in §5.

- Happy paths: 4 scenarios (D3, D5, D5a, D6)
- Edge cases: 4 scenarios (D4, D7, D7a, D8)
- Error / regression: 2-3 scenarios (D1, D2, D9 optional, D10 optional)
- Expected total: ~10 tests in `QuestForge.Engine.Tests/Combat/GlobalDefenseTests.cs` + 4-6 modified tests in `CombatClearAggroTests.cs`.
