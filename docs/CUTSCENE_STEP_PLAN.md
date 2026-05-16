# CutsceneStep — Testable Specification

**Status:** READY FOR TEST CREATION
**Phase:** Engine step expansion (post Phase 6 / pre-Phase 7)
**Position in TDD workflow:** Architect (you are here) → Tester → Builder → Reviewer

---

## 0. Scope

Add a `cutscene` step type that:

1. Is recognized by the schema layer (already wired — see §1)
2. Causes the engine to emit `Wait` while a cutscene is on screen
3. Allows the step to complete once cutscene flags clear and an optional `Expect` predicate holds

Cutscene **skip confirmation** (the Escape-press + `SelectString` callback) is already implemented at the plugin layer (`EngineHost.TryCutsceneSkipConfirm` + `EnableCutsceneSkip`). The engine itself does not press buttons; it only decides when to `Wait` vs. advance. This keeps the engine pure (no Dalamud dependency) per the architectural invariant.

---

## 1. Pre-existing state (audit)

A directory walk of the repo confirms the following are **already in place**:

| Item | Location | Status |
| --- | --- | --- |
| `CutsceneStep` class | `QuestForge.Schema/Step.cs` line 99 | EXISTS — has `Skip { get; init; } = "ifAllowed"` field |
| `[JsonDerivedType(typeof(CutsceneStep), "cutscene")]` | `Step.cs` line 20 | EXISTS |
| `[JsonSerializable(typeof(CutsceneStep))]` | `QuestForgeJsonContext.cs` line 18 | EXISTS |
| Round-trip test `CutsceneStep_RoundTrips` | `QuestForge.Schema.Tests/RoundTripTests.cs` line 179 | EXISTS |
| `UiState.CutscenePlaying` | `QuestForge.Adapters/State/IGameStateProvider.cs` line 99 | EXISTS — single bool, no skippable/non-skippable split |
| `FakeGameStateProvider.SetUiState(UiState)` | `QuestForge.Adapters.Fakes/State/FakeGameStateProvider.cs` line 58 | EXISTS |
| `CapabilityInferrer` entry `[typeof(CutsceneStep)] = "step:cutscene"` | `questforge-tools/QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs` line 21 | **EXISTS — no work needed** |
| `EngineHost.TryCutsceneSkipConfirm` | `QuestForge.Plugin/EngineHost.cs` line 338 | EXISTS — already called in `Interact`/`Wait` dispatch arms |
| `EngineHost.EnableCutsceneSkip` / `RestoreCutsceneSkip` | `EngineHost.cs` lines 348, 366 | EXISTS — toggled by `BeginRun`/`EndRun` |

**What is missing:** the `QuestEngine.ResolveActionForStep` dispatch arm for `CutsceneStep`. Today it falls through to `throw new NotSupportedException(...)` (line 196).

**What is *not* needed:** a separate "is cutscene skippable" flag on `UiState`. The plugin layer already differentiates via Dalamud `ConditionFlag` and presses Escape/confirm only when appropriate. The engine treats both identically — emit `Wait` whenever a cutscene is on screen. This keeps the engine simple, the contract minimal, and the existing skip plumbing untouched.

---

## 2. Schema contract — `CutsceneStep`

**Decision:** Keep the existing `Skip` field (already round-trip tested). Treat it as authoring metadata that the engine does not consult in this phase. Future work may toggle skip behavior per-step; for now the plugin-level skip-all setting (`EnableCutsceneSkip` → game config `Maximum`) covers all cases.

### Final field set

```csharp
public class CutsceneStep : Step
{
    /// <summary>
    /// "never" | "ifAllowed". Authoring hint; runtime ignores this in the current phase
    /// (plugin-level skip-all setting in EngineHost.EnableCutsceneSkip handles all cutscenes
    /// uniformly). Future phases may consult Skip == "never" to bypass the skip confirm press.
    /// </summary>
    public string Skip { get; init; } = "ifAllowed";

    // Inherited from Step:
    //   Id, Zone, Expect, SkipIf, StopDistance, Recover, Retry, Preconditions, Notes
}
```

