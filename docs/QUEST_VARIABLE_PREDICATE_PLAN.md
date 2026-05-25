# `questVariable` Predicate — Implementation Plan (Combat-Step PR 1)

**Status:** ready for test creation
**Input docs:** docs/COMBAT_STEP_PLAN.md §5 (why this is needed — combat completion gates on quest work variables V0–V5), docs/QUEST_VARIABLES_TRACE_PLAN.md (the per-tick `GetQuestVariables` read this predicate reuses), docs/SCHEMA.md (predicate language), docs/TRACE_FORMAT.md (observation/replay determinism)
**Output:** a new predicate function `questVariable(questId, index) -> Int` usable in `expect`/`skipIf` predicates (e.g. `questVariable(66104, 0) >= 3`). Validator (`qf-validate`) rejects an out-of-range literal `index` on data PRs; the engine evaluates it against `IQuestState.GetQuestVariables` at runtime. CI behaviour change: a quest file with `questVariable(q, 6)` (or any literal index ∉ 0–5) makes a `questforge-data` PR go red.
**Branch:** `feat/quest-variable-predicate` (questforge); a paired branch in questforge-tools.
**Spans two repos for code:** **questforge-tools** (registry + checker; parser is unchanged) and **questforge** (engine evaluator arm). `QuestForge.Engine` references `QuestForge.Predicates` via the existing cross-repo project reference.

---

## Dependency graph

```
1. questforge-tools (predicate language — source of truth for the signature)
   └── QuestForge.Predicates/FunctionRegistry.cs   ← add the questVariable signature
   └── QuestForge.Predicates/PredicateChecker.cs    ← add the index-range check
       (Parser.cs, Lexer.cs — NO change; parsing follows from the registry)
       └── consumed by ↓

2. questforge (engine)
   └── QuestForge.Engine/Predicates/PredicateEvaluator.cs  ← add the "questVariable" switch arm
       (reuses IQuestState.GetQuestVariables — ALREADY implemented, recorded, and replayed)
```

**Build order:** questforge-tools registry+checker first (the signature is the contract), then the questforge engine arm. The engine arm depends only on `FunctionRegistry`/parse producing a `FunctionCall("questVariable", [Int, Int])` node and on `IQuestState.GetQuestVariables`, both of which exist. The two repos can be developed in either order because the engine arm matches on the bare function-name string `"questVariable"` — it does not import the registry signature — but the checker must land for the validation criteria to hold.

---

## Architectural decisions (read before coding)

### D1 — Registry signature mirrors `questSequence`, with a two-arg `Fixed` arity

`questSequence` is `new("questSequence", new Fixed(1), [Int], Int)` (FunctionRegistry.cs:10). `questVariable` takes two `Int` args (the public quest id and the variable index) and returns `Int` (the byte value, widened to the predicate `Int` domain) so it composes with the existing comparison operators (`>=`, `==`, …) exactly like `questSequence`.

```csharp
// QuestForge.Predicates/FunctionRegistry.cs — add to s_functions
new("questVariable", new Fixed(2), [Int, Int], Int),
```

**Conventions confirmed by reading the file:**
- `Fixed(n)` = exactly `n` args (ParseTypes.cs:7; checker arity arm PredicateChecker.cs:68). `questVariable` has exactly 2 → `Fixed(2)`.
- `ParameterTypes` is positional; the checker compares each arg's inferred type against `ParameterTypes[min(i, count-1)]` (PredicateChecker.cs:95). `[Int, Int]` types both the quest id and the index.
- `ReturnType = Int` makes `questVariable(q, i) >= N` a valid relational comparison (the checker only allows relational ops on `Int`, PredicateChecker.cs:126-129), and the engine widens the byte to `long` so it compares against an `IntLiteral` (`long`) operand.

**Rejected alternative — a dedicated `questVariableAtLeast(q, i, n) -> Bool`:** it would hide the comparison operator, fragment the kill-count vocabulary (authors already write `questSequence(...) >= N`), and force three-arg validation. Returning `Int` and reusing the comparison grammar is the established pattern (`questSequence`, `questFlags`, `questFlagCount`, `gil`, `playerLevel`).

