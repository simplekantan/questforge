# Engine Test Fixtures

**Status:** implemented in Phase 7  
**Related:** `docs/NEXT_STEPS.md`, `docs/TRACE_FORMAT.md`, `docs/PHASE_7_PLAN.md`  
**Fixture repo:** `questforge-data/fixtures/engine/`

---

## Purpose and philosophy

Engine test fixtures are small JSON files that capture the **expected decision transitions** the engine produces when running a specific quest. They are the primary regression test corpus for the engine — when the engine's logic changes in a way that affects any committed quest type, the relevant fixture fails in CI, signalling a deliberate re-record or an accidental regression.

**What fixtures test:** engine *capability* correctness. One fixture per distinct combination of step types, predicate functions, and engine behaviours that needs regression coverage. Quest 66130 exercises `step:travel`, `step:talk`, and four predicate functions; a dungeon quest exercises `step:duty` and combat delegation. You add one fixture per capability shape, not one per quest.

**What fixtures do not test:**
- Dalamud adapter correctness (those require a live game — E2E only)
- Exact tick counts (the engine may take more or fewer ticks to reach a transition without that being a regression)
- Error recovery paths (covered by engine unit tests in `QuestForge.Engine.Tests`)

---

## Where fixtures live

```
questforge-data/
  quests/              # authored quest definitions
    arr/msq/
    ...
  fixtures/
    engine/            # engine regression fixtures (this document)
      simple-linear-acceptance.json
      with-dialogue-choices.json
      with-escort.json
      with-spd.json
      with-dungeon.json
      ...
    # future: tools/, replay/, authoring/
```

Fixtures are **not** co-located with quest definitions. They are test infrastructure, not authored content. Quest files are CC-BY-4.0 community content; fixtures are MIT developer tooling.

---

## Fixture format

```json
{
  "schemaVersion": "1.1.0",
  "description": "ARR MSQ simple linear: travel to NPC, accept quest, travel to NPC, complete",
  "initialState": "fresh",
  "capabilities": [
    "step:travel",
    "step:talk",
    "predicate:playerNear",
    "predicate:playerZone",
    "predicate:questSequence",
    "predicate:isQuestComplete"
  ],
  "questFile": "quests/arr/msq/66130-coming-to-uldah.json",
  "expectedTransitions": [
    { "stepId": "travel-to-wymond",  "actionType": "navigate" },
    { "stepId": "talk-to-wymond",    "actionType": "interact" },
    { "stepId": "travel-to-momodi",  "actionType": "navigate" },
    { "stepId": "talk-to-momodi",    "actionType": "interact" }
  ],
  "terminalOutcome": "done"
}
```

### Field reference

| Field | Type | Required | Description |
|---|---|---|---|
| `schemaVersion` | semver string | ✅ | Fixture format version. Readers reject versions they don't understand. Current version: `1.1.0`. `1.0.0` fixtures remain valid — the `sourceTrace` field added in `1.1.0` is optional. |
| `description` | string | ✅ | Human-readable summary. Used in test output and `--list-tests`. |
| `initialState` | string | ✅ | Assumed world state when the test starts. See vocabulary below. |
| `capabilities` | string[] | ✅ | Engine capabilities exercised by this fixture. See taxonomy below. |
| `questFile` | string | ✅ | Path to the quest definition, relative to the `questforge-data` root, forward slashes. |
| `expectedTransitions` | object[] | ✅ | Ordered unique consecutive `(stepId, actionType)` pairs. See below. |
| `terminalOutcome` | string | ✅ | The `run.end.outcome` value: `"done"` or `"awaitUser"`. |
| `initialOverrides` | object | Optional | Override starting world state for branch coverage. Supported keys: `zone` (int), `position` ({x,y,z}), `questSequence` (int), `slotsEquipped` (bool[]), `items` ({itemId, count}[]), `job` (string). When absent, `QuestDataDrivenState` uses the quest file's starting conditions. |
| `sourceTrace` | path string | Optional | Archival path to the source JSONL trace, relative to `questforge-data` root, forward slashes. Used by `qf-trace` CLI and local debugging only — not consumed by the CI fixture harness. Path must match filesystem case exactly (CI is Linux). |

### `initialState` vocabulary

| Value | Meaning |
|---|---|
| `"fresh"` | Quest not yet accepted. Player in the appropriate starting zone for the quest (e.g., zone 182 for quest 66130). No prior progress. |

