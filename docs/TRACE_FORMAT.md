# Trace Format Specification

**Status:** v1 — implemented through Phase 10; event types in §5 are stable; file layout (§3) revised in Phase 7; Phase 10 tooling reads the flat Phase 7+ shape
**Format version:** `v: 1`
**Owners:** QuestForge maintainers
**Related:** [DESIGN.md](./DESIGN.md), [ADAPTERS.md](./ADAPTERS.md)

---

## 1. Purpose

A trace is an append-only, line-oriented record of one quest execution attempt. The same format serves four consumers:

1. **Replay regression tests** — CI replays committed traces against current engine code to detect regressions. Determinism is non-negotiable.
2. **Trace recorder output** — the running plugin writes traces live for debugging.
3. **User bug reports** — redacted traces are attached to GitHub issues.
4. **Structured logging input** — the structured logger (separate file) cross-references traces by `runId` and `seq`.

Where consumer needs conflict, replay determinism wins.

---

## 2. Format

JSONL (newline-delimited JSON). One event per line, UTF-8, `\n` line terminators (not `\r\n`).

Rationale: append-safe, streamable, diffable, greppable, universally parseable, additive-evolution-friendly. The verbosity cost (4-10x vs binary) is acceptable at expected scale (~1000 events per quest run, ~200 bytes per event, ≤1 MB per file, well-compressed under gzip).

If profiling later proves disk size dominates a real workflow, the format may be re-encoded to MessagePack with JSONL retained as the canonical interchange representation. This is **not** done preemptively.

---

## 3. File layout

**User traces** (opt-in, written by the plugin during a run):

```
%APPDATA%\XIVLauncher\pluginConfigs\QuestForge\traces\
└── <runId>.jsonl
```

Enabled via `/qf config trace on` or automatically when Authoring mode is active. Off by default — traces can be large (hundreds of KB per run). The user controls retention; no automatic rotation in Phase 8.

**Engine fixture files** (CI regression corpus, not full traces):

```
questforge-data/
└── fixtures/
    └── engine/
        ├── simple-linear-acceptance.json
        └── ...
```

The Phase 7 implementation replaced the original "canonical trace in `questforge-data`" plan with transition-based engine fixtures (see `docs/FIXTURES.md`). Fixtures capture only the unique consecutive `(stepId, actionType)` decisions the engine emits, not tick-by-tick observations. This makes them ~50× smaller, human-readable, and regeneration-friendly. Full-trace replay is retained as a future tool (`qf-trace replay`, Phase 10) but is no longer the primary CI regression mechanism.

---

## 4. Event shape

Every event is a JSON object with these required top-level fields:

```json
{
  "v": 1,
  "seq": 42,
  "ts": 1234,
  "type": "decision",
  "runId": "7f3a9c12",
  "data": { ... }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `v` | int | Trace format schema version. Independent of quest schema version. |
| `seq` | int | Monotonic per-run sequence number. Starts at 0. **Authoritative for ordering.** |
| `ts` | int | Milliseconds elapsed since `run.start`, measured from a monotonic clock. Diagnostic only — not used by replay. |
| `type` | string | One of the seventeen event types defined in §5. |
| `runId` | string | 8-character lowercase hex. Identical across recording and replay for the same run. |
| `data` | object | Type-specific payload. May be empty for some types. |

### 4.1 Ordering and timing semantics

- **`seq` is the only authoritative ordering.** Replay sequences by `seq`. Analysis tools should treat `seq` as truth.
- **`ts` is diagnostic.** It records the monotonic offset (in ms) from `run.start`. Reconstructed wall-clock time is `run.start.data.wallClockUtc + ts`, accurate to within any clock drift over the run duration.
- **Resolution: milliseconds.** Sub-millisecond detail is intentionally discarded.
- **Source: a monotonic clock** (e.g., .NET `Stopwatch`). Wall-clock is captured **once** in `run.start.data.wallClockUtc` and never per-event.
- Non-monotonic `ts` (clock anomaly during recording) **does not invalidate the trace**. The validator emits a warning; replay is unaffected.

### 4.2 Per-tick concatenation

The engine host processes ticks in this loop:

1. Read game state (zero or more property accesses recorded as `observation` events via the recording proxy)
2. Decide next action (one `decision`)
3. Submit and dispatch action (`action.submitted` → `action.completed`)
4. **Ambient quest flag poll** — `EngineHost` calls `GetQuestFlags` on the active quest after each dispatch when tracing is enabled. The dedup layer emits an `observation` event only when the flags byte changes. This captures quest flag bit transitions as they occur naturally during a run without requiring `questFlag()` predicates in the quest file — making unknown bits discoverable for authoring.

**Exactly one `decision` event follows each group of `observation` events within a single engine tick.** If the engine reads state multiple times within a tick, each read is a separate `observation` event (deduplication suppresses repeated identical reads across ticks, not within a tick).

---

## 5. Event types

Seventeen types, organized by purpose:

- **Lifecycle** (§5.1): `run.start`, `run.end`
- **Replay-critical** (§5.2): `observation`, `decision`
- **Action lifecycle** (§5.3): `action.submitted`, `action.completed`
- **Exceptional** (§5.4): `recovery.triggered`, `adapter.error`, `engine.error`, `player.died`
- **Diagnostic** (§5.5): `dialogue.resolved`, `reward.selected`, `travel.strategy`, `navmesh.wait`, `gear.repair`, `duty.retry`, `interaction.retried`

Diagnostic events are recorded but not verified during replay. They aid debugging, telemetry, and post-hoc analysis without participating in determinism checks.

### 5.1 Lifecycle: `run.start`, `run.end`

`run.start` is always `seq: 0` and carries all run metadata.

```json
{
  "v": 1,
  "seq": 0,
  "ts": 0,
  "type": "run.start",
  "runId": "7f3a9c12",
  "data": {
    "questId": 65,
    "schemaVer": "1.3",
    "pluginVer": "0.4.1",
    "dataVer": "2026.05.10-stable",
    "dataHash": "sha256:9f1e...",
    "patchVer": "7.4",
    "wallClockUtc": "2026-05-12T14:22:01.234Z",
    "engineConfig": { ... },
    "newGamePlus": null,
    "precedingRunId": null
  }
}
```

| Field | Description |
|-------|-------------|
| `questId` | The quest being attempted. |
| `schemaVer` | Quest schema version this quest file was written against. |
| `pluginVer` | Plugin version at recording time. |
| `dataVer` | Data release version string. Human-readable. |
| `dataHash` | SHA256 of the quest JSON file plus the transitive hash of every referenced fragment. Computed at record time, verified at replay. |
| `patchVer` | FFXIV game patch version, e.g. `"7.4"`. |
| `wallClockUtc` | ISO 8601 UTC timestamp at the moment of `run.start`. **Only** wall-clock time in the entire trace. Stripped during redaction. |
| `engineConfig` | Full `EngineDecisionConfig` object (see §8). |
| `newGamePlus` | `null` for normal play; `{active: bool, chapterId?: int, chapterName?: string}` when in NG+. Affects engine behavior (skip rewards, pre-unlocked SPD difficulties). Replay determinism depends on this matching at replay time. |
| `precedingRunId` | If the engine reattempted this quest within the same plugin session, the `runId` of the previous attempt. Otherwise `null`. Chains break across plugin restarts. |

`run.end` closes the trace.

```json
{
  "v": 1,
  "seq": 847,
  "ts": 482311,
  "type": "run.end",
  "runId": "7f3a9c12",
  "data": {
    "outcome": "Success",
    "durationMs": 482311,
    "stepsCompleted": 12,
    "recoveriesTriggered": 1
  }
}
```

`outcome` is one of four values matching the `RunOutcome` enum:

- **`Success`** — quest completed normally
- **`CompletionFailure`** — engine triggered `MaxConsecutiveQuestFailures` policy; quest remains in journal
- **`StopQuestAutomation`** — user clicked the stop button; quest remains in journal
- **`Abandon`** — user explicitly abandoned the quest through the plugin UI; quest removed from journal (only outcome where `IInteractor.AbandonQuest` is called)

The engine never auto-abandons quests. `Abandon` only appears when the user invokes the explicit abandon action.

### 5.2 Replay-critical: `observation`, `decision`

`observation` records what the engine read from game state during one tick. The recording proxy captures property accesses on `IGameStateProvider` and emits them here.

```json
{
  "v": 1,
  "seq": 15,
  "ts": 1240,
  "type": "observation",
  "runId": "7f3a9c12",
  "data": {
    "node": "go-to-momodi",
    "observed": {
      "questFlag(65)": 0,
      "playerZone": 130,
      "playerPos": { "x": -9.4, "y": 40.2, "z": 12.1 },
      "nearbyNpcs": {
        "queried": [1000236, 1000241, 1000252],
        "accessed": [1000236]
      },
      "inCombat": false
    }
  }
}
```

**Critical rule:** `observation` records *only fields the engine actually consumed* during this tick. Not the full game state. This is what makes traces safe to share (no broad state dump) and replay correct (changes to fields the engine never reads cannot affect decisions).

**Collection queries** use the `{queried, accessed}` shape. `queried` is the full collection returned to the engine; `accessed` is the subset whose elements were subsequently inspected. Replay must reproduce the full `queried` collection so the engine's selection logic sees the same alternatives.

`decision` records what the engine returned.

```json
{
  "v": 1,
  "seq": 16,
  "ts": 1241,
  "type": "decision",
  "runId": "7f3a9c12",
  "data": {
    "node": "go-to-momodi",
    "chose": { "action": "interact", "npcId": 1000236 },
    "rejected": [
      { "action": "navigate", "reason": "playerNear=true" }
    ]
  }
}
```

`chose` is the engine's returned action. `rejected` is optional and lists alternatives the engine considered but did not pick, with a reason — diagnostic only, not verified during replay.

### 5.3 Action lifecycle: `action.submitted`, `action.completed`

```json
{
  "v": 1,
  "seq": 17,
  "ts": 1305,
  "type": "action.submitted",
  "runId": "7f3a9c12",
  "data": {
    "adapter": "IInteractor",
    "method": "Interact",
    "params": { "npcId": 1000236 },
    "expectedPostcondition": "dialogueOpen()"
  }
}

