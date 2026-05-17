# QuestForge Next Steps

**Status:** living document — revise as implementation surfaces what we got wrong
**Owner:** project maintainer
**Related:** [DESIGN.md](./DESIGN.md), [ARCHITECTURE.md](./ARCHITECTURE.md)

---

## Purpose

The foundation design is done — five documents totaling around 5,000 lines covering every architectural decision we've made. The work ahead is implementation.

This document is what you read on Monday morning when you sit down to actually build something. It exists because design docs and implementation roadmaps serve different purposes — design docs answer "what should the system look like," roadmaps answer "what do I do next."

This is opinionated. It assumes a solo maintainer with limited weekend bandwidth. If your situation differs, adapt freely.

---

## The starting principle

A solo project of this scope has a high failure rate. The failure mode is almost always **stalling around month 4-6**, when the initial enthusiasm has worn off and the boring middle stretches ahead. Two things protect against this:

1. **Build the smallest useful thing first, ship it, and grow it.** Don't try to build the whole architecture before having anything that works.
2. **Have a visible weekly commit cadence from week one.** Public visibility creates accountability and attracts collaborators.

The phases below are designed around these principles. Each phase produces something demonstrable, not just internal scaffolding.

---

## Phase 0: The spike (1-2 days) ✅ COMPLETE

**Goal:** validate that the architectural assumptions you've made are actually true.

**What to build:** the dumbest possible Dalamud plugin that uses vnavmesh, Lifestream, and TextAdvance via IPC to automate one specific quest end-to-end. Hardcoded everything. No engine, no schema, no traces, no adapters.

**Specifically, your spike should:**

1. Detect when the player accepts a specific quest (pick one you can manually accept in 30 seconds)
2. Teleport to a known aetheryte using Lifestream's IPC
3. Walk to a known NPC position using vnavmesh's IPC
4. Interact with the NPC, advancing dialogue (with or without TextAdvance handling it)
5. Walk to the next position
6. Interact, complete the quest

Pick the shortest quest you've done many times — something like "Close to Home" or any ARR sub-5-minute starter quest.

**Why this matters:**

The architecture has assumed working contracts with these plugins. If any of them has weird quirks, undocumented IPC parameters, or doesn't expose what we need, you find out in two days instead of three months in. Among the questions this answers:

- Does vnavmesh's IPC actually expose the methods we need? At what granularity?
- Does Lifestream's IPC handle attunement state, or do we need to check separately?
- What's TextAdvance's actual contract for selecting dialogue options? Does it work with sheet references, or only with displayed text?
- How does Dalamud's `ClientState` actually report quest sequence numbers?
- What's the real lifecycle of `Condition[ConditionFlag.BoundByDuty]` during dungeon entry?

If any answer surprises you, the architecture needs to flex.

**Rules for the spike:**

- **Throw it away.** Do not grow it into the real plugin. Its purpose is exploration, not foundation.
- **Don't write tests.** It's a spike. Tests on throwaway code are wasted effort.
- **Don't worry about clean code.** Hardcode every value. Use static fields. Put everything in `Plugin.cs`.
- **Take notes.** When something surprises you, write it down — those notes are inputs to the real architecture.

**Deliverable:** a working but disposable plugin, plus a Markdown file of surprises and quirks discovered. The notes file is far more valuable than the code.

**Done when:** you've successfully automated one quest end-to-end, even if rough, and you've documented at least three architectural assumptions that need revisiting.

---

## Phase 1: Schema validator and CI (1 week) ✅ COMPLETE

**Goal:** build the leverage that pays off across every future quest contribution.

**Why first:** the validator is the single most important piece of infrastructure for a contributor-driven data project. The moment you have one real quest file, you want CI to catch problems automatically. Building this before the engine means the engine inherits a validated schema rather than catching errors at runtime.

**What to build:**

1. **`questforge` repo skeleton** with `.NET 10` (confirmed from Dalamud `global.json`), project structure (`QuestForge.Engine`, `QuestForge.Adapters`, `QuestForge.Plugin`, etc.), `.editorconfig`, `Directory.Build.props`. Don't actually fill in the engine yet.
2. **C# types for `Quest` and step taxonomy** matching `SCHEMA.md`. Use records, use source-generated `System.Text.Json` serialization. This is the schema source of truth.
3. **`questforge-data` placeholder repo** with directory structure (`quests/arr/msq/`, `quests/heavensward/class/dark-knight/`, `fragments/`, etc.) and one hand-written quest file (the simplest ARR quest you can find).
4. **`questforge-tools` repo** with `qf-validate` CLI.
5. **Structural validator** — checks step ID uniqueness, recovery references resolve, sequence numbers strictly increasing, branch nesting depth limits, etc. (see `SCHEMA.md` §8.1)
6. **GitHub Actions workflow** that runs the validator on every PR to `questforge-data` and reports results inline.

