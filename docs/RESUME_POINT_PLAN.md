# Resume-Point (Cold-Resume Positioning) Implementation Plan (Issue #39)

**Status:** ready for test creation
**Input docs:** GitHub issue #39, docs/SCHEMA.md §4.1, docs/REQUIRED_ZONE_PLAN.md (issue #38, the prerequisite), QuestEngine.cs (current fragment-expansion + confirmed-cursor logic)
**Output:** the engine gains a *cold-resume positioning* capability: when a step declares both a `RequiredZone` and a `ResumePointFragmentId`, and the player is not yet in that zone, the engine runs a one-off positioning fragment (a zone-gated sub-loop) to get the player into the right zone before dispatching the step normally. A new `OnResumeFail` recovery hook handles adapter failures inside that fragment. The validator gains three syntax-only rules. No CI behavior changes beyond the new validator rules firing on bad fragment/quest data.
**Depends on:** issue #38 (`RequiredZone` field on `Step`). That field already exists in both schema copies (`QuestForge.Schema/Step.cs`, committed in 6e24054). This issue *consumes* it.

---

## 1. Problem summary

Issue #38 added `RequiredZone` (`string?`, e.g. `"128"`) to the `Step` base class — the zone the player must be in *before* a step can execute. It is data-only today; the engine never reads it.

The motivating scenario is **cold resume**: a user starts the plugin (or resumes a run) mid-quest with the player standing in the wrong zone. The next pending step (say a `talk` step in zone 128) cannot meaningfully dispatch — the NPC is not in the current zone, navigation will fail. Authoring a literal `travel` step before every step is wasteful and brittle, because on a *warm* run the player is already in the right zone and the travel step is a no-op.

The fix is a **positioning fragment** declared per-step: `ResumePointFragmentId` names a shared-library fragment (e.g. `"fragment-teleport-to-uldah"`) whose steps know how to get the player into `RequiredZone`. The engine triggers this fragment only when the player is in the *wrong* zone, runs it as a **zone-gated sub-loop** until the player arrives in `RequiredZone`, then falls through and dispatches the original step exactly as it would on a warm run.

This issue delivers exactly five things:

1. Two schema additions: `Step.ResumePointFragmentId` and `RecoverConfig.OnResumeFail` (in **both** schema copies — `questforge` and `questforge-tools`, byte-identical).
2. Engine state + lifecycle for the resume sub-loop (`_fragments` field, `_resumePointExecutedIds` cursor, `_activeResumeFragment` state).
3. The zone-gated trigger + sub-loop in `ResolveAction`, plus the `OnResumeFail` recovery hook.
4. Three syntax-only validator rules in `questforge-tools`.
5. A SCHEMA.md §4.1 doc update.

### 1.1 Fixed design decisions (do not relitigate)

These were confirmed before planning:

- **Zone-gated sub-loop.** The resume fragment runs until `playerZone() == RequiredZone` — *not* until every fragment step is confirmed. Zone arrival is the termination condition. Individual fragment-step `Expect` values drive per-step completion *within* the fragment (same confirm-and-dispatch pattern as the main loop), but the sub-loop ends the instant the zone matches, even if fragment steps remain.
- **`_fragments` stored as a field.** Fragments are a shared library (long-term home is `questforge-data`); they are NOT embedded in `QuestDefinition`. `StartQuest()` already receives the dict; this issue stores the reference so the resume trigger can look fragments up at tick time.
- **`OnResumeFail` reuses the existing `RecoverAction` vocabulary.** It is declared on `RecoverConfig` (the sixth hook). It fires when a resume-fragment step *would* produce `EngineAction.AwaitUser` (e.g. an adapter read failed inside the fragment).
- **Validator is syntax-only for this PR.** Cross-file fragment-existence checks are deferred (the fragment library lives in `questforge-data`, not visible to a single-file validation pass). The depth-1 and `fragment-*` naming rules run within a fragment file's own validation.
- **Naming convention is `fragment-*`** (not `restore-*`).
- **Serialization matches `Zone` / `RequiredZone`.** `ResumePointFragmentId` carries no `[JsonIgnore]`; it is written even when null. Confirmed by the §38 investigation: `QuestForgeJsonContext` sets no `DefaultIgnoreCondition`, and base `Step` properties are always written.

### 1.2 Vocabulary

- **Main step** — the step in `matchingBlock.Steps` that owns the `ResumePointFragmentId`. The step that *triggers* the resume.
- **Resume fragment** — the `FragmentDefinition` named by `ResumePointFragmentId`, looked up in `_fragments`.
- **Resume sub-loop** — the per-tick processing of `_activeResumeFragment`, ahead of the main step loop.
- **Trigger** — the four-condition check that arms `_activeResumeFragment`.

---

## 2. Schema changes

Both repos carry a byte-identical copy of `QuestForge.Schema`. Apply every change in §2 to **both**:

- `questforge`: `QuestForge.Schema/Step.cs`, `QuestForge.Schema/RecoverAction.cs`
- `questforge-tools`: `QuestForge.Schema/Step.cs`, `QuestForge.Schema/RecoverAction.cs`

### 2.1 `Step.ResumePointFragmentId`

Add immediately after `RequiredZone` so the two resume-related fields read together:

```csharp
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public string? RequiredZone { get; init; }
    public string? ResumePointFragmentId { get; init; }   // NEW — positioning fragment for cold resume
    public ExpectValue? Expect { get; init; }
    public ExpectValue? SkipIf { get; init; }
    public float? StopDistance { get; init; }
    public RecoverConfig? Recover { get; init; }
    public RetryConfig? Retry { get; init; }
    public Preconditions? Preconditions { get; init; }
    public string? Notes { get; init; }
}
```

**Serialization:** no `[JsonIgnore]`. Written verbatim including `null`, exactly like `Zone` and `RequiredZone`. The camelCase JSON key is `"resumePointFragmentId"` (STJ `PropertyNamingPolicy = CamelCase` lowercases the leading `R` and preserves `Point` and `Fragment` as word boundaries).

No new `[JsonSerializable]` registration is needed — this is a base-`Step` property and all 24 concrete subtypes are already registered in `QuestForgeJsonContext`.

### 2.2 `RecoverConfig.OnResumeFail`

Add as the sixth hook, after `OnPlayerDefeated`:

```csharp
public class RecoverConfig
{
    public RecoverAction? OnTimeout { get; init; }
    public RecoverAction? OnObstacle { get; init; }
    public RecoverAction? OnAdapterError { get; init; }
    public RecoverAction? OnPostconditionFailed { get; init; }
    public RecoverAction? OnPlayerDefeated { get; init; }
    public RecoverAction? OnResumeFail { get; init; }   // NEW — fires when a resume-fragment step would AwaitUser
}
```

`RecoverConfig` itself is not `[JsonSerializable]`-registered separately (it is reachable via `Step.Recover`); the new property is picked up automatically by source-gen. The `RecoverAction` polymorphic subtypes (`retry`/`goto`/`useReturn`/`useTeleport`/`awaitUser`/`abandon`) are unchanged and already registered.

### 2.3 Serialization rules (summary)

| Property | JSON key | Written when null? | Round-trips |
|---|---|---|---|
| `Step.ResumePointFragmentId` | `resumePointFragmentId` | **Yes** (no `[JsonIgnore]`) | missing key → null; `null` → null; `"x"` → `"x"` |
| `RecoverConfig.OnResumeFail` | `onResumeFail` | **Yes** (matches sibling hooks) | missing key → null; populated object → typed `RecoverAction` via `action` discriminator |