{
  "v": 1,
  "seq": 18,
  "ts": 1917,
  "type": "action.completed",
  "runId": "7f3a9c12",
  "data": {
    "success": true,
    "durationMs": 612,
    "postconditionMet": true
  }
}
```

These are emitted by the engine, not the adapter directly. They are reconstructed and verified during replay against the recorded values.

### 5.4 Exceptional: `recovery.triggered`, `adapter.error`, `engine.error`, `player.died`

```json
{
  "v": 1,
  "seq": 104,
  "ts": 25800,
  "type": "recovery.triggered",
  "runId": "7f3a9c12",
  "data": {
    "fromNode": "talk-to-thancred",
    "reason": "timeout",
    "recoverNode": "return-to-zone",
    "attemptNumber": 2
  }
}

{
  "v": 1,
  "seq": 845,
  "ts": 481202,
  "type": "adapter.error",
  "runId": "7f3a9c12",
  "data": {
    "adapter": "INavigator",
    "method": "NavigateTo",
    "error": "NavmeshUnloaded",
    "recoverable": true
  }
}

{
  "v": 1,
  "seq": 502,
  "ts": 290144,
  "type": "engine.error",
  "runId": "7f3a9c12",
  "data": {
    "kind": "MalformedQuestData",
    "message": "Step 'kill-bandits' references undefined recovery handler 'flee'",
    "recoverable": false
  }
}

{
  "v": 1,
  "seq": 342,
  "ts": 120504,
  "type": "player.died",
  "runId": "7f3a9c12",
  "data": {
    "atNode": "go-to-coerthas",
    "location": { "zone": 155, "position": { "x": -12.4, "y": 205.1, "z": -380.2 } },
    "instanceKind": "None",
    "context": "openWorld",
    "cause": "unknown",
    "recoveryAction": "acceptReturn"
  }
}
```

`adapter.error` is for failures originating below the engine (vnavmesh, Lifestream, etc.). `engine.error` is for failures originating in engine logic itself (malformed quest data, contract violations, internal bugs). `player.died` records death events; engine recovery is documented in `ADAPTERS.md` §15.4.

`player.died.data.context` is one of:

- **`"openWorld"`** — `InstanceKind == None`. Engine handles via accept-return flow. `recoveryAction` is one of `"acceptReturn"`, `"waitForRaise"`, `"awaitUser"`, matching the configured `DeathRecoveryPolicy`.
- **`"delegated"`** — `InstanceKind` is `Dungeon`, `Trial`, `Raid`, `AllianceRaid`. BossMod/AutoDuty/game handles. `recoveryAction` is `"none"`. The engine takes no action; death does not increment any failure counter.
- **`"spd"`** — `InstanceKind == SinglePlayerDuty`. Engine notes the death but takes no immediate action; actual SPD retry triggers when `InstanceKind` returns to `None` without postcondition met. `recoveryAction` is `"willRetryAtEntry"`.
- **`"other"`** — `InstanceKind` is `PvP`, `DeepDungeon`, `VariantDungeon`, or `Other`. Out of v1 scope. `recoveryAction` is `"none"`.

`cause` is best-effort: `"combat"` if combat was active immediately prior, `"fall"` if falling damage was suspected, `"unknown"` otherwise.

### 5.5 Diagnostic: `dialogue.resolved`, `reward.selected`, `travel.strategy`, `navmesh.wait`, `gear.repair`, `duty.retry`, `interaction.retried`

Diagnostic events are recorded for post-hoc analysis but are not verified during replay. They give bug reports and traces enough context to diagnose subtle issues without forcing every detail to participate in determinism checks.

```json
{
  "v": 1,
  "seq": 87,
  "ts": 15302,
  "type": "dialogue.resolved",
  "runId": "7f3a9c12",
  "data": {
    "queriedReference": "TEXT_JOBDRK301_02054_A1_000_116",
    "resolvedText": "I will see this through.",
    "language": "English",
    "matchedOptionId": 1,
    "totalOptionsShown": 2
  }
}