### D2 — The parser needs NO change; parsing follows from the registry

`PredicateParser`/`Parser` build a `FunctionCall(name, args)` node for any identifier-followed-by-`(args)` regardless of name (Parser.cs:183-201); arity/type/name validity is the checker's job, not the parser's. So `questVariable(66104, 0)` parses to `FunctionCall("questVariable", [IntLiteral(66104), IntLiteral(0)])` with **zero parser edits** — same as `isAttuned` landed (FunctionRegistryTests.cs `IsAttuned_Parser_AcceptsValidCall`).

**Load-bearing parser fact (constrains D3):** `ParseArg` accepts **only** number-literal, string-literal, position-literal, or parameter-ref (Parser.cs:402-464). A **nested function call is not a legal argument**. Therefore the `index` argument is statically one of exactly two shapes:
1. an `IntLiteral` (a constant, possibly a whole-number float coerced to `IntLiteral`), or
2. a `ParameterRef` (a fragment parameter, only inside fragment files with a scope).

There is no third "computed at parse time" shape. This is what makes the literal-range check in D3 well-defined.

### D3 — Checker validates a **literal** `index` is in 0–5; a non-literal index is allowed

The work-variable array is fixed length 6 (`V0`–`V5`); `FakeQuestState.SetQuestVariables` enforces exactly 6 bytes (FakeQuestState.cs:42-47) and the real adapter returns a length-6 list. An out-of-range literal index is an authoring bug that must be caught at validation (it can never succeed at runtime). The checker today has **no per-function argument-value validation** — it does generic arity + type checks only (PredicateChecker.cs `CheckCall`). This PR adds the **first** per-function value check, scoped narrowly to `questVariable`.

**New error code:** `quest-variable-index-out-of-range` (flat string, matching the existing checker convention — `unknown-function`, `arity-mismatch`, `type-mismatch`, `default-not-composable`, `unknown-parameter` — NOT the `structural/...` namespace, which belongs to the separate `StructuralValidator`).

**Message:** ``"questVariable index must be a literal in 0–5; got {n}"`` (where `{n}` is the literal value).

**Behaviour matrix:**

| `index` arg shape | Checker behaviour |
|---|---|
| `IntLiteral` in 0–5 | no error |
| `IntLiteral` < 0 or > 5 (e.g. `6`, `-1`) | one `quest-variable-index-out-of-range` |
| `ParameterRef` (Int fragment param) | **no range error** — value unknown at validation time; the engine guards it at runtime (D5). Type still checked: a non-Int param ref produces the existing `type-mismatch`. |
| wrong arg count | the existing `arity-mismatch` fires (range check is skipped when arity is wrong, to avoid double-reporting) |
| `index` is a `StringLiteral` / `PositionLiteral` | the existing `type-mismatch` fires (`expects Int`); range check is skipped (only runs on an `IntLiteral`) |

**Where it lives:** a dedicated arm in `CheckCall`, gated on `call.Name == "questVariable"`, run **after** the generic arity/type checks and **only when** arity is correct and the second arg is an `IntLiteral`. Concretely:

```csharp
// inside CheckCall, after the existing arg-type loop, before `return sig.ReturnType;`
if (call.Name == "questVariable"
    && call.Args.Count == 2
    && call.Args[1] is PredicateAst.IntLiteral { Value: var idx }
    && (idx < 0 || idx > 5))
{
    errors.Add(new ParseError("quest-variable-index-out-of-range",
        $"questVariable index must be a literal in 0–5; got {idx}", 0));
}
```

(`Column` is `0` consistent with every other semantic error this checker emits — the column-precise diagnostics come from the parser, not the checker.)

**The first arg (`questId`) is NOT range-checked.** Quest-id existence is a Lumina game-data concern deferred to Phase 2+ (PHASE_1_PLAN §"Deferred to Phase 2+"). The checker only knows it must be an `Int` (enforced by `[Int, Int]`).

