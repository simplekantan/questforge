# DraftValidator Strengthen — Targeted Quality Plan

**Status:** ready for test creation
**Type:** focused work item (not a phase)
**Input docs:** `docs/PHASE_9_PLAN.md` §3.5, §5.4; `docs/SCHEMA.md` §7; `docs/BACKLOG.md` §5.5, §12; `.claude/agents/tester.md`
**Source under change:** `QuestForge.Engine/Authoring/DraftValidator.cs`, `QuestForge.Engine.Tests/Authoring/DraftValidatorTests.cs`
**Output:** every DraftValidator rule (E1-E6, W1-W6) has a strict positive test (`AssertSingleError` / `AssertSingleWarning`) and at least one negative test. E3 is wired to `QuestForge.Predicates.PredicateParser`. W6 detects empty/placeholder quest names.

---

## 1. Dependency graph

```
QuestForge.Predicates        ← already a ProjectReference of QuestForge.Engine and .Tests
   │     PredicateParser.Parse(source) → ParseResult { Ast, Errors[] }
   │     PredicateChecker.Check(ast)   → ParseError[] (arity, type, unknown-function)
   ▼
QuestForge.Engine/Authoring/DraftValidator.cs
   │     Calls PredicateParser + PredicateChecker (replacing the substring check at line 57)
   │     Adds W6 (empty quest name)
   ▼
QuestForge.Engine.Tests/Authoring/
   ├── DraftValidatorAssertions.cs        ← new helper file (AssertSingleError / AssertSingleWarning)
   ├── DraftValidatorTestData.cs          ← new helper for constructing DraftStep / QuestDraft fixtures
   └── DraftValidatorTests.cs             ← strengthened + new tests
```

**Build order:**
1. New test assertions helper (cannot fail to compile — no DraftValidator change yet).
2. New tests asserting the strengthened contracts (RED — most fail until Builder wires E3, W6 and the helpers are used).
3. Builder wires E3 to predicate parser + adds W6 rule.
4. Builder converts the existing tests to use the strict helper.

No source needs to move; `QuestForge.Predicates` is already referenced (`QuestForge.Engine.csproj:13`, `QuestForge.Engine.Tests.csproj:13`).

---

## 2. Architectural decisions (read before coding)

### 2.1 E1 positive test bypasses `AddStep` via construction-time list manipulation through `ReplaceStep`

`QuestDraft.AddStep` actively rejects duplicate `StepId` (`QuestDraft.cs:29-30`). The existing E1 test (`DraftValidatorTests.cs:99-143`) sidesteps this by asserting the *absence* of E1 — which is tautological because `AddStep` makes the precondition unreachable through the public API.

**Three options were considered:**

| Option | Verdict |
|---|---|
| Add a public `AddStepUnchecked` to `QuestDraft` | Rejected — pollutes the production API just to enable a test. |
| Use reflection to mutate the private `_steps` list | Rejected — brittle, breaks when field is renamed, hides the test intent. |
| Add an `internal` constructor / factory on `QuestDraft` that accepts a pre-built `List<DraftStep>` and wire `[InternalsVisibleTo("QuestForge.Engine.Tests")]` | **Chosen.** |

**Concrete surface:**

```csharp
// QuestForge.Engine/Properties/AssemblyInfo.cs  (new file, or top of QuestDraft.cs)
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("QuestForge.Engine.Tests")]

// QuestForge.Engine/Authoring/QuestDraft.cs  (additions only)
public sealed class QuestDraft
{
    // ... existing members ...

    /// <summary>
    /// Test-only escape hatch: construct a draft whose internal step list is
    /// not guarded by AddStep's uniqueness invariant. Required for E1 (duplicate
    /// stepId) positive testing and for any future test that simulates a draft
    /// loaded from a corrupted file.
    /// </summary>
    internal static QuestDraft CreateForTest(
        QuestId questId,
        DateTimeOffset createdAt,
        IEnumerable<DraftStep> steps,
        string? questName = null)
    {
        var draft = new QuestDraft(questId, createdAt) { QuestName = questName };
        draft._steps.AddRange(steps);
        return draft;
    }
}
```

**Why this is safe:** `internal` + `InternalsVisibleTo` is already the pattern used elsewhere in this codebase for test-only surfaces. The method name `CreateForTest` documents intent; production code that calls it would fail review.

**What breaks if violated:** Tests that use reflection or duplicate the field would have to be rewritten whenever `_steps` is renamed. Tests that add public mutator methods would pollute the production API. Either alternative drifts the validator's testability away from its production usage shape.

### 2.2 E3 vs E5 split — exact mapping from `ParseError.Code` to validator code

The Phase 2 predicate parser surfaces failures via `ParseResult { Ast, Errors: IReadOnlyList<ParseError> }`. Each `ParseError` carries a `Code` (string). The semantic check (`PredicateChecker.Check`) emits additional `ParseError` entries.

**Mapping:**

