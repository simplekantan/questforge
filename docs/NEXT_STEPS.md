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

## Phase 5: Trace recorder (1 week) 🔄 NEXT

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

## Phase 6: Dalamud-backed adapters (3-4 weeks)

**Goal:** real implementations of the adapter interfaces against Dalamud + dependency plugins.

**Order to implement adapters (easiest to hardest):**

1. `IGameStateProvider` — mostly reads from `ClientState`, `ICondition`, `IObjectTable`. Foundation for everything else.
2. `IQuestState` — wraps `QuestManager` and Lumina sheet reads.
3. `ITeleporter` — Lifestream IPC. Simpler than navigation.
4. `INavigator` — vnavmesh IPC. Most complex due to navmesh-generation lifecycle.
5. `IInteractor` — combines Dalamud's interaction primitives with TextAdvance IPC.
6. `ICombat` — IPC with one combat plugin (start with WrathCombo or BossMod — pick whichever is simpler).
7. `IDialogueResolver` — Lumina-backed sheet reference resolution.
8. `ITimingProfile` — pure logic, seeded from runId. Trivial.
9. `IGearManager` — game built-in gear functions, plus optional Stylist IPC.
10. `IMinigameSkipper` — defer all of this unless a v0.2 quest needs it.

**Don't build all of these before testing.** Build `IGameStateProvider`, then verify it works in-game by replacing the fake in your Phase 4 test setup. Then `IQuestState`. Then move up the list.

**Reference the spike's notes constantly.** This is where the surprises from Phase 0 cash in.

**Deliverable:** the engine that ran against fakes in Phase 4 can now run against real Dalamud adapters, end-to-end, in-game.

**Done when:** you can launch the plugin in-game, accept your test quest, and watch the engine complete it. This is the first user-facing milestone.

---

## Phase 7: One real quest, canonical trace, replay harness (2-3 weeks)

**Goal:** prove the trace replay model works.

**What to build:**

1. **Author your first real quest file** — pick a slightly more complex ARR quest than the Phase 4 test. Maybe 5-10 steps, one duty (or skip duties entirely on the first one), one NPC interaction with dialogue choices.
2. **Run it in-game** with the Phase 6 plugin. Capture the trace.
3. **Commit the trace as a canonical fixture** in the quest's directory in `questforge-data`.
4. **Build the replay harness:**
   - `ReplayGameStateProvider` reads observations from a trace and serves them in order
   - `ReplayQuestState` does the same for quest data
   - Fake adapters for everything else, scripted from the trace
   - Test runner: load engine + replay providers + trace, verify the engine produces the same actions in the same order
5. **Wire replay into CI** — every PR to `questforge` triggers replay of all canonical fixtures.

**Deliverable:** a PR that breaks engine behavior is caught by replay tests.

**Done when:** you can deliberately break the engine (e.g., remove a step type's handling) and see CI fail with the specific quest and step that broke.

---

## Phase 8: UI and user surfaces (2-3 weeks)

**Goal:** the plugin becomes usable by someone who isn't you.

**What to build:**

1. **Settings UI** — toggles for the major `EngineDecisionConfig` options, dropdowns for combat plugin and gear selection method
2. **Quest selection UI** — show available quests, filter by status (supported, partial, broken)
3. **Run control** — start, stop, pause, resume
4. **Status display** — what the engine is doing right now, what step it's on, recent observations
5. **Support status display** — per `DESIGN.md` §9 — surface CI replay status, telemetry, known issues
6. **Stop button** — should always work, should be obvious

**Don't build:**

- Authoring mode (next phase)
- Bug report flow (later)
- Configuration profiles, complex automation chains (later)

**Deliverable:** someone unfamiliar with the project can install the plugin, configure it, pick a quest, run it, see what's happening, and stop it cleanly.

**Done when:** a friend who isn't you can use the plugin without your help to run a supported quest.

---

## Phase 9: Authoring mode (3-4 weeks)

**Goal:** contributors can author quest files without manually editing JSON.

**Approach:** build the panels first (Inspect mode, per `AUTHORING.md` §2.1). These have value standalone — they're useful debugging aids during the previous phases too, so consider building parts of them earlier if you find yourself opening Dalamud's developer tools repeatedly.

Then build the recorder (Author mode, per `AUTHORING.md` §2.2). Inference rules can be simple at first.

**Deliverable:** you can author a new quest by playing through it once in Author mode and exporting the result.

**Done when:** you've personally authored at least one quest using only authoring mode (no manual JSON editing).

---

## Phase 10: Incremental corpus expansion (ongoing)

**Goal:** quest count grows. Step types are added as needed.

**This is the steady-state phase.** It looks like:

- Pick a new quest to add
- Try to author it
- Hit a step type you haven't implemented yet (`use-item`, `use-action`, `branch`, etc.)
- Implement that step type
- Author the quest
- Capture the canonical trace
- Add to CI

Step types come in roughly this difficulty order:

1. `cutscene` (easy — engine just waits or skips)
2. `pickup-item`, `interact-object` (mechanically same as `talk`)
3. `accept`, `turn-in` (composed of dialog interactions)
4. `combat` (depends on combat plugin)
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

**At each step:** trace fixture, replay test, doc update.

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
