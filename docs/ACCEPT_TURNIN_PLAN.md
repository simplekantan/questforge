# AcceptStep & TurnInStep — Testable Specification

**Status:** READY FOR TEST CREATION
**Phase:** Engine step expansion (post AttunementStep / CutsceneStep)
**Position in TDD workflow:** Architect (you are here) → Tester → Builder → Reviewer

---

## 0. Scope

Add engine dispatch handlers for two step types whose schema, JSON registration, and
plugin-side wiring are already complete:

1. `AcceptStep` — engage an NPC who offers a quest; emit `Interact(NpcId)`.
2. `TurnInStep` — engage an NPC who completes a quest; emit `Interact(NpcId)`.

The plugin's `EngineHost.DispatchAction` already invokes the full quest-lifecycle button
chain on every `Interact` action (lines 220–228):

```csharp
case EngineAction.Interact i:
    await _interactor.InteractWith(i.Target, ct);
    await _interactor.AdvanceDialogue(ct);
    await _interactor.AcceptQuest(_currentQuestId, ct);     // no-op if JournalAccept addon absent
    await _interactor.CompleteQuest(_currentQuestId, ct);   // no-op if JournalResult addon absent
    break;
```

Because the plugin layer fires `AcceptQuest` and `CompleteQuest` on every `Interact`,
the engine **does not need separate `EngineAction` variants** for accept/turn-in. It only
needs to emit `Interact(NpcId(step.Target.NpcId))` at the right time. The Dalamud
button-click guards (`!addonPtr.IsReady`) make the spurious calls safe.

Reward selection orchestration is **out of scope for this phase** (see §7).

---

## 1. Pre-existing state (audit)

A walk through the repo confirms the following are **already in place**:

| Item | Location | Status |
| --- | --- | --- |
| `AcceptStep` class | `QuestForge.Schema\Step.cs:74-77` | EXISTS — fields: `Target: NpcLocation` |
| `TurnInStep` class | `QuestForge.Schema\Step.cs:79-83` | EXISTS — fields: `Target: NpcLocation`, `DialogueChoices: DialogueChoice[]` |
| `[JsonDerivedType(typeof(AcceptStep), "accept")]` | `Step.cs:16` | EXISTS |
| `[JsonDerivedType(typeof(TurnInStep), "turn-in")]` | `Step.cs:17` | EXISTS |
| `[JsonSerializable(typeof(AcceptStep))]` | `QuestForgeJsonContext.cs:14` | EXISTS |
| `[JsonSerializable(typeof(TurnInStep))]` | `QuestForgeJsonContext.cs:15` | EXISTS |
| Round-trip test `AcceptStep_RoundTrips` | `QuestForge.Schema.Tests\RoundTripTests.cs:104-115` | EXISTS |
| Round-trip test `TurnInStep_RoundTrips` | `QuestForge.Schema.Tests\RoundTripTests.cs:118-129` | EXISTS |
| `CapabilityInferrer` entry `[typeof(AcceptStep)] = "step:accept"` | `questforge-tools\QuestForge.Tools.Trace\Capabilities\CapabilityInferrer.cs:17` | EXISTS — no work needed |
| `CapabilityInferrer` entry `[typeof(TurnInStep)] = "step:turn-in"` | `CapabilityInferrer.cs:18` | EXISTS — no work needed |
| `IInteractor.AcceptQuest` | `QuestForge.Adapters\Interaction\IInteractor.cs:29` | EXISTS |
| `IInteractor.CompleteQuest` | `IInteractor.cs:30` | EXISTS |
| `DalamudInteractor.AcceptQuest` | `QuestForge.Adapters.Dalamud\Interaction\DalamudInteractor.cs:120-123` | EXISTS — clicks `JournalAccept` button 44 |
| `DalamudInteractor.CompleteQuest` | `DalamudInteractor.cs:125-128` | EXISTS — clicks `JournalResult` button 37 |
| `FakeInteractor.AcceptQuest` | `QuestForge.Adapters.Fakes\Interaction\FakeInteractor.cs:150-158` | EXISTS — sets `QuestStatus.Accepted`, adds to accepted list, fires callback |
| `FakeInteractor.CompleteQuest` | `FakeInteractor.cs:160-167` | EXISTS — sets `QuestStatus.Complete`, fires callback |
| Plugin `DispatchAction` Interact arm | `QuestForge.Plugin\EngineHost.cs:220-228` | EXISTS — calls `AcceptQuest` + `CompleteQuest` after every `InteractWith` |
| `StepInferenceEngine` rules for `accept` and `turn-in` | `QuestForge.Engine\Authoring\StepInferenceEngine.cs:17-39` | EXISTS — already emits `StepType: "turn-in"` / `"accept"` |
| `DraftValidator` `GetNpcId` arm | `QuestForge.Engine\Authoring\DraftValidator.cs:194-197` | EXISTS — knows how to extract NpcId from AcceptStep/TurnInStep |