**What to defer:**

- Predicate language parsing (next phase)
- Game-data reference validation (later phase, requires Lumina integration)
- Replay tests (requires engine first)

**Deliverable:** a PR to `questforge-data` adding a deliberately broken quest file is caught by CI before merge, with a clear error message pointing at the line.

**Done when:** broken PR → CI red. Fixed PR → CI green. The full round-trip works end-to-end.

---

## Phase 2: Predicate language parser (2-3 days) ✅ COMPLETE

**Goal:** complete the validator so it can check the semantic correctness of `expect`/`skipIf`/branch predicates.

**Why now:** predicates are the most fragile part of quest authoring. A typo in `questSequnece(65) >= 3` (note the typo) will silently fail at runtime if the validator doesn't catch it. Building the parser before the engine means the validator can verify all author-written predicates without the engine existing.

**What to build:**

1. **Parser** for the grammar in `SCHEMA.md` §7.1. Hand-written recursive descent is fine — the grammar is small. Avoid pulling in a parser generator unless you have a strong reason.
2. **State function registry** matching `SCHEMA.md` §7.2. Each function knows its arity and parameter types.
3. **Validator integration** — every predicate in every quest file is parsed at PR time. Failures produce structured errors with line/column and suggested fixes (`predicate-unknown-function: did you mean 'questSequence'?`).
4. **Tests** — unit tests on the parser. Edge cases (operator precedence, deeply nested grouping, unicode in string literals, etc.) are worth covering now.

**What to defer:**

- Predicate evaluation against game state (engine concern, later)
- Custom predicate functions defined in quest data (out of scope for v1)

**Deliverable:** the validator now catches malformed predicates, unknown functions, wrong arity, and type mismatches.

**Done when:** all the example predicates in `SCHEMA.md` §7.3 parse correctly, and a quest file with `questSequnece(65)` is caught by CI.

---

## Phase 3: Adapter interfaces and fakes (1 week) ✅ COMPLETE

**Goal:** establish the adapter layer with in-memory implementations that enable engine development without a game.

**What to build:**

1. **`QuestForge.Adapters` project** — all interface definitions from `ADAPTERS.md`. Just the interfaces. No implementations.
2. **`QuestForge.Adapters.Fakes` project** — in-memory implementations of every interface, designed for unit and integration testing.
   - `FakeGameStateProvider` lets tests script "player is at position X in zone Y with quest 12345 at sequence 2"
   - `FakeNavigator` records navigation requests and pretends to succeed (or fail, on request)
   - `FakeQuestState` returns scripted quest data
   - etc.
3. **`Result<T>` type** — the project's standard outcome type per `ADAPTERS.md` §2.3.
4. **Strong-typed identifiers** — `NpcId`, `QuestId`, `DutyId`, etc. per `ADAPTERS.md` §3.1.

**Why this matters:** with fakes in hand, you can write engine logic against the interfaces and unit-test it without touching Dalamud. This is the testability boundary the whole architecture is built around.

**Don't yet build:** the real Dalamud-backed adapter implementations. Those come after the engine works against fakes.

**Deliverable:** a test project that wires up the fakes and exercises some basic adapter interactions ("set up a fake game state, ask the navigator to move, verify the request was recorded").

**Done when:** you can write a test like:

```csharp
var state = new FakeGameStateProvider();
state.SetZone(132);
state.SetPosition(new WorldPosition(10, 0, 20));

var nav = new FakeNavigator(state);
var result = await nav.NavigateTo(new WorldPosition(50, 0, 50), CancellationToken.None);

Assert.Equal(NavigationOutcome.Arrived, result.Value);
Assert.Equal(1, nav.RecordedNavigationRequests.Count);
```

---

## Phase 4: Engine skeleton (3-4 weeks) ✅ COMPLETE

**Goal:** the smallest possible engine that runs one quest end-to-end against fakes.

**This is the largest single phase.** Budget realistically — solo developer evening time, 3-4 weeks is plausible for a working skeleton. Don't try to build all step types. Don't try to handle every recovery case. Don't try to be production-ready.