| `ParseError.Code` source | Maps to | Rationale |
|---|---|---|
| `"unknown-function"` (from `PredicateChecker`) | **E5** | Matches `PHASE_9_PLAN.md §3.5`: "predicate references an unknown function (did-you-mean)". Existing E5 message format already mentions did-you-mean. |
| `"parse-error"` (from `Parser`/`Lexer`) | **E3** | Syntax-level failure (unbalanced parens, bad token, missing arg, float on non-`playerNear` slot, etc.). |
| `"arity-mismatch"` (from `PredicateChecker`) | **E3** | Phase 9 spec puts arity under "predicate parse failure". |
| `"type-mismatch"` (from `PredicateChecker`) | **E3** | Same — type checks are part of the parser-driven validation pass. |
| `"default-not-composable"` (from `PredicateChecker`) | **E3** | Syntactically legal but semantically rejected. |
| `"unknown-parameter"` (from `Parser`) | **E3** | Drafts have no fragment-parameter scope; a `${param}` reference is a parse-time error in the draft context. |

**Order of operations inside the validator:**

```csharp
// New helper in DraftValidator
private void ValidatePredicate(string? predicate, int stepIndex, string stepId,
                               List<DraftValidationError> errors)
{
    if (predicate is null) return;

    var result = PredicateParser.Parse(predicate);   // no scope — drafts are not fragments

    // Bucket every ParseError by code
    foreach (var pe in result.Errors)
    {
        var code = pe.Code == "unknown-function" ? "E5" : "E3";
        var msg = code == "E5"
            ? $"Step '{stepId}' predicate '{predicate}' references an unknown function. {pe.Message}{(pe.Suggestion is null ? "" : $" Did you mean '{pe.Suggestion}'?")}"
            : $"Step '{stepId}' predicate '{predicate}' failed to parse: {pe.Message}";
        errors.Add(new DraftValidationError(code, msg, [stepIndex]));
    }

    // If parser produced an AST, run the semantic checker; its errors follow the same mapping
    if (result.Ast is not null)
    {
        foreach (var ce in PredicateChecker.Check(result.Ast))
        {
            var code = ce.Code == "unknown-function" ? "E5" : "E3";
            var msg = $"Step '{stepId}' predicate '{predicate}': {ce.Message}";
            errors.Add(new DraftValidationError(code, msg, [stepIndex]));
        }
    }
}
```

**Predicates evaluated:** `step.Raw.Expect` AND `step.Raw.SkipIf`, both unwrapped via the existing `ExtractPredicate(ExpectValue?)` helper. The current code only inspects `Expect`; SkipIf is a known gap (covered in §7).

**Composite Expect values (`AllExpect`, `AnyExpect`):** Each element predicate string in the array is parsed independently. `ExtractPredicate` is extended to return `IReadOnlyList<string>`:

```csharp
private static IReadOnlyList<string> ExtractPredicates(ExpectValue? expect) => expect switch
{
    PredicateExpect p => [p.Predicate],
    AllExpect a       => a.All,
    AnyExpect a       => a.Any,
    _                 => []
};
```

**What breaks if the split is wrong:** Authors typing `questSequnece(2054)` see E3 with no did-you-mean hint (the current `E5` behaviour); authors typing `playerNear({x:1})` (arity mismatch) see E5 implying a typo when there is none.

**Testability implication:** Each test fixes one predicate-error code and asserts `AssertSingleError(errors, "E3")` or `("E5")`. Theory tests cover the parse-error/arity/type axis.

### 2.3 W6 — empty / placeholder quest name detection

Per `BACKLOG.md §12`, fire when `QuestDraft.QuestName` is unset or set to a placeholder.

**Rule (final):** W6 fires when, after trimming whitespace, `QuestName` is null, empty, or — case-insensitively — `"TODO"`.

| Input | W6 fires? | Why |
|---|---|---|
| `null` | yes | Author never set a name. |
| `""` | yes | Empty string. |
| `"   "` | yes | Whitespace-only. |
| `"TODO"` | yes | Common placeholder. |
| `"todo"` | yes | Case-insensitive. |
| `"  TODO "` | yes | Trim, then compare. |
| `"Todo: figure out name"` | no | Substring of "TODO" is allowed — only the whole-string placeholder is flagged. |
| `"Close to Home"` | no | Real name. |

**Placeholder list is a single-entry constant**, not configurable:

```csharp
private static readonly HashSet<string> QuestNamePlaceholders =
    new(StringComparer.OrdinalIgnoreCase) { "TODO" };

// Detection
var name = draft.QuestName?.Trim();
if (string.IsNullOrEmpty(name) || QuestNamePlaceholders.Contains(name))
{
    warnings.Add(new DraftValidationWarning("W6",
        "Quest name is empty or a placeholder ('TODO'). Set QuestDraft.QuestName before export."));
}
```

**Alternatives rejected:**
- Configurable placeholder list — overkill for one entry; deferrable.
- Detect "TODO" anywhere in the string — false-positives like `"Todo: name pending"` would block legitimate working drafts.
- Detect "??" / `"<unnamed>"` etc. — none observed in real authoring data; expand if it ever shows up.