**Inherited fields used by engine:**

| Field | Role |
| --- | --- |
| `Expect: ExpectValue?` | Postcondition. Typically `questSequence(<id>) >= N`. Step completes once cutscene flag clears AND `Expect` evaluates true (or `Expect == null`). |
| `SkipIf: ExpectValue?` | Pre-skip predicate evaluated by the standard per-step loop before dispatch. |
| `Id` | Step identifier for trace/decision events. |

**Fields explicitly NOT on `CutsceneStep`:**

- `Target` — cutscenes have no NPC interaction target
- `Destination` — no movement
- `StopDistance` — meaningless

---

## 3. Engine handler

### 3.1 Interface read

`IGameStateProvider.GetUiState(CancellationToken ct) → Result<UiState>`

The engine reads the entire `UiState` record. The field that drives the decision is:

```csharp
public record UiState(
    bool DialogueOpen,
    bool CutscenePlaying,   // <-- this one
    bool LoadingZone,
    bool YesNoPromptOpen,
    bool SelectStringOpen,
    bool RewardSelectionOpen,
    bool ShopOpen,
    bool DeathPromptOpen,
    DialogueState? CurrentDialogue
);
```

`CutscenePlaying == true` whenever **either** Dalamud `ConditionFlag.OccupiedInCutSceneEvent` (skippable) **or** `ConditionFlag.WatchingCutscene78` (non-skippable) is active. The concrete `DalamudGameStateProvider` must OR these two flags into `CutscenePlaying`. (If the existing implementation only checks one, that is a Builder bug to fix as part of this work — verify before claiming GREEN.)

### 3.2 Dispatch arm — to be added to `QuestEngine.ResolveActionForStep`

`ResolveActionForStep` is currently synchronous (`Step → EngineAction`). Reading `UiState` is async (`Task<Result<UiState>>`). Two options:

**Option A (preferred — keep `ResolveActionForStep` sync):** Read `UiState` once at the top of `ResolveAction` (the async wrapper) and pass it down. Refactor `ResolveActionForStep` signature to `ResolveActionForStep(Step step, UiState ui)`. Lightweight — one extra read per tick.

**Option B:** Make `ResolveActionForStep` async. Larger refactor; touches every existing arm.

**Pick Option A.** Rationale: lower blast radius, every step type would eventually benefit from `UiState` access (Talk, TurnIn, etc. already need `DialogueOpen` checks in later phases), and a single read at the top of `ResolveAction` reflects what really happens in production.

### 3.3 Decision table

Evaluate **after** the standard per-step loop has already checked `step.Expect` (line 168 of current `QuestEngine.cs`) and `step.SkipIf` (line 170). Both checks naturally apply before dispatch; the dispatch arm only runs when both are false (or both are null).

| `CutscenePlaying` | `Expect` is null? | `Expect` evaluates true? | Action emitted by dispatch arm |
| --- | --- | --- | --- |
| true | n/a | n/a | `EngineAction.Wait("cutscene playing")` |
| false | yes | n/a | `EngineAction.Wait("cutscene ended; awaiting sequence advance")` |
| false | no | true | (handled by per-step loop — step is skipped via `continue` before dispatch runs) |
| false | no | false | `EngineAction.Wait("cutscene ended; awaiting sequence advance")` |

Key observation: the dispatch arm itself **never emits `Done`, `Navigate`, or `Interact`**. It only emits `Wait`. Step completion happens implicitly when the per-step loop on a later tick observes `Expect` true and `continue`s past this step. This mirrors how `AttunementStep` completion works (see `AttunementStepTests` B3).

### 3.4 Patch sketch for `QuestEngine.cs`