{
  "v": 1,
  "seq": 412,
  "ts": 480211,
  "type": "reward.selected",
  "runId": "7f3a9c12",
  "data": {
    "strategy": "HighestIlvlForCurrentJob",
    "available": [
      { "index": 0, "itemId": 3001, "ilvl": 15, "vendorPrice": 120 },
      { "index": 1, "itemId": 3005, "ilvl": 18, "vendorPrice": 95 }
    ],
    "chose": 1
  }
}

{
  "v": 1,
  "seq": 12,
  "ts": 8401,
  "type": "travel.strategy",
  "runId": "7f3a9c12",
  "data": {
    "from": { "zone": 130, "position": { "x": -9.4, "y": 40.2, "z": 12.1 } },
    "to": { "zone": 155, "position": { "x": 180.0, "y": 12.0, "z": -45.0 } },
    "plan": [
      { "step": "teleport", "aetheryteId": 23 },
      { "step": "navigate", "distance": 142.5 }
    ],
    "estimatedGil": 211
  }
}

{
  "v": 1,
  "seq": 5,
  "ts": 1200,
  "type": "navmesh.wait",
  "runId": "7f3a9c12",
  "data": {
    "zone": 622,
    "status": "Generating",
    "progress": 0.34,
    "estimatedRemainingMs": 18500
  }
}

{
  "v": 1,
  "seq": 230,
  "ts": 65300,
  "type": "gear.repair",
  "runId": "7f3a9c12",
  "data": {
    "trigger": "preDuty",
    "lowestConditionBefore": 22,
    "lowestConditionAfter": 100,
    "method": "selfRepair",
    "gilSpent": 0,
    "darkMatterUsed": 1,
    "durationMs": 8500
  }
}

{
  "v": 1,
  "seq": 510,
  "ts": 280420,
  "type": "duty.retry",
  "runId": "7f3a9c12",
  "data": {
    "stepId": "complete-axe-trial",
    "attemptNumber": 2,
    "previousDifficulty": "Normal",
    "selectedDifficulty": "Easy",
    "policy": "RetryAtEasierIfAvailable",
    "availableDifficulties": ["Normal", "Easy"]
  }
}

