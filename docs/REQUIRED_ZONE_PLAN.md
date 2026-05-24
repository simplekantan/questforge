# RequiredZone Field Implementation Plan (Issue #38)

**Status:** ready for test creation
**Input docs:** GitHub issue #38, docs/SCHEMA.md §4.1 / §8 / §9
**Output:** new optional `RequiredZone` field on every `Step`; `StepFactory` populates it from the *source* (before) zone; SCHEMA.md documents it; a new semantic-validation rule in `questforge-tools` guards it. No CI behavior changes beyond the new validator rule firing on bad data.
**Scope:** DATA-ONLY. No engine runtime behavior. See §1.1.

---

## 1. Summary and scope

### Problem

`Step.Zone` (a `string?` on the `Step` base class) is set by `StepFactory` from the *after/destination* zone — where the player ends up AFTER the step completes. For a travel step crossing zone 129→128, `Step.Zone` is `"128"`. This makes `Step.Zone` unsafe as the authoritative "where must the player be to START executing this step" signal.

### Fix

Add `RequiredZone : string?` to the `Step` base class — an explicit author/factory-set field declaring the zone the player must be in *before* the step can execute. `Step.Zone` is unchanged (after/destination zone, used for display/grouping). `RequiredZone` is new, optional, defaults to `null`.

This issue delivers exactly four things:

1. The `RequiredZone` field on `Step` (+ JSON serialization).
2. Populating `RequiredZone` in `StepFactory.Build` and its two travel helpers.
3. A SCHEMA.md §4.1 doc update.
4. One new semantic-validation rule in the `questforge-tools` repo.

### 1.1 OUT OF SCOPE (deferred to issue #39)

The following are explicitly **NOT** part of this issue. If a test or implementation reaches into any of these, it has over-scoped:

- **No `QuestEngine` changes.** The engine does not read `RequiredZone` in this issue.
- **No zone guard / dispatch gating.** No comparison of `playerZone()` to `RequiredZone`.
- **No `playerZone()` read** driven by `RequiredZone`.
- **No `resumePointFragmentId`** / cold-resume logic. That logic (positioning fragment trigger, `_resumePointExecutedIds` cursor, the cursor comparison) is owned entirely by issue #39.
- **No new predicate functions, no new step types.**

`RequiredZone` is purely a data field written by the factory and validated by the tools. It is consumed later by #39.

### 1.2 Fixed decisions (do not relitigate)

- `RequiredZone` is `string?` to match `Step.Zone`'s type (zones are stored as decimal strings like `"128"`).
- **schemaVersion stays `"1.0.0"` — do NOT bump.** SCHEMA.md §9 technically classifies "add a new optional field" as a patch bump (`1.0.0` → `1.0.1`), but the maintainer has chosen not to bump for this change. This is flagged here for the record; no action is taken on it.

---

## 2. The `Step` field addition

### 2.1 Signature and placement

Add `RequiredZone` to the `Step` base class in **both** schema copies:

- `questforge`: `QuestForge.Schema\Step.cs`
- `questforge-tools`: `QuestForge.Schema\Step.cs` (the tools repo carries a copy of the schema; both must stay identical — they are byte-for-byte identical today).

Place it immediately after `Zone` so the two zone fields read together:

```csharp
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public string? RequiredZone { get; init; }   // NEW — zone the player must be in BEFORE this step runs
    public ExpectValue? Expect { get; init; }
    public ExpectValue? SkipIf { get; init; }
    public float? StopDistance { get; init; }
    public RecoverConfig? Recover { get; init; }
    public RetryConfig? Retry { get; init; }
    public Preconditions? Preconditions { get; init; }
    public string? Notes { get; init; }
}
```

No new `[JsonSerializable]` registration is needed — `RequiredZone` is a base-class property on `Step`, and all 22 concrete subtypes are already registered in `QuestForgeJsonContext`. The source generator picks up the new base property automatically.

### 2.2 Serialization behavior — investigated finding

**The user's brief assumed `Step.Zone` is omitted when null. This is FALSE. Investigated and verified by direct experiment against the built `QuestForge.Schema.dll`:**

