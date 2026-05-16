# Phase 11B Implementation Plan: Aetheryte & Aethernet Attunement

**Status:** ✅ COMPLETE — 224 engine tests passing; `DalamudGameStateProvider.IsAetheryteAttuned` is a documented TODO stub
**Input docs:** `docs/NEXT_STEPS.md` §Phase 11B, `docs/CLAUDE.md` (architectural invariants), `docs/SCHEMA.md` (step taxonomy), `docs/ADAPTERS.md` §`IGameStateProvider`, `docs/AUTHORING.md` (snapshot/inference)
**Output:** new step type `AttunementStep` (`"attune"` discriminator), new predicate `isAttuned(id)`, new authoring inference rule (Rule 2.5), engine handler dispatching to `Interact`, fake support, Dalamud-side stub.
**Predates:** Phase 11A (CLI wired), Phase 10 (Tools.Trace library), Phase 9 (authoring), Phase 4 (engine skeleton).

---

## 1. Overview & goals

Phase 11B adds aetheryte and aethernet shard attunement as a first-class engine concern. Both are mechanically identical from the engine's perspective: navigate to the crystal, interact, observe the attunement-state flip. Treating them as a distinct step type (vs. reusing `interact-object`) gives us:

- A **schema-level discriminator** for the trace extractor and `CapabilityInferrer` to surface `step:attune`.
- A **dedicated `skipIf: isAttuned(id)`** that makes the step a no-op on characters that have already attuned — critical for idempotent re-runs.
- A **distinct authoring-inference rule** that triggers off attunement-state change with no quest-state change.

### Architectural invariants (re-stated)

- `QuestForge.Engine` must not reference Dalamud. The new step handler reads attunement via `IGameStateProvider.IsAttuned`.
- The engine returns `EngineAction.Interact(NpcId)` for the actual attunement physical interaction — no new `EngineAction` variant is introduced. Aetherytes interact via the same `Interact` channel.
- All routine failures (NPC not present, interact failed) flow through `Result<T>`; only contract violations throw.

---

## 2. Existing state (read this before designing)

### 2.1 `AetheryteId` — already exists

`C:\Users\publi\RiderProjects\questforge\QuestForge.Adapters\Types\Identifiers.cs` line 6:

```csharp
public readonly record struct AetheryteId(uint Value);
```

**No new type needed.** Reuse this. (The spec stub for "define if missing" is satisfied — it exists.)

### 2.2 `IGameStateProvider` — already has attunement reads

`C:\Users\publi\RiderProjects\questforge\QuestForge.Adapters\State\IGameStateProvider.cs` lines 32-33:

```csharp
Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct);
Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct);
```

**Decision: do NOT rename to `IsAttuned`.** The interface method already exists as `IsAetheryteAttuned`. The Tester writes tests against the existing name. The predicate function (user-facing) remains `isAttuned(id)` to match the spec, but its implementation delegates to `IsAetheryteAttuned`. This avoids an interface-renaming churn for already-committed callers.

### 2.3 `FakeGameStateProvider` — already supports attunement

Existing setter (lines 91-98 of `FakeGameStateProvider.cs`):

```csharp
public void SetAetheryteAttuned(AetheryteId id, bool attuned) { ... }
```

Existing `IsAetheryteAttuned` implementation reads from `_attunedAetherytes` HashSet. **The fake is already complete for read/write.** The spec's `SetAttuned`/`IsAttuned` map onto the existing `SetAetheryteAttuned`/`IsAetheryteAttuned`. No new fake methods.

### 2.4 `FunctionRegistry` lives in `questforge-tools`

`C:\Users\publi\RiderProjects\questforge-tools\QuestForge.Predicates\FunctionRegistry.cs` is the canonical signature registry consumed by the validator and (transitively, via `PredicateParser`) by the engine. Phase 11B must add `isAttuned` here. The arity is `Fixed(1)`, the parameter is `Int`, the return is `Bool`.

### 2.5 `PredicateEvaluator` has a switch, not a registry

`C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Predicates\PredicateEvaluator.cs` lines 99-109: the function dispatch is a `switch` keyed on the function name. Phase 11B adds an arm:

```csharp
"isAttuned" => (await _gameState.IsAetheryteAttuned(new AetheryteId((uint)(long)args[0]), ct)).ValueOrThrow,
```

### 2.6 `QuestEngine.ResolveActionForStep` is the dispatch site

`QuestEngine.cs` lines 178-195 contain the `switch` expression that maps `Step` → `EngineAction`. Phase 11B adds an arm for `AttunementStep`. The engine never sees a separate `Attune` action — it returns `Interact` (and trusts the Dalamud-side `IInteractor` to handle aetherytes via the same path it handles NPCs; aetherytes are addressable by `BaseId` like any other game object).

### 2.7 Authoring layer already routes change events

`SnapshotAggregator` has `OnZoneChanged`, `OnQuestSequenceChanged`, `OnInteraction`, etc. (lines 45-95). Phase 11B adds `OnAttunementChanged(AetheryteId)` following the same pattern.

`GameStateSnapshot` (lines 5-18 of `GameStateSnapshot.cs`) is a positional record. Phase 11B adds `AetheryteId? LastAttuned` as a new positional field at the end (after `InventoryHash`) — required to preserve binary compatibility of existing positional constructor calls.

`StepInferenceEngine.Infer` (lines 11-131) is an ordered cascade of rules. Phase 11B inserts a new rule *between* current Rule 2 (QuestAccepted) and current Rule 3 (QuestSequence advanced). It is numbered **Rule 2.5** in this spec and in code comments; existing rules retain their numbering.

