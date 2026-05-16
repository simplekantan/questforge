# FFXIV Quest Automator — Design Document

**Status:** v0.1 — foundational design; implementation through Phase 8 complete
**Working name:** TBD (placeholder: `QuestForge`)
**Maintainer model:** Solo driver accepting drive-by contributions through CI-gated PRs

---

## 1. Project goals

A Dalamud plugin that automates FFXIV quest completion for MSQ and class/job quests, with a focus on reliability, contributor-friendly quest authoring, and sustainable maintenance through CI rigor.

### 1.1 Explicit goals (ranked)

1. **Reliability** — fewer mid-quest failures than existing alternatives
2. **Test coverage and CI rigor** — automated gates catch bugs before users do
3. **Community quest authoring** — non-coders can contribute via tooling and validation
4. **Recovery from failure** — retry, resume, rollback as first-class concerns
5. **Observability** — structured logging, trace replay, debuggable failures

### 1.2 Explicit non-goals (v1)

- Side content beyond class/job quests (dailies, beast tribes, hunts, etc.)
- Combat automation (delegated to BossMod / Wrath / RSR)
- Dungeon and trial pathing (delegated to AutoDuty)
- In-zone navigation (delegated to vnavmesh)
- Inter-zone teleport (delegated to Lifestream)
- Dialogue skipping (delegated to TextAdvance)
- Designing adversarial evasion against game anti-cheat (see §8)

### 1.3 What "better than Questionable" means here