- `QuestForgeJsonContext` (`[JsonSourceGenerationOptions(...)]`) sets `PropertyNamingPolicy = CamelCase`, `UseStringEnumConverter = true`, `WriteIndented = true`. **It does NOT set `DefaultIgnoreCondition`.**
- `QuestForgeJsonContext.QuestFileOptions` (the options every quest-file writer uses) sets `TypeInfoResolver = Default`, `PropertyNamingPolicy = CamelCase`, `WriteIndented = true`, `Encoder = UnsafeRelaxedJsonEscaping`. **It also does NOT set `DefaultIgnoreCondition`.**
- `Step.Zone` carries no `[JsonIgnore]` attribute.

Experimental result — serializing a `TravelStep` with `Zone = null`:

```json
{
  "type": "travel",
  ...
  "id": "x",
  "zone": null,        ← present, written as null (NOT omitted)
  "expect": null,
  ...
}
```

**Therefore: to match `Step.Zone` exactly, `RequiredZone` must ALSO be written as `"requiredZone": null` when null — i.e. add NO `[JsonIgnore]` attribute.** A bare `public string? RequiredZone { get; init; }` produces exactly the same null-handling as `Zone`.

> The omit-when-null behavior the brief was thinking of is implemented per-property on specific records via explicit `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` (e.g. `AethernetRouteHint.From`, `RouteHint.Aethernet`). The base `Step` properties deliberately do **not** use this — they are all written verbatim including nulls. Do not add a `[JsonIgnore]` attribute to `RequiredZone`; doing so would make it inconsistent with `Zone` and break the "match exactly" requirement.

Round-trip is symmetric: a missing `requiredZone` key, or an explicit `"requiredZone": null`, both deserialize to `RequiredZone == null` (STJ treats a missing key for a nullable property as `null`). A populated `"requiredZone": "128"` deserializes to `"128"`.

---

## 3. `StepFactory` population

File: `QuestForge.Engine\Authoring\StepFactory.cs`.

### 3.1 Stringify helper (the `> 0 ? ToString() : null` convention)

The existing code computes `zoneStr` as:

```csharp
var zoneStr = zone > 0 ? zone.ToString() : null;   // zone is int
```

`RequiredZone` must follow the same convention so a zone of `0` (unknown/uninitialized) maps to `null`, not `"0"`. Define one private helper used by all population sites that need to stringify a `uint`/`int` zone:

```csharp
// Mirror the existing `zone > 0 ? zone.ToString() : null` convention used for zoneStr.
// Accepts the raw zone value (uint from a ZoneId.Value, or int) and returns "N" or null.
private static string? ZoneStr(uint zone) => zone > 0 ? zone.ToString() : null;
private static string? ZoneStr(int zone)  => zone > 0 ? zone.ToString() : null;
```

`GameStateSnapshot.Zone` is a `ZoneId` whose `.Value` is `uint`; `before?.Zone.Value` is therefore a `uint?`. Use `ZoneStr(before?.Zone.Value ?? 0u)` to collapse both "before is null" and "before zone is 0" to `null` in one expression (matching the convention). For non-travel arms, the existing `zoneStr` local already equals `RequiredZone`, so reuse it directly rather than recomputing.

### 3.2 Population table (each switch arm / helper → exact expression)

| Switch arm / helper | step types | `RequiredZone` expression | Rationale |
|---|---|---|---|
| `"accept"` | accept | `zoneStr` | same zone before/after |
| `"turn-in"` | turn-in | `zoneStr` | same zone before/after |
| `"talk"` | talk | `zoneStr` | same zone before/after |
| `"hand-over-item"` | hand-over-item | `zoneStr` | same zone before/after |
| `"attune"` | attune | `zoneStr` | same zone before/after |
| `"pickup-item"` | pickup-item | `zoneStr` | same zone before/after |
| `"interact-object"` | interact-object | `zoneStr` | same zone before/after |
| `_` (default fallback → TalkStep) | (fallback) | `zoneStr` | same zone before/after |
| `BuildNpcDialogueTravelStep` | travel (npcDialogue) | `ZoneStr(sourceZone)` where `sourceZone = srcNpc.Zone` (an `int`) | the player must be in the NPC's source zone to talk to the travel NPC |
| `BuildAethernetTravelStep` | travel (aethernet) | `ZoneStr(before.Zone.Value)` (`before` is non-null in this helper) | `AethernetRouteHint` carries only From/To shard IDs (no zones); no Lumina shard→zone map exists in the pure Engine path; the player stands at the departure shard (in `before.Zone`) when initiating the hop. Author-editable afterward. |
| `"travel"` (bare/plain arm) | travel (foot/zone-gate) | `ZoneStr(before?.Zone.Value ?? 0u)` | the player was in `before.Zone` when the Record modal opened; that is the start-of-step zone. Null when `before` is null or its zone is `0`. |