**What is missing:** the `QuestEngine.ResolveActionForStep` dispatch arms for `AcceptStep`
and `TurnInStep`. Today they fall through to `throw new NotSupportedException(...)`
(`QuestEngine.cs:209`).

**What is *not* needed:**
- A new `EngineAction` variant for "accept" or "complete" — the existing `Interact`
  variant is sufficient because the plugin layer fires the appropriate addon button
  chain on every `Interact` dispatch.
- A change to `IInteractor` — `AcceptQuest`, `CompleteQuest`, `SelectQuestReward` all
  exist (some are Phase 6 stubs, but the engine does not call them directly).
- A change to `FakeInteractor` — accept/complete state transitions are already wired.
- Any work on `CapabilityInferrer` — both tags already exist.

---

## 2. Schema contract — `AcceptStep`

### Final field set (unchanged from `Step.cs:74-77`)

```csharp
public class AcceptStep : Step
{
    /// <summary>The quest-giver NPC. Required.</summary>
    public NpcLocation Target { get; init; } = default!;

    // Inherited from Step:
    //   Id, Zone, Expect, SkipIf, StopDistance, Recover, Retry, Preconditions, Notes
}
```

**`NpcLocation`** (`SharedValueTypes.cs:14`): `record NpcLocation(uint NpcId, int Zone, Position3 Position)`

### Expected authoring usage

```jsonc
{
  "type": "accept",
  "id": "accept-coming-to-uldah",
  "target": { "npcId": 1003987, "zone": 182, "position": { "x": 35.56, "y": 4, "z": -151.18 } },
  "skipIf":  { "predicate": "isQuestAccepted(66130)" },
  "expect":  { "predicate": "isQuestAccepted(66130)" }
}
```

**Convention:** `SkipIf` and `Expect` typically use the same predicate
(`isQuestAccepted(<id>)`). The standard per-step loop checks `Expect` first and then
`SkipIf`; either firing causes the step to be skipped via `continue`. Authoring guides
should recommend setting both for clarity. The engine does not require either, but
without `Expect` the step can only complete when the game sequence advances past it.

---

## 3. Schema contract — `TurnInStep`

### Final field set (unchanged from `Step.cs:79-83`)

```csharp
public class TurnInStep : Step
{
    /// <summary>The quest-acceptor NPC. Required.</summary>
    public NpcLocation Target { get; init; } = default!;

    /// <summary>
    /// Optional dialogue choices to make during turn-in (e.g. yes/no on a reward prompt).
    /// Engine ignores this field in the current phase (see §7); included for forward compatibility
    /// and authoring tooling. Future work: feed into IInteractor.SelectDialogueOption*.
    /// </summary>
    public DialogueChoice[] DialogueChoices { get; init; } = [];

    // Inherited from Step:
    //   Id, Zone, Expect, SkipIf, StopDistance, Recover, Retry, Preconditions, Notes
}
```

### Expected authoring usage