All current fixtures use `"fresh"`. Use `initialOverrides` for branch variants instead of introducing new `initialState` values.

### `capabilities` taxonomy

Capabilities use a `namespace:name` format. Readers may filter by namespace prefix.

**Step types** (`step:`)

| Tag | Step type implemented |
|---|---|
| `step:travel` | `TravelStep` |
| `step:talk` | `TalkStep` |
| `step:accept` | `AcceptStep` |
| `step:turn-in` | `TurnInStep` |
| `step:attune` | `AttunementStep` |
| `step:hand-over-item` | `HandOverItemStep` |
| `step:cutscene` | `CutsceneStep` |
| `step:combat` | `CombatStep` |
| `step:duty` | `DutyStep` |
| `step:spd` | `DutyStep` with `kind: spd` |
| `step:use-item` | `UseItemStep` — key-item / inventory-item use with optional NPC or ground-position target |
| `step:use-action` | `UseActionStep` |
| `step:use-emote` | `UseEmoteStep` |
| `step:teleport` | `TeleportStep` |
| `step:aethernet` | `AethernetStep` — first-class intra-zone aethernet hop (use for new quests; `step:travel` with `routeHint.aethernet` is the legacy form) |
| `step:purchase-item` | `PurchaseItemStep` |
| `step:interact-object` | `InteractObjectStep` |
| `step:pickup-item` | `PickupItemStep` |
| `step:say-chat-message` | `SayChatMessageStep` |
| `step:equip-gear-for-quest` | `EquipGearForQuestStep` |
| `step:equip-best-gear` | `EquipBestGearStep` |
| `step:change-job` | `ChangeJobStep` |
| `step:register-gearset` | `RegisterGearsetStep` |
| `step:open-coffers` | `OpenCoffersStep` — open gear coffers from player inventory (post-dungeon) |
| `step:use-item-on-object` | `UseItemOnObjectStep` — approach EventObj, interact to open InventoryEvent, use key item |
| `step:minigame` | `MinigameStep` |
| `step:await-user` | `AwaitUserStep` |
| `step:wait` | `WaitStep` |
| `step:branch` | `BranchStep` |
| `step:fragment` | `FragmentStep` |

**Predicate functions** (`predicate:`)

| Tag | Predicate function |
|---|---|
| `predicate:playerNear` | `playerNear(pos, radius)` |
| `predicate:playerZone` | `playerZone()` |
| `predicate:questSequence` | `questSequence(id)` |
| `predicate:questFlag` | `questFlag(id, bit)` |
| `predicate:isQuestComplete` | `isQuestComplete(id)` |
| `predicate:isQuestAccepted` | `isQuestAccepted(id)` |
| `predicate:isAttuned` | `isAttuned(aetheryteId)` |
| `predicate:playerHasItem` | `playerHasItem(itemId, qty?)` |
| `predicate:not` | `not(pred)` |
| `predicate:inCombat` | `inCombat()` |
| `predicate:isSlotEquipped` | `isSlotEquipped(slotIndex)` |
| `predicate:isDiscipleOfWar` | `isDiscipleOfWar` |
| `predicate:isDiscipleOfMagic` | `isDiscipleOfMagic` |
| `predicate:playerInCombat` | `playerInCombat` |
| `predicate:playerJobId` | `playerJobId` |

**Engine behaviours** (`engine:`)

| Tag | Behaviour |
|---|---|
| `engine:branching` | Quest uses `BranchStep` and the engine evaluates branch conditions |
| `engine:fragments` | Quest uses `FragmentStep` and the engine performs fragment substitution |

### `expectedTransitions`

A transition is an entry in the sequence of **unique consecutive** `(stepId, actionType)` pairs the engine emits. Consecutive identical pairs are collapsed — tick count is not asserted.

Example: if the engine emits Navigate 1,847 times followed by Interact 312 times, the transitions are:
```
(travel-to-wymond, navigate)
(talk-to-wymond, interact)
```

**`actionType` canonical strings** (case-sensitive, lowercase):

