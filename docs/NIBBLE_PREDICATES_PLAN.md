# Nibble Quest-Variable Predicates — Implementation Plan (`questVariableLow` / `questVariableHigh`)

**Status:** ready for test creation
**Input docs:** docs/QUEST_VARIABLE_PREDICATE_PLAN.md (the parent `questVariable` predicate this mirrors end-to-end), docs/COMBAT_STEP_PLAN.md §5 (why combat completion gates on quest-work nibbles), docs/QUEST_VARIABLES_TRACE_PLAN.md (the per-tick `GetQuestVariables` read these predicates reuse), docs/SCHEMA.md §7.2 (predicate state-function table), docs/TRACE_FORMAT.md (observation/replay determinism)
**Output:** two new predicate functions
- `questVariableLow(questId, index) -> Int` — the **low nibble** (`byte & 0x0F`, 0–15) of quest-variable byte `index`.
- `questVariableHigh(questId, index) -> Int` — the **high nibble** (`byte >> 4`, 0–15).

Both usable in `expect`/`skipIf` predicates, e.g. `questVariableLow(65847, 0) >= 3`. `qf-validate` rejects an out-of-range literal `index` on data PRs (`predicate/quest-variable-index-out-of-range`); the engine evaluates them against `IQuestState.GetQuestVariables` at runtime and extracts the nibble. **CI behaviour change:** a quest file with `questVariableLow(q, 6)` (or any literal index ∉ 0–5) makes a `questforge-data` PR go red, exactly as `questVariable(q, 6)` does today.
**Branch:** `feat/nibble-predicates` (questforge); a paired branch in questforge-tools.
**Spans two repos for code:** **questforge-tools** (registry + checker; parser, lexer, and the generic `PredicateValidator` are unchanged) and **questforge** (engine evaluator arm). The two repos share only the function **contract** (name + arity + `Int`-return semantics + 0–5 literal-index rule); each has its own implementation (tools = static check/validate; engine = runtime evaluate). Both must agree exactly.