**What breaks if violated:** Authors export drafts with `Name = ""` and `qf-validate` later emits `structural/required-field-missing` on the produced JSON — discovered after export, not before. W6 is preventative.

### 2.4 Test discipline — new `DraftValidatorAssertions` helper

The existing `QuestBuilder.AssertSingleError` (`questforge-tools/QuestForge.Tools.Validator.Tests/QuestBuilder.cs:62`) lives in a different repo and operates on `ValidationError`. It is not reusable because `DraftValidator` returns `DraftValidationError` / `DraftValidationWarning` (different record type, returned as a tuple).

**Choice:** create `QuestForge.Engine.Tests/Authoring/DraftValidatorAssertions.cs` with parallel API:

```csharp
namespace QuestForge.Engine.Tests.Authoring;

internal static class DraftValidatorAssertions
{
    /// <summary>
    /// Asserts exactly one error with the given code, exactly zero warnings.
    /// Use when the contract is "this rule and only this rule".
    /// </summary>
    public static void AssertSingleError(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        string code)
    {
        var (errors, warnings) = result;
        Assert.True(errors.Count == 1,
            $"Expected exactly 1 error but got {errors.Count}: " +
            $"[{string.Join(", ", errors.Select(e => $"'{e.Code}': {e.Message}"))}]");
        Assert.Equal(code, errors[0].Code);
        Assert.True(warnings.Count == 0,
            $"Expected zero warnings but got {warnings.Count}: " +
            $"[{string.Join(", ", warnings.Select(w => $"'{w.Code}': {w.Message}"))}]");
    }

    /// <summary>
    /// Asserts exactly one warning with the given code, exactly zero errors.
    /// </summary>
    public static void AssertSingleWarning(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        string code)
    {
        var (errors, warnings) = result;
        Assert.True(warnings.Count == 1,
            $"Expected exactly 1 warning but got {warnings.Count}: " +
            $"[{string.Join(", ", warnings.Select(w => $"'{w.Code}': {w.Message}"))}]");
        Assert.Equal(code, warnings[0].Code);
        Assert.True(errors.Count == 0,
            $"Expected zero errors but got {errors.Count}: " +
            $"[{string.Join(", ", errors.Select(e => $"'{e.Code}': {e.Message}"))}]");
    }

    /// <summary>
    /// Asserts the result contains errors with exactly these codes (multi-set equality),
    /// and zero warnings. Use when a fixture deliberately triggers more than one rule.
    /// </summary>
    public static void AssertErrorCodes(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        params string[] expectedCodes)
    {
        var actual = result.Errors.Select(e => e.Code).OrderBy(c => c).ToArray();
        var expected = expectedCodes.OrderBy(c => c).ToArray();
        Assert.Equal(expected, actual);
        Assert.True(result.Warnings.Count == 0,
            $"Expected zero warnings but got {result.Warnings.Count}: " +
            $"[{string.Join(", ", result.Warnings.Select(w => $"'{w.Code}'"))}]");
    }

    /// <summary>
    /// Asserts the result is completely clean — no errors, no warnings.
    /// Use for the happy-path baseline (already covered by Validation_PassesForValidDraft).
    /// </summary>
    public static void AssertClean(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result)
    {
        Assert.True(result.Errors.Count == 0 && result.Warnings.Count == 0,
            $"Expected clean result. Errors: [{string.Join(", ", result.Errors.Select(e => e.Code))}], " +
            $"Warnings: [{string.Join(", ", result.Warnings.Select(w => w.Code))}]");
    }
}
```

**Why parallel and not shared:** the questforge-tools repo is consumed only via `QuestForge.Predicates` ProjectReference. The validator test helper lives next to validator tests; replicating the pattern is cheaper than reaching across repos for an `internal static` class.

### 2.5 Test data helper — `MakeDraftStep` deduplication