The reference comparison is [PunishXIV/Questionable](https://github.com/PunishXIV/Questionable). Questionable is a successful, mature project; this design borrows from it where appropriate and diverges where the original made trade-offs that limit reliability or contributor velocity.

Specific divergences:
- Explicit **hierarchical state machine** runtime model, not a flat imperative step list
- Separate **data repository** with versioned schema and runtime-fetched releases
- **Trace-replay regression testing** as a first-class test tier
- **Per-quest support status** surfaced to users, not all-or-nothing
- **Patch-day automation** for game data diffing and impact analysis

---

## 2. Repository structure

Three repositories. The coordination cost is real; it is paid for by the clean separation of concerns and the ability for non-plugin-developers to contribute to data without touching code.

### 2.1 `questforge` — plugin code (MIT)

The Dalamud plugin itself. Contains the engine, adapter implementations, UI, and configuration.

```
questforge/
├── src/
│   ├── QuestForge.Engine/           # Pure C#, no Dalamud dependency
│   ├── QuestForge.Engine.Tests/     # xUnit, runs in CI without game
│   ├── QuestForge.Adapters/         # Interfaces + concrete impls
│   ├── QuestForge.Adapters.Tests/   # Fake-game-state tests
│   ├── QuestForge.Plugin/           # Dalamud integration layer
│   └── QuestForge.UI/               # ImGui windows
├── .github/workflows/
└── LICENSE                          # MIT
```

### 2.2 `questforge-data` — quest data (CC-BY-4.0)

Quest definitions, fragments, traces, and the release pipeline.

```
questforge-data/
├── schema/
│   ├── quest.v1.json                # Generated from C# types in tools repo
│   └── CHANGELOG.md
├── quests/
│   ├── arr/
│   │   ├── msq/
│   │   └── class/
│   ├── heavensward/
│   ├── stormblood/
│   ├── shadowbringers/
│   ├── endwalker/
│   └── dawntrail/
├── fragments/                       # Reusable sub-sequences
├── traces/                          # Anonymized replay test inputs
├── manifest.json                    # questId → file path, hash, schemaVersion
└── LICENSE                          # CC-BY-4.0
```

### 2.3 `questforge-tools` — authoring & CI tooling (MIT)

CLI validators, schema generators, replay harness, optional authoring app.

```
questforge-tools/
├── src/
│   ├── QuestForge.Schema/           # C# types (source of truth)
│   ├── QuestForge.Validator/        # Semantic validation CLI
│   ├── QuestForge.Replay/           # Trace replay harness
│   └── QuestForge.Authoring/        # Avalonia authoring app (later)
└── LICENSE                          # MIT
```

### 2.4 Meta-coordination

To keep three-repo discipline sustainable for a solo maintainer:

- **Top-level meta repo** with a `Taskfile.yml` for cross-repo workflows (clone all, run full local validation, coordinated version bump, tag release across repos)
- **Renovate or Dependabot** configured so plugin auto-bumps its data-repo pin and data-repo auto-bumps its tools-repo pin via PR
- **GitHub project board** spanning all three repos for issue triage

### 2.5 Licensing rationale

- **MIT for code** — maximum reuse, compatible with Dalamud's AGPL via plugin boundary. The Dalamud team imposes no license requirement on plugins; combined-work copyleft applies at runtime composition but the source remains permissively licensed.
- **CC-BY-4.0 for data** — quest data is not clearly "software"; CC handles it cleanly. CC-BY requires attribution (vs CC0's public domain dedication) for stronger international enforceability.
- **DCO sign-off, not CLA** — `Signed-off-by:` trailer on every commit. Low contributor friction; sufficient legal chain of attribution; GitHub has native support.

---

## 3. Schema and data format

### 3.1 Canonical format: JSON

JSON is canonical. YAML is rejected as authoritative format despite real ergonomic advantages, because:

- Implicit type coercion (Norway problem, leading-zero loss, colon/hash parsing)
- Silent failure on misspelled keys
- Anchors/aliases don't survive round-trips through authoring tools
- Slower deserialization in C# vs `System.Text.Json` with source generators
- Authoring tools (proposed) emit JSON natively; YAML benefit collapses

JSON gets:
- Schema validation in VS Code/Rider out of the box
- Source-generated deserialization for fast cold start
- Predictable parsing across languages and tools

An **optional YAML import CLI** in `questforge-tools` is acceptable for contributors who insist; it converts to canonical JSON pre-commit.

### 3.2 Schema source of truth: C# types

Following Questionable's `QuestPathGenerator` pattern (this is the part of their design worth keeping):

- C# types in `QuestForge.Schema` are the source of truth
- JSON Schema is **generated** from those types as a CI artifact
- The authoring tool consumes the same C# types directly
- Engine deserializes JSON directly to those types via source-generated converters

This means schema changes are made in one place, propagate everywhere, and stay consistent.

### 3.3 Schema versioning

Every quest file declares its schema version. The plugin declares a supported schema range.

```json
{
  "schemaVersion": "1.3",
  "questId": 65,
  ...
}
```

```csharp
public static readonly VersionRange SupportedSchemas = VersionRange.Parse(">=1.0 <2.0");
```

When the plugin ships v2 supporting schema 2.0, old plugin v1 keeps pulling the latest data release on schema 1.x via the manifest's per-release compatibility matrix. No silent breakage on user machines.

---

## 4. Data delivery

### 4.1 Hybrid delivery model

Pure runtime fetch was rejected for trust and availability reasons. Pure bundled (Questionable's approach) was rejected for update latency. Hybrid:

- Plugin ships with a **bundled baseline** snapshot of quest data
- Plugin checks for updates against `questforge-data` GitHub Releases on a configurable schedule
- Updates are **opt-in by default** ("Update available — click to install"), not auto-applied
- Optional auto-update toggle for users who want it
- Last-known-good cache used when GitHub is unreachable
- Plugin can be pinned to a specific data version for stability

### 4.2 Avoiding rate limiting

GitHub's API rate limit (60/hr unauthenticated) is a real risk for naive implementations. Mitigations:

- **Check freshness via static `latest.json`** asset URL, not the API. CDN-served, no rate limit.
- **`If-None-Match` / `ETag` conditional requests** — 304 responses don't count.
- **Client-side cache** with 1-hour minimum TTL on the version check.
- **API only as fallback** if the static file is malformed.

Release asset downloads via GitHub's CDN are effectively unbounded at any realistic user scale.

### 4.3 Release signing and supply chain

A compromised data release auto-pushes to every user. Mitigations:

- **SHA256 checksum** on every release, verified before install
- **Maintainer key signature** on the manifest (Sigstore or GPG)
- **Release-only-from-protected-branch** via GitHub Actions, no manual push
- **2FA required** on the maintainer account, full stop
- **Pinned/opt-in updates by default** so a bad release doesn't auto-break everyone

### 4.4 Update timing policy

Updates are never applied mid-quest. The plugin will:

- Defer install until between quests, or on next plugin restart
- Surface the pending update in the UI but never interrupt execution
- Allow rollback to the previous version for one cycle

### 4.5 Release channels

- `stable` — default. Slow, well-tested.
- `testing` — opt-in. New schema, new quests, less verification.
- `nightly` — power users. Latest contributor PRs.

---

## 5. Plugin architecture

### 5.1 Three-layer separation

```
┌─────────────────────────────────────────────────────┐
│  Quest Engine                                       │
│  - Hierarchical state machine per quest             │
│  - Pure with respect to game state                  │
│  - Returns: Action | KeepObserving(timeout)         │
│  - No Dalamud, no hooks, no IO                      │
└─────────────────────────────────────────────────────┘
                       ↓ Actions      ↑ Observations
┌─────────────────────────────────────────────────────┐
│  Adapter Layer (interfaces)                         │
│  - IGameStateProvider                               │
│  - INavigator (vnavmesh today)                      │
│  - ITeleporter (Lifestream + native)                │
│  - IInteractor (TextAdvance)                        │
│  - ICombat (BossMod / Wrath / RSR)                  │
│  - ITimingProfile                                   │
│  - IDialogueResolver                                │
└─────────────────────────────────────────────────────┘
                       ↓                ↑
┌─────────────────────────────────────────────────────┐
│  Dalamud Integration                                │
│  - Services, ClientState, hooks, IPC                │
│  - Plugin lifecycle, configuration, UI              │
└─────────────────────────────────────────────────────┘
```

### 5.2 The engine is pure with respect to state, not time

The engine takes a `GameStateObservation` (snapshot + recent transition log) and returns one of:

- `Action(IAction request)` — do this next
- `KeepObserving(TimeSpan timeout, ObservationGoal goal)` — wait, recheck when state changes or timeout elapses
- `Done(QuestOutcome outcome)` — quest finished, success or terminal failure

This makes the engine fully testable in CI without timing fakery. Real timing comes from the adapter layer.

### 5.3 Hierarchical state machine, not behavior tree

Quests are mostly linear with branches and recovery. An HSM gives the necessary composition (superstates) and explicit transitions (including failure→recovery edges) without behavior tree subtleties (parallel nodes, Running semantics) that authors will misuse.

Contributors author quests in a **flat, recovery-block DSL** that compiles to the HSM:

```json
{
  "steps": [
    {
      "id": "go-to-momodi",
      "navigate": { "destination": "..." },
      "expect": "playerNear(npcId:1000236, radius:3.5)",
      "recover": {
        "onWrongZone": { "teleport": { "aetheryteId": 9 } },
        "onTimeout": { "retry": 3, "backoff": "exponential" }
      }
    },
    { "id": "talk", "interact": "npcId:1000236", "expect": "questFlag(65, 1)" }
  ]
}
```

The runtime constructs an HSM from this declarative form. Authors never see the state machine directly.

### 5.4 Postcondition discipline

After every action, the engine re-reads game state and verifies the declared postcondition. No "I think I did that" trust. If the postcondition isn't met after retry+backoff, the step fails up to its parent for recovery.

This single rule eliminates the largest class of bugs in step-runner architectures: divergence between believed state and actual state.

### 5.5 Travel as a composed concept

There is no DC travel requirement for MSQ/class quests (correction from earlier draft). Travel within and between zones uses:

- **Aetheryte teleport** (Lifestream / native) — between major nodes
- **Aethernet / ferry / airship** (Lifestream) — within or between sub-areas
- **On-foot** (vnavmesh) — within a map
- **Map-boundary walk-through** — for transitions requiring specific gates

The engine exposes a single `Travel` step type. Underneath, a strategy selector picks the optimal combination given current state (attuned aetherytes, gil, combat status, mount availability). Failure modes are recovered per-strategy.

---

## 6. Testing strategy

### 6.1 Five tiers, with honest ROI ranking

| Tier | Type | Where | ROI |
|------|------|-------|-----|
| 1 | Schema validation | tools repo, runs in data repo CI | Necessary, low value |
| 2 | **Semantic validation** | tools repo, runs in data repo CI | **Highest — build first** |
| 3 | Engine unit tests | plugin repo CI | Moderate; catches deterministic logic bugs |
| 4 | **Trace replay** | data repo CI | High after corpus exists |
| 5 | E2E smoke (manual) | optional alt account | Highest reliability, expensive |

### 6.2 Tier 2 — semantic validation (the workhorse)

Validates that quest data is internally consistent and externally references valid game data:

- Referenced aetheryte IDs exist in current game data
- Referenced NPC IDs exist
- Coordinates fall within the declared territory's bounding box
- Quest prerequisites form a DAG (no cycles)
- Fragments aren't orphaned
- Step IDs unique within a quest
- Postconditions reference valid game state predicates
- Recovery handlers reference defined step IDs

Runs on every PR. Catches roughly 60-70% of contributor bugs at author time, before any code runs.

### 6.3 Tier 4 — trace replay (the regression workhorse)

Traces from real quest runs are anonymized and committed to `traces/`. CI replays each trace against the current engine code and asserts that the same input state produces the same action decision.

- Tagged with the game patch they were recorded against
- Schema or engine changes that would break real quests fail loudly before shipping
- Same format as the runtime logger (see §11) so user-submitted bug-report logs are valid replay inputs

### 6.4 Engine unit test target

≥80% line coverage on `QuestForge.Engine`. Adapter glue is excluded from this target because the bug-rich code lives there and is better covered by trace replay than by unit tests.

### 6.5 CI gating for solo maintainer

Drive-by contributions only merge when CI passes. Branch protection on `main` in all three repos requires:

- Schema validation passing
- Semantic validation passing
- All engine unit tests passing
- All trace replay tests passing
- Build success across all target frameworks
- For plugin repo: a "no engine code changed without test diff" check

`CODEOWNERS` routes `/quests/**` to a trusted contributors team (auto-merge eligible) and `/src/**` to the maintainer only.

A bot comments on quest PRs with structured validation feedback so contributors self-correct without maintainer intervention.

---

## 7. Patch-day workflow

FFXIV ships major patches roughly every four months. The naive "play through and fix what breaks" approach is how every plugin in this category dies around patch 8. Automation is required for sustainability.

### 7.1 Game data diff (daily, in tools repo)

A GitHub Actions workflow fetches the latest game data sheets (from public dumps depended on by Lumina consumers) and diffs against the previously-stored snapshot. On change:

- Commits the new snapshot
- Opens a `patch-day` issue with a structured summary (quests added, sequence flags changed, NPCs moved, aetherytes added)

### 7.2 Impact analysis (triggered, in data repo)

Cross-references the game data delta against the quest corpus. For each affected quest, generates a risk-scored entry in a single tracking issue:

- "Quest 1234: NPC 5678 moved 15 units, recheck waypoints"
- "Quest 9012: uses sequence flags that no longer exist, manual review required"

Prioritized by risk × user impact (MSQ critical path ranks highest).

### 7.3 Replay regression (per game-data update, in data repo)

Existing trace-replay tests run against the new game data. Tagged traces flag divergences for human review. Divergence ≠ broken, but every divergence requires eyeballs.

### 7.4 Patch-day emergency procedure (plugin repo)

A `patch-emergency` PR label bypasses the 24-hour replay-test wait but still requires basic tests. Loudly documented as a "use 2-3 times per year" mechanism, not a habit.

### 7.5 Per-quest patch verification

Every quest carries `lastVerifiedPatch` metadata. The UI surfaces three states:

- ✅ Verified for current patch
- ⚠️ Last verified on prior patch — may work, no guarantees
- 🚫 Known broken on current patch — fix tracked at [link]

The verify button in the plugin (see §9) lets users contribute verification without touching code.

### 7.6 Hard dependencies on game internals

When SE changes memory layout, signatures, or IPC contracts, no automation helps. The plugin fails loud (refuses to start) when dependency versions don't match expected ranges, rather than running with potentially corrupted state.

---

## 8. Input timing and "human-like" inputs

### 8.1 Position

Predictable inputs are a quality problem (they feel bad and look bad) before they are a detection problem. The design produces reasonable-looking input distributions because that is good UX. The project does not optimize for adversarial evasion against specific anti-cheat systems — that changes the project's character in ways the maintainer does not endorse.

The plugin remains against ToS regardless. Users assume their own risk. The UI states this clearly.

### 8.2 The `ITimingProfile` abstraction

The engine never makes timing decisions directly. Every action that reaches the adapter layer queries a `TimingProfile`:

```csharp
public interface ITimingProfile {
    TimeSpan ReactionDelay(StimulusType stimulus);
    TimeSpan DecisionDelay(int choiceCount);
    TimeSpan InterActionGap();
    bool ShouldTakeBreak(SessionContext ctx);
    TimeSpan BreakDuration();
}
```

Implementations:

- `HumanLikeProfile` — log-normal sampling with empirically reasonable parameters
- `FastProfile` — minimal but nonzero floors, for users who explicitly opt in
- `RecordedProfile` — replays distributions captured from real human sessions

Engine logic stays deterministic for tests; the profile is the seam where non-determinism enters.

### 8.3 Timing seams

| Action | Profile call | Typical range |
|---|---|---|
| Dialogue option appears → select | `ReactionDelay(Dialogue) + DecisionDelay(N)` | 400–1500ms |
| NPC interact → first dialogue advance | `ReactionDelay(NewWindow)` | 300–800ms |
| Cutscene ends → next action | `ReactionDelay(SceneTransition)` | 500–2000ms |
| Two consecutive movement segments | `InterActionGap()` | 50–300ms |
| Quest accept → start moving | `DecisionDelay(1) + ReactionDelay(QuestStart)` | 600–2000ms |
| After every N actions | `ShouldTakeBreak()` → maybe `BreakDuration()` | 30s–5min |

### 8.4 Design invariants

- **No two action timestamps may be equal in logs.** The `InterActionGap()` floor enforces this at engine level.
- **User can override the profile.** `FastProfile` is an explicit configurable option, not hidden. Informed consent matters.
- **Distributions are model-driven, not uniform jitter.** Log-normal for reaction times; constraint-driven for decision times (Hick's law scaling).

### 8.5 What is explicitly out of scope

Mouse-path simulation, packet-level fingerprint randomization, anti-cheat-specific evasion. The architecture leaves a clean seam for these if someone forks the project, but they are not first-class design goals.

---

## 9. Per-quest support status UI

### 9.1 What users actually need to know

Five questions, in priority:

1. Will this work right now?
2. If not, why and is it being fixed?
3. If yes, how confident should I be?
4. What patch was it last verified on?
5. Has anyone else recently completed it without issues?

A single traffic-light status answers none of these well.

### 9.2 Composed status from independent signals

Per quest, the UI surfaces:

- **Implementation status** — `complete` / `partial` / `none` (from data repo)
- **Schema status** — `current` / `deprecated` / `unsupported` (from version manifest)
- **Last verified patch** — string (manual metadata)
- **CI replay status** — `passing` / `failing` / `no-traces` (polled from GitHub Actions API, 1hr cache)
- **Open issues** — count of GitHub issues tagged with this quest ID (1hr cache)
- **Community success rate** — opt-in telemetry, last 14 days, minimum 10 runs to display

Example UI panels:

```
Quest #1234 — Coming to Ul'dah
  Status:        Supported
  Verified:      Patch 7.4 (current)
  CI replay:     ✓ Passing (2h ago)
  Open issues:   0
  Recent runs:   97% success (412 runs, 14 days)
```

```
Quest #9012 — Defenders of Eorzea
  Status:        ✗ Currently broken
  Verified:      Patch 7.2 (2 patches old)
  CI replay:     ✗ Failing on step 4
  Tracked at:    github.com/.../issues/142
  Last working:  Schema 1.4, patch 7.2
                 [Pin to schema 1.4 to attempt]
```

### 9.3 Why this matters

This UI is the **contract with users**. It tells them what to expect and what not to file new issues about. It cuts duplicate-issue volume materially.

It is also a contributor recruitment tool. The list of "verified on patch 7.3, not yet re-verified on 7.4" is a literal todo list. The plugin's in-game **"Verify this quest"** button records a clean trace and opens a PR updating `lastVerifiedPatch`. Non-coders contribute by playing the game with the plugin watching.

### 9.4 Telemetry infrastructure

- Tiny self-hostable service (Go or Rust + SQLite)
- Single endpoint, payload `{questId, schemaVer, patchVer, outcome, anonymizedRunId}`
- No PII, no character data, no IP retention
- Server code published MIT alongside the plugin
- Strictly opt-in with a clear welcome-wizard prompt explaining what is collected

---

## 10. Trace format

Traces are the append-only record of one quest execution attempt. The format is the shared substrate for replay regression tests, the live recorder, user bug reports, and structured-log cross-reference.

**Full specification:** [`TRACE_FORMAT.md`](./TRACE_FORMAT.md).

Summary of properties that bear on the rest of the design:

- **JSONL, one event per line.** Append-safe, streamable, diffable. Verbosity is acceptable at expected scale.
- **`seq` is authoritative for ordering**, not `ts`. `ts` is a monotonic millisecond offset from `run.start`; the only wall-clock value in the trace is `run.start.data.wallClockUtc`, which is stripped on redaction.
- **Fourteen event types:** `run.start`, `run.end`, `observation`, `decision`, `action.submitted`, `action.completed`, `recovery.triggered`, `adapter.error`, `engine.error`, `player.died`, plus four diagnostic events (`dialogue.resolved`, `reward.selected`, `travel.strategy`, `navmesh.wait`) that are recorded but not verified during replay.
- **Observations record only what the engine actually consumed** during the tick — not full game state. Collection queries record `{queried, accessed}` to preserve selection-logic determinism without recording untouched state.
- **Replay determinism** is anchored by `runId`: engine seed is derived as `SHA256(runId)[:8]`, replay always reuses the recorded `runId`, and the engine's decision-affecting config is recorded in full (not hashed) as `engineConfig`.
- **Privacy-by-construction.** The recording proxy enforces an allowlist of recordable state keys; every trace is safe to attach to a public GitHub issue after redaction, and `qf-trace redact` is the single canonical redaction operation.
- **Canonical traces never rotate.** One single-file canonical trace per quest serves as the CI replay fixture, tracked via git LFS. Live recordings may rotate; canonical fixtures may not.

The trace format and the structured live log are deliberately separate files. The trace optimizes for replay and shareability; the log optimizes for wall-clock correlation with system logs. `qf-trace timestamp` converts between them when after-the-fact correlation is needed.

---

## 11. Adapter interfaces

The engine references ten adapter interfaces that define the contract between engine logic and the rest of the world (Dalamud, vnavmesh, Lifestream, TextAdvance, BossMod / WrathCombo / RSR, Stylist, etc.). The engine code itself depends on none of those — only on the interface definitions in `QuestForge.Adapters`. This is what makes the engine testable in CI without a game.

**Full specification:** [`ADAPTERS.md`](./ADAPTERS.md).

The ten interfaces:

- **`IGameStateProvider`** — read-only view of game state (player, world, NPCs, interactables, UI, inventory, NG+ state, derived facts)
- **`IQuestState`** — quest progression queries (status, sequence, flags, availability, unlock reasons, rewards)
- **`INavigator`** — in-zone movement (wraps vnavmesh, including navmesh-generation status)
- **`ITeleporter`** — aetheryte travel, aethernet hops, and `/return` (wraps Lifestream + native teleport)
- **`IInteractor`** — NPCs, interactable objects, dialogue (by sheet reference), prompts, quest accept/complete/abandon, duty entry (full duty and SPD), chat messages, emotes, item use (target NPC/object/position)
- **`ICombat`** — combat delegation to BossMod / WrathCombo / RotationSolverReborn, plus direct action use for quest-driven action steps
- **`IGearManager`** — gear inspection and equipping (game built-in or Stylist), job changes, gear condition monitoring, auto-repair
- **`IMinigameSkipper`** — opt-in minigame skip handlers (sniping minigames implemented in v1; others stubbed)
- **`ITimingProfile`** — input timing (the seam where non-determinism enters; seeded deterministically per run)
- **`IDialogueResolver`** — localization, resolving game-data sheet references to per-language text

The adapter abstraction is also a substitution boundary: vnavmesh, Lifestream, TextAdvance, BossMod, AutoDuty, Stylist are the **current** implementations. Future ecosystem changes (new plugins, dying plugins) require only new adapter implementations — engine code and quest data don't change.

Key properties that bear on the rest of the design:

- **All async, all cancellable, all returning `Result<T>`.** Failures are values that participate in engine logic and become trace-recordable.
- **No Dalamud types in the adapter layer.** Game concepts are expressed as adapter-layer types; the plugin layer translates.
- **The recording proxy wraps `IGameStateProvider` and `IQuestState`** at recording time. Replay tests substitute `ReplayGameStateProvider` and `ReplayQuestState` reading from the trace.
- **Composite reads** (`PlayerStateSnapshot`, `UiState`, `TravelCapability`) exist where temporally-tight state needs consistent snapshots. They're recorded as one observation key with structured value.
- **`Action.AwaitUserCompletion`** is the engine's escape hatch for "automation can't do this part." The engine polls the postcondition indefinitely; the UI surfaces the situation to the user; resumption is automatic on observed completion.
- **Death recovery is context-routed by `InstanceKind`.** Open-world deaths trigger return-to-aetheryte; dungeon deaths are ignored (combat plugin handles); SPD deaths feed into per-SPD retry logic. Dungeon deaths never increment quest-level failure counters.
- **Three distinct failure counters:** `MaxConsecutiveStepFailures`, `MaxDutyRetries` (per-SPD), `MaxConsecutiveQuestFailures`. Each has independent scope and triggers.
- **Duty entry** defaults to `DutyFallbackPolicy.SupportOnly` for full duties — try Duty Support, fail rather than queue with random players. SPDs use direct entry through their trigger NPC/object.
- **Gear auto-repair** monitors equipped condition at natural pause points (pre-duty, pre-combat, post-quest, periodic). Self-repair preferred when available; falls back to nearest mender NPC.
- **NG+ awareness:** on run start, engine reads NG+ state and adjusts behavior (skip reward selection, pre-unlocked SPD difficulties, no retainer access). Quest data is identical for first playthrough and NG+.

---

## 12. Quest schema

The data format quest authors write against. Defines the shape of a quest file, the predicate language used in `expect`/`skipIf`/branch conditions, step type taxonomy, validation rules, and the worked ARR example that proves the design.

**Full specification:** [`SCHEMA.md`](./SCHEMA.md).

Key properties that bear on the rest of the design:

- **JSON files, sequence-grouped steps.** Each quest's content is organized by FFXIV's in-game sequence number, matching how the game itself tracks quest progress. Resume-after-crash is trivial: read `questSequence`, jump to the matching block.
- **Composite step primitives.** Twelve step types (`travel`, `talk`, `interact-object`, `pickup-item`, `accept`, `turn-in`, `combat`, `duty`, `cutscene`, `say-chat-message`, `use-emote`, `equip-gear-for-quest`, `equip-best-gear`, `change-job`, `minigame`, `await-user`, `branch`, `fragment`) covering all v1 quest patterns. Each expands to multiple low-level primitives internally; authors describe quests in domain language.
- **Dialogue by sheet reference, not invented keys.** Quest data references FFXIV's text data sheets directly (e.g., `TEXT_JOBDRK301_02054_Q1_000_115`). Square Enix defines the identifiers; Lumina handles localization. No invented namespaces, no separate lookup table, no multi-language CI verification needed — translations are already handled at the game-data level.
- **Predicate language for `expect`/`skipIf`/branches.** A small declarative language (`questSequence(65) >= 3`, `questFlagAll(65, 1, 2, 3)`, etc.) with all state functions drawn from `IGameStateProvider` and `IQuestState`. Validator parses every predicate at PR time.
- **Chain support.** Quests link forward and backward (`chain.previous`, `chain.next`) with conditional branching for class-specific paths (e.g., starting Gladiator goes to Guild A; starting Pugilist goes to Guild B). Validator enforces bidirectional consistency.
- **Default recovery ladders, per-step override.** Engine has sensible defaults for navigation/interaction/combat/death failures. Quests rarely need explicit recovery blocks; when they do, the override is local.
- **Fragments for reusable sub-sequences.** Travel-between-cities and similar patterns extract to `fragments/`. No nested fragments in v1.

---

## 13. Authoring mode

In-plugin tooling so contributors can author quest definitions by playing the game with debug panels visible — without manually digging through game data files or Dalamud's developer tools.

**Full specification:** [`AUTHORING.md`](./AUTHORING.md).

Key properties:

- **Two sub-modes.** Inspect (passive observation, works alongside running automation) and Author (active recording, mutually exclusive with engine execution).
- **Three debug panels.** Player state (position, zone, job, mount), quest state (sequence and flags for accepted quests with change highlighting), interaction (current target NPC, current dialogue's sheet references).
- **Single "Record current action" button** with contextual step-type inference. The plugin observes the player's action, infers which step type best represents it, and presents a preview before recording.
- **Per-quest draft management.** Drafts live in plugin config storage with versioned backups (last 5 retained automatically). Export produces a JSON file ready to commit to `questforge-data`.
- **Lazy load with idle unload.** Authoring infrastructure isn't loaded until first activation; unloads after 30-minute idle to keep the base plugin lean.
- **No auto-abandon.** Authoring mode never abandons quests for the user. Resume-vs-restart authoring is an explicit user choice with clear instructions.

---

## 14. Other concerns (designed, not yet specified)

These will get full sections during implementation but are flagged here so they aren't forgotten.

### 14.1 Inventory pressure

Many quests require free inventory slots. The engine models inventory as a precondition. Recovery options: open retainer, sell to vendor, abandon non-essential drops, prompt user.

### 14.2 Cutscene handling

TextAdvance covers skipping but cutscene durations are variable with skip-prevention frames. The engine's "wait for postcondition" needs a generous, configurable timeout policy specifically for cutscene states.

### 14.3 Error reporting

User-friendly auto-bug-report flow: one click generates a redacted package with recent trace, plugin/data/schema/patch versions, and a fingerprint of the failure. No character names, no positions outside the failing zone.

### 14.4 Contributor onboarding

- Per-repo `CONTRIBUTING.md` that is specific, not generic
- "Your first quest contribution" walkthrough for non-developers — leveraging authoring mode
- `good-first-quest` issue label with a curated list
- Validation bot posting structured PR feedback

---

## 15. Open work items

**Foundation phase (complete):**

1. ✅ Consolidated design doc (this document)
2. ✅ Trace format specification — see [`TRACE_FORMAT.md`](./TRACE_FORMAT.md)
3. ✅ Adapter interfaces specification — see [`ADAPTERS.md`](./ADAPTERS.md)
4. ✅ Quest schema specification — see [`SCHEMA.md`](./SCHEMA.md)
5. ✅ Authoring mode specification — see [`AUTHORING.md`](./AUTHORING.md)

**Implementation phase (in suggested order):**

6. **One-day spike** — minimal hardcoded plugin proving vnavmesh + Lifestream + TextAdvance IPC contracts. Throwaway. Validates assumptions before any real work.
7. **Schema validator and CI** — C# types for `Quest` + step taxonomy, structural validator, predicate parser, GitHub Actions on a placeholder data repo. Pays off across every future contribution.
8. **Adapter interfaces and fakes** — interface definitions in `QuestForge.Adapters`, in-memory fakes for testing. Enables engine unit tests before engine exists.
9. **Engine skeleton** — HSM evaluator wired through fakes, starting with `travel` and `talk` step types only. Run one synthetic quest end-to-end in tests.
10. **Trace recorder** — early, before replay. Captures real plugin runs from day one for debugging.
11. **Dalamud-backed adapter implementations** — real `IGameStateProvider`, `INavigator`, `ITeleporter`, `IInteractor` against Dalamud + the dependency plugins.
12. **First real quest end-to-end** — pick the simplest ARR quest, run it, capture canonical trace as fixture.
13. **Replay test harness** — CI replays the canonical fixture against current engine on every PR.
14. **Authoring mode v0** — debug panels first, recording workflow second.
15. **Incremental corpus expansion** — additional step types as quests demand them, additional quests as authoring matures.

The foundation phase deliberately over-specifies for a solo project. Treat the docs as a snapshot, not a contract — revise as implementation surfaces what we got wrong.

---

## Appendix A: Design decisions and their rationale

| Decision | Alternative considered | Why |
|----------|----------------------|-----|
| Three repos | Monorepo, two repos | Worth the coordination cost for clean separation; mitigated by meta-repo tooling |
| JSON over YAML | YAML canonical | Type coercion, parser performance, tool ergonomics |
| C# types as schema source | Hand-written JSON Schema | Single source of truth; matches Questionable's working pattern |
| Hybrid delivery | Pure CDN, pure bundled | Availability + update latency tradeoff |
| HSM over behavior tree | Behavior tree, flat steps | Composition without subtle BT semantics; serializable |
| Pure-with-respect-to-state engine | Pure-with-respect-to-time engine | Reflects reality of FFXIV state observation |
| Tier 2 semantic validation as priority | Tier 4 replay first | Replay has no corpus on day 1; semantic validation pays off immediately |
| MIT plugin / CC-BY data / DCO | AGPL / CC0 / CLA | Permissive where possible, sufficient legal chain without contributor friction |
| Human-like timing as quality goal | Detection-evasion design goal | Project character; sustainability of contributions |
| JSONL trace format, `seq`-authoritative | Binary, per-event wall-clock | Append-safe, diffable, replay-deterministic across clock anomalies |
| Ten adapter interfaces | Seven (quest state merged); fewer adapters | Quest state earns its own; gear and minigame-skip add focused responsibilities |
| `Result<T>` for routine failures | Exceptions | Failures must be part of engine logic and trace-recordable |
| `SupportOnly` duty default | `SupportThenFinder` | Less surprising; doesn't queue users with random players |
| Death recovery returns home | Wait for raise | Reliable, free, deterministic; doesn't depend on other players |
| Death context-routed by `InstanceKind` | Single death recovery path | Dungeon deaths must not increment quest failure counters; routing prevents confusion |
| Three failure counters, not one | Single global failure counter | Step, duty, and quest failures have different scopes; conflating them produces false alarms |
| SPDs via direct trigger interaction | Same flow as full duties | SPDs don't go through Duty Finder; entry mechanism is fundamentally different |
| Per-SPD session memory for difficulty | Session-global flag | Difficulty unlocks are per-specific-SPD, not global; tracking matches game behavior |
| `AwaitUserCompletion` indefinite | 60-min timeout | Paternalistic timeout breaks legitimate "do duty later" workflow |
| Sequence-grouped quest steps | Flat step list | Aligns with FFXIV's quest state machine; trivial resume |
| Sheet references for dialogue | Invented namespace + lookup table | Use SE's own data; no translation layer or multi-lang CI |
| Predicate language as string | Structured AST | Readability for authors; parser lives in tools |
| Composite step primitives | Fully flat primitives | Human-readable; engine handles expansion uniformly |
| Minigame skipping opt-in | On by default | Most ToS-adjacent capability; informed consent matters |
| `PreferredDutyDifficulty` as plugin setting | Per-quest field | Difficulty preference is user-level, not author-level |
| Gear auto-repair at pause points | On-demand only | Proactive prevention vs. reactive failure recovery |
| Authoring mode in-plugin | External tool or web app | Same observation pipeline as engine; what authors see is what engine sees |
| Single "Record" button with inference | Type-specific buttons | Authors often don't know what action they just performed |

## Appendix B: Glossary

- **HSM** — Hierarchical State Machine
- **DCO** — Developer Certificate of Origin, contributor sign-off mechanism
- **PAC** — Plugin Approval Committee (Dalamud's review team)
- **IPC** — Inter-Plugin Communication (Dalamud's mechanism for plugins to call each other)
- **MSQ** — Main Scenario Quest
- **DC** — Data Center
- **Trace** — append-only JSONL log of engine observations, decisions, actions, and outcomes
- **Replay** — running stored traces against current engine code to detect regressions
- **Adapter** — interface defining the contract between the engine and game/plugin IO
- **Recording proxy** — wrapper around adapter interfaces that captures every read for trace recording
- **Composite read** — adapter method returning structured state across multiple fields, recorded as one observation key
- **`AwaitUserCompletion`** — engine action indicating automation must pause and observe for user-initiated progress
- **Duty Support** — FFXIV's NPC-companion system for soloing dungeons (Trust / Adventurer Squadron)
- **Recovery ladder** — ordered list of remediation attempts the engine applies on adapter failure
- **Return** — FFXIV's `/return` command, teleporting the player to their home aetheryte
- **Sequence** — FFXIV's per-quest progression marker; the game advances it at specific points
- **Sheet reference** — Direct identifier into FFXIV's text data sheets (e.g., `TEXT_JOBDRK301_02054_Q1_000_115`); used by quest data to reference dialogue without inventing keys
- **Predicate** — Declarative state expression used in `expect`, `skipIf`, branch `when`, and recovery triggers
- **Fragment** — Reusable sub-sequence of quest steps; lives in `fragments/` and referenced by other quests via `${name}` parameter substitution
- **Authoring mode** — In-plugin tooling for quest contributors; has Inspect and Author sub-modes
- **Draft** — A quest file under development in authoring mode, stored locally with versioned backups