**Strict scope for the first engine:**

- Two step types only: `travel` and `talk`
- One worked quest from `SCHEMA.md` (the simplest one you can write)
- Predicate evaluation against fake state
- Engine returns `Action | KeepObserving | Done` per `DESIGN.md` §5.4
- HSM evaluator that handles linear sequences (no branches, no fragments yet)
- Basic recovery: retry, with no override per-step
- Per-tick decision loop driven by fake events

**Deliberately out of scope for the skeleton:**

- Trace recording (Phase 5)
- Real adapter implementations (Phase 6)
- Failure counters, death recovery, SPD logic (Phase 7+)
- Branches, fragments, multi-target steps (Phase 7+)
- Recovery overrides, retry config (Phase 7+)
- UI (Phase 8+)
- Authoring mode (Phase 9+)

**Deliverable:** a unit test that takes a quest JSON file, wires up fakes, runs the engine, and verifies the fakes received the expected sequence of commands.

**Done when:** the test passes consistently and the engine handles at least one realistic failure case (target NPC is out of range → retry → eventually succeeds).

**This is the phase where the design documents will be most wrong.** Expect to revise `ADAPTERS.md` and `SCHEMA.md` as implementation surfaces gaps. Revise the docs in the same commits.

---

## Phase 5: Trace recorder (1 week) ✅ COMPLETE

**Goal:** capture engine runs as JSONL traces, even before replay exists.

**Why now:** debugging is faster with traces. The earlier you can see what the engine was doing when it broke, the easier every subsequent phase is. The recorder is also lower-risk than the engine — the contract is well-specified in `TRACE_FORMAT.md`.

**What to build:**

1. **`TraceWriter`** — append-only JSONL writer with the crash-safety protocol from `TRACE_FORMAT.md` §9.
2. **Recording proxy** — wraps `IGameStateProvider` and `IQuestState`, captures reads as `observation` events, captures actions as `action.submitted` / `action.completed` events.
3. **Event types** for the minimal set: `run.start`, `run.end`, `observation`, `decision`, `action.submitted`, `action.completed`.
4. **Integration into engine** — wrap the existing engine's adapter references with the proxy. Trace files appear in a known location.

**Deferred until needed:**

- Replay-side reading of traces (Phase 7)
- All the diagnostic event types (`dialogue.resolved`, `gear.repair`, etc.)
- Trace rotation, redaction (later)

**Deliverable:** running the Phase 4 quest test now produces a trace file you can inspect with `cat`.

**Done when:** a trace from a successful run is human-readable JSONL and contains the expected `run.start`, observations, decisions, actions, and `run.end`.

---

## Phase 6: Dalamud-backed adapters (3-4 weeks) ✅ COMPLETE

**Goal:** real implementations of the adapter interfaces against Dalamud + dependency plugins.

**What was built:**

- All 10 adapter implementations: `DalamudGameStateProvider`, `DalamudQuestState`, `VnavmeshNavigator`, `LifestreamTeleporter`, `DalamudInteractor`, `WrathComboAdapter`, `DalamudGearManager`, `LuminaDialogueResolver`, `SeededTimingProfile`, `NullMinigameSkipper`
- IPC wrappers for vnavmesh and Lifestream (confirmed gate names from SPIKE_NOTES.md)
- Plugin entry point: `Plugin.cs`, `EngineHost.cs`, `QfCommand.cs`, `DalamudLogger.cs`, `QuestFileLoader.cs`
- `/qf run <id>`, `/qf stop`, `/qf test gamestate|queststate` commands
- Dynamic fly detection via `PlayerState.CanFly` — quest schema can prefer flight, zone capability is checked at runtime
- Cutscene skip: `IGameConfig` (skip-all during run, restore after) + `AutoCutsceneSkipper` hook from ECommons + `SelectString` confirmation
- Quest 66130 ("Coming to Ul'dah") runs end-to-end: navigate to Wymond → dialogue → accept → navigate to Momodi → turn in → Done

**Notable implementation decisions:**

- `ObjectTable.LocalPlayer` not `ObjectTable[0]` (SDK 15)
- `IGameObject.BaseId` not `DataId` (SDK 15 rename)
- `NavigateTo` skips `PathfindAndMoveCloseTo` when already navigating to prevent per-tick re-pathfinding
- `InteractWith` throttled to 1/sec; `AdvanceDialogue` throttled to 250 ms
- `UseFlight = true` default; overridden to `false` at runtime when `PlayerState.CanFly` is false
- Predicate operator must be `and`/`or`/`not` keywords — `&&`/`||` are not lexed
- `DalamudPackager.targets` override file in `Adapters.Dalamud` suppresses manifest validation on the library project