`InferredFrom` enum (lines 3-13) gains a new value `AttunementChange`.

---

## 3. Components — testable specifications

Each subsection is one component with: **Purpose**, **Interface**, **Behaviors** (Given-When-Then), and **Design rationale**.

---

### 3.1 `AttunementStep` (schema)

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Schema\Step.cs`

**Purpose:** declarative representation of an "attune to an aetheryte/aethernet shard" quest step. Identified by JSON discriminator `"attune"`.

**Interface:**

```csharp
public class AttunementStep : Step
{
    public AetheryteId Target { get; init; }       // required; the crystal to attune to
    public NpcLocation? Location { get; init; }    // optional explicit world position
    // Inherits from Step: Id, Zone, Expect, SkipIf, StopDistance, Recover, Retry, Preconditions, Notes
}
```

**Registration:** add to the `[JsonDerivedType]` list at the top of `Step.cs`:

```csharp
[JsonDerivedType(typeof(AttunementStep), "attune")]
```

**Note on `AetheryteId` import:** `Step.cs` is in `QuestForge.Schema`. `AetheryteId` is in `QuestForge.Adapters.Types`. Adding an `Adapters → Schema` reference would be wrong-way (Schema is a leaf). **Decision:** define a thin schema-side alias in `QuestForge.Schema.SharedValueTypes.cs`:

```csharp
public readonly record struct AetheryteId(uint Value);
```

This shadows the Adapters version but lives at the schema-leaf layer, avoiding a layering inversion. The engine, when it dispatches the step, converts `schema.AetheryteId` → `adapters.AetheryteId` via `.Value`. Two records, one field, trivial round-trip. (If the Tester or Builder dislikes the duplication, an alternative is to make `Identifiers.cs` move into `QuestForge.Schema` and have `Adapters.Types` re-export — out of scope for 11B unless the Builder hits friction.)

**Behaviors:**

- **B1 (happy path — round-trip):** Given a JSON document `{"type":"attune","id":"attune-thal","target":{"value":53},"location":{"npcId":2147491840,"zone":130,"position":{"x":-15.5,"y":4,"z":-7.2}}}`, When deserialised through the source-generated `JsonSerializerContext`, Then the result is `AttunementStep` with `Target == new AetheryteId(53)`, `Location.NpcId == 2147491840`, `Location.Zone == 130`.
- **B2 (location omitted):** Given JSON without a `location` field, When deserialised, Then `Location == null`.
- **B3 (skipIf preserved):** Given JSON with `"skipIf":{"predicate":"isAttuned(53)"}`, When deserialised, Then `SkipIf` is a `PredicateExpect` whose `Predicate == "isAttuned(53)"`.
- **B4 (target required):** Given JSON missing `target`, When deserialised, Then `Target` is `default` (i.e. `AetheryteId(0)`) — structural validation is the validator's job (Phase 1 territory), not the schema's. *No exception expected here.*

**Design rationale:** mirrors the `AcceptStep`/`TurnInStep` pattern (single primary target, no `Targets` array). `Location` is optional because Phase 11B explicitly defers Lumina-based position lookup; when present, navigation runs; when absent, the engine skips navigation. The validator (separate phase) will eventually warn if `Location` is null *and* no in-zone aetheryte preceded the step, but Phase 11B does not enforce this.

**Expected tests (Schema):** 4 (round-trip + 3 variants). Add to `QuestForge.Schema.Tests/RoundTripTests.cs` and/or `IntegrationTests.cs`.

---

### 3.2 `isAttuned` predicate registration

**File:** `C:\Users\publi\RiderProjects\questforge-tools\QuestForge.Predicates\FunctionRegistry.cs`

**Purpose:** declare the function's signature so the parser/validator accepts it and so `CapabilityInferrer` can detect `predicate:isAttuned`.

**Interface change:** add one entry to the `s_functions` array:

```csharp
new("isAttuned", new Fixed(1), [Int], Bool),
```

**Behaviors:**

- **B1 (registry returns signature):** Given `FunctionRegistry.TryGet("isAttuned", out var sig)`, Then `sig.Name == "isAttuned"`, `sig.Arity` is `Fixed(1)`, `sig.ParameterTypes` is `[Int]`, `sig.ReturnType` is `Bool`.
- **B2 (parser accepts):** Given the input string `"isAttuned(53)"`, When `PredicateParser.Parse` is called, Then `IsSuccess == true` and `Ast` is a `FunctionCall("isAttuned", [IntLiteral(53)])`.
- **B3 (parser rejects wrong arity):** Given `"isAttuned()"` or `"isAttuned(53, 99)"`, When parsed and type-checked, Then a structured error reports arity mismatch.
- **B4 (suggest-similar):** Given the typo `"isAtuned"`, `FunctionRegistry.SuggestSimilar("isAtuned")` includes `"isAttuned"`.

**Design rationale:** the registry is the single source of truth shared by the validator and `CapabilityInferrer`. Adding the entry once unlocks both.

**Expected tests (Predicates):** 3-4. Add to `QuestForge.Predicates.Tests/FunctionRegistryTests.cs`.

---

### 3.3 `isAttuned` predicate evaluation

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Predicates\PredicateEvaluator.cs`

**Purpose:** evaluate the `isAttuned(id)` predicate against the live game state at runtime.

**Interface change:** add one switch arm in `EvaluateFunction` (around line 100):