```jsonc
{
  "type": "turn-in",
  "id": "turn-in-coming-to-uldah",
  "target": { "npcId": 1000327, "zone": 130, "position": { "x": 21.84, "y": 7, "z": -81.13 } },
  "skipIf":  { "predicate": "isQuestComplete(66130)" },
  "expect":  { "predicate": "isQuestComplete(66130)" },
  "dialogueChoices": []
}
```

---

## 4. Navigation sub-problem — design decision

**Decision: Option A (Interact-only).** Both `AcceptStep` and `TurnInStep` ALWAYS emit
`Interact`. Navigation is the responsibility of a preceding authored `TravelStep`. This
mirrors the established `TalkStep` and `AttunementStep` patterns.

### Why Option A

1. **Consistency.** `TalkStep` (`QuestEngine.cs:197-198`) and `AttunementStep`
   (`QuestEngine.cs:203`) both emit `Interact` unconditionally. A behavioural divergence
   here would be surprising and inconsistent with the rest of the dispatch table.
2. **Authored navigation is explicit.** Looking at the canonical fixture
   `Quest66130FlowTests`, every NPC interaction is preceded by a `TravelStep` whose
   `Expect` is `playerNear(<position>, <radius>)`. The travel step blocks until the
   player is in range. The talk/accept/turn-in step then fires `Interact`.
3. **Simpler dispatch arm.** A two-line `switch` arm with no state. The proximity check
   already exists as a first-class predicate (`playerNear`) consumable by `Expect`.
4. **Plugin's existing wiring works.** `DispatchAction` already fires `AcceptQuest` and
   `CompleteQuest` after every `Interact`, regardless of how far the player is — but the
   Dalamud addon-presence guards (`!addonPtr.IsReady`) make those calls cheap no-ops
   when the quest dialog hasn't opened.

### Rejected alternatives

- **Option B: emit `Navigate` when far, `Interact` when near.** This would require the
  dispatch arm to call `GetPlayerPosition` (an async adapter read) inside the synchronous
  `ResolveActionForStep` switch. The current dispatch is intentionally sync — it operates
  only on data already read in `ResolveAction` (`questSequence`, `UiState`). Switching to
  async would either complicate the signature or force `ResolveActionForStep` to receive a
  player position parameter for every step. The cost outweighs the benefit, since (1)
  authored TravelSteps already handle this cleanly and (2) the only "far away" failure
  mode (Interact returning `OutOfRange`) is a runtime adapter failure that the recovery
  ladder should handle in a later phase.
- **Option C: synthesize a Navigate when `Target.Position` is set and player is far.**
  This duplicates `TravelStep` semantics implicitly. Drafters would have a step that
  navigates *and* interacts, defeating the predicate-based completion model that the rest
  of the engine relies on.

### Authoring convention

Authors who use `AcceptStep` or `TurnInStep` without a preceding `TravelStep` to the
same position are expected to be already-near (e.g. the player accepts a quest
immediately upon arriving via a previous turn-in). If a step needs movement first, an
explicit `TravelStep` must precede it. The `DraftValidator` may want a future warning
when an accept/turn-in step has no preceding TravelStep AND no `playerNear` predicate
in `SkipIf`/`Expect`, but that is **out of scope** for this phase.

---

## 5. Engine handler — purpose, interface, behaviors

### Purpose

When the engine encounters an `AcceptStep` or `TurnInStep` whose `Expect` and `SkipIf`
both evaluate false (i.e. the step is current and not yet complete), emit
`EngineAction.Interact(new NpcId(step.Target.NpcId))`. The standard per-step loop
already handles skip-on-expect-true and skip-on-skipif-true via `continue`.

### Interface — code shape

The Builder will add **two new arms** to the `switch` expression in
`QuestEngine.ResolveActionForStep` (`QuestEngine.cs:187-210`):