```csharp
private async Task<(EngineAction action, string? stepId)> ResolveAction(CancellationToken ct)
{
    // ... existing seq/complete checks ...

    // NEW: read UiState once per tick for step dispatch.
    // On failure: use a safe default (CutscenePlaying=false) so non-cutscene steps are unaffected.
    // CutsceneStep will emit Wait("awaiting sequence advance") — recoverable on next tick.
    var uiResult = await _gameState.GetUiState(ct);
    var ui = uiResult is Result<UiState>.Success { Value: var v } ? v
        : new UiState(false, false, false, false, false, false, false, false, null);

    // ... existing per-step loop unchanged, but call ResolveActionForStep(step, ui) ...
}

private EngineAction ResolveActionForStep(Step step, UiState ui) => step switch
{
    // ... existing arms ...

    CutsceneStep => ui.CutscenePlaying
        ? new EngineAction.Wait("cutscene playing")
        : new EngineAction.Wait("cutscene ended; awaiting sequence advance"),

    _ => throw new NotSupportedException($"Phase 4 does not support step type {step.GetType().Name}")
};
```

### 3.5 Why `Wait` (not a new `EngineAction`)

`EngineAction.Wait` already conveys: "engine is satisfied; no adapter call needed; tick again soon." `EngineHost.DispatchAction`'s `Wait` arm already calls `TryCutsceneSkipConfirm()` (line 234), which handles the skippable case. No new `EngineAction` type is required.

---

## 4. Adapter / Fake changes

### 4.1 `IGameStateProvider`

**No change required.** `UiState.CutscenePlaying` already exists.

### 4.2 `FakeGameStateProvider`

**No change required.** `SetUiState(UiState)` already exists. Tests construct UI state like:

```csharp
harness.GameState.SetUiState(new UiState(
    DialogueOpen: false,
    CutscenePlaying: true,
    LoadingZone: false,
    YesNoPromptOpen: false,
    SelectStringOpen: false,
    RewardSelectionOpen: false,
    ShopOpen: false,
    DeathPromptOpen: false,
    CurrentDialogue: null));
```

A small helper on the harness or a local factory method per test file would reduce boilerplate, but is optional.

### 4.3 `DalamudGameStateProvider` (fix required)

The current implementation (line 289) uses:
```csharp
_svc.Condition[ConditionFlag.WatchingCutscene] || _svc.Condition[ConditionFlag.WatchingCutscene78]
```

But `EngineHost.TryCutsceneSkipConfirm` uses `ConditionFlag.OccupiedInCutSceneEvent` for the skippable case, which is a different flag. The Builder must update `CutscenePlaying` to OR all three:

```csharp
CutscenePlaying = _svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]   // skippable
               || _svc.Condition[ConditionFlag.WatchingCutscene]           // existing
               || _svc.Condition[ConditionFlag.WatchingCutscene78]         // non-skippable
```

This ensures skippable cutscenes also set `CutscenePlaying = true` at the engine boundary.

### 4.4 `CapabilityInferrer` (questforge-tools)

**No change required.** `[typeof(CutsceneStep)] = "step:cutscene"` is already on line 21 of `CapabilityInferrer.cs`. The corresponding test (T8 below) should still be authored to lock the behavior.

---

## 5. Behaviors (Given-When-Then)

All engine tests live in `QuestForge.Engine.Tests/Engine/CutsceneStepTests.cs`, modeled on `AttunementStepTests.cs`. All schema tests live in `QuestForge.Schema.Tests/RoundTripTests.cs` (already partially covered). All capability tests live in `questforge-tools/QuestForge.Tools.Trace.Tests/CapabilityInferrerTests.cs`.

### B1 — Wait while a skippable cutscene plays

- **Given** a quest with one `CutsceneStep` (Id `"watch-intro"`, `Expect = questSequence(12345) >= 1`)
  AND `UiState.CutscenePlaying == true`
  AND `questSequence(12345) == 0`
- **When** `Engine.Tick`
- **Then** `EngineAction.Wait` is returned

(The "skippable" framing is purely the production scenario. To the engine, both cutscene types look identical via `CutscenePlaying`.)

### B2 — Wait while a non-skippable cutscene plays