```csharp
"isAttuned" => (await _gameState.IsAetheryteAttuned(new AetheryteId((uint)(long)args[0]), ct)).ValueOrThrow,
```

**Behaviors:**

- **B1 (not attuned):** Given a `FakeGameStateProvider` with no `SetAetheryteAttuned` call for ID 1000, When `evaluator.Evaluate(parse("isAttuned(1000)"))` is called, Then result is `false`.
- **B2 (attuned):** Given `gameState.SetAetheryteAttuned(new AetheryteId(1000), true)`, When `evaluator.Evaluate(parse("isAttuned(1000)"))`, Then result is `true`.
- **B3 (false → true transition observable via re-read):** Given B1's state, When `SetAetheryteAttuned(1000, true)` then re-evaluate, Then second result is `true`. (Validates no internal caching.)
- **B4 (composed in `not`):** Given `isAttuned(1000) == false` (game state), When evaluator parses and evaluates `"not isAttuned(1000)"`, Then result is `true`.
- **B5 (composed in `and` for `skipIf`):** Given a quest sequence at 0 and `isAttuned(1000) == false`, When `"questSequence(66130) >= 0 and isAttuned(1000)"` is evaluated, Then result is `false`.
- **B6 (error — adapter failure):** Given a `FakeGameStateProvider` configured to return `Result.Fail` from `IsAetheryteAttuned` (Tester must extend the fake with a failure script — see §3.4), When the predicate evaluates, Then `ValueOrThrow` raises (per existing `Result<T>` contract). Wrapping behaviour: `PredicateEvaluator` does not catch — propagates. Document that the engine treats this as a `Wait` upstream, but that's `QuestEngine`'s problem, not the evaluator's.

**Design rationale:** delegates entirely to the adapter. No caching at the evaluator level — the existing `ExpectEvaluator` AST cache caches *parse trees*, not *values*, which is correct.

**Expected tests (Predicates):** 5. Add to `QuestForge.Engine.Tests/Predicates/PredicateEvaluatorTests.cs` (no new file needed).

---

### 3.4 `FakeGameStateProvider` — no new code required, but Tester verification

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Adapters.Fakes\State\FakeGameStateProvider.cs`

**Purpose:** verify that the existing `SetAetheryteAttuned` / `IsAetheryteAttuned` pair behaves correctly for Phase 11B's use cases. The fake is already implemented (§2.3).

**Interface (already exists, restated):**

```csharp
public void SetAetheryteAttuned(AetheryteId id, bool attuned);
public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct);
```

**Behaviors:**

- **B1 (default false):** Given a fresh `FakeGameStateProvider`, When `IsAetheryteAttuned(new AetheryteId(99), ct)` is awaited, Then the result is `Result.Ok(false)`.
- **B2 (set true round-trips):** Given `fake.SetAetheryteAttuned(new AetheryteId(42), true)`, When `IsAetheryteAttuned(new AetheryteId(42), ct)` is awaited, Then the result is `Result.Ok(true)`.
- **B3 (set false after true):** Given `SetAetheryteAttuned(42, true)` then `SetAetheryteAttuned(42, false)`, When read, Then result is `Result.Ok(false)`.
- **B4 (independent IDs):** Given `SetAetheryteAttuned(42, true)`, When reading `IsAetheryteAttuned(43)`, Then result is `Result.Ok(false)`.
- **B5 (`GetAttunedAetherytes` consistency):** Given `SetAetheryteAttuned(1, true)` and `SetAetheryteAttuned(2, true)`, When `GetAttunedAetherytes(ct)`, Then result contains both IDs (order undefined; assert as set).

**Design rationale:** these tests are *fixtures of the fake itself*, not of the engine. They guarantee that engine tests downstream can rely on the fake's contract. If the spec said "add `SetAttuned`" — that method already exists under the name `SetAetheryteAttuned`. The Tester names test methods after the spec's `SetAttuned` for readability but invokes the existing API.

**Optional extension (only if Tester needs B6 of §3.3 to be testable):** add a configurable failure-script to `FakeGameStateProvider`:

```csharp
public void SetAetheryteAttunedFail(string reason, string? detail = null);
public void ClearAetheryteAttunedFail();
```

Mirroring the existing `SetCurrentJobFail` pattern (line 49 of the fake). Optional; only add if test B6 of §3.3 is in-scope.

**Expected tests (Fakes):** 4 (B1-B4 mandatory, B5 optional). Add to a new `QuestForge.Adapters.Tests/State/FakeGameStateProviderAttunementTests.cs` or extend existing fake tests.

---

### 3.5 `InferredFrom.AttunementChange`

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Authoring\InferredFrom.cs`

**Purpose:** label authoring-inference results that fired off attunement-state change.

**Interface change:** add one enum value:

```csharp
public enum InferredFrom
{
    ZoneChange,
    QuestFlagChange,
    QuestSequenceChange,
    DialogueInteraction,
    QuestAccepted,
    QuestCompleted,
    AttunementChange,   // NEW
    Manual,
    None
}
```

**Behaviors:**

- **B1 (enum value exists):** `Enum.IsDefined(typeof(InferredFrom), InferredFrom.AttunementChange) == true`.
- **B2 (distinct):** `(int)InferredFrom.AttunementChange != (int)InferredFrom.QuestAccepted` and not equal to any other existing value.

**Design rationale:** placed before `Manual`/`None` (which read as terminal/sentinel values), after the change-driven values. Ordering doesn't affect serialisation (enums in JSON drafts use names — see `DraftSerializationTests`).