| Canonical string | C# type | Notes |
|---|---|---|
| `"navigate"` | `EngineAction.Navigate` | |
| `"interact"` | `EngineAction.Interact` | Also covers `AttunementStep` and similar — those steps dispatch as Interact actions on the target NPC/object. |
| `"handover"` | `EngineAction.HandOver` | `HandOverItemStep` dispatch — player handing over quest items to an NPC. |
| `"useaethernet"` | `EngineAction.UseAethernet` | `TravelStep` with `routeHint.aethernet` (legacy) or `AethernetStep` (preferred for new quests) — Lifestream aethernet shortcut. |
| `"teleport"` | `EngineAction.Teleport` | `TeleportStep` dispatch — cross-region teleport to a master aetheryte. |
| `"purchase"` | `EngineAction.Purchase` | `PurchaseItemStep` dispatch — buy item from vendor (gil or GC seals). |
| `"useaction"` | `EngineAction.UseAction` | `UseActionStep` dispatch — execute a game action (combat ability, general action, key item) on an optional NPC target. |
| `"useemote"` | `EngineAction.UseEmote` | `UseEmoteStep` dispatch — execute an emote via text command. |
| `"useitem"` | `EngineAction.UseItem` | `UseItemStep` dispatch — use a key item or inventory item on an optional NPC or ground-position target. |
| `"useitemonobject"` | `EngineAction.UseItemOnObject` | `UseItemOnObjectStep` dispatch — interact with EventObj, then use key/inventory item within the InventoryEvent event context. |
| `"saychatmessage"` | `EngineAction.SayChatMessage` | `SayChatMessageStep` dispatch — send `/say <message>` via chat. |
| `"engage"` | `EngineAction.Engage` | `CombatStep` dispatch AND global defense rule — engage an attacker before advancing. |
| `"equipgear"` | `EngineAction.EquipGear` | `EquipGearForQuestStep` dispatch — equip a specific quest-required item. |
| `"equipbestgear"` | `EngineAction.EquipBestGear` | `EquipBestGearStep` dispatch — equip recommended gear via Stylist/RecommendEquip. |
| `"changejob"` | `EngineAction.ChangeJob` | `ChangeJobStep` dispatch — switch active job/class. |
| `"registergearset"` | `EngineAction.RegisterGearset` | `RegisterGearsetStep` dispatch — save current gear as a new gearset. |
| `"opencoffer"` | `EngineAction.OpenCoffer` | `OpenCoffersStep` dispatch — open one gear coffer via ActionManager.UseAction(Item). |
| `"interactobject"` | `EngineAction.InteractObject` | `InteractObjectStep` and `PickupItemStep` dispatch — interact with a world EventObj via TargetSystem.InteractWithObject. |
| `"entersingleplayerduty"` | `EngineAction.EnterSinglePlayerDuty` | `DutyStep(kind:"spd")` dispatch — enable BossMod AI for a Single Player Duty. |
| `"enterduty"` | `EngineAction.EnterDuty` | `DutyStep(kind:"duty")` dispatch — configure AutoDuty for Support mode and start dungeon/trial. |
| `"wait"` | `EngineAction.Wait` | Rarely appears in `expectedTransitions` — only when all steps in a sequence are satisfied but the game's sequence number has not yet advanced, or when an action is gated (casting, cooldown). |
| `"awaituser"` | `EngineAction.AwaitUser` | Terminal action. Lowercased per the extractor's `ToLowerInvariant()` normalization. Never appears in `expectedTransitions` (filtered as terminal); appears in `terminalOutcome` as `"awaitUser"` with original casing. |
| `"done"` | `EngineAction.Done` | Never appears in `expectedTransitions`; appears in `terminalOutcome` only. |

When new `EngineAction` subtypes are added, a new canonical string is registered here. **The canonical strings are stable contracts**; C# type renames require updating the mapping, not every fixture.

**`stepId`** is taken directly from the quest file's step `id` field. `null` is valid for terminal and cross-step states.

**Important:** `stepId` values in quest files are stable contracts — renaming a step is a breaking change that requires updating all fixtures that reference it.

### `terminalOutcome`

The outcome written by the engine to `run.end.outcome`. The engine emits a `run.end` event (not a `decision` event) when it returns `EngineAction.Done`, so Done never appears in `expectedTransitions`.

| Value | Meaning |
|---|---|
| `"done"` | Quest completed successfully |
| `"awaitUser"` | Engine suspended and yielded control to the user |

---

## Fixture naming convention

Files live in `questforge-data/fixtures/engine/` and are named by the **capability shape** they exercise, not by the specific quest used:

```
simple-linear-acceptance.json   # travel + talk + accept + return + complete
with-attunement.json            # multi-zone attunement quest (shipped)
with-accept.json                # accept-step-led quest
with-turn-in.json               # turn-in-step-led quest
with-hand-over-item.json        # quest with hand-over (gift to NPC)
with-interact-object.json       # quest with InteractObjectStep
with-pickup-item.json           # quest with PickupItemStep
with-use-action.json            # quest with UseActionStep (e.g. MRD L5 "Axe in the Stone")
with-use-emote.json             # quest with UseEmoteStep (e.g. /cheer at NPC)
with-teleport.json              # quest with TeleportStep (cross-region travel)
with-purchase-item.json         # quest with PurchaseItemStep (gil or GC seals)
with-say-chat-message.json      # quest with SayChatMessageStep (e.g. /say password)
with-equip-gear-for-quest.json  # quest with EquipGearForQuestStep (equip a specific quest-required item)
with-equip-best-gear.json       # quest with EquipBestGearStep (equip recommended gear via Stylist/RecommendEquip)
with-change-job.json            # quest with ChangeJobStep (switch active job/class)
with-register-gearset.json      # quest with RegisterGearsetStep (save current gear as gearset)
with-open-coffers.json          # quest with OpenCoffersStep (open gear coffers from inventory)
with-use-item-on-object.json    # quest with UseItemOnObjectStep (approach EventObj + InventoryEvent item use)
with-dialogue-choices.json      # quest with SelectString branch (Phase 11+ TBD)
with-escort.json                # quest with escort NPC (Phase 11+ TBD)
with-spd.json                   # single-player duty
with-dungeon.json               # full duty (dungeon/trial)
with-gear-requirement.json      # superseded by with-equip-gear-for-quest.json, with-equip-best-gear.json, with-change-job.json
with-branching.json             # quest with BranchStep
with-fragments.json             # quest with FragmentStep
```

The `with-<shape>.json` filenames are the canonical names registered in
`TraceToFixtureExtractor.FilenameLookup` / `DistinguishingCapPriority`; using a
different filename for a registered shape will fight the extractor's filename
suggestion.

When multiple quests share the same shape, use the simplest/shortest one as the reference quest for that fixture.

---

## How the test works

The engine test is a **parametric xUnit theory** that discovers and runs all fixture files automatically:

```csharp
[Theory, MemberData(nameof(AllEngineFixtures))]
public async Task EngineProducesExpectedTransitions(string fixturePath) { ... }
```

Adding a new fixture file to `questforge-data/fixtures/engine/` automatically adds a new test case with zero code changes to the test project.

Each fixture type also requires a **scripted fake state machine** — test code that advances the fake adapters' state so the engine's predicates evaluate in the right order to produce the expected transitions. This lives in `QuestForge.Engine.Tests/Fixtures/States/`.

The state machine is responsible for advancing game state at the right moments: making `playerNear` flip to true when the engine has navigated long enough, advancing `questSequence` from 0 to 255 after the acceptance interaction, and marking `isQuestComplete` true after the completion interaction. The exact implementation is test-code detail; the fixture JSON describes only the expected outcome.

**State machine dispatch:** the parametric test uses a two-path dispatch:

1. **Quest-data-driven path** — the primary path for all current fixtures. `QuestDataDrivenState` (in `QuestForge.Engine.Tests/Replay/States/`) drives the fake adapters by deriving state mutations directly from the quest definition's `expect` predicates. `PredicateAnalyzer` inspects each step's postcondition to determine which fake-state fields must flip (zone, position, questSequence, questFlags, items, slotsEquipped, job). Navigation is resolved instantly; combat steps complete via the same predicate-mutation mechanism. No trace file is required.
2. **Skip** — if the fixture file exists but neither a `QuestDataDrivenState` registration nor a state factory is present, the test emits `Assert.Skip` with an actionable message. This makes it safe to commit fixture files before their drivers are wired.

`initialOverrides` (see field reference) provides branch coverage: different initial assumptions (different zone, questSequence, items in inventory, job, slotsEquipped) exercise alternate engine paths without separate quest files.

**Trace files are archival.** The `sourceTrace` field and `<name>.trace.jsonl` sibling convention are retained for `qf-trace` CLI tooling and local debugging. They are not used as primary fixture inputs. Trace-backed replay via `TraceReplayFixtureState` / `SegmentedObservationScanner` remains available for edge cases but is not the default path.

**The test loop:**
```
while (decisions remain to assert):
    tick engine
    compute (stepId, actionType) from CapturingTraceWriter
    if different from last → new transition
one final tick → assert terminalOutcome
```

### Validation at test start