{
  "v": 1,
  "seq": 78,
  "ts": 14200,
  "type": "interaction.retried",
  "runId": "7f3a9c12",
  "data": {
    "stepId": "interact-with-crystal",
    "attemptNumber": 2,
    "reason": "postconditionNotMet",
    "previousOutcome": "DialogueOpened",
    "elapsedSincePreviousMs": 1100
  }
}
```

**Why these seven:**

- `dialogue.resolved` — load-bearing diagnostic for the localization seam (`ADAPTERS.md` §11). Records the sheet reference the engine queried, the text resolved from Lumina for the active language, and the matched option ID in the currently-open dialogue. Without it, "wrong dialogue option chosen" bugs are nearly impossible to trace.
- `reward.selected` — captures both the available rewards at decision time and the chosen index. Diagnostic for "wrong reward chosen" reports.
- `travel.strategy` — multi-step travel plans are non-trivial; recording the plan makes "why did it teleport to the wrong aetheryte" diagnosable.
- `navmesh.wait` — distinguishes "stuck" from "waiting for navmesh generation" in trace analysis.
- `gear.repair` — records when, why, and how the engine repaired gear. `trigger` is one of `"preDuty"`, `"preCombat"`, `"postQuest"`, `"periodic"`, `"forced"` (from step `preconditions`), `"userRequested"`. `method` is `"selfRepair"` or `"npcRepair"`.
- `duty.retry` — SPD failure retry. Records attempt number, difficulty progression, available difficulties. Telemetry for "which SPDs are most failure-prone."
- `interaction.retried` — interaction retry after postcondition timeout or adapter error. Telemetry for "which interactions are flaky."

None of these affect replay correctness. The validator (`qf-trace validate`) checks structural integrity but does not require these events to match between recording and replay.

---

## 6. Determinism

Replay correctness depends on a small set of strict rules.

### 6.1 Engine seed derivation

```
engineSeed = SHA256(runId)[:8] as int64
```

The engine seed feeds any randomness consumed by engine logic (e.g., `ITimingProfile.InterActionGap()` sampling). Derivation is pure: same `runId` always produces same seed.

### 6.2 Replay reuses `runId`

Replay **always** uses the recorded `runId` to derive the seed. Replay **never** generates a new `runId`. The replay harness is explicit about this; generating an ID during replay is a bug.

### 6.3 Replay uses recorded engine config

Replay reads `engineConfig` from the trace and instantiates the engine with that config. It does **not** use a "canonical" config or the user's current config. See §8 for the contract on what `engineConfig` contains.

### 6.4 Observations feed the fake state provider

During replay, the `IGameStateProvider` is a `ReplayGameStateProvider` backed by the trace's `observation` events. Each tick, it serves the recorded observation. The engine queries the provider; recorded values are returned. Queries for fields not in the recorded observation fail loudly — this indicates the engine has started reading state it didn't read at recording time, which is a real regression to surface.

### 6.5 Decisions are compared, not re-fed

Each tick during replay produces a fresh decision from current engine code. That decision is compared against the recorded `decision`:

- Match → pass.
- Mismatch → fail with structured diff showing which fields of the action differ.

Action lifecycle events (`action.submitted`, `action.completed`) are likewise compared, not re-fed.

### 6.6 Out of scope for replay

- **Adapter correctness.** Whether vnavmesh actually navigates is not tested. That requires E2E.
- **Timing realism.** `ITimingProfile` is seeded deterministically; real timing distributions are validated elsewhere.

This is the right scope. Replay catches engine logic regressions. Other test tiers catch adapter and timing issues.

---

## 7. Privacy and shareability

**Every trace file must be safe to attach to a public GitHub issue.** This is enforced at the recording boundary, not after the fact.

### 7.1 Excluded fields

The recording proxy refuses to record:

- Character name, world, server, content ID, account ID
- Friend list, FC, party member identities
- Retainer names and inventory beyond what a quest interacts with
- Chat content
- World position outside the immediate quest area (positions are recorded only when queried, and quest queries are quest-local by construction)

Implementation: an allowlist of recordable state keys in the recording proxy. Adding a new queryable game-state field requires explicitly listing it as recordable. Default is exclusion.

### 7.2 Wall-clock time

The only absolute wall-clock value in the entire trace is `run.start.data.wallClockUtc`. Every other `ts` is a monotonic offset. Redaction strips `wallClockUtc` and replaces it with `null`. Replay does not depend on `wallClockUtc`.

### 7.3 The redaction operation

`qf-trace redact <input> <output>` is the single, canonical redaction operation. It:

1. Strips `run.start.data.wallClockUtc`
2. Verifies no excluded-fields keys appear anywhere in the trace
3. Emits the redacted trace plus a redaction report

The output is byte-for-byte stable for the same input — no timestamp insertion, no random tokens. Redacting an already-redacted trace is a no-op.

### 7.4 Bug report flow

The plugin's in-game "Export bug report" button:

1. Locates the trace file for the failed run
2. Runs `redact` in-process
3. Bundles the redacted trace plus plugin/data/schema/patch version manifest
4. Opens a preview dialog showing exactly what will be sent
5. Provides a copy-to-clipboard or save-to-file action

The user always sees and approves the redacted output before any upload.

---

## 8. `EngineDecisionConfig`

`engineConfig` in `run.start.data` is a full snapshot of the engine's decision-affecting configuration at recording time.

### 8.1 Scope

`EngineDecisionConfig` is an **explicit allowlist** of configuration fields whose values affect engine decisions. It is **not** the user's full runtime config.

Fields included (initial set, subject to design as engine implementation progresses):

- Retry counts and backoff parameters
- Recovery aggressiveness thresholds
- Feature toggles that gate decision paths
- Quest-step-skip preferences that affect routing

Fields **excluded**:

- Timing profile selection (timing is a non-decision concern; replay uses a deterministic seed regardless)
- Logging verbosity
- Update channel
- Telemetry opt-in
- UI preferences
- Any user-identifying information

Adding a field to `EngineDecisionConfig` is a deliberate, reviewed action. The class lives in `QuestForge.Engine` and is the contract.

### 8.2 Serialization

The full object is serialized into `engineConfig`, not a hash. Hashing was rejected: debuggers need to see actual values when diagnosing replay failures.

### 8.3 Schema evolution

`EngineDecisionConfig` evolves under the same rules as the trace format itself:

- Adding a field: minor, `v` unchanged. Old traces deserialize with the new field at its default value.
- Removing or renaming a field: major, `v` bumped. Migration via `qf-trace` tooling.

### 8.4 Privacy

By construction, nothing in `EngineDecisionConfig` is user-identifying. Safe to commit and share.

---

## 9. Crash safety

Recording must survive crashes without producing unreadable files.

### 9.1 Write protocol

- Open with append mode (`FileStream` with `FileMode.Append`, `FileShare.Read`) on Windows.
- Serialize one event to a memory buffer.
- Verify the serialized line is ≤ 4096 bytes (the per-event hard cap). Exceeding the cap is a recorder bug; surface loudly.
- Write the buffer plus `\n` in a single `Write` call.
- Flush per event.

### 9.2 Crash artifacts

On crash mid-write, the file may end with a partial last line (no terminating `\n`). On read:

- If the last line lacks a trailing `\n`, it is **discarded silently**. The trace is otherwise valid.
- If any earlier line is malformed, the trace is reported as corrupted. This should not happen under the above write protocol.

### 9.3 Validation contract

`qf-trace validate` performs:

- Last-line newline check
- Per-line JSON well-formedness
- Per-event schema conformance to the trace format `v`
- `seq` strict monotonic from 0
- `runId` consistency across all events
- `run.start` is `seq: 0`, `run.end` (if present) is the last event
- `engineConfig` deserializes against the current `EngineDecisionConfig` schema
- **Engine-seed integrity check:** re-derive seed from `runId`, run the first N engine ticks against the trace's first N observations, verify the resulting decisions match the recorded ones. Mismatch indicates the trace was hand-edited without re-recording — flag loudly. (N is small, e.g., 5, to keep validation fast.)

Validation warnings (non-fatal):

- Non-monotonic `ts` values
- Missing `run.end` (run was aborted mid-recording)
- `rejected` alternatives that no longer exist in current engine code

---

## 10. Size and rotation

Targets:

| Scope | Soft target | Hard limit |
|-------|-------------|------------|
| Per event | 2 KB | 4 KB |
| Live recording file | 1 MB | 10 MB |
| Canonical fixture | 1 MB | 10 MB |

### 10.1 Live recording rotation

If a live recording exceeds the 10 MB hard limit, the recorder rotates to a new file:

- Filename suffix `.part2.jsonl`, `.part3.jsonl`, etc.
- `seq` and `runId` continue across files
- Each part is independently valid JSONL with its own `run.start`-like header (a `part.start` event, structurally identical to `run.start` minus most metadata)
- Replay handles multi-part runs by concatenating

### 10.2 Canonical fixtures never rotate

A canonical trace is always a single file. If a real quest legitimately needs more than 10 MB to represent, the cap is bumped before rotation is used — this is a deliberate maintenance action, not an automated rollover.

Likely causes of exceeding the cap:

- Engine is over-observing (querying state it doesn't need) — fix at engine level
- Quest is genuinely long (200+ steps) — bump the cap or split the quest

---

## 11. Versioning

### 11.1 Trace format `v`

The `v` field is the trace format schema version. Independent of quest schema version, plugin version, and data version.

| Change kind | `v` bump | Old reader behavior |
|-------------|----------|--------------------|
| Add field to existing event `data` | No | Ignores unknown field |
| Add new event type | No | Warns, skips unknown type |
| Add new optional top-level field | No | Ignores unknown field |
| Rename or remove field | Yes | Refuses to read trace |
| Change semantic meaning of field | Yes | Refuses to read trace |

### 11.2 Migration

Major bumps trigger a one-time migration:

- `qf-trace migrate --from <oldV> --to <newV> <files...>` rewrites traces
- The committed trace corpus is migrated in a single PR
- Live recorders are updated to write the new `v` in the next plugin release

Major bumps should be rare. The expected first-year cadence is zero.

---

## 12. Tooling

CLI in `questforge-tools`. Binary name: `qf-trace`.

```
qf-trace validate <file.jsonl>
    Validates a trace file. Exit 0 on success, non-zero on failure.