**Expected tests (Authoring):** 0 dedicated (covered transitively by §3.7 Rule-2.5 tests).

---

### 3.6 `GameStateSnapshot.LastAttuned` + `SnapshotAggregator.OnAttunementChanged`

**Files:**
- `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Authoring\GameStateSnapshot.cs`
- `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Authoring\SnapshotAggregator.cs`

**Purpose:** carry the most-recently-observed attunement-state change so the inference engine can detect it.

**Interface changes:**

```csharp
// GameStateSnapshot.cs — append a new positional field
public sealed record GameStateSnapshot(
    DateTimeOffset CapturedAt,
    ZoneId Zone,
    WorldPosition Position,
    QuestId? ActiveQuest,
    int QuestSequence,
    uint QuestFlags,
    bool QuestAccepted,
    bool QuestCompleted,
    NpcId? LastNpcInteracted,
    WorldPosition? LastNpcPosition,
    string? LastDialoguePrompt,
    string? LastDialogueAnswer,
    uint InventoryHash,
    AetheryteId? LastAttuned);   // NEW — appended last to minimise call-site churn
```

```csharp
// SnapshotAggregator.cs — add a backing field and method
private AetheryteId? _lastAttuned;

public void OnAttunementChanged(AetheryteId aetheryte)
{
    _lastAttuned = aetheryte;
}
```

The `Current` property must include the new field. **Important migration note for the Tester:** every existing `BaseSnapshot` helper in `StepInferenceEngineTests` (and any other call site that constructs `GameStateSnapshot` positionally) gains a trailing `lastAttuned: null` default. The Tester should update `BaseSnapshot` to accept an optional `AetheryteId? lastAttuned = null` parameter and pass it through.

**Behaviors:**

- **B1 (snapshot default):** Given a fresh `SnapshotAggregator(activeQuest)`, When `.Current` is read, Then `LastAttuned == null`.
- **B2 (after OnAttunementChanged):** Given `aggregator.OnAttunementChanged(new AetheryteId(500))`, When `.Current` is read, Then `LastAttuned == new AetheryteId(500)`.
- **B3 (overwrite):** Given `OnAttunementChanged(AetheryteId(500))` then `OnAttunementChanged(AetheryteId(501))`, When `.Current`, Then `LastAttuned == new AetheryteId(501)`. (Aggregator tracks *most recent* — semantics match `LastNpcInteracted`.)
- **B4 (record positional equality):** Given two `GameStateSnapshot` instances with all 14 fields equal including `LastAttuned == new AetheryteId(7)`, Then `record` equality returns `true`.
- **B5 (no implicit aetheryte-id zero meaning "unset"):** `AetheryteId(0)` is a legal value; only `null` means unset. Test: `new AetheryteId(0) != null` distinction in the snapshot is preserved.

**Design rationale:** `AetheryteId?` (nullable) rather than `AetheryteId` because the absence of any attunement event is a real state — distinct from `AetheryteId(0)` (which is technically a valid game ID). Matches `ActiveQuest`/`LastNpcInteracted` patterns.

**Expected tests (Authoring):** 4 in `SnapshotAggregatorTests`; 1 in `GameStateSnapshotTests` (B4). Add to existing files.

---

### 3.7 `StepInferenceEngine` Rule 2.5

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Authoring\StepInferenceEngine.cs`

**Purpose:** when the only observable change between two snapshots is an attunement, infer an `AttunementStep` draft.

**Interface change:** add a new rule block *between* current Rule 2 (line ~40, "QuestAccepted") and current Rule 3 (line ~42, "QuestSequence advanced"):

```csharp
// Rule 2.5: Attunement changed (no quest delta took priority above)
if (after.LastAttuned != before.LastAttuned && after.LastAttuned.HasValue)
{
    var aetheryteId = after.LastAttuned.Value.Value;
    return new InferenceResult(
        StepType: "attune",
        SuggestedStepId: $"attune-aetheryte-{aetheryteId}",
        SuggestedExpect: $"isAttuned({aetheryteId})",
        Confidence: Confidence.High,
        InferredFrom: InferredFrom.AttunementChange,
        Notes: null);
}
```

**Why insert between Rule 2 and Rule 3, not after Rule 7?** Aetheryte interactions also flip quest sequence in some narratives (an aetheryte unlock can be a story beat). Without priority over Rule 3 (`questSequence advanced`), an attunement that *also* advances the quest sequence would be misclassified as `"talk"`. Rule 2.5 catches the attunement signal first because it is more specific. If both fire, the more specific (attunement) wins. Note: Rule 1 (QuestCompleted) and Rule 2 (QuestAccepted) remain higher priority because completing/accepting a quest is a stronger signal than the side effect of an attunement.

**Behaviors:**

- **B1 (happy path):** Given `before = BaseSnapshot()` (LastAttuned null), `after = BaseSnapshot(lastAttuned: new AetheryteId(1000))`, When `engine.Infer(before, after)`, Then:
  - `result.StepType == "attune"`
  - `result.SuggestedStepId == "attune-aetheryte-1000"`
  - `result.SuggestedExpect == "isAttuned(1000)"`
  - `result.Confidence == Confidence.High`
  - `result.InferredFrom == InferredFrom.AttunementChange`
  - `result.Notes == null`
- **B2 (no change → falls through):** Given `before.LastAttuned == new AetheryteId(1000)` and `after.LastAttuned == new AetheryteId(1000)` (no other deltas), When `Infer`, Then result is `InferenceResult.Empty` (Rule 8 fall-through). Rule 2.5 does not fire because `before == after`.
- **B3 (attunement value changes — fires):** Given `before.LastAttuned == new AetheryteId(500)`, `after.LastAttuned == new AetheryteId(1000)`, When `Infer`, Then `result.SuggestedExpect == "isAttuned(1000)"`. (The rule emits the *new* attunement.)
- **B4 (attunement disappears — does not fire):** Given `before.LastAttuned == new AetheryteId(500)`, `after.LastAttuned == null`, When `Infer`, Then Rule 2.5 does **not** fire (`after.LastAttuned.HasValue == false`). Falls through; if other deltas exist, lower-priority rules apply.
- **B5 (priority over Rule 3 QuestSequence):** Given attunement change *and* `after.QuestSequence > before.QuestSequence`, When `Infer`, Then `result.StepType == "attune"` (Rule 2.5 fires first).
- **B6 (priority over Rule 4 ZoneChange):** Given attunement change *and* zone change, When `Infer`, Then `result.StepType == "attune"`.
- **B7 (Rule 1 still wins — QuestCompleted):** Given `after.QuestCompleted == true && !before.QuestCompleted` *and* attunement change, When `Infer`, Then `result.StepType == "turn-in"` (Rule 1 first).
- **B8 (Rule 2 still wins — QuestAccepted):** Given `after.QuestAccepted == true && !before.QuestAccepted` *and* attunement change, When `Infer`, Then `result.StepType == "accept"` (Rule 2 first).

**Design rationale:** rule priority reflects observation-signal specificity. Attunement is more specific than quest-sequence-advance because the latter has dozens of triggers; the former has exactly one. Quest completion and acceptance remain higher because they are terminal/initial transitions of the quest itself.

**Expected tests (Authoring):** 8. Add to `QuestForge.Engine.Tests/Authoring/StepInferenceEngineTests.cs` as new `[Fact]` methods named `InferAttunement_*`.

---

### 3.8 `QuestEngine` — `AttunementStep` dispatch

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\QuestEngine.cs`

