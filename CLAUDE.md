# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

QuestForge is a Dalamud plugin for automating FFXIV (Final Fantasy XIV) quest completion. The project is currently in the **design/pre-implementation phase** — all specifications live in `questForgeDocs/`. The implementation follows a 10-phase roadmap defined in `questForgeDocs/NEXT_STEPS.md`.

## Repository Structure (Planned — Three Repos)

This repo (`questforge`, MIT) is the plugin source. Two companion repos will exist:
- `questforge-data` (CC-BY-4.0) — Quest definitions (JSON) and trace fixtures
- `questforge-tools` (MIT) — Schema validator CLI (`qf-validate`) and replay harness (`qf-trace`)

Planned project layout within this repo:
```
QuestForge.Engine/           # Pure C# — no Dalamud dependency (testability boundary)
QuestForge.Engine.Tests/     # xUnit, runs in CI without a game
QuestForge.Adapters/         # Adapter interface definitions only
QuestForge.Adapters.Dalamud/ # Concrete Dalamud-backed implementations
QuestForge.Plugin/           # Dalamud integration layer
QuestForge.UI/               # ImGui windows
```

## Build & Test Commands

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
| 10+ | Incremental quest corpus expansion | Ongoing |

**Current status: Phase 2 complete. Phase 3 (adapter interfaces + fakes) next.**