Notes:

- **Non-travel arms** set `RequiredZone = zoneStr` — for these steps the player neither moves nor changes zone, so before-zone == after-zone == `zoneStr`. (`zoneStr` is `null` when the after-zone is `0`, which is the correct null fallback here too.)
- **Bare-travel null fallback:** when `before` is `null` (Record modal opened without a captured prior snapshot) or `before.Zone.Value == 0`, `RequiredZone` is `null`. `ZoneStr(before?.Zone.Value ?? 0u)` yields exactly this.
- **NpcDialogue:** use the helper's existing `sourceZone` local (`= srcNpc.Zone`), NOT `before.Zone`. `srcNpc` is `after.DialogueNpcSource!`, whose `.Zone` is the captured source zone. This keeps `RequiredZone` consistent with `RouteHint.NpcDialogue.Target.Zone`, which is already set from `sourceZone`. (When `before` was null at capture time, the inference machinery falls `sourceZone` back to the after-zone — see existing test SF-ND-3 — so `RequiredZone` naturally tracks whatever `sourceZone` resolved to.)

### 3.3 Helper-signature changes

**None required.** Every input is already available as a local at each site:

- `BuildNpcDialogueTravelStep` already computes `var sourceZone = srcNpc.Zone;` (line ~155). Add `RequiredZone = ZoneStr(sourceZone)` to the returned `TravelStep` initializer.
- `BuildAethernetTravelStep` already receives `before` (non-null) as a parameter. Add `RequiredZone = ZoneStr(before.Zone.Value)` to the returned `TravelStep` initializer.
- The bare `"travel"` arm has `before` in scope. Add `RequiredZone = ZoneStr(before?.Zone.Value ?? 0u)` to the inline `TravelStep` initializer.
- The non-travel arms (`accept`, `turn-in`, `talk`, `hand-over-item`, `attune`, `pickup-item`, `interact-object`, default) each already have `zoneStr` in scope. Add `RequiredZone = zoneStr` to each initializer.

Do not change any method signature or any existing assignment (`Zone`, `Destination`, `RouteHint`, `Target`, etc. stay exactly as they are).

---

## 4. SCHEMA.md edit

In §4.1 "Common step fields", add a `requiredZone` row to the field table immediately after the existing `zone` row. The `zone` row text is unchanged.

**New row to insert after the `zone` row (line ~328):**

```markdown
| `requiredZone` | Optional | Zone the player must be in **before** this step can execute (the source/start zone). Distinct from `zone`, which is the destination/after zone used for display and grouping. Set by the authoring factory from the pre-step position; authors may edit. Reserved for cold-resume positioning (consumed by resume logic); the engine does not gate on it today. |
```

Also add `"requiredZone": null` to the JSON example block in §4.1 (the block beginning at line ~309), placed right after the `"zone": 129,` line, to mirror the always-written-even-when-null serialization:

```json
{
  "id": "go-to-baderon",
  "type": "travel",
  "zone": 129,
  "requiredZone": null,
  "expect": "questSequence(65657) >= 1",
  ...
}
```

One-line semantics note (already folded into the table row above): **`zone` = where the step ends; `requiredZone` = where the step must begin.**

Do **not** touch §9 versioning and do **not** bump `schemaVersion`. (Flagged per §1.2; intentionally left at `1.0.0`.)

---

## 5. Validator rule (`questforge-tools`)

### 5.1 Rule definition

Add **one** rule that walks `Step` entries (recursing into `BranchStep.Branches[].Steps`, exactly like the existing `CheckRouteHints` walk):

- **Code:** `structural/required-zone-not-numeric`
- **Level:** **Error**
- **Condition:** `step.RequiredZone is { } rz` AND `rz` is non-empty AND `!uint.TryParse(rz, NumberStyles.None, CultureInfo.InvariantCulture, out _)`.
  - i.e. fire only when `RequiredZone` is present and is *not* a non-negative decimal integer string. `null` and the (factory-impossible but author-possible) empty string are skipped — absence is always legal.
- **Message:** `$"Step '{step.Id}': RequiredZone must be a numeric zone ID string (e.g. \"128\"), but was \"{rz}\"."`