**Purpose:** translate an `AttunementStep` into the appropriate `EngineAction` at each tick.

**Interface change:** add arms to `ResolveActionForStep` (lines 178-195). The step is dispatched by the existing per-tick loop in `ResolveAction` — the engine already handles `Expect` (postcondition) and `SkipIf` (pre-skip) evaluation generically (lines 168-171 of `QuestEngine.cs`). **No new logic is needed for skipIf** — the existing per-step skipIf check in `ResolveAction` already short-circuits when `step.SkipIf` evaluates true. Phase 11B only needs the *action-resolution* branch:

```csharp
// Inside the existing ResolveActionForStep switch
AttunementStep attune when attune.Location is { } loc =>
    EngineActionForAttunement(attune, loc),

AttunementStep attune when attune.Location is null =>
    new EngineAction.Interact(new NpcId(attune.Target.Value)),

// new helper:
private static EngineAction EngineActionForAttunement(AttunementStep step, NpcLocation loc)
{
    // Phase 11B: if a Location is given, the engine first navigates there, then on the
    // next tick (when player is near the location) the step's Expect evaluates against
    // the aetheryte's NPC ID. The dispatch is the same as TravelStep + TalkStep composed:
    // first emit Navigate; on subsequent ticks the postcondition check in ResolveAction
    // (via step.Expect) determines completion.
    //
    // Since the engine has no per-step internal state, the trigger for "navigate vs interact"
    // is the same playerNear test that step.Expect must encode. RECOMMENDATION: AttunementStep
    // emits Interact directly; navigation must be authored as a separate preceding TravelStep
    // if needed. This keeps the engine-side semantics identical for "Location null" and
    // "Location present" — Location is metadata for inference/validation only in Phase 11B.
    //
    // (See "Open question" below — Tester must confirm direction.)
    return new EngineAction.Interact(new NpcId(step.Target.Value));
}
```

**Open question for design review (resolve before Tester writes tests):**

There are two possible engine semantics for `AttunementStep` when `Location` is non-null:

- **Option A (recommended):** `AttunementStep` *always* returns `Interact`, never `Navigate`. `Location` is metadata-only — used by the validator and by future scheduler pre-flight checks but never by the runtime engine. Authors who need navigation prepend a `TravelStep`. This is simpler, mirrors the way `TalkStep` works (talk does not navigate; a preceding `travel` step is expected), and matches Phase 11B's "out of scope: Lumina position lookup" boundary.
- **Option B:** `AttunementStep` returns `Navigate(loc.Position)` until `playerNear`, then `Interact`. Internal two-state behaviour. Requires `step.Expect` or an internal proximity check.

**Decision: Option A.** The Phase 11B contract is "navigate (if needed) via a preceding `TravelStep`, then attune". This keeps the engine handler trivial and consistent. The spec section under "Out of scope" already says "When `Location` is null, the engine skips navigation (assumes player is already close). Authors must provide `Location` explicitly in Phase 11B if navigation is needed." — interpret this as "Location is for authoring/inference; runtime navigation is via preceding TravelStep". This decision is consistent with Phase 4's `TalkStep` shape (talk also never navigates).

**Behaviors (under Option A):**