The existing test file inlines `MakeDraftStep` and `MakeSnapshot`. New tests will need:
- A `QuestDraft` baseline with a valid accept + turn-in (so E4 doesn't fire in unrelated tests).
- The ability to inject one mutated step.

**Choice:** lift `MakeDraftStep`/`MakeSnapshot` into a `DraftValidatorTestData` static helper in the same namespace. The existing `Validation_PassesForValidDraft` test serves as the canonical baseline.

```csharp
internal static class DraftValidatorTestData
{
    public static readonly QuestId Quest2054 = new(2054);
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    public static readonly NpcLocation ValidNpcLoc =
        new(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

    public static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) => /* ... */;

    public static DraftStep MakeDraftStep(
        string stepId, int seqNum, Step raw,
        InferredFrom inferredFrom = InferredFrom.QuestSequenceChange,
        string? notes = null) => /* ... */;

    /// <summary>Baseline: accept + turn-in, both with valid expect. No warnings, no errors.</summary>
    public static QuestDraft ValidBaseline(string questName = "Test Quest")
    {
        var draft = new QuestDraft(Quest2054, T0) { QuestName = questName };
        draft.AddStep(MakeDraftStep("accept-quest", 0,
            new AcceptStep { Id = "accept-quest", Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }},
            notes: "accept"), T0);
        draft.AddStep(MakeDraftStep("turn-in-quest", 1,
            new TurnInStep { Id = "turn-in-quest", Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }},
            notes: "turn-in"), T0);
        return draft;
    }
}
```

**Note on baseline:** the baseline supplies `notes` on every step (to suppress W2) and a real `QuestName` (to suppress W6). Tests that target a specific rule must opt into the conditions that trigger it; the baseline must be clean.

---

## 3. Rule table (post-strengthen)

| Code | Severity | Trigger | Current state | Plan |
|---|---|---|---|---|
| E1 | Error | Duplicate `stepId` across draft steps | Implemented; test is tautological | Add real positive test via `CreateForTest` |
| E2 | Error | `DraftStep.Raw == null` | Implemented; test exists | Strengthen to `AssertSingleError` |
| E3 | Error | Predicate parse error / arity / type / default-not-composable / unknown-parameter | Stubbed (substring check, see line 57) | **Wire to `PredicateParser` + `PredicateChecker`** |
| E4 | Error | No `AcceptStep` anywhere in draft | Implemented; test exists | Strengthen to `AssertSingleError` |
| E5 | Error | Predicate references unknown function name | Implemented (substring-based); existing test exists | Re-route through `PredicateChecker`; strengthen test |
| E6 | Error | `TalkStep.Target.NpcId == 0` | Implemented; **untested** | Add positive + negative tests |
| W1 | Warning | `step.Raw.Expect == null` (PredicateExpect with empty string is *not* W1; that becomes E3) | Implemented; test exists | Strengthen to `AssertSingleWarning` |
| W2 | Warning | `step.Notes == null` AND `step.InferredFrom == InferredFrom.None` | Implemented; **untested** | Add positive test. Note: PHASE_9 spec says "Confidence == Low" but the implementation uses `InferredFrom.None`; this plan keeps the implementation behaviour and updates §7 to record the drift. |
| W3 | Warning | Last step in the draft is a `TravelStep` | Implemented; **untested** | Add positive test |
| W4 | Warning | Two consecutive `TalkStep`s share `Target.NpcId` | Implemented; **untested** | Add positive + adjacency-only negative test |
| W5 | Warning | Sequence group with zero steps (cannot occur via normal Add/Remove) | Implemented (no-op in practice); has negative test only | Keep as-is — `CreateForTest` could simulate this for completeness (optional) |
| W6 | Warning | `QuestName` null/empty/whitespace/`"TODO"` (case-insensitive, trimmed) | **Not implemented** | Implement + Theory test over variants |

**Codes are bare letters** (`"E1"`, `"W6"`) — matching the existing `DraftValidationError.Code` shape. They do not use slash-notation (`"draft/E1"`) because the existing public API uses unprefixed codes; renaming them would be an unrelated breaking change.

---

## 4. Task breakdown

### Task 1 — Add `InternalsVisibleTo` + `CreateForTest` factory

**Files:**
- `QuestForge.Engine/Authoring/QuestDraft.cs` — add `CreateForTest` static factory (see §2.1).
- One of: `QuestForge.Engine/Properties/AssemblyInfo.cs` (new) **or** top-of-file `[assembly: InternalsVisibleTo("QuestForge.Engine.Tests")]` annotation. Prefer the dedicated file because the assembly may need more attributes later.

**Verification:** existing `QuestDraftTests.DraftStep_Add_RejectsDuplicateStepId` still passes (production guard unchanged).

### Task 2 — Add `DraftValidatorAssertions` helper

**File (new):** `QuestForge.Engine.Tests/Authoring/DraftValidatorAssertions.cs`

Body per §2.4. Internal-static, lives in the test assembly.

### Task 3 — Add `DraftValidatorTestData` helper

**File (new):** `QuestForge.Engine.Tests/Authoring/DraftValidatorTestData.cs`

Body per §2.5. Lifts `MakeSnapshot` / `MakeDraftStep` out of the test class and adds `ValidBaseline()`.

### Task 4 — Wire E3 to the predicate parser

**File:** `QuestForge.Engine/Authoring/DraftValidator.cs`

Replace lines 48-67 (current E3/E5 block) with:

```csharp
// E3 / E5: full predicate parser + checker against Expect and SkipIf
for (var i = 0; i < steps.Count; i++)
{
    var step = steps[i];
    if (step.Raw is null) continue;             // already flagged by E2

    foreach (var pred in ExtractPredicates(step.Raw.Expect))
        ValidatePredicate(pred, i, step.StepId, errors);

    foreach (var pred in ExtractPredicates(step.Raw.SkipIf))
        ValidatePredicate(pred, i, step.StepId, errors);
}
```

Delete `HasKnownFunction`, `ExtractFunctionName`, `FindClosest`, `CommonPrefixLength`, and the static `KnownFunctions` set. Add `ValidatePredicate` and `ExtractPredicates` per §2.2.

**Imports added:** `using QuestForge.Predicates;`

### Task 5 — Add W6 rule

**File:** `QuestForge.Engine/Authoring/DraftValidator.cs`

After the W5 comment block, add:

```csharp
// W6: empty / placeholder quest name
var name = draft.QuestName?.Trim();
if (string.IsNullOrEmpty(name) || QuestNamePlaceholders.Contains(name))
{
    warnings.Add(new DraftValidationWarning("W6",
        "Quest name is empty or a placeholder ('TODO'). Set QuestDraft.QuestName before export."));
}
```

Plus the `QuestNamePlaceholders` constant per §2.3.

### Task 6 — Rewrite the test file

**File:** `QuestForge.Engine.Tests/Authoring/DraftValidatorTests.cs`

Existing 7 tests are converted to strict assertions. New tests for E1 (real positive), E3 (5 variants), E6, W2, W3, W4, W6 (Theory) are added. The full per-test breakdown is §5 below.

### Task 7 — Update BACKLOG.md to reflect completed work

**File:** `docs/BACKLOG.md`

Small doc-only edit, done by the Builder in the same PR as Tasks 1-6:

- **§5.5:** remove the stale stub note describing E3 as substring-based; replace with a one-line entry marking E3 wiring to `PredicateParser` + `PredicateChecker` as done in this work item.
- **§12:** remove the W6 row entirely (it is now implemented and tested).

**Verification:** `git diff docs/BACKLOG.md` shows exactly the two edits above and nothing else.

---

## 5. Given-When-Then specifications (Tester reads from here)

All tests live in `QuestForge.Engine.Tests/Authoring/DraftValidatorTests.cs`. Each test starts from `DraftValidatorTestData.ValidBaseline()` and mutates exactly one thing.

### Strengthened existing tests (7)

**T1 — `Validation_PassesForValidDraft`**
*Given* the baseline (accept + turn-in, both with `expect`, both with `notes`, `QuestName = "Test Quest"`),
*when* `Validate(draft)` is called,
*then* `DraftValidatorAssertions.AssertClean(result)` — zero errors, zero warnings.
**Strictness:** This is the *negative-for-everything* test. If any new rule fires unexpectedly on a clean baseline, this test catches it.

**T2 — `E2_RawNull_ReportsSingleError`** (was `Validation_FailsForRawNullStep`)
*Given* the baseline plus a third step with `Raw = null` (constructed directly, since the baseline can't bypass type contracts but a `DraftStep` record accepts a nullable `Raw`),
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E2")`.
**Note:** The W1 warning (no `Expect`) would also fire for a null-raw step in principle, but the implementation guards `if (step.Raw is null) continue;` in the W1 loop, so W2 also skips. Confirm both W1 and W2 are suppressed. Test must use `AssertSingleError` to ensure no spillover.

**T3 — `E4_MissingAcceptStep_ReportsSingleError`** (was `Validation_FailsForMissingAcceptStep`)
*Given* a draft with only a `TalkStep` and a `TurnInStep` (both fully valid, both with `notes`, `QuestName` set),
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E4")`.

**T4 — `E5_UnknownFunction_ReportsSingleError`** (was `Validation_FailsForUnparseablePredicate`)
*Given* the baseline plus a `TalkStep` with `Expect = PredicateExpect("questSequnece(2054) >= 3")` (typo) and `Notes = "x"`,
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E5")`. The error message must contain `"questSequence"` (the did-you-mean target).

**T5 — `W1_MissingExpect_ReportsSingleWarning`** (was `Validation_WarnsForMissingExpect`)
*Given* the baseline plus a `TalkStep` with `Expect = null` and `Notes = "x"` (notes set to suppress W2),
*when* `Validate(draft)` is called,
*then* `AssertSingleWarning(result, "W1")`.

**T6 — `W5_NotEmittedForNumericGap`** (kept — already verifies absence)
*Given* draft with steps at `sequenceNumber` 0 and 2 (gap at 1), both with `notes` and `expect`,
*when* `Validate(draft)` is called,
*then* `AssertClean(result)` — no W5 (W5 is reserved for empty groups, not numeric gaps).

**T7 — *(remove this test)*** The old `Validation_FailsForDuplicateStepIds` asserts the absence of E1, which is meaningless. Replaced by T8.

### New tests for previously-uncovered rules

**T8 — `E1_DuplicateStepId_ReportsSingleError`** (new — real positive)
*Given* a draft constructed via `QuestDraft.CreateForTest` with two `DraftStep`s sharing `StepId = "step-x"` (one of them is the accept step so E4 doesn't fire),
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E1")`. The error message must contain `"step-x"`.
**Strictness:** must use `CreateForTest` (§2.1). Both steps fully populated (Raw, Expect, Notes) so no other rules fire. `QuestName` set to suppress W6.

**T9 — `E6_TalkStep_NpcIdZero_ReportsSingleError`** (new)
*Given* the baseline plus a `TalkStep` with `Target = NpcLocation(NpcId: 0, Zone: 128, Position: …)`, `Expect` set, `Notes` set,
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E6")`.

**T10 — `E6_TalkStep_NpcIdNonZero_DoesNotFire`** (new — boundary)
*Given* the baseline plus a `TalkStep` with `Target.NpcId = 1` (smallest nonzero), `Expect`, `Notes`,
*when* `Validate(draft)` is called,
*then* `AssertClean(result)`.

**T11 — `W2_NoNotesAndInferredFromNone_ReportsSingleWarning`** (new)
*Given* a draft with an accept step (notes set), a turn-in step (notes set), and a `TalkStep` with `Notes = null`, `InferredFrom = InferredFrom.None`, `Expect` set,
*when* `Validate(draft)` is called,
*then* `AssertSingleWarning(result, "W2")`.

**T12 — `W2_NotFiredWhenInferredFromIsSet`** (new — negative)
*Given* the same draft as T11 but the third step has `InferredFrom = InferredFrom.QuestSequenceChange` and `Notes = null`,
*when* `Validate(draft)` is called,
*then* `AssertClean(result)`.

**T13 — `W3_LastStepIsTravel_ReportsSingleWarning`** (new)
*Given* a draft with an accept step (notes) and a final `TravelStep` (notes, expect; the only "turn-in-equivalent" is the travel — so E4 fires only if accept is missing — accept is present),
*when* `Validate(draft)` is called,
*then* `AssertSingleWarning(result, "W3")`.
**Strictness:** the absent `TurnInStep` must not break the test — E4 fires only when no `AcceptStep` exists, not when no turn-in exists. The baseline must include the accept step.

**T14 — `W3_NotFiredWhenLastStepIsTurnIn`** (new — negative)
*Given* the baseline (accept + turn-in), exactly the same as T1,
*when* `Validate(draft)` is called,
*then* the result must not contain W3 (`AssertClean` already covers this).

**T15 — `W4_ConsecutiveTalkStepsSameNpc_ReportsSingleWarning`** (new)
*Given* a draft with accept (NpcId=1000789), TalkStep#1 (NpcId=1014875, notes, expect), TalkStep#2 (NpcId=1014875, notes, expect), turn-in (NpcId=1000789),
*when* `Validate(draft)` is called,
*then* `AssertSingleWarning(result, "W4")`.

**T16 — `W4_NonConsecutiveSameNpc_DoesNotFire`** (new — negative)
*Given* a draft with accept (NpcId=1000789), TalkStep#1 (NpcId=1014875), TalkStep#2 (NpcId=2000000), TalkStep#3 (NpcId=1014875), turn-in,
*when* `Validate(draft)` is called,
*then* result has no W4 (other rules clean).

**T17 — `W4_AcceptThenTalkSameNpc_DoesNotFire`** (new — boundary)
*Given* a draft with `AcceptStep` (Target.NpcId=1014875) followed by `TalkStep` (Target.NpcId=1014875),
*when* `Validate(draft)` is called,
*then* result has no W4 (the implementation guards `steps[i].Raw is not TalkStep` so non-talk-to-talk pairs are exempt).
**Why this matters:** documents the existing implementation behaviour — W4 is "two consecutive *TalkStep*s", not "two consecutive *anything-with-Target*s".

### E3 — Theory test over parser error kinds

**T18 — `E3_PredicateParseError_ReportsSingleError`** (new, `[Theory]`)
*Given* the baseline plus a `TalkStep` with one of the following `Expect = PredicateExpect(predicate)` strings:

| `predicate` | What's wrong | `ParseError.Code` | Maps to |
|---|---|---|---|
| `"questSequence("` | unbalanced paren | `parse-error` | E3 |
| `"questSequence(2054"` | missing `)` | `parse-error` | E3 |
| `"questSequence(2054) >="` | missing RHS literal | `parse-error` | E3 |
| `"questSequence()"` | arity mismatch (expects 1, got 0) | `arity-mismatch` | E3 |
| `"questSequence(2054, 99)"` | arity mismatch (expects 1, got 2) | `arity-mismatch` | E3 |
| `"questSequence(\"x\")"` | type mismatch (expects Int, got String) | `type-mismatch` | E3 |
| `"playerZone() == \"x\""` | type mismatch (Int == String) | `type-mismatch` | E3 |
| `"default and isQuestComplete(2054)"` | `default` not composable | `default-not-composable` | E3 |
| `"${param}"` | parameter ref outside fragment scope (drafts have no scope, so `Parser.ResolveParameterRef` hits the line-254 `parse-error` branch before the line-259 `unknown-parameter` branch — either code maps to E3, but in practice this row produces `parse-error`) | `parse-error` | E3 |

*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E3")`. Test must also assert the error message contains the original predicate string (so authors can locate the bad predicate in the UI).

**Strictness:** **exactly one** error. If the parser emits multiple `ParseError` entries for one predicate (e.g., a single typo can cascade), the validator must aggregate or the test must be marked tolerant. Recommended Builder approach: surface every `ParseError` separately (one validator error per parser error); the Theory rows above are crafted to produce exactly one parser error each. If a row produces >1 error in practice, change the row, not the assertion.

### SkipIf coverage

**T19 — `E5_UnknownFunctionOnSkipIf_AlsoReported`** (new — cross-check that the SkipIf predicate path is reached; the E5 code is incidental, the point of this test is that *any* predicate error from SkipIf produces a validator error)
*Given* the baseline plus a `TalkStep` whose `SkipIf = PredicateExpect("questSequnece(2054)")` (typo, so E5 from SkipIf) and `Expect = PredicateExpect("questSequence(2054) >= 1")` (valid),
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E5")` — the SkipIf path is reached.
**Why this matters:** the current implementation only inspects `Expect`. This test enforces the fix. Parallel coverage for parse-errors on SkipIf is provided implicitly because T18's rows would behave identically on the SkipIf path (the validator routes both through `ValidatePredicate`); a future test could add an explicit E3-on-SkipIf row if needed.

**T20 — `E5_DidYouMeanHintPresent`** (new, complements T4)
*Given* the baseline plus a TalkStep with `Expect = PredicateExpect("questSequnece(2054)")`,
*when* `Validate(draft)` is called,
*then* `AssertSingleError(result, "E5")` and the error message contains the substring `"questSequence"` (the suggestion from `FunctionRegistry.SuggestSimilar`).

### W6 — Theory test over name variants

**T21 — `W6_EmptyOrPlaceholderName_ReportsSingleWarning`** (new, `[Theory]`)

| `QuestName` | W6 fires? |
|---|---|
| `null` | yes |
| `""` | yes |
| `"   "` | yes |
| `"\t"` | yes |
| `"TODO"` | yes |
| `"todo"` | yes |
| `"Todo"` | yes |
| `"  TODO "` | yes |

Each row builds a draft from `ValidBaseline()` then sets `draft.QuestName = row`. *Then* `AssertSingleWarning(result, "W6")`.

**T22 — `W6_RealName_DoesNotFire`** (new, `[Theory]`, negative)

| `QuestName` | W6 fires? |
|---|---|
| `"Close to Home"` | no |
| `"Todo: figure out name"` | no (substring of "TODO" allowed) |
| `"A"` | no (1-char real name) |
| `"TODOs aplenty"` | no (substring, not whole-string match) |

*Then* `AssertClean(result)`.

### Multi-error fixture (validates the multi-error helper)

**T23 — `MultipleRules_CoexistCorrectly`** (new)
*Given* a draft with: no accept step (E4), one TalkStep with `Target.NpcId = 0` (E6), and `QuestName = null` (W6),
*when* `Validate(draft)` is called,
*then* `AssertErrorCodes(result, "E4", "E6")` — and verify warnings list contains exactly W6.

**Strictness:** confirms rule-suppression rules are absent where unintended. If E4 silently suppressed E6 (it shouldn't), this would catch it.

---

## 6. Test count summary

| Category | Count |
|---|---|
| Strengthened existing facts (T1, T2, T3, T4, T5, T6) | 6 |
| New positive Facts (T8, T9, T11, T13, T15, T19, T20, T23) | 8 |
| New negative Facts (T10, T12, T14, T16, T17) | 5 |
| Theory rows in T18 (E3 axis) | 9 |
| Theory rows in T21 (W6 positive) | 8 |
| Theory rows in T22 (W6 negative) | 4 |
| **Removed:** T7 (tautological E1 test) | -1 |
| **Total xUnit test cases** | **~40 cases (19 [Fact] + 21 Theory inline rows)** |

---

## 7. Known drift recorded by this work

These items are intentionally outside the scope of strengthening but noted for the Reviewer:

1. **W2 spec drift.** `PHASE_9_PLAN.md §3.5` says W2 fires when `Confidence == Low`; the current implementation fires on `InferredFrom.None`. This plan preserves the *implementation* behaviour because production callers rely on it (Confidence is not present on `DraftStep`; it's on `InferenceResult`, which is consumed before the step is appended). PHASE_9_PLAN should be amended in a separate doc-only change. The W2 tests (T11, T12) assert the implementation behaviour.
2. **E6 zone check.** `PHASE_9_PLAN.md §3.5` says E6 fires on `TalkStep.Target.NpcId == 0 OR Target.Zone < 0`. The implementation only checks `NpcId == 0`. This plan does not add zone-checking (out of scope). The Reviewer should file a follow-up if the zone branch is needed; `NpcLocation.Zone` is `int`, so `< 0` is a real possible value but unlikely in practice (Lumina zones are always positive).
3. **SkipIf coverage.** Adding `ExtractPredicates(step.Raw.SkipIf)` is a behavioural change (more predicates evaluated). T19 documents the new contract. Existing tests pass because none of them set `SkipIf`.
4. **`AllExpect` / `AnyExpect` coverage.** The new `ExtractPredicates` walks composite Expect arrays. There are no tests in this plan that exercise composite forms (they have no representative use in current quest drafts); a follow-up could add them when authoring tooling for composite predicates lands.

---

## 8. Implementation order

**Phase A — Helpers (no production change yet)** ~30 min
1. Add `DraftValidatorAssertions.cs` (Task 2). Build green; tests still pass.
2. Add `DraftValidatorTestData.cs` (Task 3). Build green; tests still pass.
3. Add `InternalsVisibleTo` and `QuestDraft.CreateForTest` (Task 1). Build green; existing `QuestDraftTests` still pass.

**Phase B — RED tests** ~1 hour
4. Rewrite `DraftValidatorTests.cs` per §5. T1–T6 should pass immediately (strengthened versions of working rules). T8, T9, T11, T13, T15, T17, T19, T20, T23 fail (E1 positive, E6 untested, W2/W3/W4 untested, SkipIf untested, multi-error). For T18 (E3 Theory): eight of the nine rows currently fail because the validator emits *no error* for parse / arity / type / `default-not-composable` / `${param}` errors (the legacy substring check only flags unknown function names); the unknown-function row is not present in T18 (it lives in T4/T20), and the parse-error rows currently produce empty error lists, failing `AssertSingleError(result, "E3")`. T21 / T22 (W6) fail (W6 doesn't exist).

**Phase C — Builder GREEN** ~1 hour
5. Wire E3 to `PredicateParser` + `PredicateChecker` (Task 4). T18 + T19 + T20 + T4 should now pass.
6. Add W6 (Task 5). T21 + T22 + T1 (baseline must remain clean) should pass.
7. Update `BACKLOG.md` per Task 7 (remove the stale §5.5 stub note and the §12 W6 row).
8. Run `dotnet test QuestForge.Engine.Tests`. All tests green.

**Phase D — Reviewer** ~15 min
9. Verify the strict helpers caught nothing unintended (e.g., a new rule fires silently in T1 because of a fixture quirk).
10. Confirm message strings include enough detail (predicate string, step ID, suggestion) for the authoring UI to render usefully.

---

## 9. Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidator"` passes with the expected test count (~40 cases per §6).
2. `DraftValidator.cs` no longer contains `KnownFunctions`, `HasKnownFunction`, `ExtractFunctionName`, `FindClosest`, `CommonPrefixLength`, or the `TODO(E3)` comment.
3. `DraftValidator.cs` references `QuestForge.Predicates.PredicateParser` and `QuestForge.Predicates.PredicateChecker`.
4. `docs/BACKLOG.md §5.5` is updated to mark E3-wiring as done (stale stub note removed; new one-line "done" entry added) as part of Task 7 in the same PR.
5. `docs/BACKLOG.md §12` is updated to remove the W6 row entirely (W6 is now implemented and tested) as part of Task 7 in the same PR.
6. No test uses raw `Assert.Contains(errors, e => e.Code == "X")` for a single-rule contract. (`Assert.Contains` may still be used inside `AssertErrorCodes` internals, but not in test bodies.)
7. The existing `Validation_FailsForDuplicateStepIds` tautological test is deleted; T8 (`E1_DuplicateStepId_ReportsSingleError`) is its replacement.
8. `QuestDraft.CreateForTest` is `internal static` and only callable from `QuestForge.Engine.Tests`.

---

## 10. Exclusions

Out of scope for this work item — do not attempt:

- E6 zone-check branch (`Target.Zone < 0`) — file a follow-up.
- PHASE_9_PLAN amendments for W2 spec drift — file a follow-up doc PR.
- Composite Expect (`AllExpect` / `AnyExpect`) test coverage — defer until composite predicates appear in real drafts.
- `DraftValidator` refactor (e.g., rule plugin interface) — keep the existing flat method body.
- Slash-notation error codes (`"draft/E1"`) — separate API change.
- Any change to `QuestForge.Predicates` — read-only dependency in this work.
- Any change to `QuestForge.Plugin` or `QuestForge.Adapters.Dalamud` — Mac cannot build them.
- Tests for any component other than `DraftValidator`.

---

## ✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §5.

- Happy paths: T1, T6, T10, T12, T14, T17 (clean baselines / boundary negatives)
- Edge cases: T16 (non-consecutive talks), T20 (did-you-mean hint), T22 (substring quest names), T23 (multi-error coexistence)
- Error cases: T2 (E2), T3 (E4), T4 (E5), T8 (E1), T9 (E6), T18 (nine E3 Theory rows), T19 (E5-on-SkipIf), T23 (multi-error fixture)
- Warning cases: T5 (W1), T11 (W2), T13 (W3), T15 (W4), T21 (eight W6 Theory rows); T6 covers the W5 negative
- Expected total: ~40 xUnit test cases (19 `[Fact]` + 21 `[Theory]` inline rows) in `QuestForge.Engine.Tests/Authoring/DraftValidatorTests.cs`, plus 2 new helper files (`DraftValidatorAssertions.cs`, `DraftValidatorTestData.cs`).