```csharp
private EngineAction ResolveActionForStep(Step step, UiState ui) => step switch
{
    // ... existing arms unchanged ...

    AcceptStep accept =>
        new EngineAction.Interact(new NpcId(accept.Target.NpcId)),

    TurnInStep turnIn =>
        new EngineAction.Interact(new NpcId(turnIn.Target.NpcId)),

    // ... existing arms unchanged ...
};
```

The `UiState ui` parameter is unused by these arms but must remain present on the
method signature (CutsceneStep depends on it).

### Behaviors — Given / When / Then

#### B1 — `AcceptStep` not yet accepted → emit `Interact`

> **Given** a quest with one `AcceptStep` whose `Target.NpcId == 1003987`
> **And** `isQuestAccepted(<questId>)` is false (no `SkipIf` fires; no `Expect` fires)
> **And** the current quest sequence matches the step's containing sequence block,
> **When** `Engine.Tick` is called,
> **Then** `EngineAction.Interact(new NpcId(1003987))` is returned.

#### B2 — `AcceptStep.SkipIf` true → step is skipped

> **Given** a quest with one `AcceptStep` whose `SkipIf` is `isQuestAccepted(<questId>)`
> **And** `FakeQuestState.AddAcceptedQuest(new QuestId(<questId>))` has been called
> *(Note: `IsQuestAccepted` checks the `_accepted` HashSet, populated by `AddAcceptedQuest`, NOT by `SetQuestStatus`. Use `AddAcceptedQuest` for the accepted predicate.)*
> **And** the current quest sequence matches,
> **When** `Engine.Tick` is called,
> **Then** the step is skipped via `continue` in the per-step loop
> **And** since no further steps exist in the block, `EngineAction.Wait` is returned.

#### B3 — `AcceptStep.Expect` met after an Interact → step completes

> **Given** a quest with one `AcceptStep` whose `Expect` is `isQuestAccepted(<questId>)`
> **And** the initial state has the quest not accepted,
> **When** `Engine.Tick` is called once → returns `Interact(NpcId(<id>))`
> **And** the test then simulates the in-game effect by calling
>   `FakeQuestState.AddAcceptedQuest(new QuestId(<questId>))`
>   (which makes `isQuestAccepted` evaluate true — `IsQuestAccepted` checks `_accepted`, not `_statuses`),
> **When** `Engine.Tick` is called a second time,
> **Then** the per-step loop short-circuits via `Expect`; no further steps exist;
> `EngineAction.Wait` is returned.

#### B4 — `AcceptStep.Expect` unmet → engine retries `Interact`

> **Given** B1's setup, with `Expect = isQuestAccepted(<id>)`
> **And** the test does **not** modify the quest status between ticks,
> **When** `Engine.Tick` is called twice,
> **Then** both ticks return `EngineAction.Interact(new NpcId(<id>))`.
> This is the standard stateless retry contract — the engine re-reads state every tick.

#### B5 — `TurnInStep` not yet complete → emit `Interact`

> **Given** a quest with one `TurnInStep` whose `Target.NpcId == 1000327`
> **And** `isQuestComplete(<questId>)` is false
> **And** the current quest sequence matches,
> **When** `Engine.Tick` is called,
> **Then** `EngineAction.Interact(new NpcId(1000327))` is returned.

#### B6 — `TurnInStep.SkipIf` true → step is skipped

> **Given** a quest with one `TurnInStep` whose `SkipIf` is `isQuestComplete(<questId>)`
> **And** `FakeQuestState.SetQuestStatus(<questId>, QuestStatus.Complete)` has been called
> **And** the current quest sequence matches,
> **When** `Engine.Tick` is called,
> **Then** the step is skipped via `continue`
> **And** since no further steps exist, `EngineAction.Wait` is returned.

#### B7 — `TurnInStep.Expect` met after an Interact → step completes