**Deliverable:** plugin loads in Dalamud, quest 66130 completes end-to-end in ~41 seconds, trace written to `pluginConfigs\QuestForge\traces\*.jsonl`.

**Done when:** ✅ Quest 66130 completed in-game. Trace file written. All 103 Phase 5 tests continue passing.

---

## Phase 7: Engine fixture harness + opt-in tracing (2-3 weeks) ✅ COMPLETE

**Goal:** engine regression tests that run in CI without a game, plus production tracing that doesn't burden normal users.

**What was built:**

- **Recording proxy hoist** — `RecordingGameStateProvider` and `RecordingQuestState` moved from `QuestForge.Adapters.Fakes` to `QuestForge.Adapters.Recording` (production-grade, used by `EngineHost`)
- **Observation deduplication** — recording proxies only emit an `observation` event when the value changes from the last emission for that `(method, argument)` pair; reduces trace files from ~12,500 lines to ~50–100 lines for quest 66130
- **Replay infrastructure** (in `QuestForge.Adapters.Fakes.Replay`) — `TraceReader`, `ObservationScanner` with last-known-value fallback, `ReplayGameStateProvider`, `ReplayQuestState`; retained for local debugging and future authoring tools
- **Engine fixture format** — transition-based JSON fixtures in `questforge-data/fixtures/engine/` (see `docs/FIXTURES.md`); one fixture per distinct engine capability shape, not one per quest; ~4–5 transition entries rather than tick-by-tick logs
- **Parametric fixture test** — `QuestForge.Engine.Tests` discovers and runs all `fixtures/engine/*.json` files automatically; new fixtures require zero test code changes
- **Opt-in tracing** — `NullTraceWriter` in production; traces disabled by default for normal users; toggled via `/qf config trace on|off`; authoring mode re-enables automatically

**Notable design decisions:**

- Transition-based fixtures (unique consecutive `(stepId, actionType)` pairs) instead of full-trace replay — decouples tick count from correctness, dramatically smaller fixtures, regeneration-friendly
- Fixtures test engine *capability shapes*, not specific quests — one fixture per `(step:travel + step:talk + predicate:questSequence + ...)` combination
- `ReplayGameStateProvider` kept (not deleted) for `qf-trace replay` CLI tooling in Phase 10
- Two copies of quest 66130 definition: `Engine.Tests/Fixtures/66130.json` for offline engine unit tests; `questforge-data/quests/...` for fixture integration tests (see `FIXTURES.md §CI integration`)

**Deliverable:** CI catches engine regressions for any committed fixture. `dotnet test` passes without a running game or `questforge-data` checkout (fixture tests skip gracefully).

**Done when:** ✅ Parametric fixture test passes for `simple-linear-acceptance.json`. Opt-in trace toggle works. Deliberately breaking engine logic fails CI with the fixture name and diverging transition.

---

## Phase 8: Quest scheduler + UI (3-5 weeks) ✅ COMPLETE

**Goal:** QuestForge becomes a fully automated questing system. The user presses Start; the plugin handles everything — class quests, blocking prerequisites, MSQ continuation — without further input.

### What was built

`QuestScheduler` in `QuestForge.Engine.Scheduling` — pure C#, no Dalamud dependency, 31 unit tests. See `docs/PHASE_8_SCHEDULER_SPEC.md` for the full TDD specification.

```
User presses Start
    ↓
QuestScheduler.NextQuestToRun() selects next quest
    ↓
QuestEngine runs that quest to Done
    ↓
QuestScheduler loops
```

**Priority tiers (int, lower = higher priority):**
- **Tier 0** — User-pinned manual chain. Never interrupted. Blocker → stops all automation (`AwaitingUser`), user resolves manually.
- **Tier 1** — Class/job/role quests for the active job + `"blue-urgent"` quests (no job filter). Class quests filtered by `IsClassQuestForJob`; blue-urgent included via `classJobCatId==0` check.
- **Tier 2** — Dynamic blockers: quests that are prerequisites for Tier-1 or Tier-3 quests, resolved recursively with cycle guard.
- **Tier 3** — Auto chain continuation (MSQ, etc.) — default automation tier.
- **Tier 4** — `"blue"` feature-unlock quests (opt-in, `EnableBlueQuests`, no job filter — e.g. Hildebrand, Gold Saucer).
- **Tier 5** — Side quests (opt-in, `EnableSideQuests`, off by default).