- **B1 (skipIf already-attuned):** Given a quest with one `AttunementStep` where `SkipIf == PredicateExpect("isAttuned(1000)")`, and `FakeGameStateProvider.SetAetheryteAttuned(1000, true)`, and quest sequence matches the step's sequence block, When `Tick`, Then the engine **does not** emit `Interact` for this step — it advances past the step. (Likely returns `Wait`/`Done` depending on whether subsequent steps exist or sequence postcondition is satisfied.)
- **B2 (emits Interact when not attuned):** Given the same setup with `SetAetheryteAttuned(1000, false)`, When `Tick`, Then `EngineAction.Interact(new NpcId(1000))` is returned. (Target's `AetheryteId.Value` is reused as NPC ID — Lumina lookup is out of scope.)
- **B3 (completes after postcondition):** Given B2's setup, after `Interact` is dispatched the test simulates the in-game effect by calling `SetAetheryteAttuned(1000, true)`. Now if the step's `Expect == PredicateExpect("isAttuned(1000)")`, When the next `Tick` runs, Then the step's `Expect` evaluates `true` and the step is considered complete. The engine advances past it (returns the *next* step's action, or `Wait` / `Done` if none).
- **B4 (postcondition unmet — retries):** Given B2's setup, after `Interact` is dispatched but `SetAetheryteAttuned` is *not* called (in-game state unchanged), When `Tick` is called again, Then `EngineAction.Interact(new NpcId(1000))` is returned **again** (standard retry — the step's `Expect` is still false).
- **B5 (Location ignored at runtime — Option A):** Given an `AttunementStep` with `Location.NpcId == 999999`, When `Tick`, Then the emitted `Interact` carries `NpcId(Target.Value)`, **not** `NpcId(Location.NpcId)`. (Verifies that `Location` is metadata only.)
- **B6 (skipIf without explicit Expect):** Given an `AttunementStep` with `SkipIf == "isAttuned(1000)"` and `Expect == null`, with `SetAetheryteAttuned(1000, false)`, When `Tick`, Then `Interact(NpcId(1000))` is returned. *Without `Expect`, the engine has no postcondition*, so the next `Tick` after a simulated `SetAetheryteAttuned(1000, true)` will *still* fall through to `Interact` unless `SkipIf` saves it on the next pass. Test: confirm that subsequent `SetAetheryteAttuned(1000, true)` + `Tick` causes the step's `SkipIf` to fire and the step is skipped. (Validates skipIf double-duty as postcondition.)
- **B7 (Recover/Retry — out of scope for 11B):** standard retry counters and recovery ladder behaviour is delegated to the engine's existing failure-counter logic, not Phase 11B's concern. The Tester does not write Phase 11B-specific recover tests; existing `RecoveryTests` patterns cover the generic case.

**Design rationale:** by reusing `EngineAction.Interact` and the existing `Expect`/`SkipIf` per-step machinery, `AttunementStep` adds zero new engine state. The only new code is one switch arm. Postcondition discipline (re-read state after action) is already enforced by `ResolveAction`'s loop — Phase 11B inherits it.

**Expected tests (Engine):** 6 (B1-B6). Add to a new file `QuestForge.Engine.Tests/Engine/AttunementStepTests.cs`. Use the existing `EngineTestHarness` from `Engine.Tests/Helpers`.

---

### 3.9 `DalamudGameStateProvider.IsAetheryteAttuned` — real implementation (stub upgrade)

**File:** `C:\Users\publi\RiderProjects\questforge\QuestForge.Adapters.Dalamud\State\DalamudGameStateProvider.cs`

**Purpose:** replace the Phase 6 placeholder (line 248-250, always returns `false`) with a real ClientStructs read.

**Interface (unchanged):**

```csharp
public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct);
```

**Implementation sketch:**

```csharp
public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
{
    // ClientStructs API: FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState
    // The exact accessor name varies by SDK version. Candidates to verify (Builder
    // confirms which exists in the current ClientStructs build):
    //   PlayerState.Instance()->IsAetheryteRegistered(uint aetheryteId)  -> byte (0/1)
    //   PlayerState.Instance()->IsAetheryteUnlocked(ushort aetheryteId)  -> bool
    //
    // If neither is present, fall back to scanning PlayerState->UnlockedAetherytes
    // (a bitfield in some versions).
    //
    // Casting note: AetheryteId is uint internally; ClientStructs expects ushort
    // for some accessors. Cast safely with checked arithmetic; emit Result.Fail
    // if value > ushort.MaxValue.
    unsafe
    {
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (ps is null)
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("noPlayerState", "PlayerState.Instance() returned null"));

        if (aetheryte.Value > ushort.MaxValue)
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("aetheryteIdOutOfRange",
                $"AetheryteId {aetheryte.Value} exceeds ushort.MaxValue"));

        var attuned = ps->IsAetheryteRegistered((ushort)aetheryte.Value) != 0;
        return Task.FromResult<Result<bool>>(Result.Ok(attuned));
    }
}
```

**Builder must verify** the actual ClientStructs method name against `FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState` in the current SDK. If the method name is uncertain, the Builder leaves the **Phase 6 stub in place and adds a TODO** with a link to the relevant ClientStructs source line — this is acceptable for Phase 11B because the engine-side logic is the actual deliverable, and the Dalamud stub merely unblocks runtime smoke testing.

**Behaviors:** not unit-testable (Dalamud-bound). Manual verification only:

- **M1 (manual smoke):** plugin loads in Dalamud; `/qf debug aetheryte 8` (new ad-hoc command, optional) prints `attuned: true` if the character is attuned to New Gridania (aetheryte 2 in the game data, ID may differ by table).
- **M2 (predicate works in-game):** a test quest with `skipIf: isAttuned(2)` is skipped on a character that has attuned to New Gridania.

