# QuestForge Backlog

**Status:** living document — updated as items are resolved or re-prioritised
**Related:** `docs/NEXT_STEPS.md`, `docs/AUTHORING.md`, `docs/DESIGN.md`

This document collects every explicit deferral, TODO, open question, and known gap accumulated across Phases 0–10. Items are grouped by theme and roughly ordered within each group from higher to lower impact. Each entry cites the originating source.

---

## 1. Engine step completion — predicate discipline and persistent cursor

### 1.0 `expect` must use permanent predicates only (authoring rule + validator enforcement)

The engine re-evaluates all steps from scratch every tick. Step completion is determined by the `expect` predicate being currently true. **Transient predicates** (`playerZone()`, position) break on restart because they become false when the player leaves the zone — causing the engine to re-execute already-completed steps and loop.

**Rule:** `expect` must only reference permanent game state (quest flags, quest accepted/complete, item ownership, attunement). Travel steps must use the downstream side-effect as their expect (e.g. `isQuestComplete(65647)` not `playerZone() == 129`).

**Enforcement:** DraftValidator (backlog §5.5, issue #33) should flag transient predicates in `expect` at export time.

**Future:** Persistent step cursor — engine writes confirmed step index to disk on completion, reads it on restart to skip confirmed steps regardless of predicate state. Tracked in issue #33.
- Source: Quest 65644 authoring — `travel-to-zone-129` used `playerZone() == 129`, became false on return trip causing loop over all subsequent steps.

---

## 2. Adapter completeness (Phase 6 stubs)

The Dalamud-backed adapters were minimally wired in Phase 6 to complete quest 66130. Many methods return `Result.Fail("notImplemented", ...)`. These block the engine from automating step types beyond `travel` and `talk`.

### 2.1 Interactor stubs — highest priority; block most step types

`QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs` currently has ~15 stub method bodies returning `Result.Fail("notImplemented", ...)`:

| Method | Needed for |
|---|---|
| `GetCurrentDialogueChoices` | `talk` steps with branching dialogue |
| `SelectDialogueChoice` | same |
| `GetQuestRewards` / `SelectReward` / `TakeReward` | `turn-in` with reward selection |
| `UseEmote` | `use-emote` steps |
| `UseAction` / `BeginCastAction` | `use-action` steps |
| `SayInChat` | `say-chat-message` steps |
| `AbandonQuest` | authoring restart flow |
| `EnterDuty` / `EnterContentFinder` | `duty` steps |
| `EnterSinglePlayerDuty` | `duty` steps with `kind: spd` |
| `UseItemOn` / `UseItemFromInventory` / `UseItemTargeted` | `use-item` steps |
| `BuyFromVendor` / `SellToVendor` | vendor interaction steps |

### 2.2 Combat — not yet implemented

`DalamudGameStateProvider` and `WrathComboAdapter` have Phase 6 stubs for all combat-related methods. These block `combat` step types; combat delegation to WrathCombo/RSR is specified but not wired (`ADAPTERS.md §8`).

### 2.3 Gear management — not yet implemented

`DalamudGearManager` (Stylist IPC) is a Phase 6 stub. Blocks `equip-gear-for-quest` and `equip-best-gear` step types. Stylist IPC contract validation is the first step.

### 2.4 Teleporter gaps

`LifestreamTeleporter` implements `TeleportTo` but several `GetAetheryteList`, `GetNearestAetheryte`, and cost-query methods are stubs. These block teleport-selection logic in quest planning.

### 2.5 Dialogue resolver

`LuminaDialogueResolver` returns the current client language but all sheet-reference lookup and dialogue-option parsing methods are stubs. Full implementation needed for `questFlag` predicate authoring and automated dialogue choice.

### 2.6 Instance kind detection

`DalamudGameStateProvider.GetCurrentInstanceKind` returns `InstanceKind.Other` for any instanced territory. Needs proper Lumina/ClientStructs table lookup to correctly distinguish duty, trial, raid, and SPD.

---

## 3. Step type engine work (corpus expansion blockers)

Each new step type beyond `travel` and `talk` requires: engine handling in `QuestEngine`, a schema entry in `SCHEMA.md`, a fake in `QuestForge.Adapters.Fakes`, tests, and a `FIXTURES.md` capability tag.

✅ `attune` — implemented in Phase 11B (`AttunementStep`, `isAttuned` predicate, `StepInferenceEngine` Rule 2.5). `DalamudGameStateProvider.IsAetheryteAttuned` reads `UIState->IsAetheryteUnlocked` (wired in Phase 11B post-update).

Ordered roughly by difficulty (`NEXT_STEPS.md §Phase 11`):

1. `cutscene` — wait or skip; engine integration with TextAdvance's cutscene hook
2. `interact-object` / `pickup-item` — mechanically similar to `talk` but targets `EventObject`
3. `accept` / `turn-in` — composed of dialogue interactions; need Interactor stubs above
4. `combat` — delegated to BossMod/Wrath/RSR; need WrathComboAdapter wired
5. `duty` — delegated to AutoDuty; entry via `EnterContentFinder` or `EnterDuty`
6. `use-emote`, `say-chat-message` — straightforward once Interactor stubs exist
7. `equip-gear-for-quest` / `equip-best-gear` / `change-job` — need Stylist IPC
8. `use-item` / `use-action` — multiple target variants; cooldown awareness needed
9. `branch` / `fragment` — engine HSM changes; most complex
10. `duty` with `kind: spd` — SPD retry logic, difficulty selection

---

## 4. Scheduler refinements

### 4.1 `EnableCraftGatherQuests` — reserved but not evaluated

`PluginConfig.EnableCraftGatherQuests` and `SchedulerOptions.EnableCraftGatherQuests` exist in the UI and config but the scheduler's Tier-1 evaluation does not use them to gate DoH/DoL class quests. Originally Tier 4, then repurposed but left wired. Decide: remove the field, or wire it as a Tier-1 filter for DoH/DoL jobs ("even if I'm on a crafter, skip class quests — I just want MSQ").
- Source: `docs/PHASE_8_SCHEDULER_SPEC.md` amendment note; `QuestForge.Engine/Scheduling/SchedulerOptions.cs:11`

### 4.2 Chain files — reconsidered but not built

The original design called for explicit chain definition files in `questforge-data` to define quest ordering (e.g., MSQ chapter groupings). Phase 8 replaced this with Lumina-based prerequisite ordering plus `JournalGenre.SortKey`. Revisit if:
- Lumina ordering proves inaccurate for specific quest chains
- Authors need to express chain metadata (name, type) that Lumina doesn't provide
- Source: `docs/NEXT_STEPS.md §Phase 8`, `docs/PHASE_8_SCHEDULER_SPEC.md §3`

### 4.3 Scheduler `IQuestDataProvider` — Lumina tier verification

`LuminaQuestDataProvider.CategoryToTier` returns `null` for unknown categories and `3` as the default for all non-class quests. The `JournalCategory` IDs for side quests, seasonal events, and other categories are not yet verified in-game. Use `/qf debug quest <id>` to check `journalCat` values and add explicit mappings to avoid scheduling seasonal/daily quests if they ever enter the corpus.
- Source: `QuestForge.Adapters.Dalamud/Scheduling/LuminaQuestDataProvider.cs`, `docs/PHASE_10_PLAN.md` note

### 4.4 Manual chain (Tier 0) — `IQuestDataProvider` not exposed to UI

The `ManualChain` in `SchedulerOptions` is always `[]` from the UI — there's no way for a user to set it without code changes. Add a UI control to the `MainWindow` settings for specifying a pinned quest ID.

---

## 5. Authoring mode (Phase 9 gaps)

### 5.1 Step re-recording

`AUTHORING.md §6.4` specifies a "Re-record" option in the edit modal that returns to record mode scoped to replacing a specific step. `StepEditModal` is currently read-only. Implement: detect "Re-record" click → open `RecordStepModal` in replace mode → on confirm, call `QuestDraft.ReplaceStep`.
- Source: `docs/AUTHORING.md:293`, `QuestForge.Plugin/UI/Authoring/StepEditModal.cs`

### 5.2 Dialogue sheet reference browser

`AUTHORING.md §8.1` specifies a `[📚 Browse dialogue]` button showing all Lumina text sheet references for the active quest, filterable, with all four language variants side by side. Currently `InteractionPanel` shows the most recently observed prompt/answer strings **and available quests from the targeted NPC** (up to 5, filtered by `IsQuestAvailable` — shipped post-Phase 9). Full dialogue sheet-reference browsing still requires Lumina `Quest` text-sheet enumeration and a new `DialogueBrowserWindow`.
- Source: `docs/AUTHORING.md §8.1`

### 5.3 Player state panel — missing game state fields

`PlayerStatePanel` shows only Zone and Position (from `GameStateSnapshot`). `AUTHORING.md §4.1` specifies Job, Level, Mount state, Combat state, HP, and Instance kind. These require `SnapshotAggregator` to poll `IGameStateProvider` (or `EngineHost.GetGameStateSummary`) on the heartbeat.
- Source: `docs/AUTHORING.md:88`, `QuestForge.Plugin/UI/Authoring/PlayerStatePanel.cs`

### 5.4 Authoring lifecycle — lazy-load and idle-unload

`AuthoringHost` is eagerly constructed in `Plugin.cs`. `AUTHORING.md §3` specifies lazy-loading on first `/qf inspect` or `/qf author` activation, and idle-unload after 30 minutes of no panel activity. Implement: construct `AuthoringHost` on first command; track last-active timestamp; dispose on idle timeout.
- Source: `docs/AUTHORING.md:65–69`, `QuestForge.Plugin/Plugin.cs`

### 5.5 DraftValidator E3 — full predicate parser integration

`DraftValidator.cs` has a `TODO(E3)` comment. Error E3 (full predicate syntax validation) is currently stubbed as a substring function-name check (which is actually E5 did-you-mean). Wire up the Phase 2 predicate parser (`QuestForge.Predicates.PredicateParser`) to validate the full syntax, operator precedence, and type constraints.
- Source: `QuestForge.Engine/Authoring/DraftValidator.cs:57`

### 5.6 Fragment and branch authoring

AUTHORING.md §11 and PHASE_9_PLAN.md §6 explicitly defer fragment and branch recording. For v2:
- **Fragments**: define an authoring workflow for creating a new `FragmentDefinition`; currently hand-written only
- **Branches**: `RecordStepModal` has no UI for capturing `BranchStep` cases; authors hand-edit JSON post-export
- **Bulk mode**: record multiple steps without modal confirmation between each (batch mode)

### 5.7 Multi-character authoring sessions

`SnapshotAggregator` assumes one active character per authoring session. If the user switches characters mid-session (e.g., swaps alt), the aggregator continues accumulating state for the wrong character. Define: either warn and stop authoring, or reset the aggregator on character change.
- Source: `docs/AUTHORING.md:495`, `QuestForge.Engine/Authoring/SnapshotAggregator.cs`

### 5.8 GitHub PR submission from plugin (v2)

`AUTHORING.md §10.2` notes a planned `[📤 Submit PR]` flow that opens GitHub's fork-and-branch web flow in the browser. Deferred to v2 once authoring patterns stabilize.

---

## 6. Passive trace coverage gaps (UIObserver)

Authoring-mode traces and passive traces (`TraceMode.Always`/`Recording`) currently capture different signals. The goal is for any trace — regardless of mode — to contain enough data to reconstruct a quest definition via `qf-trace extract-quest`.

### 6.0 Extract `UIObserver` service — close the passive trace gap

Three signals required for aethernet and NPC-dialogue step inference are emitted only by `AuthoringHost` (authoring-mode), because they require direct Dalamud UI addon access that `IGameStateProvider` does not model:

| Signal | Gap |
|---|---|
| `AethernetTeleportCompleted` (from/to shard IDs) | Requires TelopTown addon polling |
| `DialogueNpcCaptured` (NPC location when SelectIconString opens) | Requires SelectIconString + TargetManager |
| `DialogueOptionSelected` (choice index on close) | Requires SelectIconString polling |
| `GetTarget` / `AethernetShardTargeted` | PollTargetNpc is authoring-only |

**Fix:** extract the UI polling logic (`PollAethernetDestination`, `PollDialogueOption`, `PollTargetNpc`) from `AuthoringHost` into a standalone `UIObserver` component that:
- Registers on `IFramework.Update` always (not just in authoring mode)
- Writes directly to `TraceSession` (gating already handled by TraceSession)
- Is shared by both `AuthoringHost` and the passive recording path

This is the recording proxy pattern described in `ARCHITECTURE.md`. `AuthoringHost` would delegate to `UIObserver` rather than owning the polls.

**Prerequisite:** quest 65644 authorship and run validated.
- Source: `QuestForge.Plugin/Authoring/AuthoringHost.cs` (PollAethernetDestination, PollDialogueOption, PollTargetNpc)

---

## 7. Trace format compliance

The recorder produces a flat Phase 7+ shape. Several fields specified in `TRACE_FORMAT.md` are not yet emitted. These matter when full replay, redaction, and tooling are built.

### 7.0 `inventory.changed` → `ObservationEvent` (issue #34)

`InventoryChangedEvent` is a separate event type rather than an `ObservationEvent`. The replay harness needs all game state in observation form for uniform fake-adapter reconstruction. Refactor to `WriteObservation("InventoryChanged", ...)`. Blocked on replay harness design to confirm exact observation shape needed.
- Source: `QuestForge.Plugin/Authoring/AuthoringHost.cs`, `QuestForge.Plugin.Tracing/UIObserver.cs`

| Field | Status | Impact |
|---|---|---|
| `seq` — monotonic per-event sequence number | Not emitted; line order is implicit | Blocks deterministic replay ordering |
| `ts` — ms offset from `run.start` | Not emitted; each event has `at` (UTC) | Blocks performance profiling |
| `data` wrapper — payload inside `"data"` sub-object | Not used; fields are flat | Aspirational spec; update spec or implement |
| `v` — format version on every event | Not emitted | Blocks migration tooling |
| `run.start` metadata: `pluginVer`, `dataVer`, `dataHash`, `patchVer`, `engineConfig`, `precedingRunId` | Most not emitted | Blocks trace provenance and reproducibility |

Source: `docs/TRACE_FORMAT.md` Known Divergences table

---

## 8. `qf-trace` tooling gaps

### 8.1 CLI output wiring ✅ COMPLETE (Phase 11A)

`qf-trace` is now fully wired. All four subcommands work from the command line with full argument parsing, exit-code routing, auto quest-data root resolution, and formatted output. 58 tests passing. See `docs/PHASE_11A_PLAN.md`.

### 8.2 `qf-trace replay` — trace replay CLI

`ReplayGameStateProvider` and `ReplayQuestState` exist in `QuestForge.Adapters.Fakes.Replay` but are not exposed as a CLI tool. `qf-trace replay <trace.jsonl>` would run the engine against a recorded trace for regression testing without a game.
- Source: `docs/PHASE_10_PLAN.md §1.1`, `docs/TRACE_FORMAT.md §12`

### 8.3 `qf-trace redact` — privacy scrubbing

`TRACE_FORMAT.md §7` specifies a `qf-trace redact` command that strips `wallClockUtc` from `run.start` and any other PII-adjacent fields before a trace is attached to a bug report. Not yet implemented.
- Source: `docs/TRACE_FORMAT.md §7`, `docs/PHASE_10_PLAN.md §1.1`

### 8.4 `qf-trace validate` — trace integrity checks

Validates a trace file for: `seq` monotonicity (once `seq` is emitted), engine-seed consistency, non-negative `ts` offsets, and schema version support.
- Source: `docs/PHASE_10_PLAN.md §1.1`

### 8.5 NPC and zone name resolution in `extract-quest`

`TraceToQuestExtractor` produces `NpcLocation(uint NpcId, int Zone, ...)` with no names. To reduce manual author work, add an optional `--lumina <game-data-path>` flag that resolves NPC IDs and zone IDs to human-readable names using the Lumina `ExcelSheet<ENpcResident>` and `TerritoryType` sheets. This requires shipping Lumina as a `questforge-tools` dependency (it's already a transitive dep via `QuestForge.Schema`).
- Source: `docs/PHASE_10_PLAN.md §1.1`, `QuestForge.Tools.Trace/Quest/TraceToQuestExtractor.cs`

### 8.6 `Requirements` inference in `extract-quest`

Quest level requirements and ClassJobCategory restrictions are available in Lumina but not in traces. The same `--lumina` flag as above could populate `Requirements` (min level, job restriction) and prerequisite quest IDs automatically.

### 8.7 questforge-data CI: fixture validation

The `qf-trace` CLI is now wired (Phase 11A). Add a second workflow step to `questforge-data/.github/workflows/validate.yml` that runs `qf-trace validate-fixture` on every `fixtures/engine/*.json` file on PRs that touch fixtures. The blocker (CLI wiring) is resolved; this is now a straightforward CI config addition.
- Source: `docs/PHASE_10_PLAN.md §7`, `docs/FIXTURES.md §CI integration`

---

## 9. Dependency plugin expansions

The adapter interfaces are defined for all dependency plugins but implementations are stubs beyond vnavmesh, Lifestream, and TextAdvance.

| Plugin | Interface | Status | Needed for |
|---|---|---|---|
| **WrathCombo / RSR / BossMod** | `ICombat` | Stub | `combat` steps |
| **AutoDuty** | `INavigator` (duty context) | Stub | `duty` steps |
| **Stylist** | `IGearManager` | Stub | `equip-*` steps, `change-job` |
| **TextAdvance** — cutscene skip | `IMinigameSkipper` | Null impl | `cutscene` steps with confirmation |

When any dependency plugin changes its IPC contract, only `QuestForge.Adapters.Dalamud` changes — the engine and quest data are unaffected. This is the primary value of the adapter layer.

For each new plugin integration: validate the IPC contract against the plugin source (we have local clones), implement the adapter, add fakes, add tests.

---

## 10. Corpus priorities (Phase 11)

The corpus currently contains one quest (66130 — Coming to Ul'dah). Suggested expansion order for maximum coverage:

1. **Complete the 66130 turn-in sequence** — the current quest file may not have a fully working `turn-in` step
2. **First MSQ chain** — 5–10 quests from A Realm Reborn opening, all `travel`+`talk`+`accept`+`turn-in`
3. **First class quest** — one quest per combat starting class (GLA, MRD, LNC) to validate Tier-1 scheduling
4. **First `blue-urgent` quest** — My Feisty Little Chocobo (unlocks chocobo companion)
5. **First `blue` quest** — Hildebrand intro or Gold Saucer unlock
6. **First cutscene quest** — validate `cutscene` step type and skip behaviour
7. **First duty quest** — validate `duty` step type and AutoDuty delegation
8. **First SPD quest** — validate `duty` with `kind: spd`

For each new quest: author via Phase 9 mode → `qf-trace extract-fixture` → commit definition + fixture together.

---

## 11. Observability and debugging

### 11.1 `/qf debug quest` — extend to show scheduler view

Currently prints raw Lumina fields. Extend to also show: computed tier, whether the quest is in the active corpus, current `WhyUnavailable` result, and `IsQuestAvailable` result. Useful for diagnosing why a quest isn't being scheduled.

### 11.2 Trace size management

User traces in `pluginConfigs/QuestForge/traces/` have no automatic rotation. After many runs the directory can grow large. Add `/qf config trace-retention <days>` or a UI toggle that auto-deletes traces older than N days.

### 11.3 Ambient flag polling — trace interpretation

Quest flag bits captured in traces via ambient polling (Phase 8) have no tooling to interpret them. `qf-trace` could display a flag-change summary (`quest <id>: bit 2 set at 14:22:01, bit 4 set at 14:22:45`) to help authors correlate bit changes with quest steps.

---

## 12. Minor TODOs (low priority / opportunistic)

| Item | Location | Note |
|---|---|---|
| `SnapshotAggregator.Current` timestamp accuracy | `QuestForge.Engine/Authoring/SnapshotAggregator.cs:26` | Uses `_clock.UtcNow` on read — 250ms staleness window acceptable, noted in reviewer comments |
| `RecordStepModal` zero-placeholder Raw steps | `QuestForge.Plugin/UI/Authoring/RecordStepModal.cs:182` | TravelStep/AcceptStep/TurnInStep built with zero coordinates; populate from `LastNpcInteracted`/`Position` |
| `DraftValidator` W6 — empty quest name | `QuestForge.Engine/Authoring/DraftValidator.cs` | No warning for `QuestName = ""` or `"TODO"` at export time |
| `ExportDialog` file browser | `QuestForge.Plugin/UI/Authoring/ExportDialog.cs` | Path field is a text box; no native file-picker dialog |
| `FileDraftStorage` — `RawJson` as nested object | `QuestForge.Adapters.Dalamud/Authoring/FileDraftStorage.cs` | Steps serialize `Raw` as an escaped JSON string rather than a nested object; readable but ugly in file editors |
| `qf-validate` `--help` | `questforge-tools/qf-validate/Program.cs` | No `--help` flag; exits with parse error on unknown flags |
| Steam Deck / Linux UI scaling | `docs/AUTHORING.md:498` | ImGui panels untested on smaller screens; SizeConstraints may need tuning |
| `AuthoringSessionPanel` — `[📁 Open file location]` button | `QuestForge.Plugin/UI/Authoring/AuthoringSessionPanel.cs` | Specified in AUTHORING.md §6.3 but not implemented |
| JournalCategory ID verification | `QuestForge.Adapters.Dalamud/Scheduling/LuminaQuestDataProvider.cs:13` | `MainScenarioCategoryId = 1` assumed but not confirmed; use `/qf debug quest <id>` |

---

## 13. Explicit non-goals (unlikely to change)

Per `DESIGN.md §1.2` — these are not planned and require explicit reconsideration before any work begins:

- Side content beyond class/job quests (beast tribes, hunts, dailies)
- In-game adversarial evasion against anti-cheat
- General plugin development tooling (other Dalamud plugins have their own)
- Quest data validation against the *current player's* state mid-authoring (false positives)
- Automated multi-language testing (CI handles post-PR)
- Automated quest abandonment (engine never abandons quests)