No `schemaVersion` bump (consistent with §38's decision for additive optional fields).

---

## 3. Engine changes (`QuestForge.Engine/QuestEngine.cs`)

### 3.1 New fields

```csharp
// Stored fragment library reference, set in StartQuest. Used at tick time to look up
// a step's ResumePointFragmentId. Null when no fragments were supplied.
private IReadOnlyDictionary<string, FragmentDefinition>? _fragments;

// Steps whose resume positioning has already completed this run+sequence. Same lifecycle
// as _confirmedStepIds: cleared in BeginRun and on sequence change. Keyed by the MAIN step's Id.
private readonly HashSet<string> _resumePointExecutedIds = new();

// Active resume sub-loop state. Null when no resume fragment is running.
private ActiveResumeFragment? _activeResumeFragment;
```

The active-resume state is a small private record:

```csharp
private sealed record ActiveResumeFragment(
    string ForStepId,          // the MAIN step that triggered this resume
    string RequiredZone,       // the zone the player must reach (the termination gate)
    Step MainStep,             // the MAIN step (carries Recover for the OnResumeFail hook)
    IReadOnlyList<Step> Steps, // the fragment's steps (already expanded if needed)
    HashSet<string> ConfirmedFragmentStepIds  // per-fragment-step cursor, internal to the sub-loop
);
```

Rationale for `MainStep` being carried: the `OnResumeFail` hook reads `MainStep.Recover?.OnResumeFail`. The resume fragment's own steps may have their own `Recover`, but `OnResumeFail` is declared on the *main* step (the issue says "check `step.Recover?.OnResumeFail`" where `step` is the triggering main step).

Rationale for a per-`ActiveResumeFragment` confirmed set (rather than reusing `_confirmedStepIds`): the fragment-step IDs are scoped to the sub-loop's lifetime, are torn down when the zone matches, and must never collide with main-loop cursor entries. Keeping them in a field on the record makes the sub-loop self-contained and discarded atomically when `_activeResumeFragment` is cleared.

### 3.2 `StartQuest` — store the fragments dict

`StartQuest(quest, fragments)` already expands inline `FragmentStep` references. Add one line to retain the dict:

```csharp
public void StartQuest(
    QuestDefinition quest,
    IReadOnlyDictionary<string, FragmentDefinition>? fragments = null)
{
    if (quest is null) throw new ArgumentNullException(nameof(quest));

    _fragments = fragments;   // NEW — retain for resume-point lookups at tick time

    // ... existing expansion of inline FragmentStep references unchanged ...
    _quest = quest with { Sequences = rewrittenSequences };
}
```

`ResumePointFragmentId` is resolved **lazily at tick time**, not eagerly in `StartQuest`. This is deliberate (see §3.8): unlike inline `FragmentStep` (always expanded), a resume fragment may never run (warm resume — player already in zone), so we do not pre-expand or pre-validate it at `StartQuest`. A missing/null `_fragments` only matters if a resume actually triggers.

### 3.3 `BeginRun` — clear the new cursor and active state

```csharp
public void BeginRun(string runId)
{
    if (string.IsNullOrWhiteSpace(runId))
        throw new ArgumentException("runId must be non-empty", nameof(runId));
    _runId = runId;
    _runStartEmitted = false;
    _confirmedStepIds.Clear();
    _lastKnownSequence = -1;
    _resumePointExecutedIds.Clear();   // NEW
    _activeResumeFragment = null;      // NEW
}
```

### 3.4 `ResolveAction` — read player zone once per tick

Today `ResolveAction` reads UI state and player position once per tick. Add a single zone read alongside them, fail-open to `null` on adapter failure:

```csharp
// Read player zone once per tick for RequiredZone gating.
// On adapter failure: null. A null zone is treated as "unknown" and never satisfies a
// RequiredZone gate (so the resume sub-loop will not falsely terminate), and never TRIGGERS
// a resume either (we cannot prove the player is in the wrong zone) — see §3.6 condition 2.
var zoneResult = await _gameState.GetPlayerZone(ct);
var playerZone = zoneResult is Result<ZoneId>.Success { Value: var z } ? (ZoneId?)z : null;
```

`GetPlayerZone(CancellationToken)` already exists on `IGameStateProvider` and returns `Task<Result<ZoneId>>`. `ZoneId` is `readonly record struct ZoneId(uint Value)`.

### 3.5 `ResolveAction` — process the active resume sub-loop FIRST

Immediately after the sequence-change handling and after the per-tick reads, **before** the main `foreach (var step in matchingBlock.Steps)` loop, process any active resume fragment:

```csharp
if (_activeResumeFragment is { } resume)
{
    var (resumeAction, resumeStepId, resumeDone) =
        await ProcessActiveResume(resume, playerZone, ui, playerPos, ct);

    if (!resumeDone)
        return (resumeAction!, resumeStepId);   // still positioning; do not touch the main loop this tick

    // Resume complete: player has arrived in RequiredZone.
    _resumePointExecutedIds.Add(resume.ForStepId);
    _activeResumeFragment = null;
    // FALL THROUGH to the main step loop — the main step dispatches normally THIS tick.
}
```

#### `ProcessActiveResume` contract

```csharp
// Returns (action, stepId, done).
//   done == true  → player is now in RequiredZone; action/stepId are ignored; caller falls through.
//   done == false → still positioning; action/stepId are the resume step's dispatch for this tick.
private async Task<(EngineAction? action, string? stepId, bool done)> ProcessActiveResume(
    ActiveResumeFragment resume, ZoneId? playerZone, UiState ui, WorldPosition? playerPos,
    CancellationToken ct)
{
    // 1. Zone gate FIRST. If the player is in RequiredZone, the resume is done — regardless of
    //    how many fragment steps remain unconfirmed.
    if (playerZone is { } pz && ZoneMatches(pz, resume.RequiredZone))
        return (null, null, done: true);

    // 2. Otherwise execute the next unconfirmed fragment step (confirm-and-dispatch, like the main loop).
    foreach (var fstep in resume.Steps)
    {
        if (resume.ConfirmedFragmentStepIds.Contains(fstep.Id))
            continue;

        if (fstep.Expect is not null && await _expectEvaluator.Evaluate(fstep.Expect, ct))
        {
            resume.ConfirmedFragmentStepIds.Add(fstep.Id);
            continue;
        }

        if (fstep.SkipIf is not null && await _expectEvaluator.Evaluate(fstep.SkipIf, ct))
            continue;

        var action = ResolveActionForStep(fstep, ui, playerPos);

        // 3. OnResumeFail interception (see §3.7).
        if (action is EngineAction.AwaitUser && resume.MainStep.Recover?.OnResumeFail is { } onFail)
            return (MapRecoverAction(onFail, resume.MainStep), resume.MainStep.Id, done: false);

        return (action, fstep.Id, done: false);
    }

    // 4. All fragment steps confirmed but the player is STILL not in RequiredZone.
    //    The fragment did not achieve its goal. Emit Wait (next tick re-checks the zone gate).
    return (new EngineAction.Wait(
        $"resume fragment '{resume.ForStepId}' exhausted but player not yet in zone {resume.RequiredZone}"),
        null, done: false);
}

private static bool ZoneMatches(ZoneId playerZone, string requiredZone)
    => uint.TryParse(requiredZone, out var rz) && playerZone.Value == rz;
```

Notes:

- **Zone gate is checked before any fragment step.** This is the literal "zone-gated sub-loop" decision: the very first thing each tick is "are we there yet?" If yes, done — even if fragment steps are unconfirmed.
- **Null `playerZone` never satisfies the gate** (condition 1 requires `playerZone is { }`). So an adapter zone-read failure keeps the sub-loop running rather than falsely declaring success.
- **Confirm-and-dispatch mirrors the main loop exactly**: cursor check → Expect (confirm + skip) → SkipIf (skip, no confirm) → dispatch.
- **§3.6 step 4 (fragment exhausted, zone still wrong)** is a soft state, not an error. We emit `Wait` so the next tick re-checks the gate (the player may still be in transit, e.g. a teleport cast is resolving). It does NOT clear `_activeResumeFragment` — that only happens on a true zone match (or sequence change / `BeginRun`).

### 3.6 `ResolveAction` main loop — the resume trigger

In the main step loop, after the existing `SkipIf` check and **before** `ResolveActionForStep(step, ui, playerPos)`, insert the four-condition trigger:

```csharp
foreach (var step in matchingBlock.Steps)
{
    if (_confirmedStepIds.Contains(step.Id))
        continue;

    if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
    {
        _confirmedStepIds.Add(step.Id);
        continue;
    }

    if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct))
        continue;

    // --- NEW: resume-point trigger ---------------------------------------
    // Arm the resume sub-loop iff ALL FOUR conditions hold:
    //   (a) step is not confirmed                         — guaranteed here (cursor check above)
    //   (b) step.RequiredZone is set AND player is NOT in it
    //   (c) step.ResumePointFragmentId is set
    //   (d) step.Id not already in _resumePointExecutedIds (resume not already done this seq)
    if (step.ResumePointFragmentId is { } fragId                       // (c)
        && step.RequiredZone is { } reqZone                            // (b) part 1
        && !ZoneAlreadySatisfied(playerZone, reqZone)                  // (b) part 2
        && !_resumePointExecutedIds.Contains(step.Id))                 // (d)
    {
        var resume = ArmResumeFragment(step, fragId, reqZone);
        // Return the FIRST resume action on THIS tick (do not fall through to ResolveActionForStep).
        var (action, stepId, done) = await ProcessActiveResume(resume, playerZone, ui, playerPos, ct);
        if (done)
        {
            // Edge: player already in zone by the time we armed (e.g. RequiredZone == current).
            // Mark executed, discard, dispatch the main step this tick.
            _resumePointExecutedIds.Add(step.Id);
            _activeResumeFragment = null;
            return (ResolveActionForStep(step, ui, playerPos), step.Id);
        }
        _activeResumeFragment = resume;
        return (action!, stepId);
    }
    // ---------------------------------------------------------------------

    return (ResolveActionForStep(step, ui, playerPos), step.Id);
}
```

Helpers:

```csharp
// (b) part 2: returns true when we KNOW the player is already in RequiredZone (no resume needed),
// OR when RequiredZone is not a parseable numeric (cannot gate → do not trigger; treat as satisfied).
// Returns false (i.e. "not satisfied → trigger") only when we can prove player zone != RequiredZone.
private static bool ZoneAlreadySatisfied(ZoneId? playerZone, string requiredZone)
{
    if (!uint.TryParse(requiredZone, out var rz)) return true;   // unparseable → never trigger
    if (playerZone is not { } pz) return true;                   // unknown zone → never trigger (fail-safe)
    return pz.Value == rz;                                       // already there → satisfied
}

private ActiveResumeFragment ArmResumeFragment(Step mainStep, string fragId, string reqZone)
{
    if (_fragments is null || !_fragments.TryGetValue(fragId, out var def))
        throw new InvalidOperationException(
            $"Step '{mainStep.Id}' declares ResumePointFragmentId '{fragId}' but no such fragment " +
            "was provided to StartQuest.");

    return new ActiveResumeFragment(
        ForStepId: mainStep.Id,
        RequiredZone: reqZone,
        MainStep: mainStep,
        Steps: def.Steps,
        ConfirmedFragmentStepIds: new HashSet<string>(StringComparer.Ordinal));
}
```

**Why `ZoneAlreadySatisfied` is fail-safe (returns true → no trigger) on null/unparseable:**
- Null player zone: we cannot prove the player is in the wrong zone, so triggering a teleport fragment would be reckless. Fall through and let the main step dispatch (it has its own fail-open behavior for null position/zone).
- Unparseable `RequiredZone`: this is a data bug the *validator* catches (`structural/required-zone-not-numeric`, §38; and §4 rules here). The engine does not gate on garbage; it falls through.

**What breaks if you get condition (b) wrong:** if you trigger on a zone *match* (player already in zone), you run an unnecessary teleport fragment on every warm run — the exact waste this feature avoids. If you fail to trigger on a true mismatch, cold resume is broken and the main step dispatches into the wrong zone.

### 3.7 `OnResumeFail` semantics

A resume-fragment step's dispatch can produce `EngineAction.AwaitUser` (the only adapter-error surface in the Phase-4 dispatcher is the implied-navigation / interaction path returning `AwaitUser`, plus any future failure that maps to `AwaitUser`). When that happens inside the resume sub-loop:

1. Check `resume.MainStep.Recover?.OnResumeFail`.
2. If set, fire that recovery action **instead of** the `AwaitUser`.
3. If not set, propagate the `AwaitUser` (the run halts, as it would for any step).

The recovery action is mapped through the engine's existing recover-action handling (`MapRecoverAction`). For this issue's tests, the only `OnResumeFail` shape that must be exercised end-to-end is `AwaitUserRecoverAction` (maps to `EngineAction.AwaitUser` with the recover action's `Reason`) and `AbandonRecoverAction` (maps to `EngineAction.AwaitUser` / `Done` per the existing recover convention). The *interception point* (checking `OnResumeFail` before emitting `AwaitUser`) is the load-bearing behavior; the downstream mapping reuses existing code.

```csharp
// Minimal mapping for the actions OnResumeFail is expected to carry. Reuse the engine's
// existing recover dispatch if one exists; otherwise:
private static EngineAction MapRecoverAction(RecoverAction action, Step mainStep) => action switch
{
    AwaitUserRecoverAction au => new EngineAction.AwaitUser(au.Reason),
    AbandonRecoverAction      => new EngineAction.AwaitUser($"resume abandoned for step '{mainStep.Id}'"),
    // goto/useReturn/useTeleport/retry: out of scope for #39 OnResumeFail tests; map to AwaitUser
    // with a descriptive reason so behavior is defined but not asserted as primary cases.
    _ => new EngineAction.AwaitUser($"resume recovery '{action.GetType().Name}' for step '{mainStep.Id}'")
};
```

> **Builder note:** if the engine already has a recover-action dispatcher used by `OnTimeout`/`OnObstacle`/etc., route `OnResumeFail` through it rather than the stub above. The acceptance criteria assert only `AwaitUserRecoverAction` reason propagation (B-group), so any mapping that preserves the `AwaitUser` reason for `AwaitUserRecoverAction` is conformant.

### 3.8 Sequence change — clear resume state too

In the existing sequence-change block, extend the clear to cover the two new pieces of state:

```csharp
if (_lastKnownSequence != -1 && _lastKnownSequence != currentSeq)
{
    _confirmedStepIds.Clear();
    _resumePointExecutedIds.Clear();   // NEW
    _activeResumeFragment = null;      // NEW
}
_lastKnownSequence = currentSeq;
```

Rationale: a resume is scoped to "get into the right zone for *this* sequence's pending step." When the game advances the sequence, any in-flight positioning is stale (the new sequence's step may need a different zone), and the executed-marker is meaningless for the new block.

### 3.9 Tick-flow summary (one tick)

1. Read sequence; if quest complete → `Done`.
2. Match the sequence block; evaluate block-level `SkipIf`.
3. Read `UiState`, player position, **and player zone** (all once, fail-open).
4. Sequence-change detection → clear `_confirmedStepIds`, **`_resumePointExecutedIds`, `_activeResumeFragment`**.
5. **If `_activeResumeFragment` is set → `ProcessActiveResume`:**
   - zone matches → mark executed, clear active, **fall through to step 6**;
   - else → return the resume step's action (or `OnResumeFail` action, or `Wait` if exhausted).
6. Main step loop: cursor → Expect → SkipIf → **resume trigger (4 conditions)** → dispatch.
   - trigger fires → arm + return first resume action this tick (or, if already in zone, mark executed and dispatch the main step this tick).
7. All steps satisfied → `Wait`.

---

## 4. Validator changes (`questforge-tools` only)

These are **syntax-only** rules. No cross-file fragment-existence checks (the fragment library is not visible to a single-file pass). All three are modeled on the existing `CheckRouteHints` / `CheckRequiredZone` recursive walkers in `StructuralValidator.cs`, emitted via the existing `E(...)` helper, and must recurse into `BranchStep.Branches[].Steps`.

The fragment-internal rules (R2, R3) require the validator to also walk `FragmentDefinition.Steps` when validating a fragment file. The validator already loads fragment files (`IFragmentRegistry` / `FileFragmentRegistry`). Add a `ValidateFragmentDefinition(FragmentDefinition, ValidationContext)` entry point (or extend the existing fragment-file validation) that runs R2 and R3 over `fragment.Steps`. R1 runs in the quest-file `StructuralValidator.Validate` pipeline (alongside `ValidateRequiredZone`).

### 4.1 Rule table

| # | Rule | Code | Level | Scope | Suppression |
|---|---|---|---|---|---|
| R1 | `ResumePointFragmentId`, if set, must be a non-empty, non-whitespace string | `structural/resume-point-fragment-id-empty` | Error | quest files + fragment files; all step types; recurses into branches | — |
| R2 | A step inside a `FragmentDefinition.Steps` must NOT itself set `ResumePointFragmentId` (depth-1 only — no recursive resumes) | `structural/resume-point-nested` | Error | fragment files only | — |
| R3 | Step IDs inside a `FragmentDefinition` should follow the `fragment-*` naming convention | `structural/fragment-step-id-convention` | Warning | fragment files only | suppressed for a given step if R2 fired on it (a nested-resume step is already flagged) |

### 4.2 Rule R1 — `resume-point-fragment-id-empty`

- **Condition:** `step.ResumePointFragmentId is { } id && string.IsNullOrWhiteSpace(id)`.
  - i.e. fire only when the field is *present* but empty/whitespace. `null` (absent) is always legal.
- **Message:** `$"Step '{step.Id}': ResumePointFragmentId, when set, must be a non-empty, non-whitespace string."`
- **Walker:** new `ValidateResumePoint(quest, ctx, errors)` in the quest pipeline + `CheckResumePoint(steps, scope, ctx, errors)` recursive walker, identical shape to `CheckRequiredZone`. Also invoked over `fragment.Steps` from the fragment-file validation path.
- **Rationale for Error (not Warning):** a present-but-empty fragment ID can never resolve to a fragment; it is a hard authoring/tooling defect, consistent with `required-zone-not-numeric` being Error for an impossible value.

### 4.3 Rule R2 — `resume-point-nested`

- **Scope:** only steps in `FragmentDefinition.Steps` (and nested branch steps within a fragment).
- **Condition:** `step.ResumePointFragmentId is not null` (any value, including empty — its presence at all inside a fragment is the violation).
- **Message:** `$"Fragment '{fragment.FragmentId}' step '{step.Id}': fragments may not declare ResumePointFragmentId (resume positioning is depth-1 only; nested resumes are not supported)."`
- **Rationale:** the engine's resume sub-loop runs a fragment's steps directly; a fragment step that itself triggered a resume would require a nested sub-loop the engine does not implement (mirrors the existing `structural/fragment-nested` rule that forbids `FragmentStep` inside a fragment).

### 4.4 Rule R3 — `fragment-step-id-convention`

- **Scope:** only steps in `FragmentDefinition.Steps` (and nested branch steps within a fragment).
- **Condition:** `!step.Id.StartsWith("fragment-", StringComparison.Ordinal)`.
- **Level:** Warning.
- **Message:** `$"Fragment '{fragment.FragmentId}' step '{step.Id}': fragment step IDs should follow the 'fragment-*' naming convention."`
- **Suppression:** if R2 fired on the same step, skip R3 for that step (avoid double-flagging a step that is already a hard error).
- **Note:** this is the only Warning-level rule added here. It does not fail CI unless `--fail-on-warning` is set.

### 4.5 Files touched (questforge-tools)

- `QuestForge.Schema/Step.cs` — add `ResumePointFragmentId` (mirror of §2.1, byte-identical to the `questforge` copy).
- `QuestForge.Schema/RecoverAction.cs` — add `OnResumeFail` (mirror of §2.2).
- `QuestForge.Tools.Validator/StructuralValidator.cs` — add `ValidateResumePoint` + `CheckResumePoint` (R1) to the quest pipeline; add a fragment-file validation path running R1+R2+R3 over `fragment.Steps`.
- `QuestForge.Tools.Validator.Tests/ResumePointValidationTests.cs` — **new** test file (group C), modeled on `RequiredZoneValidationTests.cs` + `QuestBuilder`.

> **Open item for the Builder:** confirm whether the tools repo already validates `FragmentDefinition` files through a dedicated path. If fragment files currently only feed `IFragmentRegistry` (lookup) and are not themselves structurally validated, R2/R3 require adding that validation entry point. If a `qf-validate` fragment pass already exists, hook R2/R3 into it. The Tester can unit-test R2/R3 by calling the fragment-validation method directly with an in-memory `FragmentDefinition`.

---

## 5. Docs changes (`questforge` only)

### 5.1 SCHEMA.md §4.1 — common step fields

In §4.1 (line ~330, immediately after the existing `requiredZone` row), add a `resumePointFragmentId` row:

```markdown
| `resumePointFragmentId` | Optional | Names a shared-library positioning fragment used for **cold resume**. When set together with `requiredZone`, and the player is NOT in `requiredZone` at the moment this step would dispatch, the engine runs the named fragment as a zone-gated sub-loop until the player arrives in `requiredZone`, then dispatches this step normally. On a warm run (player already in `requiredZone`) the fragment never runs. Depth-1 only: a fragment's own steps may not set this field. |
```

Also add `"resumePointFragmentId": null` to the §4.1 JSON example block (after the `"requiredZone": null,` line, line ~314) to mirror the always-written-even-when-null serialization:

```json
{
  "id": "go-to-baderon",
  "type": "travel",
  "zone": 129,
  "requiredZone": null,
  "resumePointFragmentId": null,
  "expect": "questSequence(65657) >= 1",
  ...
}
```

### 5.2 SCHEMA.md §6 (recovery) — `onResumeFail`

If §6 documents the `RecoverConfig` hook keys (`onTimeout`, `onObstacle`, etc.), add an `onResumeFail` entry: "Fires when a step inside a `resumePointFragmentId` positioning fragment would halt the run (e.g. an adapter read failed). Uses the standard recovery-action vocabulary." (Locate the recovery-hooks list in §6; if no such list exists, this sub-edit is skipped — the §4.1 row is the load-bearing doc change.)

Do **not** bump `schemaVersion` (additive optional fields; consistent with §38).

---

## 6. Acceptance criteria

A Tester converts each numbered case below into a failing test before any implementation. Tests live in:

- **`QuestForge.Schema.Tests`** (questforge) — serialization round-trip (group **A**). Follow the §38 `RequiredZoneRoundTripTests.cs` patterns: `QuestForgeJsonContext.QuestFileOptions`, compacted-JSON `Assert.Contains` for the written-when-null check.
- **`QuestForge.Engine.Tests`** (questforge) — engine trigger/execution (group **B**). Model on `Engine/FragmentExecutionTests.cs` and `Engine/ConfirmedStepCursorTests.cs`: use `EngineTestHarness`, `harness.QuestState.SetQuestSequence`, `harness.GameState.SetZone(new ZoneId(...))`, `harness.GameState.SetPosition(...)`, `StartQuest(quest, fragments)`, `BeginRun`, `Tick`.
- **`QuestForge.Tools.Validator.Tests`** (questforge-tools) — validator rules (group **C**). New file `ResumePointValidationTests.cs`, modeled on `RequiredZoneValidationTests.cs` + `QuestBuilder`.
- Doc review (group **D**) — verified in PR, no automated test.

### Group A — serialization round-trip (`QuestForge.Schema.Tests`)

- **A1 (round-trip populated)** — Given a `TravelStep` with `ResumePointFragmentId = "fragment-teleport-uldah"`, when serialized as `Step` via `QuestFileOptions` and deserialized back, then `result.ResumePointFragmentId == "fragment-teleport-uldah"`.
- **A2 (written when null)** — Given a `TravelStep` with `ResumePointFragmentId = null`, when serialized via `QuestFileOptions`, then the compacted JSON **contains** `"resumePointFragmentId":null` (the key is present, written as null — matching `Zone`/`RequiredZone`; no `[JsonIgnore]`). Assert the exact camelCase key `resumePointFragmentId`.
- **A3 (missing key → null)** — Given JSON for a step that omits `resumePointFragmentId` entirely, when deserialized, then `result.ResumePointFragmentId == null`.
- **A4 (independent of RequiredZone)** — Given JSON with `"requiredZone":"128"` and `"resumePointFragmentId":"fragment-x"` and no `"zone"` key, when deserialized, then `RequiredZone == "128"`, `ResumePointFragmentId == "fragment-x"`, `Zone == null` (three independent fields).
- **A5 (non-travel subtype)** — Given an `AcceptStep` with `ResumePointFragmentId = "fragment-x"`, when round-tripped, then `result.ResumePointFragmentId == "fragment-x"` (base-class property serializes for every subtype).
- **A6 (OnResumeFail round-trip)** — Given a `RecoverConfig` with `OnResumeFail = new AwaitUserRecoverAction { Reason = "stuck" }` attached to a step's `Recover`, when the step is round-tripped, then `result.Recover!.OnResumeFail` is an `AwaitUserRecoverAction` with `Reason == "stuck"`.
- **A7 (OnResumeFail written when null)** — Given a `RecoverConfig` with `OnResumeFail = null` (other hooks set), when serialized, then the compacted JSON contains `"onResumeFail":null` (matches sibling hooks' null-handling).

### Group B — engine trigger and execution (`QuestForge.Engine.Tests`)

Common harness shape for B-group: a synthetic 1-sequence quest whose single main step has `RequiredZone` and `ResumePointFragmentId` set; a `_fragments` dict containing the named positioning fragment; `StartQuest(quest, fragments)`; `BeginRun`; `Tick`. Use `harness.GameState.SetZone(new ZoneId(n))` to control player zone.

- **B1 (warm run — no trigger)** — Given a main `TalkStep` with `RequiredZone = "128"`, `ResumePointFragmentId = "fragment-go-128"`, expect false; AND player zone == 128 (already in zone); AND a fragment `"fragment-go-128"` whose only step is a `TravelStep` to (999,0,999). When `Tick`, then the engine dispatches the **main** step (e.g. `Interact`/`Navigate` for the talk target), NOT the fragment's `Navigate(999,0,999)`. The resume must not trigger when the player is already in `RequiredZone`.
- **B2 (cold run — trigger fires, fragment dispatches)** — Given the same main step, but player zone == 130 (wrong zone); AND fragment `"fragment-go-128"` whose first step is a `TravelStep` to (50,0,50) with no expect. When `Tick`, then the engine returns `Navigate(50,0,50)` (the fragment's first step), NOT the main step's action.
- **B3 (zone gate ends the sub-loop, then main step dispatches same/next tick)** — Continue B2 across ticks with one harness:
  - Tick 1: zone 130 → `Navigate(50,0,50)` (fragment running).
  - Set zone to 128 (player arrived). Tick 2: the resume sub-loop sees the zone match → marks executed, clears active, **falls through**, and the **main** step dispatches this same tick. Assert the action is the main step's action (e.g. `Navigate`/`Interact` for the talk target, with `Origin.Id` == the main step's id), NOT a fragment action.
- **B4 (zone gate ends sub-loop even with unconfirmed fragment steps)** — Given a fragment with TWO travel steps (`fragment-a` to (10,0,10), `fragment-b` to (20,0,20), neither with expect); player starts in zone 130.
  - Tick 1: zone 130 → `Navigate(10,0,10)` (fragment-a; fragment-b not reached, fragment-a has no expect so it is the first unconfirmed).
  - Set zone to 128. Tick 2: zone matches → sub-loop terminates **without** dispatching `fragment-b` → main step dispatches. Assert the action is the main step's action, proving zone match (not fragment-step exhaustion) ends the sub-loop.
- **B5 (executed marker prevents re-trigger same sequence)** — After B3/B4 complete the resume and the main step has been dispatched/confirmed: move the player BACK to zone 130 (wrong zone again), keep the same sequence, and `Tick`. Assert the resume does NOT re-trigger (the main step's id is in `_resumePointExecutedIds`) — the main step dispatches directly (or `Wait`/`Interact` per its state), NOT the fragment's `Navigate`. (Construct so the main step is not yet confirmed, e.g. its expect is still false, so the trigger conditions other than `_resumePointExecutedIds` are still met — isolating the executed-marker guard.)
- **B6 (sequence change clears executed marker and active resume)** — Given a 2-sequence quest where the seq-1 main step also has `RequiredZone`/`ResumePointFragmentId`. Trigger and complete a resume in seq-0 (so `_resumePointExecutedIds` contains the seq-0 step id). Advance the game to seq-1 with the player in the wrong zone for the seq-1 step. `Tick`. Assert the seq-1 resume **triggers** (returns the fragment action) — proving the executed marker and active state were cleared on sequence change.
- **B7 (BeginRun clears resume state)** — Trigger a resume (active fragment set) on run-1, then call `BeginRun("run-2")` with the player still in the wrong zone, and `Tick`. Assert the resume re-arms from scratch (fragment first-step action returned) — and that no stale `_activeResumeFragment` from run-1 leaks (i.e. the action corresponds to the fragment's FIRST step, not a mid-fragment step). 
- **B8 (no RequiredZone → no trigger even with fragment id set)** — Given a main step with `ResumePointFragmentId = "fragment-x"` but `RequiredZone == null`; player in any zone. When `Tick`, the resume does NOT trigger (condition (b) requires `RequiredZone` to be set) → the main step dispatches directly.
- **B9 (RequiredZone set but no fragment id → no trigger)** — Given a main step with `RequiredZone = "128"` but `ResumePointFragmentId == null`; player in zone 130 (wrong zone). When `Tick`, the resume does NOT trigger (condition (c)) → the main step dispatches directly (this is the §38 data-only behavior — `RequiredZone` alone does not gate).
- **B10 (missing fragment in dict → throws at trigger time)** — Given a main step with `RequiredZone = "128"`, `ResumePointFragmentId = "fragment-missing"`, player in zone 130; AND `_fragments` does NOT contain `"fragment-missing"` (or `StartQuest` was called with `fragments: null`). When `Tick`, then an exception is thrown (the resume cannot resolve). Use `Assert.ThrowsAnyAsync<Exception>(() => harness.Engine.Tick(...))`. (Contrast with inline `FragmentStep`, which throws at `StartQuest`; a resume fragment is resolved lazily, so the throw is at `Tick`.)
- **B11 (OnResumeFail intercepts AwaitUser)** — Given a main step with `RequiredZone = "128"`, `ResumePointFragmentId = "fragment-fail"`, and `Recover = new RecoverConfig { OnResumeFail = new AwaitUserRecoverAction { Reason = "resume blocked" } }`; player in zone 130; AND `"fragment-fail"` whose first step's dispatch yields `AwaitUser` (configure the fake so the fragment step produces an adapter failure → `AwaitUser`; e.g. a step type / state that the dispatcher maps to `AwaitUser`). When `Tick`, then the returned action is `EngineAction.AwaitUser` whose `Reason == "resume blocked"` (the `OnResumeFail` action), NOT the raw fragment-failure reason.
- **B12 (no OnResumeFail → raw AwaitUser propagates)** — Same as B11 but `Recover.OnResumeFail` is null (or `Recover` is null). When `Tick`, the returned `AwaitUser`'s reason is the raw fragment-step failure reason (not intercepted). This locks that interception only happens when `OnResumeFail` is set.
- **B13 (fragment exhausted, zone still wrong → Wait, stays armed)** — Given a fragment with one step that has an `Expect` that is already true (so it confirms immediately) but does NOT move the player; player in zone 130, `RequiredZone = "128"`.
  - Tick 1: fragment step confirmed; zone still 130 → returns `EngineAction.Wait` (exhausted-but-not-arrived). `_activeResumeFragment` remains set.
  - Tick 2 (zone still 130): still `Wait` (re-checks the gate; fragment step still confirmed). Assert the engine has NOT fallen through to the main step and has NOT cleared the active resume.
- **B14 (resume cursor independent of main cursor)** — Given a fragment step and a main step that share the same raw `Id` (e.g. both `"go"`). Trigger the resume (player in wrong zone). Assert the fragment step's confirmation does NOT confirm the main step: after the resume completes (zone match) and the main step dispatches, the main step is dispatched (not skipped as if confirmed). Proves the per-fragment `ConfirmedFragmentStepIds` set is separate from `_confirmedStepIds`.

### Group C — validator rules (questforge-tools `QuestForge.Tools.Validator.Tests`)

- **C1 (R1: set + non-empty → no error)** — Given a `TravelStep` with `ResumePointFragmentId = "fragment-x"` inside `QuestBuilder.Valid(...)`, when validated, then `AssertNoError(errors, "structural/resume-point-fragment-id-empty")`.
- **C2 (R1: null → no error)** — Given a step with `ResumePointFragmentId = null`, when validated, then no `resume-point-fragment-id-empty` error.
- **C3 (R1: empty string → Error)** — Given a step with `ResumePointFragmentId = ""`, when validated, then exactly one `structural/resume-point-fragment-id-empty` error with `Severity.Error` and `StepId` == the step's id.
- **C4 (R1: whitespace → Error)** — Given `ResumePointFragmentId = "   "`, when validated, then one `resume-point-fragment-id-empty` Error.
- **C5 (R1: nested in branch → reported)** — Given a `BranchStep` whose nested step has `ResumePointFragmentId = ""`, when validated, then one `resume-point-fragment-id-empty` Error with the nested step's id (walker recurses into `Branches[].Steps`).
- **C6 (R2: fragment step declaring ResumePointFragmentId → Error)** — Given a `FragmentDefinition` one of whose steps sets `ResumePointFragmentId = "fragment-y"` (any value), when the fragment is validated, then one `structural/resume-point-nested` Error with that step's id.
- **C7 (R2: fragment step without ResumePointFragmentId → no R2 error)** — Given a `FragmentDefinition` whose steps all leave `ResumePointFragmentId` null, when validated, then no `resume-point-nested` error.
- **C8 (R3: fragment step id not `fragment-*` → Warning)** — Given a `FragmentDefinition` with a step id `"go-to-uldah"` (no `fragment-` prefix) and `ResumePointFragmentId` null, when validated, then one `structural/fragment-step-id-convention` **Warning** with that step's id.
- **C9 (R3: fragment step id with `fragment-*` → no warning)** — Given a `FragmentDefinition` with step id `"fragment-go-to-uldah"`, when validated, then no `fragment-step-id-convention` warning.
- **C10 (R3 suppressed when R2 fires)** — Given a fragment step with id `"go"` (violates R3) AND `ResumePointFragmentId = "x"` (violates R2), when validated, then exactly one error/warning for that step: `resume-point-nested` (Error), and NO `fragment-step-id-convention` warning for the same step (R3 suppressed when R2 fired).
- **C11 (R1 in fragment file too)** — Given a `FragmentDefinition` step with `ResumePointFragmentId = ""`, when validated — note this trips R2 (any presence) AND R1 (empty). Assert at minimum the `resume-point-nested` Error is present; document expected interaction: R2 fires on presence, R1 fires on emptiness. (Builder decides whether both emit or R2 short-circuits; the Tester asserts `resume-point-nested` is present and pins the chosen behavior for R1 with a follow-up assertion once the Builder confirms. Default expectation: both may emit since they are independent walkers; the test asserts `resume-point-nested` is present and does not over-constrain R1.)

### Group D — docs (PR review, no automated test)

- **D1** — SCHEMA.md §4.1 field table contains a `resumePointFragmentId` row documenting the cold-resume / zone-gated-sub-loop semantics and the depth-1 restriction.
- **D2** — SCHEMA.md §4.1 JSON example contains `"resumePointFragmentId": null` after the `"requiredZone": null,` line.
- **D3** — `schemaVersion` remains `"1.0.0"` (no bump).

---

## 7. Implementation order

**Phase A — Schema (both repos), 0.5 day.**
Add `Step.ResumePointFragmentId` and `RecoverConfig.OnResumeFail` to `QuestForge.Schema` in `questforge` and the byte-identical copy in `questforge-tools`. Make group A tests pass (in `questforge`). Done before B and C.

**Phase B — Engine (`questforge`), 1.5 days.**
1. Add `_fragments`, `_resumePointExecutedIds`, `_activeResumeFragment` fields + the `ActiveResumeFragment` record.
2. Store `_fragments` in `StartQuest`; clear new state in `BeginRun` and on sequence change.
3. Add the per-tick `GetPlayerZone` read.
4. Implement `ProcessActiveResume` and the main-loop resume trigger + `ArmResumeFragment` / `ZoneAlreadySatisfied` / `ZoneMatches` helpers.
5. Implement the `OnResumeFail` interception (`MapRecoverAction` or reuse existing recover dispatch).
6. Make group B pass. Done before nothing else depends on B; B and C are independent across repos.

**Phase C — Validator (`questforge-tools`), 0.5 day.**
Add `ValidateResumePoint` / `CheckResumePoint` (R1) to the quest pipeline; add/extend the fragment-file validation path running R1+R2+R3. Make group C pass.

**Phase D — Docs (`questforge`).**
Apply the SCHEMA.md §4.1 (and optional §6) edits. No tests; reviewed in PR.

---

## 8. Done criteria

1. `Step.ResumePointFragmentId` and `RecoverConfig.OnResumeFail` exist in **both** schema copies, byte-identical, and round-trip; `ResumePointFragmentId == null` serializes as `"resumePointFragmentId":null` and `OnResumeFail == null` as `"onResumeFail":null` (group A green) — i.e. no `[JsonIgnore]` added.
2. On a **warm** run (player already in `RequiredZone`), the resume never triggers and the main step dispatches normally (B1, B8, B9 green).
3. On a **cold** run (player not in `RequiredZone`, step has `ResumePointFragmentId`), the engine runs the positioning fragment as a zone-gated sub-loop and dispatches the main step only after the player reaches `RequiredZone` — termination is the zone match, not fragment-step exhaustion (B2, B3, B4 green).
4. The resume is one-shot per run+sequence: re-entering the wrong zone does not re-trigger within the same sequence (B5), but a sequence change or `BeginRun` re-arms it (B6, B7 green).
5. A missing resume fragment throws at `Tick` (lazy resolution), distinct from inline `FragmentStep` which throws at `StartQuest` (B10 green).
6. `OnResumeFail`, when set on the main step, intercepts a resume-fragment `AwaitUser` and substitutes its own recovery action; when unset, the raw `AwaitUser` propagates (B11, B12 green).
7. An exhausted fragment whose goal zone is not yet reached emits `Wait` and stays armed (B13 green); fragment and main cursors are independent (B14 green).
8. A `questforge-data` fragment/quest PR with an empty `resumePointFragmentId` → CI red via `structural/resume-point-fragment-id-empty` (Error); a fragment whose step sets `ResumePointFragmentId` → CI red via `structural/resume-point-nested` (Error); a non-`fragment-*` fragment step id → CI warning via `structural/fragment-step-id-convention` (group C green).
9. SCHEMA.md §4.1 documents `resumePointFragmentId`; `schemaVersion` remains `"1.0.0"` (group D).
10. All existing tests still pass — `ConfirmedStepCursorTests`, `FragmentExecutionTests`, `RequiredZone*Tests`, the §38 round-trip tests, and the full validator suite are unaffected (the new per-tick zone read fails open; the trigger only fires when all four conditions hold, so steps without `ResumePointFragmentId` are completely unaffected).

---

## 9. Exclusions

This issue does **NOT** include:

- **Cross-file fragment-existence validation.** The validator does not check that a `resumePointFragmentId` resolves to a real fragment in the (future) `questforge-data` library. Validation is syntax-only (§4). Cross-file resolution is deferred until the fragment library has a canonical home.
- **Nested / recursive resume positioning.** Depth-1 only. A resume fragment's steps may not declare `ResumePointFragmentId` (enforced by R2). No nested sub-loops.
- **Parameter substitution into resume fragments.** Inline `FragmentStep` supports `${name}` token substitution; resume fragments are referenced by id only (no `Params`). If a resume fragment needs parameters, that is a future enhancement.
- **Aethernet / teleport authoring of the fragment itself.** The plan does not author any concrete positioning fragment (e.g. `fragment-teleport-to-uldah`); those live in `questforge-data`. Engine tests use synthetic fragments.
- **`OnResumeFail` for non-`AwaitUser` failure modes.** Only the `AwaitUser` surface is intercepted (the only adapter-error surface the resume sub-loop produces). Death recovery, timeouts, and obstacles inside a resume fragment are out of scope; they would route through the existing per-step recovery if/when the dispatcher emits those signals.
- **Live `TraceMode` switching, UI surfacing of resume state, or trace events specific to resume.** The existing decision/observation trace events cover resume actions (they are ordinary `EngineAction`s); no new event types are added.
- **`schemaVersion` bump** (additive optional fields; consistent with §38).
- **Eager `StartQuest`-time validation of resume fragments.** Resume fragments are resolved lazily at `Tick` precisely because a warm run never needs them; pre-expanding/validating them at `StartQuest` would defeat the warm-run optimization.

---

## 10. Open questions / risks

1. **Two schema copies can drift.** `Step.ResumePointFragmentId` and `RecoverConfig.OnResumeFail` must be added byte-identically to both `questforge` and `questforge-tools`. (Roadmap Phase 3 unifies via NuGet; until then this is a manual sync risk. Group A round-trip tests run only in `questforge`; the tools-side additions are exercised indirectly by group C.)
2. **Recover-action dispatch reuse.** §3.7 assumes either an existing recover-action dispatcher (route `OnResumeFail` through it) or the stub `MapRecoverAction`. The Builder must confirm which exists; the acceptance criteria assert only `AwaitUserRecoverAction` reason propagation, so any conformant mapping passes.
3. **Fragment-file validation entry point.** R2/R3 require the validator to structurally walk `FragmentDefinition.Steps`. If the tools repo currently only loads fragments into `IFragmentRegistry` for lookup (no structural pass), the Builder adds that entry point (§4.5 open item). The Tester can unit-test R2/R3 against the method directly regardless.
4. **`AwaitUser` as the sole resume-failure surface.** §3.7 ties `OnResumeFail` to the dispatcher emitting `EngineAction.AwaitUser`. If a future step type inside a resume fragment fails via a different action, `OnResumeFail` will not catch it. Acceptable for #39; the dispatcher's only failure surface today maps to `AwaitUser`.
5. **C11 interaction (R1 + R2 on an empty id inside a fragment).** R1 (empty) and R2 (presence) both apply to an empty `ResumePointFragmentId` inside a fragment. The plan treats them as independent walkers (both may emit). The Tester asserts `resume-point-nested` is present and does not over-constrain R1 until the Builder confirms whether to short-circuit.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §6.
- Happy paths: 9 scenarios (A1, A3, A4, A5, A6, B1, B2, B3, C1)
- Edge cases: 13 scenarios (A2, A7, B4, B5, B6, B7, B8, B9, B13, B14, C2, C7, C9)
- Error cases: 11 scenarios (B10, B11, B12, C3, C4, C5, C6, C8, C10, C11) plus the C2 no-error guard
- Expected total: ~33 tests — ~7 in `QuestForge.Schema.Tests` (group A), ~14 in `QuestForge.Engine.Tests` (group B), ~11 in `QuestForge.Tools.Validator.Tests` (group C, questforge-tools). Group D is PR review only.