- **Given** the same quest as B1 but framed as a non-skippable cutscene (still `CutscenePlaying == true`)
- **When** `Engine.Tick`
- **Then** `EngineAction.Wait` is returned

(B1 and B2 are structurally identical assertions; they exist as separate tests to document the contract intent: the engine does not distinguish.)

### B3 — Step completes when cutscene ends AND `Expect` is satisfied

- **Given** a quest with one `CutsceneStep` (`Expect = questSequence(12345) >= 1`)
  AND `UiState.CutscenePlaying == false`
  AND `questSequence(12345) == 1` (advanced by the game after the cutscene)
- **When** `Engine.Tick`
- **Then** the per-step loop short-circuits via `step.Expect` evaluating true (the dispatch arm never runs); `EngineAction.Wait` is returned because no further steps exist in the sequence (matches `AttunementStepTests` B3 pattern)

### B4 — Cutscene ended but `Expect` not yet satisfied — keep waiting

- **Given** a quest with one `CutsceneStep` (`Expect = questSequence(12345) >= 1`)
  AND `UiState.CutscenePlaying == false`
  AND `questSequence(12345) == 0` (game has not yet advanced sequence)
- **When** `Engine.Tick`
- **Then** `EngineAction.Wait` is returned (dispatch arm hits the second `Wait` branch)

This is the "brief gap" case between the cutscene addon closing and the quest journal updating.

### B5 — No `Expect`: completes as soon as cutscene ends

- **Given** a quest with one `CutsceneStep` where `Expect == null` and `SkipIf == null`
  AND `UiState.CutscenePlaying == false`
- **When** `Engine.Tick`
- **Then** `EngineAction.Wait` is returned (dispatch arm hits the "cutscene ended; awaiting sequence advance" branch)
- AND no exception is thrown

Note: with no `Expect`, the step does not self-complete in the per-step loop — it stays "current" until the sequence advances or a later tick passes. This is acceptable behavior; cutscene steps are normally paired with an `Expect` predicate in real quests. The test documents that the engine does not throw or hang.

### B6 — `SkipIf` skips before cutscene starts

- **Given** a quest with one `CutsceneStep` where `SkipIf = questSequence(12345) >= 1`
  AND `questSequence(12345) == 1` (cutscene has already been watched in a prior session)
  AND `UiState.CutscenePlaying == false`
- **When** `Engine.Tick`
- **Then** the per-step loop skips the step via `SkipIf`; `EngineAction.Wait` is returned (no more steps; mirrors `AttunementStepTests` B1 pattern)
- AND `GetUiState` may or may not have been called (we do not assert on it — the optimization is incidental)

### B7 — Schema round-trip

- **Given** a `CutsceneStep` instance with `Id`, `Skip`, and `Expect` populated
- **When** serialized via `QuestForgeJsonContext.QuestFileOptions` and deserialized back
- **Then** all three fields round-trip identically AND the discriminator `"type": "cutscene"` is present in the JSON

This test already exists at `RoundTripTests.cs:179` and should be left as-is (no action needed). A new assertion is added: serialized JSON contains the literal string `"type":"cutscene"` (verifies the discriminator). If preferred, add as a new `[Fact]` so the existing test remains untouched.

### B8 — `CapabilityInferrer` emits `step:cutscene`

- **Given** a `QuestDefinition` containing one sequence with one `CutsceneStep`
- **When** `CapabilityInferrer.Infer(quest)` is called
- **Then** the returned list contains `"step:cutscene"` (and `"predicate:questSequence"` if the step has an `Expect`)

Test location: `questforge-tools/QuestForge.Tools.Trace.Tests/CapabilityInferrerTests.cs` (add new fact or extend existing parameterized test if one exists).

---

## 6. Edge / error cases

| Case | Expected behavior | Covered by |
| --- | --- | --- |
| `GetUiState` returns `Result<UiState>.Failure` | Engine returns `AwaitUser($"adapter failure reading UI state: {reason}")` | Optional B9 — recommended |
| `Skip == "never"` | Currently no engine-side effect (runtime ignores `Skip`). Round-trip test guarantees the value survives. | B7 |
| `CutsceneStep` with `Target` or other unsupported fields in JSON | Deserialization ignores unknown fields per STJ defaults; no special handling needed | Existing schema tests |

