# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

QuestForge is a Dalamud plugin for automating FFXIV (Final Fantasy XIV) quest completion. The project is in **active implementation** — all specifications live in `docs/`. The implementation follows a 10-phase roadmap defined in `docs/NEXT_STEPS.md`.

## Repository Structure (Planned — Three Repos)

This repo (`questforge`, MIT) is the plugin source. Two companion repos will exist:
- `questforge-data` (CC-BY-4.0) — Quest definitions (JSON) and trace fixtures
- `questforge-tools` (MIT) — Schema validator CLI (`qf-validate`) and replay harness (`qf-trace`)

Actual project layout:
```
QuestForge.Engine/           # Pure C# — no Dalamud dependency (testability boundary)
QuestForge.Engine.Tests/     # xUnit, runs in CI without a game
QuestForge.Adapters/         # Adapter interface definitions only
QuestForge.Adapters.Dalamud/ # Concrete Dalamud-backed implementations
QuestForge.Adapters.Fakes/   # In-memory fakes + recording proxies for tests
QuestForge.Plugin/           # Dalamud integration layer (entry point, EngineHost, /qf commands)
QuestForge.Schema/           # Quest file schema types + STJ source-generated context
```

## Build & Test Commands

**SDK requirement:** This project targets .NET 10. The `global.json` pins SDK `10.0.202` with `rollForward: latestMinor`. The SDK is installed at `C:\Users\publi\.dotnet\` and the system PATH has it ahead of `C:\Program Files\dotnet\` (which only has SDK 8.0).

```bash
dotnet build
dotnet publish -c Release          # produces plugin DLL
dotnet test QuestForge.Engine.Tests
dotnet test QuestForge.Adapters.Tests
```

When `questforge-tools` exists:
```bash
qf-validate quests/                # schema + semantic validation on quest data PRs
qf-trace replay <fixture.jsonl>    # trace replay regression testing
```

## Core Architectural Invariant

**The engine (`QuestForge.Engine`) must never reference concrete Dalamud types.** It depends only on the adapter interfaces in `QuestForge.Adapters`. This is the testability boundary — the engine runs in CI without a running game instance.

## Three-Layer Architecture

```
Quest Engine (pure C#)
  └─ Hierarchical State Machine, step planning, predicate evaluation, failure recovery
         ↕ adapter interfaces only
Adapter Layer (interfaces)
  └─ IGameStateProvider, IQuestState, INavigator, ITeleporter, IInteractor,
     ICombat, IGearManager, ITimingProfile, IDialogueResolver, IMinigameSkipper
         ↕ IPC + Dalamud APIs
Dalamud Integration Layer
  └─ Concrete adapter implementations, plugin lifecycle, ImGui UI
```

## Key Design Patterns

**Postcondition discipline:** After every action the engine re-reads game state and verifies the declared postcondition. No "trust I did that."

**Recording proxy pattern:** A proxy wraps adapter reads transparently, capturing observations and actions into an append-only JSONL trace. This same trace serves runtime debugging, bug reports, and CI replay regression tests.

**Seeded deterministic timing:** All timing decisions flow through `ITimingProfile`, seeded from `runId`. Same `runId` → same decisions → identical replay.

**Result\<T\> for routine failures:** Navigation blocked, NPC not found, etc. use `Result<T>` return values — not exceptions. Exceptions are reserved for genuine bugs (null arguments, violated contracts).

**Three independent failure counters:**
- `MaxConsecutiveStepFailures` — per step, triggers `AwaitUserCompletion`
- `MaxDutyRetries` — per single-player duty (SPD)
- `MaxConsecutiveQuestFailures` — across chained quests, stops all automation
- Dungeon deaths **never** increment any counter (context-routed by `InstanceKind`)

**Death recovery is context-routed:**
- Open world → accept return to home aetheryte, re-plan from new position
- Dungeon/Trial/Raid → no action (delegated plugin handles it)
- SPD → note death, wait for instance exit, check postcondition

**Schema as source of truth:** C# types in `QuestForge.Schema` define the canonical quest schema. JSON Schema is auto-generated from these types. The same types drive runtime deserialization (source-generated `System.Text.Json`), the validator, and authoring tools.

**TraceSession / TraceMode:** `TraceSession` (in `QuestForge.Adapters/Tracing/TraceSession.cs`) is the unified trace shared by both `AuthoringHost` and `EngineHost` — replacing the two separate trace systems that existed through Phase 9. `TraceMode` is a config enum with values `Off`, `Always`, `Authoring`, and `Recording`. Changing `TraceMode` requires a plugin reload (live switching is tracked in issue #27).

## Dependency Plugins (Adapter Targets)

The engine delegates to these external Dalamud plugins via adapter implementations:
- **vnavmesh** — in-zone navigation (`INavigator`)
- **Lifestream** — aetheryte teleport (`ITeleporter`)
- **TextAdvance** — dialogue skipping (`IInteractor`)
- **BossMod / WrathCombo / RSR** — combat (`ICombat`, delegated entirely)
- **AutoDuty** — dungeon pathing (delegated entirely)
- **Stylist** — gear management (`IGearManager`)

When a dependency plugin changes its IPC contract, only the `QuestForge.Adapters.Dalamud` implementation changes — the engine and quest data are unaffected.

## Testing Strategy (Five Tiers, ROI Order)

1. **Schema validation** — structural JSON schema checks on every quest data PR
2. **Semantic validation** — references exist, coordinates in bounds, predicates valid, no DAG cycles (build this first; catches ~60-70% of contributor bugs)
3. **Engine unit tests** — xUnit against fake adapters, ≥80% line coverage target
4. **Trace replay** — canonical traces replayed against current engine on every PR (the regression workhorse once corpus exists)
5. **E2E smoke** — manual, optional alt account

## Specification Documents

All design is in `docs/`:
- `DESIGN.md` — Goals, data delivery model, testing strategy, timing profiles, patch-day workflow
- `ARCHITECTURE.md` — C4-style diagrams (system context, containers, engine internals, authoring mode, trace subsystem)
- `ADAPTERS.md` — Full specifications for all 10 adapter interfaces, cross-cutting types, recovery behaviors
- `SCHEMA.md` — Quest file format, step types, predicate language, fragment composition, recovery ladders
- `TRACE_FORMAT.md` — JSONL event format, event types, replay determinism, privacy design
- `AUTHORING.md` — Inspect mode (passive debug panels) and Author mode (active recording with step inference)
- `NEXT_STEPS.md` — 10-phase implementation roadmap with deliverables and done criteria

## Implementation Roadmap Summary

| Phase | Scope | Duration |
|-------|-------|----------|
| 0 | Spike: vnavmesh/Lifestream/TextAdvance contract validation | 1-2 days |
| 1 | Schema validator + CI on data PRs | 1 week |
| 2 | Predicate language parser | 2-3 days |
| 3 | Adapter interfaces + in-memory fakes | 1 week |
| 4 | Engine skeleton (HSM, `travel` + `talk` step types, one quest against fakes) | 3-4 weeks |
| 5 | Trace recorder + recording proxy | 1 week |
| 6 | Dalamud-backed adapter implementations + first in-game test | 3-4 weeks |
| 7 | First real quest + canonical trace + replay harness wired to CI | 2-3 weeks |
| 8 | UI (settings, quest selection, run control, status) | 2-3 weeks |
| 9 | Authoring mode (inspect + record + draft management + export) | 3-4 weeks |
| 10 | Trace extractor CLI (`qf-trace`) | 2-3 weeks |
| 11 | Corpus expansion: new step types as quests require them | Ongoing |

**Current status: Phase 11 in progress. AttunementStep, HandOverItemStep, and playerHasItem predicate implemented and validated in-game. Expanding quest corpus.**

## Adding a New Step Type — Fixed Slice Order

When adding a new step type (e.g. `UseActionStep`, `UseEmoteStep`, `TeleportStep`), follow this fixed slice order. Each slice is its own feature branch + PR with the full TDD cycle (architect → tester → builder → reviewer). Do NOT skip the architect even for "simple" steps — the spec doc becomes the contract for the tester and reviewer.

### Slice 1 — Architect spec
- `docs/<STEP>_STEP_PLAN.md` — design decisions numbered (e.g. UA1–UA14), test scenarios numbered (e.g. U1–U13)
- Mirror the rigor of `docs/USE_ACTION_STEP_PLAN.md` and `docs/USE_EMOTE_STEP_PLAN.md`
- Read at minimum: closest analog plan, `QuestForge.Schema/Step.cs`, `QuestForge.Engine/QuestEngine.cs` (esp. `ResolveTeleportAction`/`ResolveUseAction` patterns), `QuestForge.Engine/EngineAction.cs`, the analog test file

### Slice 2 — Engine + schema + validator (single PR)
Bundle these; they're tightly coupled and small:

**Schema** (`QuestForge.Schema/`)
- New `<Step>Step` in `Step.cs` (sealed class, init-only properties; `[JsonIgnore(WhenWritingNull)]` on optional)
- `[JsonDerivedType(typeof(<Step>Step), "<kebab-case>")]` on `Step`'s polymorphic attribute list
- `[JsonSerializable(typeof(<Step>Step))]` in `QuestForgeJsonContext.cs`
- JSON round-trip test in `QuestForge.Schema.Tests/RoundTripTests.cs` (use canonical bare-string form for `expect`; never invent new shapes)

**Adapter** (`QuestForge.Adapters/`)
- New `IXxxExecutor` interface in `QuestForge.Adapters/<Concern>/` — small/focused (mirror `IMount`, `ITeleporter`)
- Any pure-logic helper (e.g. `ActionStatusInterpreter`, `EmoteCommandResolver`) lives in `QuestForge.Adapters/` so it stays Dalamud-free and unit-testable
- `EngineAction.<Action>` discriminated case in `QuestForge.Engine/EngineAction.cs`

**Fake** (`QuestForge.Adapters.Fakes/`)
- New `FakeXxxExecutor` mirroring `FakeActionExecutor` shape: `RecordedCalls`, `ScriptNextResult`/`ScriptNextFailure`, `Reset()`

**Engine** (`QuestForge.Engine/`)
- `IXxxExecutor?` as an **optional** ctor param on `QuestEngine` (null → AwaitUser at dispatch, doesn't break old tests)
- `ResolveXxx` async pre-arm in `QuestEngine.cs` mirroring `ResolveTeleportAction` (guards in priority order; do NOT set `_lastResolvedStep` in pre-arms)
- Wire `<Step>Step` into the step-dispatch switch
- `EngineTestHarness`: add `FakeXxxExecutor` property, pass to ctor, add `case EngineAction.<Action>:` arm in `RunToCompletion`

**Validator** (`QuestForge.Engine/Authoring/DraftValidator.cs`)
- Add E# rules for impossible-zero fields (e.g. `<Step>.<RequiredId> == 0` → error; mirror E6/E7/E9)
- Add E# rule for explicit-zero optional NPC targets (null is OK; explicit 0 is rejected; mirror E8/E10)
- Add W# rule for missing `Expect` AND extend the W1 suppression guard (mirror W7/W8 + the `not (UseActionStep or UseEmoteStep)` pattern). The W# message must contain "spin-loop" so authors understand the runtime cost.

**Tests** — engine tests in `QuestForge.Engine.Tests/Engine/<Step>Tests.cs` (mirror `UseActionStepTests.cs`); validator tests in `QuestForge.Engine.Tests/Authoring/DraftValidator<Step>Tests.cs`; pure-helper tests in `QuestForge.Adapters.Tests/<Concern>/` if applicable.

### Slice 3 — Dalamud impl + EngineHost dispatch arm + tooling catch-up (paired PRs)
**Critical: tooling catch-up MUST land in the same slice as Dalamud impl, not retroactively** (we've burned this twice — see the catch-up PR for use-action / use-emote).

**questforge repo:**
- `QuestForge.Adapters.Dalamud/<Concern>/Dalamud<Step>Executor.cs` — thin shell wrapping the pure helper + native calls. Mirror `DalamudActionExecutor` / `DalamudEmoteExecutor`.
- `QuestForge.Plugin/EngineHost.cs`:
  - Field declaration alongside sibling adapters
  - Construct in ctor
  - Pass to `QuestEngine` in `BeginRun` as `xxxExecutor: _xxxExecutor`
  - Dispatch arm in `DispatchAction` between existing cases — mirror UseAction arm: debounced log, `_navigator.Stop` if `IsNavigating`, then `await _xxxExecutor.Xxx(...)`
  - Add `IXxxExecutor DebugXxxExecutor => _xxxExecutor;` if other Debug accessors exist
- Decide explicitly whether to add to the lazy-dismount exemption list (`is not EngineAction.Navigate and not EngineAction.Teleport`) — `Teleport` is exempt because the game auto-dismounts on arrival; most actions ARE NOT exempt. Pin the decision with mounted+prior-Navigate / standalone-mounted test pair (mirror U8/U9, UE6/UE7).
- **Recording-proxy decision**: write-only adapters (the typical action-executor shape) don't need a `RecordingXxxExecutor` wrapper — `action.submitted`/`action.completed` events from `EngineHost.DispatchAction` already capture writes. Add a `RecordingXxx` wrapper ONLY if the adapter has reads worth recording or multi-stage operations (precedent: `RecordingCombat` records `SetTarget`/`StartRotation` pairs).

**questforge-tools repo** (paired PR, push both before either merges):
- `QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs`: add `[typeof(<Step>Step)] = "step:<kebab-case>"` entry to `StepCapabilities` dict
- `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs`:
  - Add exact-shape entry to `FilenameLookup` (typically `["step:talk", "step:travel", "step:<kebab-case>"], "with-<kebab-case>.json"`)
  - Add fallback entry to `DistinguishingCapPriority` (priority chosen by how shape-defining the step is; action > emote > teleport > purchase-item is the current ordering)
- `QuestForge.Tools.Trace/TraceConstants.cs`: add `ActionXxx = "<lowercased EngineAction type name>"` constant. No behavior change (`IsTerminalAction` only uses `done`/`awaituser`); documents what `DecisionEvent.ActionType.ToLowerInvariant()` actually emits.
- Tests for the above in `QuestForge.Tools.Trace.Tests/` (mirror the catch-up PR pattern)

**questforge docs:**
- `docs/FIXTURES.md` — add the step's row to the capabilities table and the action's row to the `actionType` canonical strings table

### Slice 4 — In-game smoke test (manual)
Run the engine path in-game on a real or test quest. Verify:
- Action fires (animation plays / target acquired / chat command sent)
- Engine's debounced log line appears in `dalamud.log`
- Stateless retry behaves correctly across multiple ticks (no spam if Expect is unmet briefly)

### Slice 5 — Authoring inference (REQUIRED, NEVER DEFER)
Authoring inference is a crucial feature — without it, every use of the new step in a quest requires manual authoring. **This slice is mandatory** for every step type, no exceptions.

If the detection signal isn't obvious, the FIRST task of this slice is signal research: investigate FFXIVClientStructs (`BattleChara.CastInfo.ResponseGlobalSequence` for actions, `Character.EmoteController.EmoteId` for emotes, etc.), Lumina sheets, existing addon state, and game-side polling sources until a workable signal is identified. Document the chosen signal in the inference plan doc before the architect concludes.

If no polling signal can be found after honest research, surface the problem to the user explicitly — do not silently defer.

- `docs/<STEP>_INFERENCE_PLAN.md` — separate spec, mirror `USE_ACTION_INFERENCE_PLAN.md` / `USE_EMOTE_INFERENCE_PLAN.md`
- Engine side (pure C#):
  - `<Action>CompletedSignal` record + nullable property on `GameStateSnapshot` (mirror `ActionCompletedSignal` / `EmoteCompletedSignal`)
  - `SnapshotAggregator.On<Action>Completed` / `On<Action>Consumed` (does NOT clear in `ResetDeltas` — survives reset)
  - `InferredFrom.<Action>Completed` enum value
  - `StepInferenceEngine` Rule 3.5x — placement above Rule 3 (sequence advance) and Rule 4 (zone change), below Rules 1 and 2.x. Tie-breaker with sibling action rules: more-specific signal wins.
  - `StepFactory` `"<kebab-case>"` arm
- Plugin side (polling — no game hooks unless the user explicitly agrees):
  - `IGameProbe.GetXxx()` accessor for the detection field
  - `DalamudGameProbe` impl reading the FFXIVClientStructs field
  - `UIObserver.PollXxx` state machine. Two patterns:
    - **Monotonic counter** (like `CastInfo.ResponseGlobalSequence`): first observation is silent baseline; only later increments fire
    - **Momentary state** (like `EmoteController.EmoteId`): first non-zero observation fires immediately; same value persisting = no fire; transition to 0 silently resets
  - Wire into `OnFrameworkUpdate`; in `ResetWindowState` clear baseline + call `_aggregator?.On<Action>Consumed()`
- `AuthoringHost.RecordStep`: add `_aggregator.On<Action>Consumed()` to the existing consume sequence
- `AuthoringHost.PreviewInference`: extend the `[QF-DIAG]` log line with `<Action>Completed=N`
- Tests: inference tests in `QuestForge.Engine.Tests/Authoring/<Step>InferenceTests.cs`; `UO_<X>*` polling tests appended to `UIObserverTests.cs`; `FakeGameProbe` extension

### Slice 6 — In-game smoke for inference (manual)
Enter Author mode, perform the action in-game, click Record in the modal. Verify `[QF-DIAG] PreviewInference: ... <Action>Completed=N ...` appears and the drafted step matches.

### Process invariants
- **Always create a feature branch** before any slice (`feat/<step>-<slice>` — see `feedback_use_feature_branches.md`)
- **Pure logic gets unit tests** even for Dalamud-bound adapters — extract enum mappings, status interpretation, command formatting (see `feedback_tdd_even_for_adapters.md`)
- **Never invent new JSON shapes** to satisfy a test (canonical form for `expect` is bare string)
- **`_lastResolvedStep` is NOT set in async pre-arms** (matches Teleport/Purchase/UseAction/UseEmote precedent)
- **Decision UE7-style "actionType lowercase"** is the convention: `EngineAction.<Name>` → `<name>` via `.GetType().Name.ToLowerInvariant()`. The TraceConstants catalog documents what gets emitted.

### When extract-quest needs per-step work
By default, no. `TraceToQuestExtractor`'s fast path uses `StepRecordedEvent` (which serializes the full step), and the slow path reuses the shared `StepInferenceEngine`. Per-step extension to `QuestForge.Tools.Trace/SnapshotState.cs` is only required for steps with multi-stage observation semantics across ticks (precedent: `PurchaseItemStep`'s gil/seal delta tracking across a buy span). If your step is single-tick → snapshot-signal → infer, you don't need to touch `SnapshotState`.