**Why this exists.** FFXIV packs per-objective progress two-per-byte into quest variables (a verified game fact: quest 65847's three "defeat 3×" objectives complete at V0-low==3, V1-low==3, V1-high==3). The existing `questVariable(questId, index)` returns the whole byte, which is unusable for nibble-packed objectives (and the byte resets to 0 on full sequence completion). These two functions are clean-room siblings of `questVariable`: identical registration, arity, type, validation, and runtime read path — the *only* new logic is `& 0x0F` / `>> 4` on the resolved byte. No other plugin's source is referenced; nibble-packing is the game's own QuestWork storage layout.

---

## Dependency graph

```
1. questforge-tools (predicate language — source of truth for the signatures)
   └── QuestForge.Predicates/FunctionRegistry.cs   ← add TWO signatures (Low, High)
   └── QuestForge.Predicates/PredicateChecker.cs    ← widen the index-range gate to the 3 names
       (Parser.cs, Lexer.cs, ParseTypes.cs, PredicateAst.cs — NO change)
       (QuestForge.Tools.Validator/PredicateValidator.cs — NO change; generic, picks up the registry)
       └── consumed by ↓

2. questforge (engine)
   └── QuestForge.Engine/Predicates/PredicateEvaluator.cs  ← add TWO switch arms + nibble extraction
       (reuses IQuestState.GetQuestVariables — ALREADY implemented, recorded, and replayed)
```

**Build order:** questforge-tools registry+checker first (the signatures are the contract), then the questforge engine arms. The engine arms depend only on the parser producing `FunctionCall("questVariableLow"/"questVariableHigh", [Int, Int])` nodes (which it does for any identifier-call, with zero parser edits) and on `IQuestState.GetQuestVariables`, both of which exist. The two repos can be developed in either order (the engine arms match on the bare name strings, not the registry signature), but the checker must land for the validation criteria to hold.

---

## Function contract (the cross-repo agreement)

| | `questVariableLow` | `questVariableHigh` |
|---|---|---|
| Arity | `Fixed(2)` — `(questId, index)` | `Fixed(2)` — `(questId, index)` |
| Parameter types | `[Int, Int]` | `[Int, Int]` |
| Return type | `Int` (0–15) | `Int` (0–15) |
| Value of byte `b` | `b & 0x0F` | `b >> 4` (i.e. `(b & 0xF0) >> 4`) |
| Literal-index range | 0–5 (checker error otherwise) | 0–5 (checker error otherwise) |
| Composition | `questVariableLow(q,i) >= N` (relational; reuses the Int comparison grammar) | same |

The return is `Int` (boxed `long` at runtime) so it composes with `>=`, `>`, `<=`, `<`, `==`, `!=` exactly like `questSequence`/`questVariable`. A nibble is mathematically in 0–15; the checker does NOT range-check the *return* value (it is a runtime quantity), only the literal *index* argument.

---

## Architectural decisions (read before coding)

### D1 — Registry: two `Fixed(2) [Int,Int] -> Int` entries, siblings of `questVariable`

`questVariable` is `new("questVariable", new Fixed(2), [Int, Int], Int)` (FunctionRegistry.cs:38). The two nibble functions are byte-for-byte identical signatures, differing only in name:

```csharp
// QuestForge.Predicates/FunctionRegistry.cs — add to s_functions, adjacent to questVariable
new("questVariableLow",  new Fixed(2), [Int, Int], Int),
new("questVariableHigh", new Fixed(2), [Int, Int], Int),
```

**Conventions confirmed by reading the file:**
- `Fixed(n)` = exactly `n` args (ParseTypes.cs:7). Both have exactly 2 → `Fixed(2)`.
- `ParameterTypes` is positional; the checker compares each arg's inferred type against `ParameterTypes[min(i, count-1)]` (PredicateChecker.cs:95). `[Int, Int]` types both the quest id and the index.
- `ReturnType = Int` makes `questVariableLow(q, i) >= N` a valid relational comparison (the checker only allows relational ops on `Int`, PredicateChecker.cs:135-138), and the engine widens the nibble to `long` so it compares against an `IntLiteral` (`long`) operand.
- **`FunctionRegistry.All.Count` goes 29 → 31** (it was raised to 29 when `questVariable` landed — see `FunctionRegistryTests.All_Contains29Functions`). The Tester updates that assertion to 31.

**Naming.** `questVariableLow` / `questVariableHigh` — camelCase, prefix `questVariable`, suffix the nibble half. This matches the established camelCase convention for every registry entry (`questSequence`, `questFlagAny`, `playerHasItem`, `isAttuned`, `questVariable`). `Low`/`High` rather than `Lo`/`Hi` for readability and to match the `& 0x0F` / `>> 4` mental model in the COMBAT plan.

**Rejected alternative — a single `questVariableNibble(q, i, half)` with a string/int `half` arg:** it adds a third argument the checker must validate (and a new enum-string vocabulary `"low"`/`"high"`), fragments the kill-count grammar, and is harder to read at a quest-authoring site than two named functions. Two siblings keep arity at 2 and reuse the existing `questVariable` validation arm verbatim.

**Rejected alternative — `questVariableHi` / `questVariableLo`:** abbreviations are inconsistent with the otherwise-spelled-out registry; rejected for naming hygiene.

### D2 — The parser needs NO change; parsing follows from the registry

`PredicateParser`/`Parser` build a `FunctionCall(name, args)` node for any identifier-followed-by-`(args)` regardless of name; arity/type/name validity is the checker's job. So `questVariableLow(65847, 0)` parses to `FunctionCall("questVariableLow", [IntLiteral(65847), IntLiteral(0)])` with **zero parser edits** — exactly as `questVariable` and `isAttuned` did.

**Load-bearing parser fact (constrains D3):** `ParseArg` accepts **only** number-literal, string-literal, position-literal, or parameter-ref. A nested function call is not a legal argument. Therefore the `index` argument is statically one of exactly two shapes: an `IntLiteral` (a constant) or a `ParameterRef` (a fragment parameter). There is no third "computed at parse time" shape — this is what makes the literal-range check in D3 well-defined, identical to the `questVariable` case.

### D3 — Checker validates a **literal** `index` is in 0–5 for all three quest-variable functions

The work-variable array is fixed length 6 (`V0`–`V5`). An out-of-range literal index is an authoring bug that can never succeed at runtime. The checker today has a per-function value check **scoped to `questVariable`** (PredicateChecker.cs:102-109):

```csharp
if (call.Name == "questVariable"
    && call.Args.Count == 2
    && call.Args[1] is PredicateAst.IntLiteral { Value: var idx }
    && (idx < 0 || idx > 5))
{
    errors.Add(new ParseError("quest-variable-index-out-of-range",
        $"questVariable index must be a literal in 0–5; got {idx}", 0));
}
```

**Change:** widen the gate from a single name to the set of three quest-variable functions, and make the message use the actual function name so diagnostics are precise. Concretely:

```csharp
private static readonly HashSet<string> s_questVariableFunctions =
    new(StringComparer.Ordinal) { "questVariable", "questVariableLow", "questVariableHigh" };

// inside CheckCall, after the existing arg-type loop, before `return sig.ReturnType;`
if (s_questVariableFunctions.Contains(call.Name)
    && call.Args.Count == 2
    && call.Args[1] is PredicateAst.IntLiteral { Value: var idx }
    && (idx < 0 || idx > 5))
{
    errors.Add(new ParseError("quest-variable-index-out-of-range",
        $"{call.Name} index must be a literal in 0–5; got {idx}", 0));
}
```

**Error code stays `quest-variable-index-out-of-range`** (flat string, NOT the `structural/...` namespace). It is the *same rule* applied to a wider family of functions; reusing the code keeps `qf-validate` output and any CI annotation logic stable, and the message text disambiguates which function tripped it. (When surfaced by the validator the prefixed code is `predicate/quest-variable-index-out-of-range` — see D4.)

**Message text note for the Tester:** the message now begins with the function name (`"questVariableLow index must be a literal in 0–5; got 6"`). Tests assert on the **code**, not the message, so this is non-breaking; but a test that previously matched the literal `"questVariable index"` substring (if any) must be checked. Grep confirms existing checker tests assert only on `e.Code`, so no message assertion changes are required.

**Behaviour matrix (identical to `questVariable`, applied to each new name):**

| `index` arg shape | Checker behaviour |
|---|---|
| `IntLiteral` in 0–5 | no error |
| `IntLiteral` < 0 or > 5 (e.g. `6`) | one `quest-variable-index-out-of-range` |
| `ParameterRef` (Int fragment param) | **no range error** (value unknown at validation); engine guards at runtime (D6). A non-Int param ref still produces the existing `type-mismatch`. |
| wrong arg count | the existing `arity-mismatch` fires; range check skipped (avoids double-reporting) |
| `index` is `StringLiteral`/`PositionLiteral` | the existing `type-mismatch` fires; range check skipped (only runs on an `IntLiteral`) |

**The first arg (`questId`) is NOT range-checked** — quest-id existence is a Lumina concern deferred to Phase 2+, same as every other quest-id argument.

**Negative literal index (e.g. `-1`):** per the parent plan, the lexer emits `-` as an error token outside position-literal keys, so `questVariableLow(65847, -1)` does **not** parse (a parse error, not a checker error). Out-of-range coverage is via the `6`/`7` cases. The Tester should not write a `-1` checker case.

### D4 — `PredicateValidator` needs NO change; it is generic and picks up the registry

`PredicateValidator.Validate` (PredicateValidator.cs:12-27) collects predicate sites and runs `PredicateParser.Parse` + `PredicateChecker.Check` on each, translating every `ParseError` via `Translate` → `predicate/{err.Code}` (PredicateValidator.cs:136). It has **no per-function logic**. Therefore:
- An out-of-range `questVariableLow(q, 6)` in a quest file surfaces as `predicate/quest-variable-index-out-of-range` automatically, with no validator edits.
- A valid `questVariableLow(q, 0) >= 3` passes with no errors automatically, because the registry now knows the function.

**No change to** `PredicateValidator.cs`, `StructuralValidator`, `QuestLoader`, the CLI, or the GitHub Actions workflow. The CI behaviour change (red on out-of-range index) is delivered entirely by the registry + checker change flowing through the unchanged validator.

### D5 — Engine evaluation reuses the SAME `GetQuestVariables` read as `questVariable`, then masks/shifts

The evaluator adds two switch arms in `EvaluateFunction` (PredicateEvaluator.cs:100-115) and one private helper that takes the nibble selector. The existing `questVariable` arm and `EvaluateQuestVariable` helper (PredicateEvaluator.cs:103, 118-124) are the template:

```csharp
// EvaluateFunction switch — existing:
"questVariable"     => await EvaluateQuestVariable((long)args[0], (long)args[1], Nibble.Whole, ct),
// NEW:
"questVariableLow"  => await EvaluateQuestVariable((long)args[0], (long)args[1], Nibble.Low,  ct),
"questVariableHigh" => await EvaluateQuestVariable((long)args[0], (long)args[1], Nibble.High, ct),
```

```csharp
private enum Nibble { Whole, Low, High }

private async Task<object> EvaluateQuestVariable(long questId, long index, Nibble nibble, CancellationToken ct)
{
    var vars = (await _questState.GetQuestVariables(new QuestId((uint)questId), ct)).ValueOrThrow;
    // Runtime index safety (the checker catches literal out-of-range; this guards
    // parameterised indices and any path that bypassed validation). Out-of-bounds → 0.
    if (index < 0 || index >= vars.Count)
        return 0L;
    var b = vars[(int)index];
    return (long)(nibble switch
    {
        Nibble.Low   => (byte)(b & 0x0F),
        Nibble.High  => (byte)(b >> 4),
        _            => b
    });
}
```

**Refactor vs. duplicate:** extend the existing `EvaluateQuestVariable` with a `Nibble` selector (the chosen design above) rather than adding two parallel helper methods. The read, the bounds guard, and the not-accepted/failure semantics (D6) are identical across the three functions; only the final mask/shift differs. This keeps the three switch arms one-liners and the semantics in one place. (If the Builder prefers, two tiny extra helpers that call a shared core are acceptable — the contract is what is tested, not the private shape. The single-helper-with-selector form is preferred.)

**Return type is `long`** (boxed `object`, like every Int-returning arm) so `EvaluateComparison`'s `leftVal is long && rightVal is long` path fires against an `IntLiteral` RHS. `(byte)(b & 0x0F)` and `(byte)(b >> 4)` are both in 0–15 before the widen.

### D6 — Runtime semantics: not-accepted → 0, failure propagates, out-of-bounds → 0 (inherited from `questVariable`)

Identical to QUEST_VARIABLE_PREDICATE_PLAN §D5, because the read path is shared:
- **Not-accepted quest → 0.** `IQuestState.GetQuestVariables` returns a length-6 all-zero list for a not-accepted quest (FakeQuestState; the real `DalamudQuestState` matches). The low nibble of 0 is 0 and the high nibble of 0 is 0, so `questVariableLow(q, i)` and `questVariableHigh(q, i)` both evaluate to 0 for a not-accepted `q` with no special-casing.
- **Adapter failure propagates (does NOT fail-open to 0).** The arm uses `.ValueOrThrow` exactly like `questVariable`/`questSequence`. A `Result.Fail` from `GetQuestVariables` throws (`InvalidOperationException`), surfaced like any other predicate adapter failure. Silently returning 0 would let a kill-count gate evaluate `false` forever with no diagnostic.
- **Out-of-bounds index → 0** (the runtime guard above). Defence-in-depth for parameterised indices and any predicate that bypassed `qf-validate`.

### D7 — Fixture-starvation analysis: NO new risk (same `(method, arg)` key as `questVariable`)

The nibble predicates call `GetQuestVariables(q)` with the **predicate's** quest id — the identical `(method, arg)` read pattern `questVariable` and the per-tick engine read already produce (keyed by quest id, serialized `QuestId` → `{"value":<id>}`). For the only shipped use (`q == the active quest's id`, the combat-completion gate), this is the **same key** the per-tick `GetQuestVariables(activeQuestId)` read already records and replays. **No new read pattern. No new starvation. No fixture re-record required by this PR.** The mask/shift is a pure CPU transform on the already-observed bytes — it does not touch the adapter channel.

The only way to introduce starvation is to author a predicate against a *different* quest id (`questVariableLow(otherQuestId, …)`), which this PR does not ship and which is the owning data PR's responsibility to record for — identical to the parent plan's case 2.

### D8 — Determinism / replay safety (no recording/replay code)

`GetQuestVariables` is already recorded by `RecordingQuestState` and replayed by `ReplayQuestState`, with the value serialized as a JSON byte array round-tripped via `ObservationMaterializer`. The nibble predicates reuse this exact path and the same `(method, arg)` key (D7), so they are fully replay-safe: under replay the comparison resolves deterministically from recorded bytes, and the `& 0x0F` / `>> 4` is a deterministic transform. **No edits** to `RecordingQuestState`, `ReplayQuestState`, `ObservationMaterializer`, or `FakeQuestState`.

### D9 — SCHEMA.md documentation

`questVariable` is **not** listed in the SCHEMA.md §7.2 state-function table today (the parent PR did not add it). To keep the docs honest and the predicate set discoverable, this PR **adds three rows** to §7.2 (the missing `questVariable` plus the two new nibble functions) — a docs-only, non-load-bearing change:

```
| `questVariable(questId, index)`     | int | Whole quest-work byte V0–V5 (index 0–5) |
| `questVariableLow(questId, index)`  | int | Low nibble (byte & 0x0F, 0–15) of V[index] |
| `questVariableHigh(questId, index)` | int | High nibble (byte >> 4, 0–15) of V[index] |
```

This is the only docs change and is optional for the GREEN gate (no test asserts on SCHEMA.md). Include it in the questforge-tools PR or the questforge PR — it lives in the `questforge` repo's `docs/`.

---

## Per-file change list

### questforge-tools

| File | Change |
|---|---|
| `QuestForge.Predicates/FunctionRegistry.cs` | Add two entries: `new("questVariableLow", new Fixed(2), [Int, Int], Int)` and `new("questVariableHigh", new Fixed(2), [Int, Int], Int)`. Count goes 29 → 31. |
| `QuestForge.Predicates/PredicateChecker.cs` | Widen the existing `questVariable` literal-index range gate to the set `{questVariable, questVariableLow, questVariableHigh}` (D3); use `call.Name` in the message. |
| `QuestForge.Predicates/Parser.cs`, `Lexer.cs`, `ParseTypes.cs`, `PredicateAst.cs` | **No change** (D2). |
| `QuestForge.Tools.Validator/PredicateValidator.cs`, `StructuralValidator`, `QuestLoader`, CLI | **No change** (D4 — generic). |
| `QuestForge.Predicates.Tests/FunctionRegistryTests.cs` | (Tester) update count 29→31; add `Low`/`High` signature + suggest-similar tests mirroring `QuestVariable_Signature_IsCorrect`. |
| `QuestForge.Predicates.Tests/PredicateCheckerTests.cs` | (Tester) add the checker criteria (CK-L/CK-H groups). |
| `QuestForge.Predicates.Tests/ParserTests.cs` | (Tester) add parse-acceptance tests mirroring the `questVariable` parse tests. |
| `QuestForge.Tools.Validator.Tests/PredicateValidatorTests.cs` | (Tester) add one end-to-end validator test proving an out-of-range `questVariableLow` index surfaces as `predicate/quest-variable-index-out-of-range`, and one happy-path test. |

### questforge

| File | Change |
|---|---|
| `QuestForge.Engine/Predicates/PredicateEvaluator.cs` | Add the `"questVariableLow"`/`"questVariableHigh"` arms in `EvaluateFunction` and extend `EvaluateQuestVariable` with the `Nibble` selector + mask/shift (D5/D6). |
| `docs/SCHEMA.md` | (Optional, D9) add three rows to §7.2. |
| `QuestForge.Engine.Tests/Predicates/...` | (Tester) add nibble-evaluation criteria (EV-N group) against `FakeQuestState.SetQuestVariables`. May extend the existing `QuestVariablePredicateEvaluatorTests.cs` or add a sibling file. |

**No change** to: `IQuestState`, `FakeQuestState`, `RecordingQuestState`, `ReplayQuestState`, `ObservationMaterializer`, `QuestEngine` (the per-tick read already exists), the trace format, any schema type, `TraceMode`, the Dalamud adapter (the real `GetQuestVariables` is already implemented and in-game-verified).

---

## Validation rule table (the one rule this PR extends in `qf-validate`)

| Rule | Validator code | Severity | Fires when | Suppressed when |
|---|---|---|---|---|
| `questVariableLow`/`questVariableHigh` literal index must be in 0–5 | `predicate/quest-variable-index-out-of-range` | Error | `questVariableLow(_, k)` or `questVariableHigh(_, k)` where `k` is an `IntLiteral` and `k < 0 \|\| k > 5` | arity is wrong (`predicate/arity-mismatch` fires); the index arg is not an `IntLiteral` (`predicate/type-mismatch` for a String/Position arg; a `ParameterRef` index is allowed) |

(The raw checker code is `quest-variable-index-out-of-range`; the validator prefixes it to `predicate/quest-variable-index-out-of-range` — D4.)

Reusing existing codes (no new code added; the one above is shared with `questVariable`):
- Typo `questVariableLwo` → existing `unknown-function` (Levenshtein suggests `questVariableLow`).
- Wrong arity (`questVariableLow(65847)`, `questVariableLow(1,2,3)`) → existing `arity-mismatch`.
- Wrong arg type (`questVariableLow("65847", 0)`, `questVariableLow(65847, "0")`) → existing `type-mismatch`.
- Bare Int misuse (`questVariableLow(65847, 0)` used bare) → existing `type-mismatch` ("bare predicate expression must be Bool; got Int").

---

## Given-When-Then specifications

These mirror the `questVariable` GWT specs (RG/PA/CK/EV groups), duplicated for `Low` and `High` and adding the nibble-extraction semantics. Where a `Low` and `High` case is symmetric, write both.

### Registry (questforge-tools — `FunctionRegistryTests`)

- **RG-N1** — `FunctionRegistry.TryGet("questVariableLow", out var sig)` → `found == true`, `sig.Name == "questVariableLow"`, `sig.Arity` is `Fixed` with `Count == 2`, `sig.ParameterTypes == [Int, Int]`, `sig.ReturnType == Int`.
- **RG-N2** — same as RG-N1 for `"questVariableHigh"`.
- **RG-N3** — `FunctionRegistry.All.Count == 31` (was 29 before these two entries).
- **RG-N4** — `SuggestSimilar("questVariableLwo")` (transposed `wo`) contains `"questVariableLow"` (Levenshtein distance 2 ≤ default maxDistance 2).
- **RG-N5** — `SuggestSimilar("questVariableHigh")` (exact) contains `"questVariableHigh"`; and (sanity) `SuggestSimilar("questVariable")` contains `"questVariable"`, `"questVariableLow"`, and `"questVariableHigh"` (all within distance 2 of `"questVariable"`: `+"Low"` is distance 3, `+"High"` is distance 4 — **so they are NOT suggested for the bare `questVariable` typo**; assert only that `questVariable` itself is present, OR drop the cross-suggestion clause). *Tester note: verify the Levenshtein distances before asserting cross-suggestions; `Low`/`High` suffixes exceed distance 2 from `questVariable`, so do not assert they are suggested for a `questVariable` typo. Keep RG-N4/RG-N5 to direct typos of each full name.*

### Parser (questforge-tools — `ParserTests`; no parser change, pins that parsing follows from the registry)

- **PA-N1** — `"questVariableLow(65847, 0)"` parses: `IsSuccess == true`, `Ast` is `FunctionCall("questVariableLow", [IntLiteral(65847), IntLiteral(0)])`.
- **PA-N2** — `"questVariableHigh(65847, 1) >= 3"` parses to `Comparison(FunctionCall("questVariableHigh", [IntLiteral(65847), IntLiteral(1)]), GtEq, IntLiteral(3))`. (Confirms it composes with comparison like `questVariable`.)
- **PA-N3** — `"questVariableLow(65847, 6)"` (out-of-range index) → `IsSuccess == true`. The parser does not range-check; range is the checker's job. (Guards that the index error is semantic, not syntactic — load-bearing for EV-N7.)

### Checker (questforge-tools — `PredicateCheckerTests`)

For each new function (`Low`, `High`):

- **CK-L1 / CK-H1** (happy, in-range) — `questVariableLow(65847, 0) >= 3` / `questVariableHigh(65847, 1) >= 3` → no semantic errors.
- **CK-L2 / CK-H2** (boundary low, index 0) — `questVariableLow(65847, 0) >= 1` / `questVariableHigh(65847, 0) >= 1` → no error.
- **CK-L3 / CK-H3** (boundary high, index 5) — `questVariableLow(65847, 5) >= 1` / `questVariableHigh(65847, 5) >= 1` → no error.
- **CK-L4 / CK-H4** (out-of-range, index 6) — `questVariableLow(65847, 6) >= 1` / `questVariableHigh(65847, 6) >= 1` → exactly one `quest-variable-index-out-of-range`.
- **CK-L5 / CK-H5** (out-of-range, index 7) — `questVariableLow(65847, 7) == 0` / `questVariableHigh(65847, 7) == 0` → exactly one `quest-variable-index-out-of-range`.
- **CK-L6 / CK-H6** (wrong arity, too few) — `questVariableLow(65847) >= 1` / `questVariableHigh(65847) >= 1` → exactly one `arity-mismatch` (range check suppressed).
- **CK-L7 / CK-H7** (wrong arity, too many) — `questVariableLow(65847, 0, 1) >= 1` / `questVariableHigh(65847, 0, 1) >= 1` → exactly one `arity-mismatch`.
- **CK-L8 / CK-H8** (wrong type, quest id is string) — `questVariableLow("65847", 0) >= 1` / `questVariableHigh("65847", 0) >= 1` → exactly one `type-mismatch` (NOT a range error).
- **CK-L9 / CK-H9** (wrong type, index is string) — `questVariableLow(65847, "0") >= 1` / `questVariableHigh(65847, "0") >= 1` → exactly one `type-mismatch` (range check skipped — index not an `IntLiteral`).
- **CK-L10 / CK-H10** (unknown-function typo) — `questVariableLwo(65847, 0) >= 1` → exactly one `unknown-function` with a suggestion containing `questVariableLow`. (Symmetric `questVariableHihg` → `questVariableHigh`.)
- **CK-L11 / CK-H11** (bare Int misuse) — `questVariableLow(65847, 0)` / `questVariableHigh(65847, 0)` used bare → exactly one `type-mismatch` ("bare predicate expression must be Bool; got Int").
- **CK-L12 / CK-H12** (fragment param index, Int — allowed) — with a scope declaring `slot: Int`, `questVariableLow(65847, ${slot}) >= 1` / `questVariableHigh(65847, ${slot}) >= 1` → no semantic errors (parameterised index not range-checked).
- **CK-L13 / CK-H13** (fragment param index, wrong type) — with a scope declaring `slot: String`, `questVariableLow(65847, ${slot}) >= 1` / `questVariableHigh(65847, ${slot}) >= 1` → exactly one `type-mismatch` (existing param-type check; NOT a range error).
- **CK-MSG** (one message-text check, optional) — for `questVariableLow(65847, 6)`, the single error's `Message` contains `"questVariableLow"` (proves the gate uses `call.Name`, not a hardcoded `"questVariable"`). One assertion is enough; the rest assert on `Code`.

### Validator end-to-end (questforge-tools — `PredicateValidatorTests`)

- **VAL-N1** (happy) — a `QuestBuilder.Valid` quest with a step whose `Expect` is `questVariableLow(65847, 0) >= 3` → `Validate` returns **no** errors. (Proves the generic validator accepts the new function with no `PredicateValidator` change — D4.)
- **VAL-N2** (CI red) — a quest with a step whose `Expect` is `questVariableHigh(65847, 6) >= 1` → exactly one error, `Code == "predicate/quest-variable-index-out-of-range"`, `Location` is the step's expect location, `StepId` is the step id. (Pins the CI behaviour change: a `questforge-data` PR with an out-of-range nibble index goes red.)

### Engine evaluation (questforge — `QuestForge.Engine.Tests`, against `FakeQuestState`)

The canonical nibble fixtures use byte `0x13` (= decimal 19): **low nibble 3, high nibble 1**. Use `SetQuestVariables(q, 0x13, …)` and the quest 65847 example from the COMBAT plan.

- **EV-N1** (low nibble extraction) — Given `SetQuestVariables(65847, 0x13, 0,0,0,0,0)` (V0 == 0x13), When evaluating `questVariableLow(65847, 0) == 3`, Then `true`; and `questVariableLow(65847, 0) == 0x13` → `false` (proves the whole byte is NOT returned). (`0x13 & 0x0F == 3`.)
- **EV-N2** (high nibble extraction) — Same state, When evaluating `questVariableHigh(65847, 0) == 1`, Then `true`; and `questVariableHigh(65847, 0) == 3` → `false`. (`0x13 >> 4 == 1`.)
- **EV-N3** (nibble of 0 — both halves zero) — Given `SetQuestVariables(65847, 0, 0,0,0,0,0)`, When evaluating `questVariableLow(65847, 0) == 0` and `questVariableHigh(65847, 0) == 0`, Then both `true`. (Pins nibble-of-zero; also the not-accepted path via the adapter's all-zero list.)
- **EV-N4** (full-byte 0xFF — both nibbles 15) — Given `SetQuestVariables(65847, 0xFF, 0,0,0,0,0)`, When evaluating `questVariableLow(65847, 0) == 15` and `questVariableHigh(65847, 0) == 15`, Then both `true`. (Pins the upper bound and that high uses `>> 4`, not `& 0xF0`.)
- **EV-N5** (the verified quest-65847 multi-objective layout) — Given `SetQuestVariables(65847, 0x03, 0x33, 0,0,0,0)` (V0 low == 3; V1 low == 3, V1 high == 3 — the three "defeat 3×" objectives complete), When evaluating each of `questVariableLow(65847, 0) >= 3`, `questVariableLow(65847, 1) >= 3`, `questVariableHigh(65847, 1) >= 3`, Then all `true`; and with `SetQuestVariables(65847, 0x02, 0x33, …)` (V0 low == 2) `questVariableLow(65847, 0) >= 3` → `false`. (Directly encodes the COMBAT plan completion gate.)
- **EV-N6** (non-zero index is read) — Given `SetQuestVariables(65847, 0, 0, 0, 0x5A, 0, 0)` (V3 == 0x5A), When evaluating `questVariableLow(65847, 3) == 10` and `questVariableHigh(65847, 3) == 5`, Then both `true`. (`0x5A` → low `0xA`=10, high `5`. Proves index 3 is read, not index 0.)
- **EV-N7** (composes as Int across all operators) — Given `SetQuestVariables(65847, 0x35, …)` (low == 5), evaluating `questVariableLow(65847, 0)` against `>= 5` (true), `> 5` (false), `<= 5` (true), `== 5` (true), `!= 5` (false) returns the stated bool. (Proves the nibble boxes as `long` and hits the Int comparison path.)
- **EV-N8** (not-accepted quest → 0 for both halves) — Given **no** `SetQuestVariables` call for 65847, When evaluating `questVariableLow(65847, 0) == 0` and `questVariableHigh(65847, 0) == 0`, Then both `true`; `questVariableLow(65847, 0) >= 1` → `false`. (Pins D6 not-accepted.)
- **EV-N9** (only reads the specified quest id — no fan-out) — Using a `SpyQuestState` (mirror the one in `QuestVariablePredicateEvaluatorTests.cs`), with `SetQuestVariables(65847, 0x13, …)`, When evaluating `questVariableLow(65847, 0) >= 1`, Then every `GetQuestVariables` read carries `QuestId(65847)` only. (Pins D7 reuse-don't-multiply.)
- **EV-N10** (adapter failure propagates) — Using a `FailingQuestState` whose `GetQuestVariables` returns `Result.Fail`, When evaluating `questVariableLow(65847, 0) >= 1`, Then evaluation throws `InvalidOperationException` (does NOT return `false`/0). Symmetric for `questVariableHigh`. (Pins D6 failure propagation.)
- **EV-N11** (runtime out-of-bounds index → 0, no throw) — Given `SetQuestVariables(65847, 1,1,1,1,1,1)` and a parsed `questVariableLow(65847, 9) == 0` AST (parser accepts index 9 per PA-N3; the checker would reject it, but the engine is not checker-gated), When evaluated, Then `true` and no exception. Symmetric for `questVariableHigh`. (Pins the D6 defence-in-depth bounds guard.)

### Replay / recording delegation

No new recording/replay code (D8). No dedicated replay test in this PR — the `GetQuestVariables` round-trip is already pinned by the quest-variables-trace work, and the nibble transform is a pure CPU operation on the recorded bytes. EV-N9 indirectly guards that the predicate read uses the existing recorded `(method, arg)` pair.

---

## PR slicing (recommended)

**Two PRs, one per repo, tools-first.**

### PR-A — `questforge-tools` (predicate language: registry + checker + tests)
- Branch: `feat/nibble-predicates` in `questforge-tools`.
- Adds the two registry entries (D1), widens the checker gate (D3); `PredicateValidator` unchanged (D4).
- Tester deliverables: RG-N*, PA-N*, CK-L*/CK-H*/CK-MSG, VAL-N1/VAL-N2.
- **CI-gated test projects:** `QuestForge.Predicates.Tests` and `QuestForge.Tools.Validator.Tests` (both in the questforge-tools CI). `QuestForge.Tools.Trace.Tests` is unaffected.
- This PR alone delivers the **CI behaviour change** on `questforge-data` (out-of-range nibble index → red), because `qf-validate` consumes the registry+checker.

### PR-B — `questforge` (engine evaluator + tests + optional SCHEMA.md)
- Branch: `feat/nibble-predicates` in `questforge`.
- Adds the two `EvaluateFunction` arms and the `Nibble` selector (D5/D6); optional SCHEMA.md rows (D9).
- Tester deliverables: EV-N1 … EV-N11.
- **CI-gated test project:** `QuestForge.Engine.Tests`.

### Dependency / order
- **PR-A should merge first** (the signatures are the contract, and PR-A delivers the validation gate that protects any quest data using the new functions). PR-B's engine arms match on the bare name strings and do **not** import the registry, so PR-B *compiles and tests green independently* of PR-A — but merging A first keeps the authoritative contract (registry + validator) in place before the runtime path exists, preventing a window where a quest could use a function the validator rejects but the engine would have run. The two PRs are otherwise independent; either can be reviewed in parallel.
- **No `questforge-data` PR ships in either** — these are language + engine changes only. A data PR that *uses* `questVariableLow(activeQuest, …)` for the combat-completion gate is a follow-up, gated on the combat step landing (COMBAT_STEP_PLAN), and rides the already-recorded per-tick `GetQuestVariables` observation (D7) — no re-record.

---

## Build / test commands (net10 SDK required)

The default `dotnet` on PATH is 8.0.416; the net10 SDK is at `C:\Users\publi\.dotnet` (10.0.202). `questforge` pins it via `global.json` (10.0.202, rollForward latestMinor); **`questforge-tools` has no `global.json`**, so it would otherwise resolve 8.0 — prepend the net10 SDK explicitly for tools builds.

```bash
# questforge-tools (PR-A) — prepend net10 SDK since tools has no global.json
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Predicates.Tests
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Tools.Validator.Tests

# questforge (PR-B) — global.json pins net10; plain dotnet picks it up
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge/QuestForge.Engine.Tests
```

---

## Done criteria

1. `FunctionRegistry` exposes `questVariableLow` and `questVariableHigh` each as `Fixed(2) [Int,Int] -> Int`; `FunctionRegistry.All.Count == 31` (RG-N1–RG-N3 green).
2. `questVariableLow(65847, 0) >= 3` / `questVariableHigh(65847, 1) >= 3` parse to a `Comparison` over a `FunctionCall(name, [Int, Int])` with no parser edits (PA-N1, PA-N2 green); an out-of-range literal index still **parses** (PA-N3 green) — range is semantic, not syntactic.
3. `qf-validate` emits exactly one `predicate/quest-variable-index-out-of-range` for a literal index ∉ 0–5 on either function (CK-L4/5, CK-H4/5, VAL-N2) and no range error for valid indices, parameterised indices, or arity/type errors that pre-empt it (CK-L*/CK-H* happy + suppression cases, CK-L12/13, CK-H12/13). **CI behaviour change:** a `questforge-data` PR containing `questVariableLow(q, 6)` or `questVariableHigh(q, 6)` goes red with `predicate/quest-variable-index-out-of-range`; correcting the index to 0–5 turns it green.
4. The error message names the offending function (`"questVariableLow index must be a literal in 0–5; got 6"`) — the gate uses `call.Name`, not a hardcoded name (CK-MSG green).
5. The validator accepts a valid nibble predicate with **no `PredicateValidator` change** (VAL-N1 green — generic validator picks up the registry).
6. The engine evaluates `questVariableLow(q, i)` as `(GetQuestVariables(q)[i]) & 0x0F` and `questVariableHigh(q, i)` as `(GetQuestVariables(q)[i]) >> 4`, both widened to the Int comparison domain (EV-N1, EV-N2, EV-N4, EV-N6, EV-N7 green); the verified quest-65847 layout gates correctly (EV-N5 green); nibble-of-zero and a not-accepted quest yield 0 (EV-N3, EV-N8 green); an adapter failure propagates rather than returning 0 (EV-N10 green); a runtime out-of-bounds index returns 0 without throwing (EV-N11 green).
7. The engine introduces **no new `(method, arg)` read pattern**: evaluating either nibble function for the active quest reuses the per-tick `GetQuestVariables(activeQuestId)` observation key and never issues a `GetQuestVariables` read for any other quest id (EV-N9 green). **No fixture re-record is required by this PR** (D7).
8. No edits to `IQuestState`, the recording/replay proxies, the materializer, `FakeQuestState`, `QuestEngine`, the trace format, any schema type, or `PredicateValidator`/CLI; no `questforge-data` change ships with this PR.

---

## Exclusions

This PR does **NOT** include:

- **Quest-id existence validation** (Lumina game-data check on the first arg) — deferred to Phase 2+, same as every other quest-id argument.
- **Any quest data that uses the nibble predicates against a non-active quest** — a new observation pattern (D7 case 2); the owning `questforge-data` PR must be authored against a freshly-recorded trace if/when needed.
- **The combat step itself** (`ICombat`/`CombatStep`, combat controller, WrathCombo IPC, death routing). This PR only adds the nibble *completion-gate predicates*; combat is a separate PR of COMBAT_STEP_PLAN.
- **Removing or changing the existing `questVariable` (whole-byte) function** — it stays; the nibble functions are additive siblings.
- **Any change to the per-tick `GetQuestVariables` read in `QuestEngine`** — it already exists and is reused as-is.
- **Recording/replay/materializer code** — the nibble predicates ride the existing `IQuestState` observation channel unchanged (D8).
- **Fragment-scope plumbing in the engine evaluator** — `ParameterRef` evaluation is still unsupported at runtime; CK-L12/13 and CK-H12/13 validate the *checker* path for parameterised indices, but engine evaluation of a `${param}` index is not in this PR (the shipped combat-completion uses are literal indices).
- **A `byte`/`uint`-typed return** — the return is `Int` (boxed `long`), consistent with `questVariable`/`questSequence`, so it composes with the existing comparison grammar.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then.
- Happy paths: 14 scenarios (RG-N1, RG-N2, PA-N1, PA-N2, CK-L1, CK-H1, VAL-N1, EV-N1, EV-N2, EV-N5, EV-N6, EV-N7, EV-N9 + one of EV-N3)
- Edge cases: 18 scenarios (RG-N3, RG-N4, RG-N5, PA-N3, CK-L2/3, CK-H2/3, CK-L6/7, CK-H6/7, CK-L11, CK-H11, CK-L12, CK-H12, EV-N3, EV-N4, EV-N8, EV-N11)
- Error cases: 14 scenarios (CK-L4/5, CK-H4/5, CK-L8/9, CK-H8/9, CK-L10, CK-H10, CK-L13, CK-H13, CK-MSG, VAL-N2, EV-N10)
- Expected total: ~40 tests —
  - questforge-tools `QuestForge.Predicates.Tests`: ~5 in `FunctionRegistryTests` (RG-N*), ~3 in `ParserTests` (PA-N*), ~26 in `PredicateCheckerTests` (CK-L*/CK-H*/CK-MSG, paired Low/High);
  - questforge-tools `QuestForge.Tools.Validator.Tests`: ~2 in `PredicateValidatorTests` (VAL-N1, VAL-N2);
  - questforge `QuestForge.Engine.Tests`: ~11 nibble scenarios in `QuestVariablePredicateEvaluatorTests` or a sibling `NibbleQuestVariablePredicateEvaluatorTests` (EV-N1 … EV-N11, several as `[Theory]` rows).

CI-testable vs in-game-only:
- **All ~40 acceptance criteria are CI-testable** (registry/parser/checker/validator against the questforge-tools test projects; engine evaluation against `FakeQuestState`).
- **In-game-only (already done, NOT in this PR's test scope):** the real Dalamud `DalamudQuestState.GetQuestVariables` read — implemented and in-game-verified in the quest-variables work; the nibble predicates ride it unchanged.