### Recommended additional test (B9) — adapter failure path

- **Given** the harness `FakeGameStateProvider` is scripted to fail `GetUiState`
- **When** `Engine.Tick` runs on a sequence whose current step is `CutsceneStep`
- **Then** `EngineAction.AwaitUser` is returned with reason starting with `"adapter failure reading UI state"`

This requires adding a `SetUiStateFail(string reason, string? detail = null)` setter to `FakeGameStateProvider` mirroring `SetCurrentJobFail`. **Optional for first pass.** If skipped now, file a tracking note.

---

## 7. Test count summary

| Bucket | Count | Tests |
| --- | --- | --- |
| Happy path | 3 | B1, B2, B5 |
| Step lifecycle | 2 | B3, B4 |
| Skip semantics | 1 | B6 |
| Schema | 1 | B7 (new discriminator assertion; existing round-trip retained) |
| Tooling | 1 | B8 |
| **Subtotal (core)** | **8** | |
| Optional | 1 | B9 (adapter failure) |
| **Total** | **8–9** | |

---

## 8. Implementation order for the Builder

1. **Verify `DalamudGameStateProvider.CutscenePlaying` ORs both `ConditionFlag.OccupiedInCutSceneEvent` and `ConditionFlag.WatchingCutscene78`.** Fix if not. (Spot-check, not a test — production-only path.)
2. **Refactor `QuestEngine.ResolveActionForStep` signature** to accept `UiState ui` (Option A in §3.2). Add a `GetUiState` read at the top of `ResolveAction` and pass `ui` to all dispatch calls. Existing arms ignore `ui`; their tests continue to pass unchanged.
3. **Add the `CutsceneStep` arm** per §3.4.
4. **Run `CutsceneStepTests`** — RED → GREEN.
5. **(No work needed)** `CapabilityInferrer` already has the entry; B8 just locks it.
6. **(No work needed)** `CutsceneStep` schema class and JSON context registration already exist.

Estimated build effort after tests are written: 30–60 minutes for an experienced contributor (mostly the signature refactor and verifying no existing test regresses).

---

## 9. Files touched

| Path | Change |
| --- | --- |
| `QuestForge.Engine/QuestEngine.cs` | Read `UiState` in `ResolveAction`; thread to `ResolveActionForStep`; add `CutsceneStep` arm |
| `QuestForge.Engine.Tests/Engine/CutsceneStepTests.cs` | NEW — tests B1–B6 (and B9 if included) |
| `QuestForge.Schema.Tests/RoundTripTests.cs` | OPTIONAL — extend with discriminator assertion (B7); existing test remains |
| `questforge-tools/QuestForge.Tools.Trace.Tests/CapabilityInferrerTests.cs` | Add or extend test for B8 |
| `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs` | Verify (and fix if needed) the `CutscenePlaying` OR-expression |

Files explicitly NOT touched:

- `QuestForge.Schema/Step.cs` (class already complete)
- `QuestForge.Schema/QuestForgeJsonContext.cs` (registration already complete)
- `QuestForge.Adapters/State/IGameStateProvider.cs` (`UiState.CutscenePlaying` already exists)
- `QuestForge.Adapters.Fakes/State/FakeGameStateProvider.cs` (unless B9 is included — then add `SetUiStateFail`)
- `QuestForge.Plugin/EngineHost.cs` (cutscene skip plumbing already wired)
- `questforge-tools/QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs` (entry already present)

---

## READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.

- Happy paths: **3** scenarios (B1, B2, B5)
- Edge cases: **3** scenarios (B3, B4, B6)
- Error cases: **0** required, **1** optional (B9)
- Cross-cutting (schema + tooling): **2** scenarios (B7, B8)
- Expected total: **~8 tests** (9 if B9 included)