**Key design decisions:**
- No "chain files" — FFXIV's own prerequisite system drives ordering via Lumina `JournalGenre.SortKey` + `PreviousQuest`
- Re-evaluation after each quest completes only (not per-step)
- `IQuestDataProvider` interface abstracts Lumina reads so the scheduler stays in `QuestForge.Engine`
- `SchedulerOptions` record: `ManualChain`, `EnableCraftGatherQuests` (reserved), `EnableSideQuests`, `EnableBlueQuests`
- `SchedulerStatus` discriminated union: `Running`, `SelectingNext`, `AwaitingUser`, `Idle`, `Paused`
- `WhyUnavailable` on `DalamudQuestState` reads Lumina `PreviousQuest[0..2]`, `PreviousQuestJoin`, `ClassJobCategory0`, `ClassJobLevel[0]`

**Ambient quest flag polling** — `EngineHost.TickAsync` now proactively calls `GetQuestFlags` on the active quest after each dispatch when tracing is enabled. The dedup layer in `RecordingQuestState` suppresses unchanged frames — zero overhead when bits don't change. This captures flag bit changes as they occur naturally during a trace, making the `questFlag(id, bit)` predicate discoverable without manual inspection.

**Also built (UI):**
- `LuminaQuestDataProvider` — reads `QuestDefinition.Category` from quest files for tier; Lumina used for prerequisites, level, `IsClassQuestForJob` only. Unknown categories → null (scheduler skips). Poll throttled to 2s between runs.
- `EngineHost` auto-mode loop — `StartAutoMode/StopAutoMode`; `TickAsync` polls scheduler every 2s between runs; `AwaitUser` surfaces to chat via debounced log.
- `MainWindow` — ImGui window: Start/Stop button, live `SchedulerStatus` display, toggles for blue/side/tracing.
- `/qf start` — launches auto mode; `/qf ui` — opens window; `/qf debug quest <id>` — prints Lumina fields + tier for any corpus quest.
- `JobCategoryHelper` extracted so `DalamudQuestState` and `LuminaQuestDataProvider` share the job-mapping switch.

**Quest categories:** `"msq"` → 3, `"class"`/`"job"`/`"role"` → 1, `"blue-urgent"` → 1 (no job filter), `"blue"` → 4, `"side"` → 5, unknown → null.

**Done when:** ✅ Complete.

---

## Phase 9: Authoring mode (3-4 weeks) ✅ COMPLETE

**Goal:** contributors can author quest files without manually editing JSON.

**Approach:** build the panels first (Inspect mode, per `AUTHORING.md` §2.1). These have value standalone — they're useful debugging aids during the previous phases too, so consider building parts of them earlier if you find yourself opening Dalamud's developer tools repeatedly.

Then build the recorder (Author mode, per `AUTHORING.md` §2.2). Inference rules can be simple at first.

**Deliverable:** you can author a new quest by playing through it once in Author mode and exporting the result.

**Done when:** ✅ COMPLETE. Core authoring loop works. Additional authoring quality-of-life features shipped post-Phase 9:
- `InteractionPanel` shows available quests from targeted NPC (up to 5, filtered by `IsQuestAvailable`; handles class-specific same-name quests like "Close to Home" automatically)
- "Show completed quests" toggle in panel settings (default off)
- `/qf quest <name>` — Lumina search returning up to 10 results with ID, level, and availability status
- `/qf author stop` — explicit authoring exit with mid-session warning

---

## Phase 10: Trace extractor CLI (`questforge-tools`) (2-3 weeks) ✅ COMPLETE

**Goal:** complete the authoring pipeline — run a quest with the engine, get a regression fixture AND a recoverable quest draft with one command.

**Why here:** Phase 9 produces quest drafts via manual recording. Phase 10 adds a second, complementary extraction path: the engine trace. Phase 11 (corpus expansion) benefits from both tools. Without Phase 10, fixtures must be hand-authored and quest files cannot be recovered from traces.

**What to build** (in `questforge-tools`, alongside `qf-validate`):

### Fixture extraction