Before driving the engine, the test validates the fixture:
1. `questFile` exists in questforge-data (fail loudly with a clear message, not skip — a missing quest file is a broken fixture, not a missing dependency)
2. All `expectedTransitions[].stepId` values exist as step IDs in the loaded quest definition
3. `terminalOutcome` is a recognised value

A fixture with a typo in a step ID fails immediately with a clear message, not with a confusing "transition never appeared" failure.

**Path case sensitivity:** `questFile` and `sourceTrace` paths must match the actual filesystem exactly, including case. CI runs on Linux (case-sensitive); Windows developers may not notice case mismatches locally. Always use the exact casing from the `questforge-data` directory listing. The `<name>.trace.jsonl` sibling convention is derived from the fixture's own on-disk path and is always case-correct; the case risk applies only to the explicit `sourceTrace` field.

---

## Trace-backed fixtures (archival / qf-trace CLI)

A **trace-backed fixture** pairs a fixture JSON with a source `.jsonl` trace. Traces are no longer the primary fixture driver — `QuestDataDrivenState` is. Traces are retained as archival records and consumed by `qf-trace replay` for local debugging. The description below documents the format for that use case.

### Two-file model

```
questforge-data/fixtures/engine/
  simple-linear-acceptance.json       # scripted fixture — no trace
  with-attunement.json                # trace-backed fixture
  with-attunement.trace.jsonl         # its source trace (single engine run, filtered to one runId)
```

The trace file contains the **recorded observations** (engine inputs: player position, zone, quest state) from a single real in-game run. The fixture's `expectedTransitions` contain the **recorded decisions** (engine outputs) from that same run. The harness replays the recorded inputs through the *current* engine and compares its outputs to `expectedTransitions`.

### Why this is a real regression test

The trace (inputs) is immutable ground truth — a recording of what the game presented to the engine. If engine logic changes so the engine produces different decisions for the same inputs, the fixture fails. This is a genuine regression test, not a round-trip of the same data.

### Producing a trace-backed fixture