qf-trace redact <input.jsonl> [<output.jsonl>]
    Applies the privacy redaction. If output is omitted, prints to stdout.

qf-trace replay <quest-id> <file.jsonl>
    Standalone replay against current engine code. Reports pass/fail/diff.

qf-trace diff <file1.jsonl> <file2.jsonl>
    Structured diff between two traces. Useful for debugging replay failures.

qf-trace timestamp <file.jsonl> [--seq N | --all]
    Converts trace timestamps to wall-clock for log correlation.
    Outputs run.start.wallClockUtc + ts for the requested events.

qf-trace migrate --from <v> --to <v> <files...>
    Migrates traces across format-version bumps.
```

The plugin exposes an in-game command (separate name to avoid CLI confusion, e.g. `/qf showtrace`) for opening the current run's trace file in the user's default editor.

---

## 13. Log correlation

The trace format and the structured live log are separate files with different purposes.

| | Trace | Structured log |
|--|-------|---------------|
| Purpose | Replay determinism + bug reports | Live debugging + system log correlation |
| Time field | Monotonic offset (ms) | Wall-clock |
| Per-event content | Engine inputs/outputs only | Engine, plus adapter detail, plus context |
| Privacy | Redactable, shareable | Local only |
| Lifetime | Indefinite (canonical fixtures) | Rotated, eventually deleted |

The structured log emits one line per trace event with both timestamps (`wallClockUtc` and `runId`/`seq` cross-reference). Live debugging gives wall-clock correlation for free; after-the-fact correlation uses `qf-trace timestamp`.

---

## Appendix A: Worked example

A minimal complete trace for a hypothetical two-step quest:

```jsonl
{"v":1,"seq":0,"ts":0,"type":"run.start","runId":"a1b2c3d4","data":{"questId":42,"schemaVer":"1.0","pluginVer":"0.4.1","dataVer":"2026.05.10-stable","dataHash":"sha256:abc...","patchVer":"7.4","wallClockUtc":"2026-05-12T14:22:01.234Z","engineConfig":{"maxRetries":3,"recoveryAggressiveness":"normal"},"precedingRunId":null}}
{"v":1,"seq":1,"ts":12,"type":"observation","runId":"a1b2c3d4","data":{"node":"go-to-npc","observed":{"playerZone":130,"playerPos":{"x":-9.4,"y":40.2,"z":12.1},"nearbyNpcs":{"queried":[1000236],"accessed":[1000236]}}}}
{"v":1,"seq":2,"ts":13,"type":"decision","runId":"a1b2c3d4","data":{"node":"go-to-npc","chose":{"action":"interact","npcId":1000236}}}
{"v":1,"seq":3,"ts":250,"type":"action.submitted","runId":"a1b2c3d4","data":{"adapter":"IInteractor","method":"Interact","params":{"npcId":1000236},"expectedPostcondition":"dialogueOpen()"}}
{"v":1,"seq":4,"ts":820,"type":"action.completed","runId":"a1b2c3d4","data":{"success":true,"durationMs":570,"postconditionMet":true}}
{"v":1,"seq":5,"ts":830,"type":"observation","runId":"a1b2c3d4","data":{"node":"complete-quest","observed":{"questFlag(42)":1}}}
{"v":1,"seq":6,"ts":831,"type":"decision","runId":"a1b2c3d4","data":{"node":"complete-quest","chose":{"action":"finish"}}}
{"v":1,"seq":7,"ts":840,"type":"run.end","runId":"a1b2c3d4","data":{"outcome":"success","durationMs":840,"stepsCompleted":2,"recoveriesTriggered":0}}
```

## Appendix B: Glossary

- **Canonical trace** — the single trace per quest used as the CI replay fixture.
- **Candidate trace** — any other recorded trace, kept for diversity, debugging, or future promotion.
- **Engine seed** — deterministic integer derived from `runId`, fed to engine RNGs.
- **EngineDecisionConfig** — explicit allowlist of decision-affecting configuration fields, recorded per run.
- **Observation** — recorded set of game-state property accesses during one engine tick.
- **Promotion** — replacing the canonical trace for a quest with a different candidate, via PR.
- **Recording proxy** — wrapper around `IGameStateProvider` that records every property access for the current tick.
- **Tick** — one cycle of the engine's observe-decide-act loop.

## Appendix C: Design decisions and rationale

| Decision | Alternative considered | Why |
|----------|----------------------|-----|
| JSONL | Binary (MessagePack, CBOR), YAML, CSV | Append-safe, streamable, diffable, greppable. Verbosity is acceptable. |
| `seq` authoritative for ordering | `ts` authoritative | Monotonic clock anomalies must not break replay. |
| Relative-millis `ts` | Wall-clock per event | Crash-resistance to clock jumps, smaller events, single redaction point. |
| Record only consumed fields | Full game-state snapshot | Privacy (no broad state dump), replay correctness (changes to unread fields can't affect decisions). |
| `{queried, accessed}` for collections | Record only accessed elements | Preserves selection-logic determinism without recording state the engine didn't ask for. |
| Engine seed derived from `runId` | Independent seed | Fewer moving parts; trace fully describes its own replay. |
| Replay uses recorded `engineConfig` | "Canonical" replay config | Traces from any user config can replay; canonical-config would invalidate non-default traces. |
| Full `engineConfig`, not hash | Hash | Debuggers need actual values. |
| Canonical traces never rotate | Multi-part canonical | Single-file fixtures are reviewable; multi-part complicates PR review. |
| git LFS for trace files | Out-of-tree releases, plain git | PR review can see traces inline; LFS handles the size. |
| Recorder writes monotonic time | Wall-clock writes | Resistance to NTP slew, DST, manual clock changes. |
| Diagnostic events not replay-verified | Verify everything | Replay focuses on decision determinism; diagnostic events aid debugging without forcing every detail to participate in determinism checks. |
| `player.died` as dedicated event type | Fold into `engine.error` | Death is recoverable engine behavior, not an error; misclassifying it would confuse trace readers. |
| `player.died` context discrimination | Single death event shape | Dungeon deaths and open-world deaths have very different recovery flows; trace consumers need to filter cleanly. |
| `newGamePlus` in `run.start` | Per-event NG+ markers | NG+ state is set at run start and doesn't change mid-run; single field is sufficient. |
| Three diagnostic events for SPD/repair/retry | Fold into `engine.error` or `recovery.triggered` | Distinct categories with distinct telemetry value; conflating them would obscure analysis. |

---

## Known divergences from this spec (Phase 5–6 implementation, current as of Phase 11)

The trace recorder produces a structurally simpler output than the full spec above. These divergences originate in Phase 5–6 and most remain intentionally in place through Phase 10. Phase 10 (`qf-trace`) explicitly accepts and reads the flat Phase 7+ shape.

| Spec field / feature | Current behaviour | Status |
|---|---|---|
| `seq` (monotonic sequence number per event) | **Not emitted.** Line order in the file is the implicit sequence. | Deferred — `qf-trace` uses event order. |
| `ts` (monotonic offset in ms from `run.start`) | **Not emitted.** Each event carries `at` (absolute UTC `DateTimeOffset`). | Deferred. |
| `data` sub-object wrapper | **Not used.** Payload fields are flattened at the top level. E.g. spec shows `{"type":"decision","data":{"stepId":"..."}}` but the recorder emits `{"type":"decision","stepId":"..."}`. Phase 10 reads the flat shape. | Intentionally flat; spec example is aspirational. |
| `v` (format version) | **Not emitted.** Each event has no `v` field. | Deferred. |
| `runId` in every event | **Emitted** as a top-level field. | Matches spec intent — no change needed. |
| `run.start` metadata fields (`pluginVer`, `dataVer`, `dataHash`, `patchVer`, `engineConfig`, `precedingRunId`) | **Not emitted.** `run.start` carries `runId`, `questId`, `questSchemaId`, `at` only. | Deferred — add as plugin config layer matures. |
| Cutscene skip confirmation | **Not recorded.** Deterministic from `IsRunActive` so does not affect replay. | No change needed. |
| `action.submitted` / `action.completed` pairs | **Now emitted** (added Phase 7) from `EngineHost.DispatchAction`. Phase 10 `extract-quest` reads these to recover navigate destinations and NPC IDs. | ✅ Reconciled in Phase 7. |

**Observation deduplication (added Phase 7):** `RecordingGameStateProvider` and `RecordingQuestState` emit an `observation` event only when the value changes from the previous emission for that `(method, argument)` pair. This is also the reason Phase 10 `SnapshotState` uses last-value-wins semantics.

**Ambient quest flag polling (added Phase 8):** `EngineHost.TickAsync` proactively calls `GetQuestFlags` on the active quest after each dispatch when tracing is enabled, so flag-bit transitions appear in traces without requiring `questFlag()` predicates in quest files.

**Phase 10 reading contract:** `qf-trace` reads traces produced by the Phase 7+ recorder — top-level `type` discriminator, `runId` at top level, flat payload fields, `action.submitted`/`action.completed` pairs present. Traces from Phase 5–6 (no action pairs, no type discriminator on some events) are not supported by Phase 10 tooling.

**Phase 11A CLI wiring:** All four `qf-trace` subcommands (`extract-fixture`, `validate-fixture`, `list-fixtures`, `extract-quest`) are now fully implemented and wired in the `qf-trace` CLI. The divergences listed above that were tooling-only (i.e. existed only because the CLI was not yet built) are resolved. The remaining divergences — `seq`, `ts`, `v`, and the `data` sub-object — are still deferred implementation details in the recorder itself.

**`inventory.changed` event (added Phase 11):** When `InventoryChangedEvent` fires (FNV-1a hash-based key-item inventory change detection), the trace recorder emits an `inventory.changed` diagnostic event. This event carries a `KeyItemDelta` payload listing which item IDs were added and which were removed. It is a diagnostic event (not replay-verified) and serves as the signal that `StepInferenceEngine` Rule 2.6 uses to infer `HandOverItemStep` during authoring.