Rationale: zones are stored as decimal strings throughout the schema (`Step.Zone`, `NpcLocation.Zone` stringified). A non-numeric `RequiredZone` is always an author/tooling bug and can never resolve to a real zone, so Error (not Warning) is correct — it is a hard structural defect, consistent with `route-hint-aethernet-to-missing` being an Error for an impossible value.

### 5.2 Why NOT the "warn if RequiredZone != Zone for non-travel" candidate

The brief floated an optional secondary rule: for non-travel step types, warn when `RequiredZone != Zone`. **Rejected** for this issue:

- For every non-travel arm, `StepFactory` sets `RequiredZone = zoneStr = Zone`, so factory-produced data can never trip it.
- It would false-positive on legitimate hand-authored data (an author may deliberately set a different start zone, e.g. a talk step the player reaches mid-walk).
- It requires the validator to reason about step type semantics that #39 has not finalized.

Keeping a single, unambiguous "must be numeric" rule matches the existing validator's minimalism and avoids speculative warnings. One rule only.

### 5.3 Files touched (questforge-tools)

- `QuestForge.Schema\Step.cs` — add the `RequiredZone` property (mirror of §2.1; keep byte-identical to the `questforge` copy).
- `QuestForge.Tools.Validator\StructuralValidator.cs` — add a `ValidateRequiredZone(quest, ctx, errors)` call in `Validate(...)` (next to the existing `ValidateRouteHints(...)` call at line ~30) and a `CheckRequiredZone(steps, scope, ctx, errors)` recursive walker modeled on `CheckRouteHints` (lines ~565–597). Emit via the existing `E(...)` helper (signature: `E(ctx, code, location, message, stepId, severity)`).
- `QuestForge.Tools.Validator.Tests\RequiredZoneValidationTests.cs` — **new** test file (see §6 case group D). Use the existing `QuestBuilder` helpers (`Valid`, `Seq`, `AssertNoError`, plus inline `Assert` for count/severity, mirroring `RouteHintValidationTests`).

The validator's `E` helper and `ValidationError` record need no changes.

---

## 6. Testable acceptance criteria

A Tester converts each numbered case below into a failing test before any implementation. Tests live in three projects:

- **`QuestForge.Schema.Tests`** — serialization round-trip (group A). Follow `RoundTripTests.cs` patterns (the `RoundTrip<T>` helper, `QuestForgeJsonContext.QuestFileOptions`).
- **`QuestForge.Engine.Tests`** — `StepFactory` population (groups B, C). Add to / mirror `Authoring\NpcDialogueStepFactoryTests.cs` and `Authoring\AethernetStepFactoryTests.cs`; reuse their `MakeSnapshot` helpers and `StepFactory.Build(...)` call shapes. A new file `Authoring\RequiredZoneStepFactoryTests.cs` is recommended.
- **questforge-tools** `QuestForge.Tools.Validator.Tests` — validator rule (group D). New file `RequiredZoneValidationTests.cs`, modeled on `RouteHintValidationTests.cs` + `QuestBuilder`.

### Group A — serialization round-trip (`QuestForge.Schema.Tests`)