**Expected tests:** 0 unit tests. Manual smoke documented in the PR description.

---

### 3.10 `CapabilityInferrer` — `step:attune` emission

**File:** `C:\Users\publi\RiderProjects\questforge-tools\QuestForge.Tools.Trace\Capabilities\CapabilityInferrer.cs`

**Purpose:** emit the capability tag `step:attune` when a quest's step list contains `AttunementStep`.

**Interface change:** add one entry to `StepCapabilities` (around line 34):

```csharp
[typeof(AttunementStep)] = "step:attune",
```

**Behaviors:**

- **B1 (single attune step):** Given a `QuestDefinition` with one `AttunementStep`, When `CapabilityInferrer.Infer(quest)` is called, Then the result includes `"step:attune"`.
- **B2 (with isAttuned predicate):** Given a quest where the step has `SkipIf == PredicateExpect("isAttuned(53)")`, Then the result also includes `"predicate:isAttuned"`.
- **B3 (deduplication):** Given two `AttunementStep`s in the same quest, Then `"step:attune"` appears exactly once.
- **B4 (sorting):** Given a quest with `TravelStep`, `AttunementStep`, `TalkStep`, Then the result list is sorted (`StringComparer.Ordinal`) and `"step:attune"` appears in alphabetical order among `step:talk`, `step:travel`.

**Design rationale:** parallels every other step-type entry. `CapabilityInferrer` is the single point of truth for "which engine capabilities does this quest exercise"; failing to register `AttunementStep` would silently drop attunement quests from fixture coverage analysis.

**Expected tests (Tools.Trace):** 3-4. Add to `QuestForge.Tools.Trace.Tests/CapabilityInferrerTests.cs`.

---

## 4. Implementation order (for the Builder)

1. **`AetheryteId` (schema-side) + `AttunementStep` schema type** — §3.1 — must compile before everything else. Run schema round-trip tests (B1-B4 of §3.1).
2. **`FunctionRegistry` entry for `isAttuned`** — §3.2 — separate repo (`questforge-tools`); push first, bump submodule.
3. **`PredicateEvaluator` switch arm for `isAttuned`** — §3.3 — depends on (2). Run predicate tests B1-B5.
4. **`FakeGameStateProvider` confirmation tests** — §3.4 — no code change unless adding failure-script extension; just lock in the fake's behaviour.
5. **`InferredFrom.AttunementChange`** — §3.5 — trivial enum extension.
6. **`GameStateSnapshot.LastAttuned` + `SnapshotAggregator.OnAttunementChanged`** — §3.6 — depends on (5). Update all `BaseSnapshot` helpers in tests. Run snapshot tests B1-B5.
7. **`StepInferenceEngine` Rule 2.5** — §3.7 — depends on (5) and (6). Run inference tests B1-B8.
8. **`QuestEngine` `AttunementStep` dispatch** — §3.8 — depends on (1) and (3). Run engine tests B1-B6.
9. **`DalamudGameStateProvider.IsAetheryteAttuned` real implementation** — §3.9 — Dalamud-side, no unit tests; smoke-only.
10. **`CapabilityInferrer` `step:attune` entry** — §3.10 — separate repo (`questforge-tools`). Run capability tests B1-B4.

---

## 5. Out of scope (re-stated for clarity)

- **Lumina lookup of aetheryte world positions** — when `AttunementStep.Location` is null and a preceding `TravelStep` is absent, the engine emits `Interact` immediately. Future phase will add Lumina-driven position resolution.
- **Scheduler pre-flight attunement checks** — the scheduler does not yet inspect `step:attune` capabilities to verify the character can complete a quest chain. Future phase.
- **`SnapshotAggregator` Dalamud-side polling for attunement deltas** — Phase 11B wires the *engine-side* `OnAttunementChanged`, but the Dalamud plugin's snapshot-polling loop that *calls* `OnAttunementChanged` when `IsAetheryteAttuned` flips is a Phase 11B follow-up once the real ClientStructs read in §3.9 is verified. The hook point is in the Dalamud-side authoring/recording code path, not the engine.
- **`qf-trace extract-quest` end-to-end attunement recovery** — works mechanically once §3.6 + §3.7 are in place, but no canonical attunement-trace fixture is committed in 11B.
- **Multiple attunement targets per step / `Targets` array** — single `Target` only.

---

## 6. Test summary

| Section | Component | Happy paths | Edge cases | Error cases | Subtotal |
|---|---|---:|---:|---:|---:|
| 3.1 | `AttunementStep` schema | 1 (B1) | 2 (B2, B3) | 1 (B4) | 4 |
| 3.2 | `FunctionRegistry` entry | 1 (B1) | 2 (B2, B4) | 1 (B3) | 4 |
| 3.3 | `isAttuned` predicate | 2 (B1, B2) | 3 (B3, B4, B5) | 1 (B6, optional) | 5-6 |
| 3.4 | `FakeGameStateProvider` | 2 (B1, B2) | 3 (B3, B4, B5) | 0 | 5 |
| 3.5 | `InferredFrom` enum | — (covered in 3.7) | — | — | 0 |
| 3.6 | `GameStateSnapshot` + `SnapshotAggregator` | 2 (B1, B2) | 3 (B3, B4, B5) | 0 | 5 |
| 3.7 | `StepInferenceEngine` Rule 2.5 | 1 (B1) | 3 (B2, B3, B4) | 0 | 8 (B1-B8: priority interactions) |
| 3.8 | `QuestEngine` dispatch | 2 (B2, B3) | 4 (B1, B4, B5, B6) | 0 (recovery deferred) | 6 |
| 3.9 | `DalamudGameStateProvider` | 0 (manual smoke only) | 0 | 0 | 0 |
| 3.10 | `CapabilityInferrer` | 1 (B1) | 3 (B2, B3, B4) | 0 | 4 |
| **Total** | | **12** | **23** | **3** | **~41** |