**Rejected alternative — reject a non-literal (ParameterRef) index:** fragment authors legitimately parameterise an index (`questVariable(${questId}, ${slot})`); the engine bounds-guards at runtime (D5), so a parameterised index is safe. Forbidding it would make `questVariable` the only function that bans parameter refs in a typed position.

### D4 — Engine evaluation reuses the SAME `IQuestState.GetQuestVariables` path as the per-tick read

The evaluator adds one switch arm in `EvaluateFunction` (PredicateEvaluator.cs:100-114), mirroring the `questSequence` arm:

```csharp
"questSequence" => (long)(await _questState.GetQuestSequence(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
// NEW:
"questVariable" => await EvaluateQuestVariable((long)args[0], (long)args[1], ct),
```

```csharp
private async Task<object> EvaluateQuestVariable(long questId, long index, CancellationToken ct)
{
    var vars = (await _questState.GetQuestVariables(new QuestId((uint)questId), ct)).ValueOrThrow;
    // Runtime index safety (the checker catches literal out-of-range; this guards
    // parameterised indices and any path that bypassed validation). Out-of-bounds → 0,
    // mirroring the all-zero "not-accepted quest" semantics rather than throwing.
    if (index < 0 || index >= vars.Count)
        return 0L;
    return (long)vars[(int)index];
}
```

**Why a private helper rather than an inline expression:** the not-accepted, failure, and bounds semantics (D5) need three lines; the `questSequence` arm is a one-liner only because it has none of those. The helper keeps the switch readable and the semantics testable.

**Return type is `long`** (boxed `object`, like every other Int-returning arm) so `EvaluateComparison`'s `leftVal is long && rightVal is long` path (PredicateEvaluator.cs:129) fires against an `IntLiteral` RHS (which is also `long`, AST `IntLiteral(long Value)`).

### D5 — Runtime semantics: not-accepted → 0, failure propagates, out-of-bounds → 0

- **Not-accepted quest → returns 0.** `IQuestState.GetQuestVariables` already returns a length-6 **all-zero** list for a quest that has not been accepted (FakeQuestState.cs:171 `new byte[6]`; the real `DalamudQuestState` behaves identically per QUEST_VARIABLES_TRACE_PLAN §1.1). So `questVariable(q, i)` for a not-accepted `q` evaluates to `0` with no special-casing in the evaluator — the adapter's existing contract gives us this for free. Documented so the Tester asserts it.
- **Adapter failure propagates (does NOT fail-open to 0).** The arm uses `.ValueOrThrow` exactly like the `questSequence`/`questFlag` arms. A `Result.Failure` from `GetQuestVariables` throws (surfaced as the same exception type those siblings throw), which the engine's predicate-evaluation call site handles like any other predicate adapter failure. **Rationale:** silently returning 0 on a read failure would let a kill-count gate (`questVariable(q,0) >= 3`) evaluate `false` and stall the run forever with no diagnostic; throwing makes the failure visible and is consistent with every other `IQuestState` predicate arm. (Contrast the *engine's per-tick discarded read*, which is fail-open because it has no consumer — QUEST_VARIABLES_TRACE_PLAN §2.2. The predicate read DOES have a consumer, so it must not swallow failures.)
- **Out-of-bounds index → returns 0** (the runtime guard in D4). The checker should have rejected a literal out-of-range index; this guard is defence-in-depth for parameterised indices and for any predicate string that reached the engine without going through `qf-validate`. Choosing 0 (rather than throwing) keeps a mis-authored fragment from crashing a run, and 0 is the same value a never-set variable yields — a benign, inert result.

### D6 — Fixture-starvation analysis (THE load-bearing risk)

**Mechanism.** The replay scanners (`SegmentedObservationScanner.Next`, `ObservationScanner.Next`) match a read on the key `"{method}:{serialized-argument}"` and throw `ReplayObservationStarvationException` only when that exact `(method, arg)` pair **never appears anywhere** in the trace (SegmentedObservationScanner.cs:78-100). The serialized argument for `GetQuestVariables(quest)` is the `QuestId` record-struct → `{"value":<id>}` (Identifiers.cs:5; `RecordingQuestState.Record` serialises `argument: quest`, RecordingQuestState.cs:148; the scanner serialises the same `quest` via `ReplayJsonOptions.Default`). **So the pair is keyed by the quest id.**