1. Enable tracing: `/qf config trace on`
2. Run the quest: `/qf run <questId>`
3. Run the extractor: `qf-trace extract-fixture <session>.jsonl` — this produces both `<suggested-name>.json` and `<suggested-name>.trace.jsonl` (the trace is filtered to the engine run's runId automatically)
4. Review the fixture JSON, write a description, and commit both files

### Read-pattern maintenance

The `ObservationScanner` tolerates read-count and read-order drift within known `(method, arg)` pairs via its scan-forward + last-seen fallback. An engine change that reads `GetPlayerPosition` three extra times does **not** starve — it reuses the last-seen value.

Starvation occurs **only** when the engine calls a `(method, arg)` pair that **never appears at all** in the trace — i.e., the engine added a genuinely new adapter read. When that happens:

1. The test fails with an explicit "OBSERVATION STARVATION" message naming the trace file and the missing `(method, arg)` pair
2. Re-record the trace (run the quest in-game again, re-run `qf-trace extract-fixture`, re-commit both files)
3. Do NOT modify the engine to avoid reading the new pair — understand why the read pattern changed first

A re-record is a deliberate, reviewed event (same discipline as "Updating a fixture when it breaks" below).

### `--with-trace` / `--no-trace` flags

`qf-trace extract-fixture` defaults to co-emitting the source trace (`--with-trace` is ON by default). Use `--no-trace` to suppress the trace file when you only need the fixture JSON draft.

---

## Fixture lifecycle

### Creating a fixture (Phase 7 — manual)

1. Identify the quest that best exercises the target capability shape
2. Identify the step IDs in that quest's definition
3. Hand-author the fixture JSON
4. Run the engine test locally: `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~EngineFixture`
5. Iterate until the transitions match
6. Commit to `questforge-data/fixtures/engine/`

### Creating a fixture (Phase 10 — tool-assisted) ✅ library complete; CLI output polish pending

1. Enable tracing: `/qf config trace on`
2. Run the quest: `/qf run <questId>`
3. Run the extractor: `qf-trace extract-fixture <runId>.jsonl`
4. Tool outputs a fixture draft:
   - `expectedTransitions` derived from the trace's decision events
   - `terminalOutcome` from the trace's `run.end.outcome`
   - `capabilities` inferred from the quest file's step types and predicate functions
   - `questFile` resolved from `RunStartEvent.questId`
   - `description: "TODO: add description"` and filename suggestion
5. Developer reviews, writes description, validates capabilities list
6. Commit to `questforge-data/fixtures/engine/`

### Updating a fixture when it breaks

A fixture breaks when the engine legitimately produces different decisions for the same quest. This is expected when:
- A new step type is added and the quest file is updated to use it (e.g., `AcceptStep` replaces a `TalkStep`)
- Engine predicate evaluation changes and a step transitions at a different point
- A new pre-step action is added (e.g., gear equip before a combat step)

**Do not blindly update the fixture.** Understand *why* it changed first. If the change is intentional:
1. Re-run the quest in-game (with tracing on)
2. Run `qf-trace extract-fixture` on the new trace (Phase 10), or hand-update the JSON
3. Commit the updated fixture with a clear commit message explaining the reason

If the change is unexpected, it's a regression — fix the engine, not the fixture.

---

## CI integration

Fixtures require `questforge-data` to be checked out alongside `questforge`. The CI workflow:

```yaml
- uses: actions/checkout@v4
  with:
    path: questforge

- uses: actions/checkout@v4
  with:
    repository: simplekantan/questforge-data
    path: questforge-data

- name: Run tests (includes fixture regression)
  working-directory: questforge
  run: dotnet test QuestForge.Engine.Tests
```

When `questforge-data` is absent (developer without the data repo), the fixture test **skips** (via `Assert.Skip`) rather than failing. Engine unit tests (Phase 4/5) remain runnable without `questforge-data`.

**Phase 4/5 engine tests** use a local copy of quest 66130 at `QuestForge.Engine.Tests/Fixtures/66130.json` and do NOT require `questforge-data`. These test engine mechanisms in isolation (does `TravelStep` produce `Navigate`?) and are intentionally decoupled from the canonical quest definition. When the canonical quest changes, update the local copy if the Phase 4/5 tests rely on the changed structure.

---

## Coverage matrix

The `capabilities` field across all committed fixtures gives a live coverage map. A future CI check can enforce that all implemented step types have at least one fixture:

```
step:travel             ✅ simple-linear-acceptance.json, with-attunement.json
step:talk               ✅ simple-linear-acceptance.json, with-attunement.json
step:accept             ✅ with-attunement.json
step:turn-in            ✅ with-attunement.json
step:attune             ✅ with-attunement.json
step:hand-over-item     ✅ with-attunement.json
step:use-action         ❌ (no fixture yet — record from MRD L5 quest)
step:use-emote          ❌ (no fixture yet — record from any /cheer quest)
step:teleport           ❌ (no fixture yet)
step:purchase-item      ❌ (no fixture yet)
step:say-chat-message   ❌ (no fixture yet — record from any /say-password quest)
step:duty               ❌
step:combat             ❌
```

**Current corpus:** 7 fixtures across 4 quests, including 4 branch variants for quest 65999 exercising `initialOverrides`. All driven by `QuestDataDrivenState`.

A new fixture automatically fills coverage gaps. The `capabilities` list is **informational** — it is not verified against the quest file's actual predicates at commit time. The `qf-trace validate-fixture` tool cross-validates capabilities against the quest definition.

---

## Known limitations

- **Linear sequences only.** `expectedTransitions` is an ordered list with no branching support. Fixtures for branching quests use one fixture per branch with `initialOverrides` to set the starting state that routes into each branch.
- **Happy path only.** Error recovery paths (player death, NPC not found, duty failure) are covered by engine unit tests, not fixtures.
- **Fresh initial state only.** Mid-quest fixtures (`initialState: "quest-accepted"`) are not yet supported.
- **No parameter assertions.** `expectedTransitions` captures `actionType` and `stepId` only. Asserting the *exact destination* of a Navigate action (e.g., specific coordinates) requires a future fixture format extension.
- **No timing assertions.** The fixture does not assert how many ticks each transition takes. Performance regressions require a separate mechanism.
- **Capabilities list can drift.** The `capabilities` field is manually maintained. Adding a new predicate to the quest file does not automatically update the fixture's capabilities. The `qf-trace validate-fixture` tool (Phase 10) will detect drift.

---

## Future extensions

Reserved field names (do not use for other purposes until specified):

- `"branches"` — for branching quest paths (Phase 11+)
- `"maxTransitionCount"` — upper bound on total transitions before timeout (performance guard)
- `"requiredAdapters"` — explicit list of adapters the scripted state machine must activate
- `"initialState"` values beyond `"fresh"` — `"quest-accepted"`, `"pre-completion"`, etc.

New fields added to the format increment the minor version (`1.1.0`). Breaking changes (removed or renamed required fields) increment the major version (`2.0.0`).