> **Given** a quest with one `TurnInStep` whose `Expect` is `isQuestComplete(<questId>)`
> **And** the quest is initially not complete,
> **When** `Engine.Tick` is called → returns `Interact(NpcId(<id>))`
> **And** the test calls `FakeQuestState.SetQuestStatus(<id>, QuestStatus.Complete)`,
> **When** `Engine.Tick` is called again,
> **Then** the per-step loop short-circuits via `Expect`; `EngineAction.Wait` is returned.

#### B8 — `TurnInStep.Expect` unmet → engine retries `Interact`

> **Given** B5's setup, with `Expect = isQuestComplete(<id>)`
> **And** the test does not modify the quest status,
> **When** `Engine.Tick` is called twice,
> **Then** both ticks return `EngineAction.Interact(new NpcId(<id>))`.

#### B9 — `AcceptStep` schema round-trip preserves `type` discriminator

> **Given** an `AcceptStep` instance serialized via `QuestForgeJsonContext.QuestFileOptions`,
> **When** the resulting JSON is inspected,
> **Then** the JSON contains `"type": "accept"`
> **And** deserializing the JSON back produces a `Step` instance whose runtime type is `AcceptStep`
> **And** `Target.NpcId` is preserved.

(Note: a basic version of this test already exists in `RoundTripTests.cs:104-115`. The
new test should additionally assert the `"type"` discriminator literal is present in the
JSON text — the existing test only round-trips the object.)

#### B10 — `TurnInStep` schema round-trip preserves `type` discriminator and `dialogueChoices`

> **Given** a `TurnInStep` instance with a non-empty `DialogueChoices` array, serialized
> via `QuestForgeJsonContext.QuestFileOptions`,
> **When** the resulting JSON is inspected,
> **Then** the JSON contains `"type": "turn-in"`
> **And** deserializing the JSON back produces a `Step` instance whose runtime type is `TurnInStep`
> **And** `Target.NpcId` is preserved
> **And** `DialogueChoices` round-trips (count + first element fields match).

---

## 6. Error handling strategy

| Condition | Engine behavior | Recovery |
| --- | --- | --- |
| `step.Target == null` | Schema deserialization yields `default!` — accessing `Target.NpcId` throws `NullReferenceException`. This is a malformed quest file; schema validation should reject it upstream (`qf-validate`). | Not covered by engine tests. |
| `step.Target.NpcId == 0` | Engine emits `Interact(new NpcId(0))`. The Dalamud `InteractWith` will return `NpcNotFound` (no object with BaseId 0). The plugin layer logs but does not retry. `DraftValidator` already flags `NpcId == 0` on `TalkStep` (E6); a parallel check for `AcceptStep`/`TurnInStep` is **out of scope** for this phase. | Authoring-time fix via validator (future work). |
| Player is far from NPC | Engine still emits `Interact`; Dalamud returns `NpcNotFound` (NPC not in object table) or `OutOfRange`. The plugin logs and the next tick re-emits `Interact`. Standard stateless retry. | A preceding `TravelStep` should prevent this in practice. |
| Quest journal dialog opens but `AcceptQuest` button not present | `DalamudInteractor.AcceptQuest` returns `Result.Fail("acceptButtonMissing")`. The plugin's dispatch ignores the result (best-effort). Subsequent ticks re-emit `Interact` until `isQuestAccepted` becomes true. | Bounded by `MaxConsecutiveStepFailures` (recovery ladder — future phase). |
| `AdvanceDialogue` returns `NoActiveDialogue` | Treated as success (`Result.Ok(NoActiveDialogue)`); plugin moves on to AcceptQuest/CompleteQuest which themselves no-op if the addon is absent. | None — by design. |

**The engine does NOT throw for any of these conditions.** All failures route through the
stateless re-tick loop. Genuine bugs (null `Target`, null `Quest`) propagate as exceptions
per the project's "exceptions for bugs, Result for runtime failures" convention.

---

## 7. Out of scope for this phase

The following are deferred. Future tracking issues should be opened for each:

1. **Reward selection orchestration.** `IInteractor.SelectQuestReward` is a Phase 6
   stub. The engine does not call it. For quests with multiple rewards, the player will
   currently see the reward selection UI and must click manually — *or* the
   `DalamudInteractor.CompleteQuest` button-click will fire even when the reward UI is
   open, which may behave incorrectly. The engine should NOT try to compensate; a
   dedicated `RewardSelectionPolicy` and an interactor implementation are needed. The
   schema-level `RewardOverride` field on `QuestDefinition` and the engine-level
   `EngineDecisionConfig.DefaultRewardStrategy` are the levers; routing them into
   `IInteractor.SelectQuestReward` is the future work.
2. **`DialogueChoices` on `TurnInStep`.** Field exists; engine ignores it. Future:
   feed each entry into `IInteractor.SelectDialogueOption` / `ConfirmYesNoPrompt` /
   `SelectStringOption` based on `DialogueChoice.Type`.
3. **`DraftValidator` rules for `AcceptStep` / `TurnInStep` with `NpcId == 0`.**
   Mirror E6 (the existing TalkStep rule). Currently absent.
4. **Authoring-time warning when an accept/turn-in step has no preceding TravelStep
   AND no `playerNear` predicate.** A "may not be near the NPC" lint.
5. **Distinct `EngineAction` variants** like `AcceptQuest` / `CompleteQuest`. Considered
   and rejected — the existing `Interact` flow with plugin-side fan-out is sufficient
   and avoids a new dispatch surface.
6. **Recovery ladder.** Recovery configs on these steps are read into `step.Recover`
   per the inherited `Step` schema, but the engine does not yet act on them
   (consistent with all other step types in the current phase).

---

## 8. Integration points and mock locations

| Production code | Test seam |
| --- | --- |
| `QuestEngine.ResolveActionForStep` (sync, no I/O) | Directly tested via `EngineTestHarness.Engine.Tick`. |
| `FakeQuestState.SetQuestStatus(QuestId, QuestStatus)` | Used to flip `isQuestAccepted` / `isQuestComplete` between ticks. Already exists. |
| `FakeQuestState.SetQuestSequence(QuestId, int)` | Used to align the current sequence with the test's sequence block. Already exists. |
| `FakeInteractor` | Not driven directly by these tests — the harness's `RunToCompletion` helper invokes it, but per-tick tests using `Engine.Tick` do not. Use `Engine.Tick` directly for the B1-B8 tests to avoid scoping side effects. |
| `QuestForgeJsonContext.QuestFileOptions` | Used in B9/B10 schema tests for serialization with the canonical encoder. |

**No new fakes, no new adapters, no new interfaces.** All test seams exist.

---

## 9. Implementation order (for the Builder)

1. **Confirm pre-existing audit** by spot-checking each row of §1. All should be green.
2. **Add the two switch arms** in `QuestEngine.ResolveActionForStep`:
   ```csharp
   AcceptStep accept => new EngineAction.Interact(new NpcId(accept.Target.NpcId)),
   TurnInStep turnIn => new EngineAction.Interact(new NpcId(turnIn.Target.NpcId)),
   ```
   Insert these arms between the existing `TalkStep` arms and the `AttunementStep` arm
   to keep all "Interact-emitting" arms grouped.
3. **Run the new test suite** at `QuestForge.Engine.Tests\Engine\AcceptStepTests.cs`
   and `QuestForge.Engine.Tests\Engine\TurnInStepTests.cs`. Expect all 10 tests to pass.
4. **Run the full test suite** to confirm no regressions in `Quest66130FlowTests`,
   `RecoveryTests`, `AwaitUserTests`, `AttunementStepTests`, `CutsceneStepTests`.
5. **Manually verify** in-game (Phase 6 smoke test) by running a quest that begins with
   `AcceptStep` and ends with `TurnInStep`. Both should fire the appropriate addon button
   via the plugin's existing wiring.

---

## 10. Test scaffolding hints (for the Tester)

### Suite layout

Two new files, mirroring `AttunementStepTests.cs` layout:

- `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine.Tests\Engine\AcceptStepTests.cs`
  — contains B1, B2, B3, B4 (4 tests)
- `C:\Users\publi\RiderProjects\questforge\QuestForge.Engine.Tests\Engine\TurnInStepTests.cs`
  — contains B5, B6, B7, B8 (4 tests)

Schema round-trip tests B9, B10 should be added to:

- `C:\Users\publi\RiderProjects\questforge\QuestForge.Schema.Tests\RoundTripTests.cs`
  — supplementing (not replacing) the existing `AcceptStep_RoundTrips` and
  `TurnInStep_RoundTrips`. Name the new tests
  `AcceptStep_JsonContainsTypeDiscriminator` and
  `TurnInStep_JsonContainsTypeDiscriminatorAndDialogueChoices`.

### Harness usage pattern (per-tick tests B1-B8)

```csharp
var harness = new EngineTestHarness();
harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
// For SkipIf-true tests, also call:
//   harness.QuestState.SetQuestStatus(new QuestId(12345), QuestStatus.Accepted);

var quest = BuildSingleStepQuest(
    questId: 12345,
    sequence: 0,
    step: new AcceptStep
    {
        Id = "accept-test",
        Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(0, 0, 0)),
        SkipIf = new PredicateExpect { Predicate = "isQuestAccepted(12345)" },
        Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
    });

harness.Engine.StartQuest(quest);

var action = await harness.Engine.Tick(CancellationToken.None);
var interact = Assert.IsType<EngineAction.Interact>(action);
Assert.Equal(new NpcId(1003987), interact.Target);
```

Use the same `BuildSingleStepQuest` factory shape as `AttunementStepTests` (lines 331-352).
Keep `AcceptFrom` as a placeholder `new NpcLocation(0u, 0, new Position3(0,0,0))` — the
engine does not consult `QuestDefinition.AcceptFrom` for `AcceptStep` dispatch (it is
metadata for the scheduler, not the runtime).

### Predicate dependencies

- `isQuestAccepted(<id>)` and `isQuestComplete(<id>)` are both already implemented in
  `PredicateEvaluator.cs` and backed by `FakeQuestState`. No new predicate work required.
- **`isQuestAccepted`** calls `FakeQuestState.IsQuestAccepted` which checks `_accepted.Contains(quest)`.
  Toggle via `harness.QuestState.AddAcceptedQuest(new QuestId(<id>))`.
- **`isQuestComplete`** calls `FakeQuestState.IsQuestComplete` which checks `_statuses[quest] == Complete`.
  Toggle via `harness.QuestState.SetQuestStatus(new QuestId(<id>), QuestStatus.Complete)`.
- Do NOT use `SetQuestStatus(Accepted)` to make `isQuestAccepted` true — it populates `_statuses` (used by `GetQuestStatus`), not `_accepted` (used by `IsQuestAccepted`).

### Schema tests (B9, B10)

Use the existing test pattern from `RoundTripTests.cs`. Assert directly on the JSON
string:

```csharp
var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
Assert.Contains("\"type\": \"accept\"", json);
```

Note that the canonical encoder uses `WriteIndented = true`, so the literal `"type": "accept"`
(with a space after the colon) is what will appear. Verify locally before committing.

---

## 11. Estimated test counts (for the Tester handoff)

| Category | Count | Tests |
| --- | --- | --- |
| Happy paths | 4 | B1, B3, B5, B7 |
| Edge cases | 4 | B2, B4, B6, B8 |
| Error cases | 0 | (deferred — see §6 / §7) |
| Schema round-trips | 2 | B9, B10 |
| **Total** | **10** | |

---

OK READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.
- Happy paths: 4 scenarios (B1, B3, B5, B7)
- Edge cases: 4 scenarios (B2, B4, B6, B8)
- Error cases: 0 scenarios (deferred to recovery-ladder phase)
- Schema round-trips: 2 scenarios (B9, B10)
- Expected total: ~10 tests