**The engine already reads `GetQuestVariables(activeQuestId)` once per tick** (QuestEngine.cs:353, the discarded anticipatory read from the quest-variables-trace work) and that read is **already recorded** by `RecordingQuestState` and **already replayed** by `ReplayQuestState`. Every fixture recorded after that PR therefore already contains a `GetQuestVariables:{"value":<activeQuestId>}` observation.

**Two cases for a `questVariable(q, i)` predicate evaluation:**

1. **`q == the active quest's id` (the overwhelmingly common case — and the ONLY case the combat-completion gate uses).** Evaluating the predicate calls `GetQuestVariables(activeQuestId)` — the **identical** `(method, arg)` pair the per-tick read already produces. **No new read pattern. No new starvation. Zero new risk.** Under replay the scanner serves the recorded value; under recording the proxy dedups it against the per-tick read's value (same key) so it does not even add a trace line. This is the design intent recorded in QUEST_VARIABLES_TRACE_PLAN §2.1 ("the future `questVariable(...)` predicate will read variables via the same `IQuestState.GetQuestVariables` path the engine reads each tick, so the trace naturally contains exactly the values the predicate would observe").

2. **`q != the active quest's id` (a predicate that references a DIFFERENT quest — e.g. a prerequisite/chained quest's variable).** This **would** be a new `(method, arg)` pair `GetQuestVariables:{"value":<otherId>}` not produced by the per-tick read, so a replay of an old trace recorded before such a predicate existed would **starve** on it. This is the standard "engine read-pattern change → re-record" cascade (MEMORY: trace-emission-refactor; QUEST_VARIABLES_TRACE_PLAN §5).

**Specified mitigation / how to keep case 1 the only path this PR introduces:**

- **This PR ships NO quest data that uses `questVariable` against a non-active quest.** The combat-completion use (COMBAT_STEP_PLAN §5) is always `questVariable(<the quest being run>, i)` — case 1. There is no engine-side change that issues a `GetQuestVariables(otherId)` read on the common per-tick path.
- **The engine evaluator must NOT add a second distinct read.** It calls `GetQuestVariables(q)` with the **predicate's** quest id (which, in all shipped uses, equals the active quest id) and nothing else — no pre-fetch of "all referenced quests", no per-tick scan of predicate quest ids. Reuse, do not multiply, the read.
- **If/when a quest legitimately needs `questVariable(otherQuestId, …)`** (not in this PR), it is a new observation pattern and the owning data PR must be authored against a freshly-recorded trace that contains `GetQuestVariables:{"value":<otherId>}`. That is a data/re-record concern for that future PR, not a code change here. Flag it in that PR; do not pre-emptively widen the per-tick read to cover arbitrary quests (that would bloat every trace).

**Conclusion (the key question): there is NO new fixture-starvation risk from this PR.** All shipped `questVariable` uses target the active quest, whose `GetQuestVariables` observation is already recorded and replayed by the per-tick read. The predicate evaluation reuses that exact `(method, arg)` pair. The only way to introduce starvation is to author a predicate against a *different* quest id, which this PR does not do and which is the authoring PR's responsibility to record for.

### D7 — Determinism / replay safety

`GetQuestVariables` is recorded by `RecordingQuestState.GetQuestVariables` (RecordingQuestState.cs:145-150) and replayed by `ReplayQuestState.GetQuestVariables` (ReplayQuestState.cs:96-100), with the value serialised as a JSON **array** of bytes (not base64) and round-tripped via `ObservationMaterializer` (QUEST_VARIABLES_TRACE_PLAN §2.5/§3.3). Because `questVariable` evaluation reuses this exact path (D4) and the same `(method, arg)` key as the already-recorded per-tick read (D6 case 1), the predicate's read is **fully replay-safe**: under replay it consumes a recorded observation and the comparison resolves deterministically from recorded bytes. No new recording/replay code is required — `questVariable` rides the existing `IQuestState` observation channel. No `RecordingQuestState`/`ReplayQuestState`/`ObservationMaterializer`/`FakeQuestState` edits in this PR.

---

## Per-file change list

### questforge-tools

| File | Change |
|---|---|
| `QuestForge.Predicates/FunctionRegistry.cs` | Add one entry: `new("questVariable", new Fixed(2), [Int, Int], Int)` to `s_functions`. (Count goes 28 → 29 — see `FunctionRegistryTests.All_Contains28Functions`, which the Tester updates to 29.) |
| `QuestForge.Predicates/PredicateChecker.cs` | Add the `questVariable` literal-index range check in `CheckCall` (D3), emitting `quest-variable-index-out-of-range`. |
| `QuestForge.Predicates/Parser.cs`, `Lexer.cs`, `ParseTypes.cs`, `PredicateAst.cs` | **No change** (D2). |
| `QuestForge.Predicates.Tests/FunctionRegistryTests.cs` | (Tester) update the count assertion 28→29; add a `questVariable` signature test mirroring `IsAttuned_Signature_IsCorrect`. |
| `QuestForge.Predicates.Tests/PredicateCheckerTests.cs` | (Tester) add the checker criteria (CK group). |
| `QuestForge.Predicates.Tests/ParserTests.cs` | (Tester) add a parse-acceptance test mirroring `IsAttuned_Parser_AcceptsValidCall`. |

### questforge

| File | Change |
|---|---|
| `QuestForge.Engine/Predicates/PredicateEvaluator.cs` | Add the `"questVariable"` arm in `EvaluateFunction` + the private `EvaluateQuestVariable` helper (D4/D5). |
| `QuestForge.Engine.Tests/Predicates/...` | (Tester) add the engine-evaluation criteria (EV group) against `FakeQuestState.SetQuestVariables`. |

**No change** to: `IQuestState`, `FakeQuestState`, `RecordingQuestState`, `ReplayQuestState`, `ObservationMaterializer`, `QuestEngine` (the per-tick read already exists), the trace format, any schema type, `TraceMode`, the Dalamud adapter (the real `GetQuestVariables` is already implemented and in-game-verified).

---

## Validation rule table (the one rule this PR adds to `qf-validate`)

| Rule | Error code | Severity | Fires when | Suppressed when |
|---|---|---|---|---|
| `questVariable` literal index must be in 0–5 | `quest-variable-index-out-of-range` | Error | `questVariable(_, k)` where `k` is an `IntLiteral` and `k < 0 || k > 5` | arity is wrong (`arity-mismatch` fires instead); the index arg is not an `IntLiteral` (a `type-mismatch` fires for a String/Position arg; a `ParameterRef` index is allowed) |

Reusing existing codes (no new code beyond the one above):
- Unknown name `questVaraible` → existing `unknown-function` (Levenshtein suggests `questVariable`).
- Wrong arity (`questVariable(66104)`, `questVariable(1,2,3)`) → existing `arity-mismatch`.
- Wrong arg type (`questVariable("66104", 0)`, `questVariable(66104, "0")`) → existing `type-mismatch`.
- Bare/relational misuse inherited from the generic checker (e.g. `questVariable(66104, 0)` used as a bare predicate → existing `type-mismatch` "bare predicate expression must be Bool; got Int", same as `questSequence`).

---

## Given-When-Then specifications

### Registry (questforge-tools)

- **RG1** — Given `FunctionRegistry.TryGet("questVariable", out var sig)`, Then `found == true`, `sig.Name == "questVariable"`, `sig.Arity` is `Fixed` with `Count == 2`, `sig.ParameterTypes == [Int, Int]`, `sig.ReturnType == Int`.
- **RG2** — Given `FunctionRegistry.All`, Then `Count == 29` (was 28 before this entry).
- **RG3** — Given the typo `"questVaraible"`, When `SuggestSimilar("questVaraible")`, Then the result contains `"questVariable"` (Levenshtein distance 2 ≤ default maxDistance 2).

### Parser (questforge-tools — no parser change, this pins that parsing follows from the registry)

- **PA1** — Given `"questVariable(66104, 0)"`, When `PredicateParser.Parse`, Then `IsSuccess == true` and `Ast` is `FunctionCall("questVariable", [IntLiteral(66104), IntLiteral(0)])`.
- **PA2** — Given `"questVariable(66104, 0) >= 3"`, When parsed, Then `Ast` is a `Comparison(FunctionCall("questVariable", [IntLiteral(66104), IntLiteral(0)]), GtEq, IntLiteral(3))`. (Confirms it composes with comparison exactly like `questSequence`.)
- **PA3** — Given `"questVariable(66104, 6)"` (out-of-range index), When parsed, Then `IsSuccess == true` — the **parser** does not range-check; range is the checker's job (PA3 guards that the index error is a *semantic* error, not a parse error).

### Checker (questforge-tools)

- **CK1** (happy, in-range) — `questVariable(66104, 0) >= 3` → no semantic errors.
- **CK2** (boundary low) — `questVariable(66104, 0) >= 1` → no error (index 0 is valid).
- **CK3** (boundary high) — `questVariable(66104, 5) >= 1` → no error (index 5 is valid).
- **CK4** (out-of-range high) — `questVariable(66104, 6) >= 1` → exactly one error, code `quest-variable-index-out-of-range`.
- **CK5** (out-of-range, just over) — `questVariable(66104, 7) == 0` → exactly one `quest-variable-index-out-of-range`.
- **CK6** (negative index) — `questVariable(66104, -1) >= 1` → exactly one `quest-variable-index-out-of-range`. *(Tester note: the lexer/parser must produce an `IntLiteral(-1)` or this expression must reach the checker as one; if `-1` cannot lex as a negative int in this position, replace with a literal the lexer supports and assert the same code. Verify lexing of negative ints first — position literals special-case `-`, but top-level negative number literals may not lex; if not, CK6 is covered by CK4/CK5 and this case is dropped.)*
- **CK7** (wrong arity, too few) — `questVariable(66104) >= 1` → exactly one `arity-mismatch` (the range check is suppressed because arity is wrong).
- **CK8** (wrong arity, too many) — `questVariable(66104, 0, 1) >= 1` → exactly one `arity-mismatch`.
- **CK9** (wrong type, quest id) — `questVariable("66104", 0) >= 1` → exactly one `type-mismatch` (and NOT a range error).
- **CK10** (wrong type, index is string) — `questVariable(66104, "0") >= 1` → exactly one `type-mismatch` (range check skipped — index is not an `IntLiteral`).
- **CK11** (unknown function) — `questVaraible(66104, 0) >= 1` → exactly one `unknown-function` with a suggestion containing `questVariable`.
- **CK12** (bare Int misuse) — `questVariable(66104, 0)` used bare → exactly one `type-mismatch` ("bare predicate expression must be Bool; got Int"), matching `questSequence` bare behaviour.
- **CK13** (fragment parameter index — allowed) — with a fragment scope declaring `slot: Int`, `questVariable(66104, ${slot}) >= 1` → no semantic errors (parameterised index is not range-checked; D3).
- **CK14** (fragment parameter index, wrong type) — with a scope declaring `slot: String`, `questVariable(66104, ${slot}) >= 1` → exactly one `type-mismatch` (the existing param-type check), NOT a range error.

### Engine evaluation (questforge — against `FakeQuestState`)

- **EV1** (happy, reads the indexed byte) — Given `FakeQuestState.SetQuestVariables(q, 3,0,0,0,0,0)`, When evaluating `questVariable(<q>, 0) >= 3`, Then `true`. With the same state, `questVariable(<q>, 0) >= 4` → `false`. (Proves the byte at index 0 (=3) is returned and compared.)
- **EV2** (non-zero index) — Given `SetQuestVariables(q, 0,0,0,7,0,0)`, When evaluating `questVariable(<q>, 3) == 7`, Then `true`; `questVariable(<q>, 3) == 0` → `false`. (Proves index 3 is read, not 0.)
- **EV3** (not-accepted quest → all-zero → 0) — Given a quest id with **no** `SetQuestVariables` call (FakeQuestState returns `new byte[6]`), When evaluating `questVariable(<q>, 0) == 0`, Then `true`; `questVariable(<q>, 0) >= 1` → `false`. (Pins D5 not-accepted semantics.)
- **EV4** (return type composes as Int) — Given `SetQuestVariables(q, 5,0,0,0,0,0)`, evaluating each of `>= 5` (true), `> 5` (false), `<= 5` (true), `== 5` (true), `!= 5` (false) against `questVariable(<q>, 0)` returns the stated bool. (Proves the value boxes as `long` and hits the Int comparison path.)
- **EV5** (reuses the active-quest read — no distinct pattern) — Given the engine harness running quest `<q>` with `SetQuestVariables(<q>, …)`, When a tick evaluates a step whose `expect` is `questVariable(<q>, 0) >= 1`, Then `FakeQuestState.RecordedReads` contains `GetQuestVariables` reads **all carrying the same active quest id** — there is no `GetQuestVariables` read for any *other* quest id. (Pins D6 case-1 "reuse, don't multiply"; the evaluator does not fan out to other quests.)
- **EV6** (adapter failure propagates — does NOT return 0) — Given an `IQuestState` whose `GetQuestVariables` returns `Result.Fail`, When evaluating `questVariable(<q>, 0) >= 1`, Then evaluation throws (the same exception `.ValueOrThrow` raises for the `questSequence` arm) — it does NOT silently evaluate to `false`/`0`. (Pins D5 failure propagation; contrasts the fail-open per-tick read.)
- **EV7** (runtime out-of-bounds index → 0, no throw) — Given `SetQuestVariables(q, 1,1,1,1,1,1)` and a parsed `questVariable(<q>, 9)` AST built directly (bypassing the checker, since the checker would reject a literal 9), When evaluated as `questVariable(<q>, 9) == 0`, Then `true` and no exception. (Pins the D5 defence-in-depth bounds guard. Tester builds the AST via `PredicateParser.Parse("questVariable(<q>, 9) == 0")` — the parser accepts it, PA3 — then evaluates with the engine `PredicateEvaluator`, which is NOT gated by the checker.)

### Replay / recording delegation

No new recording/replay code (D7). No dedicated test in this PR — the round-trip is already pinned by the quest-variables-trace work (QUEST_VARIABLES_TRACE_PLAN §4 group RT). EV5 indirectly guards that the predicate read uses the existing recorded `(method, arg)` pair.

---

## Implementation order

**Phase A — questforge-tools registry + parse (TDD), 0.25 day.**
1. Add the `questVariable` registry entry (D1).
2. Make RG1–RG3 and PA1–PA3 green. (PA tests pass once the registry entry exists — the parser is unchanged.)
Done before B.

**Phase B — questforge-tools checker (TDD), 0.25 day.**
1. Add the literal-index range check in `CheckCall` (D3).
2. Make CK1–CK14 green (CK6 conditional on lexer support for negative literals — verify first).
3. Build/run `QuestForge.Predicates.Tests`.
Done before C (the engine arm does not strictly depend on the checker, but landing the checker first keeps the validation contract authoritative).

**Phase C — questforge engine evaluator (TDD), 0.25 day.**
1. Add the `"questVariable"` arm + `EvaluateQuestVariable` helper (D4/D5) in `PredicateEvaluator`.
2. Make EV1–EV7 green against `FakeQuestState`.
3. Build/run `QuestForge.Engine.Tests`.

**Build commands (net10 SDK required):**
```bash
# questforge-tools
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Predicates.Tests
# questforge
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH dotnet test QuestForge.Engine.Tests
```

---

## Done criteria

1. `FunctionRegistry` exposes `questVariable` as `Fixed(2) [Int,Int] -> Int`; `FunctionRegistry.All.Count == 29` (RG1, RG2 green).
2. `questVariable(66104, 0) >= 3` parses to a `Comparison` over a `FunctionCall("questVariable", [Int, Int])` with no parser edits (PA1, PA2 green); an out-of-range literal index still **parses** (PA3 green) — range is a semantic, not syntactic, error.
3. `qf-validate` emits exactly one `quest-variable-index-out-of-range` for a literal index ∉ 0–5 (CK4, CK5, CK6) and emits no range error for valid indices (CK1–CK3), parameterised indices (CK13), or arity/type errors that pre-empt it (CK7–CK10, CK14). **CI behaviour change:** a `questforge-data` PR containing `questVariable(q, 6)` goes red with `quest-variable-index-out-of-range`; correcting the index to 0–5 turns it green.
4. The engine evaluates `questVariable(q, i)` by reading `IQuestState.GetQuestVariables(q)` and returning `variables[i]` widened to the Int comparison domain (EV1, EV2, EV4 green); a not-accepted quest yields 0 (EV3 green); an adapter failure propagates rather than returning 0 (EV6 green); a runtime out-of-bounds index returns 0 without throwing (EV7 green).
5. The engine introduces **no new `(method, arg)` read pattern**: evaluating `questVariable(<activeQuestId>, i)` reuses the per-tick `GetQuestVariables(activeQuestId)` observation key and never issues a `GetQuestVariables` read for any other quest id (EV5 green). **No fixture re-record is required by this PR** (D6).
6. No edits to `IQuestState`, the recording/replay proxies, the materializer, `FakeQuestState`, `QuestEngine`, the trace format, or any schema type; no `questforge-data` change ships with this PR.

---

## Exclusions

This PR does **NOT** include:

- **Quest-id existence validation** (Lumina game-data check on the first arg) — deferred to Phase 2+ game-data validation, same as every other `Int` quest-id argument today.
- **Any `questVariable(otherQuestId, …)` quest data** (a predicate referencing a quest other than the one being run). That is a new observation pattern (D6 case 2) and, if a future quest needs it, the owning `questforge-data` PR must be authored against a freshly-recorded trace — not this code PR.
- **The combat step itself** (`ICombat`/`CombatStep` realignment, combat controller, WrathCombo IPC, death routing). This PR only unblocks the *completion-gate predicate*; combat is PR 2/PR 3 of COMBAT_STEP_PLAN.
- **Any change to the per-tick `GetQuestVariables` read in `QuestEngine`** — it already exists and is reused as-is.
- **Recording/replay/materializer code** — `questVariable` rides the existing `IQuestState` observation channel unchanged (D7).
- **A new `RecordingCombat`/combat-trace mechanism** — out of scope (COMBAT_STEP_PLAN §3.4, PR 2).
- **Fragment-scope plumbing in the engine evaluator** — `ParameterRef` evaluation is still unsupported at runtime (PredicateEvaluator.cs:85-87, "Phase 7+"); CK13/CK14 validate the *checker* path for parameterised indices, but engine evaluation of a `${param}` index is not in this PR (the shipped combat-completion uses are literal indices).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then.
- Happy paths: 9 scenarios (RG1, PA1, PA2, CK1, EV1, EV2, EV3, EV4, EV5)
- Edge cases: 11 scenarios (RG2, RG3, PA3, CK2, CK3, CK7, CK8, CK12, CK13, CK14, EV7)
- Error cases: 8 scenarios (CK4, CK5, CK6*, CK9, CK10, CK11, EV6, + the §"Validation rule table" CI red→green data check)
- Expected total: ~28 tests — ~3 in `FunctionRegistryTests`, ~3 in `ParserTests`, ~14 in `PredicateCheckerTests` (questforge-tools `QuestForge.Predicates.Tests`); ~7 in a new `QuestVariablePredicateEvaluatorTests` (questforge `QuestForge.Engine.Tests`).

CI-testable vs in-game-only:
- **All ~28 acceptance criteria above are CI-testable** (registry/parser/checker against `QuestForge.Predicates.Tests`; engine evaluation against `FakeQuestState`).
- **In-game-only (already done, NOT in this PR's test scope):** the real Dalamud `DalamudQuestState.GetQuestVariables` read — implemented and in-game-verified in the quest-variables work; the predicate rides it unchanged.
- **CK6** is conditional on the lexer supporting a top-level negative integer literal in argument position — the Tester verifies lexing first; if unsupported, CK6 is dropped and the out-of-range behaviour stays covered by CK4/CK5.