1. **`qf-trace extract-fixture <runId>.jsonl`** — reads a local trace, produces a fixture JSON draft:
   - `expectedTransitions` derived from the trace's `DecisionEvent` entries (unique consecutive pairs)
   - `terminalOutcome` from `RunEndEvent.outcome`
   - `questFile` resolved from `RunStartEvent.questId` by looking up the quest in `questforge-data`
   - `capabilities` inferred from the quest file's step types and predicate functions
   - `description: "TODO"` and a suggested filename
2. **`qf-trace validate-fixture <fixture.json>`** — cross-validates a fixture against its referenced quest file:
   - All `stepId` values exist in the quest definition
   - `capabilities` list matches the quest file's actual step types and predicates
   - `questFile` path exists
3. **`qf-trace list-fixtures`** — lists all committed fixtures with their capability coverage, highlighting gaps

**Integration into questforge-data CI:** run `qf-trace validate-fixture` on every fixture file in every PR, failing if a step ID or quest file reference is invalid.

### Quest extraction (new)

4. **`qf-trace extract-quest <runId>.jsonl`** — reads a trace and produces a `QuestDefinition` draft using the same `StepInferenceEngine` logic from Phase 9:
   - Replays `observation` events as (before, after) `GameStateSnapshot` pairs across each action boundary
   - Calls `StepInferenceEngine.Infer(before, after)` for each action to get step type and suggested `expect` predicate
   - Pulls destination coordinates from `action.submitted Navigate` events → `TravelStep.Destination`
   - Pulls NPC IDs from `action.submitted Interact` events → `TalkStep.Target.NpcId` / `AcceptStep.Target.NpcId`
   - Pulls quest sequence/flag predicates from observation deltas → `expect` field
   - Synthesises step IDs from the same slug rules as `StepInferenceEngine.SuggestedStepId`
   - Outputs a draft `.json` with `supportStatus.implementation = "partial"` and a `TODO` on any field that couldn't be inferred (NPC names, zone names, prerequisites)

   **Primary use cases:**
   - **Recovery** — quest file was lost; reconstruct from a saved trace of a successful run
   - **Bootstrap** — rough first draft for a quest with no file yet; author reviews and edits before committing
   - **Validation** — compare extracted draft against committed quest file to detect drift between what the engine ran and what the file says

   **What `extract-quest` cannot infer from a trace** (must be filled in manually):
   - Quest `name`, `expansion`, `category` (not present in trace — resolve from Lumina via the questId in `RunStartEvent`)
   - NPC names (IDs only; names come from Lumina)
   - `Requirements` (level, job prerequisites — come from Lumina, not trace)
   - `prerequisites` / `chain` metadata

   **Implementation note:** `extract-quest` shares the `StepInferenceEngine` class from `QuestForge.Engine.Authoring` — the `questforge-tools` project references `QuestForge.Engine` so no code is duplicated.

**Deliverables:**
- `qf-trace extract-fixture` produces a valid fixture draft in under 5 seconds
- `qf-trace extract-quest` produces an editable quest draft in under 5 seconds

**Done when:** ✅ COMPLETE. `QuestForge.Tools.Trace` library fully implemented with 44 tests. CLI fully wired in Phase 11A — see below.

## Phase 11A: Wire `qf-trace` CLI (✅ COMPLETE)

**Goal:** make `qf-trace` usable from the command line so the authoring pipeline is closed end-to-end.

**What was built:**
- `CliArgsParser` — argument parsing for all four subcommands with flag/positional routing, parse-error detection
- `QuestDataRootResolver` — auto-probes `./quests/` and `../questforge-data/quests/` when `--quest-data` is not supplied
- `OutputFormatters` — `FormatIssues`, `FormatTodos`, `FormatFixtureList`, `FormatFixtureListJson`
- `FixtureModelSerializer` — cached `JsonSerializerOptions` matching `FIXTURES.md` byte format
- `ListFixturesCommand.Enumerate` / `.ComputeGaps` — implemented (were `NotImplementedException` stubs)
- `qf-trace/Program.cs` — full dispatch with exit-code routing and `--help` text
- 14 new tests (58 total in `QuestForge.Tools.Trace.Tests`)

**Full pipeline now works:**
```bash
qf-trace extract-fixture <runId>.jsonl --quest-data ../questforge-data
qf-trace validate-fixture fixtures/engine/simple-linear-acceptance.json --quest-data ../questforge-data
qf-trace list-fixtures --quest-data ../questforge-data
qf-trace extract-quest <runId>.jsonl --quest-data ../questforge-data --out 66130-draft.json
```