- **A1** — Given a `TravelStep` with `RequiredZone = "129"`, when serialized as `Step` and deserialized back, then `result.RequiredZone == "129"`.
- **A2** — Given a `TravelStep` with `RequiredZone = null` (unset), when serialized via `QuestForgeJsonContext.QuestFileOptions`, then the JSON **contains** the key `"requiredZone"` written as `null` (matching `Step.Zone`'s always-written behavior — assert against compacted JSON: `Assert.Contains("\"requiredZone\":null", compact)`). This locks the "no `[JsonIgnore]`, written-when-null" decision from §2.2.
- **A3** — Given JSON for a step with `"requiredZone": "128"` and no `"zone"` key, when deserialized, then `result.RequiredZone == "128"` and `result.Zone == null` (the two fields are independent).
- **A4** — Given JSON for a step that omits `requiredZone` entirely, when deserialized, then `result.RequiredZone == null` (missing key → null).
- **A5** — Given a non-travel step (e.g. `AcceptStep`) with `RequiredZone = "128"`, when round-tripped, then `result.RequiredZone == "128"` (base-class property serializes for every subtype; pick one non-travel subtype to lock cross-subtype behavior).

### Group B — `StepFactory` travel population (`QuestForge.Engine.Tests`)

- **B1 (npcDialogue travel = source zone)** — Given the SF-ND-1 happy-path inputs (`before.Zone = 131`, `after.Zone = 200`, `dialogueNpcSource.Zone = 131`, dialogue option selected), when `Build("travel", ...)`, then `step.RequiredZone == "131"` (the NPC's source zone / `sourceZone`), NOT `"200"`.
- **B2 (aethernet travel = before.Zone)** — Given the SF_NEW_1 aethernet inputs (`before.Zone = 131`, shard==NPC==125, `aethernetDestinationSelected = 77`, `after.Zone = 130`), when `Build("travel", ...)`, then `step.RequiredZone == "131"` (`before.Zone`), NOT `"130"`.
- **B3 (bare travel = before.Zone)** — Given a plain zone-gate travel (`before.Zone = 129`, `after.Zone = 128`, no dialogue, no aethernet destination), when `Build("travel", ...)`, then `step.RequiredZone == "129"` (`before.Zone`), NOT `"128"`. (Construct `before`/`after` so neither the npcDialogue nor aethernet sub-case fires — same shape as SF-ND-4/SF_NEW_2 negative setups but for the plain arm.)
- **B4 (bare travel with null before → null)** — Given `Build("travel", ..., after, before: null)` with after producing a plain TravelStep (no dialogue/aethernet signals), then `step.RequiredZone == null`.
- **B5 (bare travel with zero before-zone → null)** — Given `before.Zone = ZoneId(0)` (and `after.Zone > 0`, plain travel), when `Build("travel", ...)`, then `step.RequiredZone == null` (the `> 0 ? ... : null` convention; a zero source zone is treated as unknown).
- **B6 (Zone unchanged regression)** — For B2/B3, additionally assert `step.Zone` is still the after-zone string (`"130"` / `"128"`) — proves `RequiredZone` is additive and did not alter `Zone`.

### Group C — `StepFactory` non-travel population (`QuestForge.Engine.Tests`)

- **C1 (non-travel = Zone)** — For each of `accept`, `turn-in`, `talk`, `hand-over-item`, `attune`, `pickup-item`, `interact-object`: given `after.Zone = 130`, when `Build(<type>, ...)`, then `step.RequiredZone == "130"` AND `step.RequiredZone == step.Zone`. (A `[Theory]` over the seven step-type strings is the natural shape; assert both equalities per case.)
- **C2 (default fallback arm = Zone)** — Given an unrecognized step type string (e.g. `Build("totally-unknown", ...)` which falls through to the default `TalkStep` arm) with `after.Zone = 130`, then the produced step's `RequiredZone == "130"`.
- **C3 (non-travel null when after-zone is 0)** — Given `Build("talk", ...)` with `after.Zone = ZoneId(0)`, then `step.RequiredZone == null` (matches existing `zoneStr` null behavior; `RequiredZone == Zone == null`).

### Group D — validator rule (questforge-tools `QuestForge.Tools.Validator.Tests`)

- **D1 (numeric → no error)** — Given a `TravelStep` with `RequiredZone = "128"` inside `QuestBuilder.Valid(...)`, when validated, then `AssertNoError(errors, "structural/required-zone-not-numeric")`.
- **D2 (null → no error)** — Given a step with `RequiredZone = null`, when validated, then no `required-zone-not-numeric` error.
- **D3 (empty string → no error)** — Given `RequiredZone = ""`, when validated, then no `required-zone-not-numeric` error (absence/empty is legal; only a present non-empty non-numeric value fails).
- **D4 (non-numeric → Error)** — Given `RequiredZone = "Ul'dah"`, when validated, then exactly one error `structural/required-zone-not-numeric` with `Severity.Error` and `StepId` equal to the offending step's id.
- **D5 (negative/garbage → Error)** — Given `RequiredZone = "-5"` (or `"12a"`), when validated, then one `structural/required-zone-not-numeric` Error. (`uint.TryParse` with `NumberStyles.None` rejects signs and non-digits.)
- **D6 (multiple steps → only bad one)** — Given two steps, one `RequiredZone = "128"` and one `RequiredZone = "bad"`, when validated, then exactly one `required-zone-not-numeric` error whose `StepId` is the bad step's id.
- **D7 (nested in branch → reported)** — Given a `BranchStep` whose nested step has `RequiredZone = "bad"`, when validated, then one `required-zone-not-numeric` Error with the nested step's id (proves the walker recurses into `Branches[].Steps`, mirroring RHV_9).
- **D8 (non-travel step type also checked)** — Given an `AcceptStep` with `RequiredZone = "bad"`, when validated, then one `required-zone-not-numeric` Error (the rule walks all `Step` types, not just `TravelStep` — distinct from the RouteHint rule which is TravelStep-only).

---

## 7. Implementation order

**Phase A — Schema field (both repos), 0.5 day.** Add `RequiredZone` to `Step.cs` in `questforge` and the identical copy in `questforge-tools`. Make group A tests pass. Done before B.

**Phase B — `StepFactory` population (`questforge`), 0.5 day.** Add the `ZoneStr` helper(s); wire `RequiredZone` into every switch arm and both travel helpers per §3.2. Make groups B and C pass. Done before D is unblocked but B/D are otherwise independent across repos.

**Phase C — Validator rule (`questforge-tools`), 0.5 day.** Add `ValidateRequiredZone` / `CheckRequiredZone` to `StructuralValidator`; make group D pass.

**Phase D — Docs.** Apply the SCHEMA.md §4.1 edits (§4 above). No tests; reviewed in PR.

---

## 8. Done criteria

1. `Step.RequiredZone` exists in both schema copies and round-trips (group A green).
2. Serializing a step with `RequiredZone == null` emits `"requiredZone": null` (key present), identical to `Zone`'s null-handling (A2 green) — i.e. **no** `[JsonIgnore]` was added.
3. `StepFactory.Build` sets `RequiredZone` to: source zone for npcDialogue travel; `before.Zone` for aethernet travel; `before?.Zone` (null on null/zero) for bare travel; `Zone` for all non-travel arms and the default fallback (groups B, C green).
4. Existing `StepFactory` tests (`NpcDialogueStepFactoryTests`, `AethernetStepFactoryTests`, `DialogueOptionStepFactoryTests`) still pass — `Zone`, `Destination`, `RouteHint` unchanged (B6 + existing suite green).
5. A `questforge-data` PR with a step whose `requiredZone` is a non-numeric string makes the validator emit `structural/required-zone-not-numeric` (Error) → CI red; correcting it to a numeric string → CI green (group D green).
6. SCHEMA.md §4.1 documents `requiredZone` with the zone-vs-requiredZone distinction; `schemaVersion` remains `"1.0.0"`.
7. No `QuestEngine`, zone-guard, `playerZone()`-gating, or `resumePointFragmentId` code was added (scope check — §1.1).

---

## 9. Exclusions

This issue does **NOT** include (deferred to #39 unless noted):

- Any engine read of `RequiredZone`, any positioning fragment, any cold-resume cursor logic (#39).
- A zone-mismatch gate comparing `playerZone()` to `RequiredZone`.
- A Lumina shard→zone map for deriving aethernet source zones (the aethernet case deliberately uses `before.Zone` instead; author-editable).
- The speculative "warn when RequiredZone != Zone for non-travel steps" validator rule (rejected — §5.2).
- Any `schemaVersion` bump (§1.2).
- JSON Schema regeneration / authoring-UI editor field for `RequiredZone` (not requested).

---

## 10. Open questions / risks

1. **Two schema copies can drift.** `questforge` and `questforge-tools` each carry `QuestForge.Schema\Step.cs`. The field must be added to both, byte-identically. (Phase 3 of the roadmap plans to unify these via NuGet; until then this is a manual sync risk. Group A's serialization test runs in `questforge`; the tools repo has no equivalent base-`Step` round-trip test, so the tools-side addition is only indirectly exercised by group D — acceptable for a one-line additive property.)
2. **Aethernet `RequiredZone` is best-effort.** It is set to the *departure* zone (`before.Zone`), which is correct for cold-resume positioning in the common case, but is author-editable because no shard→zone map exists in the pure Engine path. #39 must tolerate an author having edited or cleared it.
3. **schemaVersion not bumped (deliberate).** Old plugins ignore the unknown `requiredZone` field (additive optional), so this is forward/backward compatible regardless of the version string. Flagged only to keep the §9 patch-bump convention on record.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §6.
- Happy paths: 9 scenarios (A1, A3, A4, A5, B1, B2, B3, C1, C2)
- Edge cases: 6 scenarios (A2, B4, B5, B6, C3, D3)
- Error cases: 5 scenarios (D4, D5, D6, D7, D8) plus 2 no-error guards (D1, D2)
- Expected total: ~22 tests — ~5 in `QuestForge.Schema.Tests`, ~9 in `QuestForge.Engine.Tests` (note C1 is a 7-case `[Theory]`), ~8 in `QuestForge.Tools.Validator.Tests` (questforge-tools).