---

## 7. Files modified

### `questforge` repo

- `QuestForge.Schema/SharedValueTypes.cs` — add `AetheryteId` record struct
- `QuestForge.Schema/Step.cs` — add `[JsonDerivedType(typeof(AttunementStep), "attune")]` + `AttunementStep` class
- `QuestForge.Engine/Predicates/PredicateEvaluator.cs` — add `isAttuned` switch arm
- `QuestForge.Engine/Authoring/InferredFrom.cs` — add `AttunementChange`
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` — add `LastAttuned` field
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` — add `_lastAttuned` field + `OnAttunementChanged` method + `Current` include
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` — insert Rule 2.5
- `QuestForge.Engine/QuestEngine.cs` — add `AttunementStep` arm in `ResolveActionForStep`
- `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs` — real `IsAetheryteAttuned` (or update TODO)

### `questforge-tools` repo

- `QuestForge.Predicates/FunctionRegistry.cs` — add `isAttuned` entry
- `QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs` — add `AttunementStep → "step:attune"`

### Test files

- `QuestForge.Schema.Tests/RoundTripTests.cs` (or new `AttunementStepTests.cs`) — 4 schema tests
- `QuestForge.Predicates.Tests/FunctionRegistryTests.cs` — 4 registry tests
- `QuestForge.Engine.Tests/Predicates/PredicateEvaluatorTests.cs` — 5-6 predicate tests
- `QuestForge.Adapters.Tests/State/FakeGameStateProviderAttunementTests.cs` (new) — 5 fake tests
- `QuestForge.Engine.Tests/Authoring/SnapshotAggregatorTests.cs` — 4-5 aggregator tests
- `QuestForge.Engine.Tests/Authoring/StepInferenceEngineTests.cs` — 8 inference tests (B1-B8)
- `QuestForge.Engine.Tests/Engine/AttunementStepTests.cs` (new) — 6 engine tests
- `QuestForge.Tools.Trace.Tests/CapabilityInferrerTests.cs` — 4 capability tests

---

## 8. Integration points & mock locations

- **Engine ↔ Game state:** `IGameStateProvider.IsAetheryteAttuned` — mocked by `FakeGameStateProvider` in all engine tests.
- **Engine ↔ Predicate parser:** `PredicateParser.Parse(string)` from `questforge-tools` submodule — real parser, no mock.
- **Engine ↔ Quest state:** `IQuestState` — `FakeQuestState`, unchanged.
- **Engine ↔ Trace:** `ITraceWriter` — `NullTraceWriter` or fake; trace events from `AttunementStep` runs follow the existing `DecisionEvent`/`RunStartEvent`/`RunEndEvent` shapes — no new event types in Phase 11B.
- **Authoring ↔ Snapshot:** `SnapshotAggregator` is in-memory and synchronous; tests construct it directly with `new SnapshotAggregator(activeQuest, clock)`.
- **Authoring ↔ Inference:** `StepInferenceEngine` is pure (no dependencies); tests instantiate directly.

---

## 9. Acceptance criteria (Phase 11B done when)

1. All ~41 tests above pass.
2. The validator (`qf-validate`) accepts a quest file containing an `AttunementStep` with `target.value` set, `location` optional, `skipIf: isAttuned(...)` optional.
3. `CapabilityInferrer.Infer` on such a quest returns a list including `"step:attune"` and (if a relevant predicate is used) `"predicate:isAttuned"`.
4. The engine, run against fakes with an `AttunementStep` quest, emits `EngineAction.Interact` when the aetheryte is not attuned, and skips the step when `SetAetheryteAttuned(target, true)` is in effect (via `skipIf`).
5. `StepInferenceEngine.Infer`, given a before/after snapshot pair whose only delta is `LastAttuned`, returns an `attune` step with `Confidence.High` and `InferredFrom.AttunementChange`.
6. Dalamud-side `IsAetheryteAttuned` either reads the real ClientStructs `PlayerState` or carries a documented TODO with a smoke-test plan.

---

## ✅ READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.

- Happy paths: 12 scenarios
- Edge cases: 23 scenarios
- Error cases: 3 scenarios
- Expected total: ~41 tests

Distribution by test project:

- `QuestForge.Schema.Tests`: ~4 tests
- `QuestForge.Predicates.Tests` (in `questforge-tools`): ~4 tests
- `QuestForge.Engine.Tests/Predicates`: ~5-6 tests
- `QuestForge.Adapters.Tests/State`: ~5 tests
- `QuestForge.Engine.Tests/Authoring` (`SnapshotAggregatorTests` + `StepInferenceEngineTests`): ~13 tests
- `QuestForge.Engine.Tests/Engine` (new `AttunementStepTests.cs`): ~6 tests
- `QuestForge.Tools.Trace.Tests` (in `questforge-tools`): ~4 tests

The Builder works through §4's ordered implementation list, running tests after each step.