---

## Phase 11B: Aetheryte and aethernet attunement ✅ COMPLETE

**Goal:** enable automated questing through zones and cities the player has not previously visited by supporting aetheryte and aethernet attunement as a first-class step type.

**Why here:** corpus expansion immediately hits aetheryte gates. Any MSQ quest that routes through a new zone or city requires attunement before Lifestream can teleport there on subsequent quests. Without this, the scheduler silently fails when trying to return to a zone the player has never visited.

**What to build (full feature, no deferral):**

1. **`AttunementStep`** — new step type in `QuestForge.Schema/Step.cs` with `Target: AetheryteId` (works identically for main aetherytes and aethernet shards — both require physical interaction to unlock)
2. **`IGameStateProvider.IsAttuned(AetheryteId, ct)`** — new adapter method; `DalamudGameStateProvider` reads attunement state from ClientStructs `PlayerState`
3. **`isAttuned(id)` predicate** — registered in `FunctionRegistry` and `PredicateEvaluator` alongside existing predicates; used in `skipIf` so re-runs are no-ops on already-attuned crystals
4. **`GameStateSnapshot` attunement field** — `SnapshotAggregator` polls for attunement changes; `StepInferenceEngine` adds a new rule: interaction with no quest-state change but attunement change → infer `AttunementStep` automatically
5. **Engine handler** — navigate to aetheryte/shard, interact, wait for `isAttuned(id)` postcondition
6. **Fake/test support** — `FakeGameStateProvider.SetAttuned(AetheryteId, bool)` for deterministic tests

**Key design decision:** `AttunementStep` is a schema-level distinction from `interact-object`; the underlying trace events are identical (same `Interact` action). The step type enables `CapabilityInferrer` to emit `step:attune` and lets the scheduler eventually pre-flight check attunement requirements before starting a quest chain.

**Done when:** ✅ COMPLETE. 224 engine tests passing. `AttunementStep` (`"attune"` discriminator) in schema; `isAttuned(id)` predicate in `FunctionRegistry` and `PredicateEvaluator`; `StepInferenceEngine` Rule 2.5; `GameStateSnapshot.LastAttuned`; engine dispatches `Interact`. `DalamudGameStateProvider.IsAetheryteAttuned` still a stub — see `BACKLOG.md §1` for the ClientStructs upgrade.

---

## Phase 11: Incremental corpus expansion (ongoing)

**Goal:** quest count grows. Step types are added as needed.

**This is the steady-state phase.** It looks like:

- Pick a new quest to add
- Author it via Authoring mode (Phase 9)
- Hit a step type not yet implemented (`use-item`, `use-action`, `branch`, etc.)
- Implement that step type in the engine
- Re-author the quest
- Extract the fixture with `qf-trace extract-fixture` (Phase 10)
- Commit quest definition + fixture together

**Phase 11 progress — step types implemented so far:**

| Step type | Status | Phase | Notes |
|---|---|---|---|
| `travel` | ✅ Phase 4 | Core | Navigation via vnavmesh |
| `talk` | ✅ Phase 4 | Core | NPC interaction + dialogue |
| `attune` | ✅ Phase 11B | New | Aetheryte/aethernet attunement; `IsAetheryteAttuned` now reads UIState |
| `cutscene` | ✅ Phase 11 | New | Waits on both skippable + non-skippable flags; skip handled by plugin |
| `accept` | ✅ Phase 11 | New | Quest accept via plugin's existing AcceptQuest wiring |
| `turn-in` | ✅ Phase 11 | New | Quest turn-in via plugin's existing CompleteQuest wiring |

**Step types remaining (in difficulty order):**

1. `pickup-item`, `interact-object` (mechanically same as `talk` — good first issues)
2. `combat` (depends on WrathCombo/RSR IPC wiring)
3. `duty` (depends on AutoDuty IPC wiring)
4. `await-user` (UI work, conceptually simple)
5. `use-emote`, `say-chat-message` (straightforward once interactor stubs are wired)
6. `equip-gear-for-quest` / `equip-best-gear` / `change-job` (depends on Stylist IPC)
7. `use-item` (multiple target variants)
8. `use-action` (combat infrastructure + cooldown awareness)
9. `branch` (engine HSM changes)
10. `fragment` (composition + parameter substitution)
11. `duty` with `kind: "spd"` (SPD retry logic, difficulty selection)
12. `minigame` (one minigame type at a time)

**Other Phase 11 work completed:**
- `IsAetheryteAttuned` ClientStructs implementation (UIState, not PlayerState)
- NPC quest discovery in `InteractionPanel` (targets an NPC → shows available quests by class)
- `/qf quest <name>` search command and `/qf author stop` command
- Lazy NPC→quest index for O(1) Lumina lookup on NPC retarget
- 28 GitHub Issues created across three repos for backlog tracking
- `qf-trace` CLI fully wired (all four subcommands: extract-fixture, validate-fixture, list-fixtures, extract-quest)
- NuGet Package Source Mapping (eliminates dalamud.dev 404 errors locally and in CI)
- Debug commands for authoring: `/qf debug addon <name>` (AtkValue dump), `/qf debug offered-quest` (reads JournalAccept AtkValue[261] to identify offered quest ID without accepting), `/qf debug target` (NPC/quest-index diagnostics), enhanced `/qf debug quest <id>` (full job coverage from ClassJobCategory)
- `InteractionPanel` now auto-opens when entering inspect or author mode via `/qf inspect` or `/qf author`

**At each step:** author quest, extract fixture, add CI coverage entry.
5. `duty` (regular only first; SPD logic is more complex)
6. `await-user` (UI work, but conceptually simple)
7. `use-emote`, `say-chat-message` (straightforward)
8. `equip-gear-for-quest` (depends on IGearManager being fleshed out)
9. `equip-best-gear`, `change-job` (depends on Stylist or gearset infrastructure)
10. `use-item` (multiple target variants make this more complex)
11. `use-action` (combat infrastructure + cooldown awareness)
12. `branch` (engine HSM changes)
13. `fragment` (composition + parameter substitution)
14. `duty` with `kind: "spd"` (SPD retry logic, difficulty selection)
15. `minigame` (one minigame type at a time)

**At each step:** author quest, extract fixture, add CI coverage entry.

---

## What to do when you get stuck

- **If you're stuck on a design question:** the docs are wrong, not you. Revise the docs (with the new insight clearly noted), then proceed.
- **If you're stuck on a Dalamud quirk:** check the spike's notes file. Check Questionable's source for prior art. Ask in the Dalamud Discord — they're helpful.
- **If you're losing motivation:** ship something small and visible. A commit, a screenshot, a blog post. Public progress is renewable energy.
- **If the project starts feeling like a job:** stop for a week. Come back. If after a week it still feels like a job, consider whether you actually want to keep going — life is too short for unpaid jobs disguised as hobbies.

---

## Things I want to flag as risks

**Risk 1: Spike findings invalidate the architecture.**
Probable. Plan for the docs to need revision after Phase 0. Don't treat the foundation as locked.

**Risk 2: Dependency plugins break or disappear.**
The adapter abstraction protects against this in theory. In practice, if vnavmesh stops being maintained mid-project, you have a serious problem. Stay close to the Dalamud community to know early.

**Risk 3: Patch days will eat weekends.**
FFXIV major patches every ~4 months break things. The patch-day workflow in `DESIGN.md` §7 mitigates but doesn't eliminate this. Budget for it.

**Risk 4: The trace-replay test investment is large for one developer.**
True. You may want to defer the full replay harness (Phase 7) and rely on manual testing longer. That's fine — the architecture supports it. Just don't pretend you have replay tests when you don't.

**Risk 5: Anthropic's behavior toward FFXIV plugins or Dalamud could change.**
Probably won't. But this is a tooling-around-a-game-EULA project. Maintain a clean conscience about what you build and how it's used.

---

## What success looks like at 6 months

If everything goes well:

- One supported MSQ chain (~10 quests) end-to-end
- Replay tests covering all of them
- A small community of contributors (3-5 people) submitting quest data PRs
- Authoring mode usable enough that contributors don't need to ask you for help
- Plugin downloads in the low thousands

If things go less well but still successfully:

- The spike-only investment has been documented and shared (useful even without the rest)
- The schema and design docs exist as public reference (useful for anyone else attempting this)
- You've learned a lot about Dalamud plugin development
- You quit cleanly and openly with a "lessons learned" post

The second outcome is not a failure. Knowledge produced and shared is valuable even when the project that produced it doesn't ship.

---

## One last thing

These docs are a snapshot. They will be wrong in places we can't yet see. As you build, revise. Don't let the design constrain implementation when implementation has learned something new.

Good luck.
