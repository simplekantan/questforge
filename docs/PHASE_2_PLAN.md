# Phase 2 Implementation Plan: Predicate Language Parser

**Status:** ready to implement
**Input docs:** docs/SCHEMA.md §7 (full predicate spec), docs/NEXT_STEPS.md §Phase 2
**Output:** quest file with `questSequnece(65)` → CI red, with `predicate-unknown-function` and a "did you mean 'questSequence'?" suggestion. Every predicate in every quest file parses, type-checks, and arity-checks at PR time.
**Predates:** Phase 1 (Schema validator + CI), now complete with 136 structural tests passing.

---

## Dependency graph

The parser is consumed by two callers, both of which exist (or will exist) downstream:

```
QuestForge.Schema                       ← C# schema types (Phase 1, done)
        │
        ▼
QuestForge.Predicates  ← NEW Phase 2 project: parser, AST, validator, registry
        │
        ├──► QuestForge.Tools.Validator (Phase 1, extends with PredicateValidator)
        │
        └──► QuestForge.Engine          (Phase 4+, runtime evaluator over same AST)
```

**Build order:**
1. `QuestForge.Predicates` project (tokens → AST → semantic check)
2. `QuestForge.Predicates.Tests` (unit tests; the bulk of Phase 2 effort)
3. Wire `PredicateValidator : IValidator` into the existing `ValidatorPipeline`
4. Walk every `ExpectValue`, `skipIf`, branch `when`, chain `next[].when` string and feed them through the parser
5. Update Phase 1 test fixtures so previously-tolerated predicate strings still parse cleanly

---

## Architectural decisions (read before coding)

### 1. Separate `QuestForge.Predicates` project — not inside the validator

The parser is **a new project**, sibling to `QuestForge.Schema` and `QuestForge.Tools.Validator`. Reasons:

1. **Runtime reuse.** The Phase 4 engine evaluates predicates against `IGameStateProvider`. The Phase 2 validator parses and type-checks them. Both consume the same parser. The runtime engine cannot depend on the validator project (the validator has a tools-only footprint and pulls in error-reporting infrastructure the engine has no use for), so the parser must be in a project both can reference.
2. **No Dalamud dependency.** The parser is pure C# (no `System.IO`, no Lumina, no `Dalamud.Plugin`). It can be referenced by `QuestForge.Engine` without violating the engine's testability boundary.
3. **Layered responsibilities.** Parser → AST is one concern; "is this AST valid given the function registry" is a second; "evaluate this AST against game state" is a third. Splitting #1+#2 into `QuestForge.Predicates` and leaving #3 in the engine cleanly separates author-time from runtime.
4. **Test isolation.** `QuestForge.Predicates.Tests` runs without the schema validator. Faster red/green cycles while writing the parser.

**Project layout (mirrors the questforge-tools repo where the validator already lives):**

```
questforge-tools/
  QuestForge.Schema/                    ← (Phase 1) schema types
  QuestForge.Predicates/                ← NEW: parser, AST, semantic checker, function registry
  QuestForge.Predicates.Tests/          ← NEW: xUnit suite
  QuestForge.Tools.Validator/           ← (Phase 1) wires PredicateValidator into pipeline
  QuestForge.Tools.Validator.Tests/     ← (Phase 1) gains integration tests
  qf-validate/                          ← (Phase 1) no code changes
```

**Cross-repo reuse story:** when `QuestForge.Engine` (in the plugin repo) needs the parser in Phase 4, `QuestForge.Predicates` ships as a NuGet package alongside `QuestForge.Schema`, on the same Phase 3 NuGet timeline. Until then, the engine references it by `<ProjectReference>` via a git submodule, the same shape as the Schema reference in Phase 1.

### 2. Hand-written recursive descent — not ANTLR, not Sprache/Pidgin

The grammar in SCHEMA.md §7.1 is tiny:

- 5 productions (predicate, atom, binary, unary, grouped)
- 6 comparison operators
- 3 boolean operators (`and`, `or`, `not`)
- ~30 state functions

**Decision: hand-written recursive descent with a Pratt-style operator precedence climber for the boolean layer.**

Rejected alternatives:

| Approach | Reason rejected |
|---|---|
| **ANTLR** | Code-gen step in build, runtime dependency on ANTLR runtime, much larger than the problem warrants. Worse error messages out of the box. Adds a `.g4` file to the cognitive surface. |
| **Sprache / Pidgin combinators** | Brings a NuGet dependency. Error messages from combinator failures are notoriously bad. The grammar is too small for combinators to pay back the dependency cost. |
| **Regex + ad hoc string splitting** | The grammar has nesting (`grouped`, `not`, function-call args) — regex is the wrong tool. |
| **Roslyn `CSharpScript`** | Allows arbitrary C# in quest files. Massive security and authoring footgun. Hard veto. |

**Why hand-written wins here:** roughly 400 lines of straightforward code; full control over error messages (`predicate-parse-error` can name the exact column and offer "did you mean"); zero runtime dependencies; identical behavior author-time and runtime; trivially debuggable in the IDE.

**Operator precedence (lowest to highest):**

```
or              ← left associative
and             ← left associative
not             ← right associative (prefix unary)
== != > < >= <= ← non-associative comparison
function call, literal, grouped  ← atoms
```

`and` binds tighter than `or` (standard). `not` binds tighter than `and`. Comparison binds tighter than `not`. Grouping with `(` and `)` overrides.

### 3. `default` is a reserved keyword that produces a sentinel atom

`default` appears in two places:

- Branch `when: "default"` — the fallback case
- Chain `next[].when: "default"` — the fallback transition

The parser treats `default` as a **keyword token**, not as an identifier or a state-function call. It produces an `AstNode.DefaultLiteral` (singleton). The semantic checker reports it has type `Bool` and is always true.

**Where `default` is legal:** only as the **entire** predicate string. A predicate like `"default and questFlag(65, 1)"` is `predicate/default-not-composable` — the keyword is not composable. The validator emits this with the message "the `default` keyword must be the entire predicate; combine real conditions with `and` / `or` if you need composition."

### 4. Arity descriptors support fixed, optional-tail, and variadic-min

The function registry stores an **arity descriptor**, not a single integer. Three shapes:

```csharp
public abstract record Arity
{
    public sealed record Fixed(int Count) : Arity;
    public sealed record OptionalTail(int Required, int Optional) : Arity;
    public sealed record VariadicMin(int Minimum) : Arity;
}
```

Examples from the spec:

| Function | Arity | Notes |
|---|---|---|
| `questSequence(questId)` | `Fixed(1)` | |
| `playerLevel(job?)` | `OptionalTail(0, 1)` | Zero required, one optional |
| `playerHasItem(itemId, count?)` | `OptionalTail(1, 1)` | |
| `playerHasEquipped(itemId, slot?)` | `OptionalTail(1, 1)` | |
| `playerNear(position, radius)` | `Fixed(2)` | |
| `questFlagAny(questId, bit, ...)` | `VariadicMin(2)` | First arg is questId, then ≥1 bit |
| `questFlagAll(questId, bit, ...)` | `VariadicMin(2)` | Same shape |
| `questFlagCount(questId, bit, ...)` | `VariadicMin(2)` | Same shape |
| `questFlag(questId, bit)` | `Fixed(2)` | Single-bit variant |
| `playerZone()` | `Fixed(0)` | |

The arity-mismatch error names the descriptor: "function `questFlagAny` requires at least 2 arguments; got 1."

### 5. Three primitive types: `Int`, `Bool`, `String`. Plus a `Position` sentinel.

| Type | Where it appears |
|---|---|
| `Int` | `questSequence`, `playerLevel`, `playerZone`, `gil`, etc.; numeric literals |
| `Bool` | `questFlag`, `isQuestComplete`, etc.; boolean ops; `default` |
| `String` | `playerStartingClass`, `currentJob`, `instanceKind`; string literals |
| `Position` | **Argument type only.** First arg to `playerNear` only. |

`gil()` returns `long` in the spec. The predicate language collapses `long`, `int`, and `uint` into a single `Int` type. Floating-point literals are rejected outside position literals and the `playerNear` radius arg: `predicate/parse-error: numeric literals must be integers in v1 (except inside position literals and playerNear radius)`.

**Comparison rules:**

| Left | Op | Right | Verdict |
|---|---|---|---|
| `Int` | `== != > < >= <=` | `Int` | OK |
| `Bool` | `== !=` | `Bool` | OK |
| `Bool` | `> < >= <=` | `Bool` | `predicate/type-mismatch` |
| `String` | `== !=` | `String` | OK |
| `String` | `> < >= <=` | `String` | `predicate/type-mismatch` |
| `Int` | any | `String` | `predicate/type-mismatch` |
| any | any | `Position` | `predicate/type-mismatch` |

**Bare-atom legality:** a function call returning `Bool` is a legal predicate on its own (`isQuestComplete(65)`). A function call returning `Int` or `String` standing alone is `predicate/type-mismatch: bare expression must be Bool` — the author wrote `questSequence(65)` and forgot the comparison.

### 6. `playerNear` position arg uses an inline JSON-object literal

The grammar in SCHEMA.md §7.1 lists `arg := number | string | identifier` — no object syntax. But SCHEMA.md's worked example contains:

```
"expect": "playerNear({\"x\":6.91,\"y\":-1.92,\"z\":47.29}, 5.0)"
```

**Decision: extend the predicate grammar with a `position-literal` production.**

```
position-literal := '{' 'x' ':' number ',' 'y' ':' number ',' 'z' ':' number '}'
```

Keys may appear in any order; values must be numeric (`float` or `int`, coerced to `float`). Anything else under the braces is `predicate/parse-error: position literal requires x, y, z numeric keys`.

In the AST: `AstNode.PositionLiteral(float X, float Y, float Z)`. Type-checked as `Position`. Only legal as the first argument to `playerNear`.

The position sub-parser also accepts a `-` prefix on numeric values (negative coordinates are common in FFXIV).

**`playerNear` radius handling:** the radius (second arg) is declared as `Int` in the function registry but authors commonly write it as a float literal (e.g. `5.0`). The parser accepts float literals in radius position under one rule: the value must be a whole number (fractional part is zero). `5.0` → `IntLiteral(5)` is fine. `2.5` → `predicate/parse-error: playerNear radius must be a whole number; use an integer literal instead`. This avoids silent truncation that would change the gameplay check at runtime. Non-whole float literals anywhere outside position keys and the playerNear radius always produce `predicate/parse-error: float literals are not supported in v1`.

**Radius float mechanism:** the generic `ArgList` parser doesn't know which function it belongs to, so it cannot do the playerNear-specific check inside `ParseArg`. The check happens one level up: the function-call builder, after parsing all args for a function named `"playerNear"`, inspects the second arg token. If the raw token was `NumberFloat` and the value is whole, it substitutes `IntLiteral(truncated)`; if non-whole, it emits a parse-error. All other functions receive a parse-error immediately on any `NumberFloat` token in arg position. This is the only site where function identity leaks into arg parsing.

**Fragment parameter placeholder form** `${name}` is also accepted as an arg and captured as `AstNode.ParameterRef(string Name)`, type-resolved against the enclosing fragment's declared parameter types.

### 7. Stable AST records as public API

```csharp
public abstract record PredicateAst
{
    public sealed record DefaultLiteral() : PredicateAst;
    public sealed record IntLiteral(long Value) : PredicateAst;
    public sealed record StringLiteral(string Value) : PredicateAst;
    public sealed record PositionLiteral(float X, float Y, float Z) : PredicateAst;
    public sealed record ParameterRef(string Name) : PredicateAst;
    public sealed record FunctionCall(string Name, IReadOnlyList<PredicateAst> Args) : PredicateAst;
    public sealed record Comparison(PredicateAst Left, ComparisonOp Op, PredicateAst Right) : PredicateAst;
    public sealed record And(PredicateAst Left, PredicateAst Right) : PredicateAst;
    public sealed record Or(PredicateAst Left, PredicateAst Right) : PredicateAst;
    public sealed record Not(PredicateAst Inner) : PredicateAst;
}

public enum ComparisonOp { Eq, NotEq, Gt, Lt, GtEq, LtEq }
```

The engine evaluator (Phase 4) pattern-matches over this tree. The validator (Phase 2) walks it for semantic checks. No `Grouped` node — grouping is consumed during parse and influences tree shape only.

**`IdentifierLiteral` is removed.** The original spec listed `literal := number | string | identifier | enum-value`, suggesting bare unquoted words could appear on the RHS of comparisons. In practice, the schema consistently uses quoted strings for all string values (`instanceKind() == "None"`, `currentJob() == "Gladiator"`). An unquoted bare word on the RHS is always either a typo or a forgotten function call — both are better caught as `predicate/unknown-function` (if it looks like a function name) or `predicate/parse-error` (otherwise) than silently accepted as an identifier literal. Removing `IdentifierLiteral` from the AST makes the type system cleaner and forces authors to quote string values explicitly.

### 8. Errors are positional and collected, not thrown

```csharp
public record ParseError(string Code, string Message, int Column, string? Suggestion = null);
public record ParseResult(PredicateAst? Ast, IReadOnlyList<ParseError> Errors);
```

The parser tries to recover at obvious sync points (commas inside arg lists, `and`/`or` keywords) so one mistake doesn't cascade. The validator translates `ParseError` → `ValidationError`:

| ParseError.Code | ValidationError.Code |
|---|---|
| `parse-error` | `predicate/parse-error` |
| `unknown-function` | `predicate/unknown-function` |
| `arity-mismatch` | `predicate/arity-mismatch` |
| `type-mismatch` | `predicate/type-mismatch` |
| `default-not-composable` | `predicate/default-not-composable` |
| `unknown-parameter` | `predicate/unknown-parameter` |

### 9. "Did you mean…" suggestions via Levenshtein distance ≤ 2

When the parser sees an unknown identifier in function-call position, it computes Levenshtein distance against every registered function name. Any with distance ≤ 2 is suggested: `questSequnece` → distance 1 from `questSequence` → "did you mean 'questSequence'?"

This is the single highest-leverage UX feature in Phase 2 and the only reason `predicate-unknown-function` is worth distinguishing from `predicate-parse-error`.

### 10. Caching: parse once per (source, scope) pair

```csharp
private readonly Dictionary<(string Source, int ScopeId), ParseResult> _cache = new();
```

A single quest file may repeat the same predicate string across many steps. The `PredicateValidator` caches parse results to avoid redundant work.

**Cache key is `(source, scopeId)` not just `source`.** The same predicate string can appear in two fragment files with different `Parameters` declarations — `"playerNear(${pos}, 5)"` where `pos` is `position` in one fragment and `npcId` in another. Keying on source alone would return a stale cached result for the second file. `scopeId` is `0` for quest-level predicates (no scope), and `fragmentId.GetHashCode()` for fragment predicates.

Use `TryGetValue` + indexer assignment — `GetOrAdd` is `ConcurrentDictionary`-only and will not compile on `Dictionary<>`. `scopeId` comes from `CollectPredicates` as a pre-computed fragment ID hash, not from `scope.GetHashCode()` (which would use object-reference identity and defeat cross-call caching):

```csharp
var key = (predicate, scopeId);   // scopeId: 0 for quest, fragmentId.GetHashCode() for fragment
if (!_cache.TryGetValue(key, out var result))
    _cache[key] = result = PredicateParser.Parse(predicate, scope);
```

---

## Task 1 — `QuestForge.Predicates` project

### 1.1 Project skeleton

Add to `questforge-tools.sln`:
- `QuestForge.Predicates/QuestForge.Predicates.csproj` — `net10.0`, no dependencies beyond BCL
- `QuestForge.Predicates.Tests/QuestForge.Predicates.Tests.csproj` — xUnit, references the above

No reference to `QuestForge.Schema`. The parser must not know about quest types — it only knows strings and the function registry.

### 1.2 Public surface

```csharp
namespace QuestForge.Predicates;

public static class PredicateParser
{
    public static ParseResult Parse(string source);
    public static ParseResult Parse(string source, IFragmentParameterScope? scope);
}

public interface IFragmentParameterScope
{
    bool TryGetParameterType(string name, out PredicateType type);
}

public enum PredicateType { Int, Bool, String, Position }

public static class FunctionRegistry
{
    public static IReadOnlyDictionary<string, FunctionSignature> All { get; }
    public static bool TryGet(string name, out FunctionSignature sig);
    public static IReadOnlyList<string> SuggestSimilar(string name, int maxDistance = 2);
}

public record FunctionSignature(
    string Name,
    Arity Arity,
    IReadOnlyList<PredicateType> ParameterTypes,
    PredicateType ReturnType
);

public static class PredicateChecker
{
    public static IReadOnlyList<ParseError> Check(PredicateAst ast, IFragmentParameterScope? scope = null);
}
```

### 1.3 Token kinds

```csharp
internal enum TokenKind
{
    Identifier, NumberInt, NumberFloat, String,
    LParen, RParen, LBrace, RBrace, Comma, Colon,
    Eq, NotEq, Gt, Lt, GtEq, LtEq,
    KeywordAnd, KeywordOr, KeywordNot, KeywordDefault,
    ParameterRef, EndOfInput, Error
}
```

Identifier: `[a-zA-Z_][a-zA-Z0-9_]*`. Lookup in `{and, or, not, default}` after matching to detect keyword status.

### 1.4 State function registry (full v1 list)

```csharp
internal static readonly FunctionSignature[] Functions =
{
    new("questSequence",             new Fixed(1),          [Int],           Int),
    new("questFlag",                 new Fixed(2),          [Int, Int],      Bool),
    new("questFlags",                new Fixed(1),          [Int],           Int),
    new("questFlagAny",              new VariadicMin(2),    [Int, Int],      Bool),
    new("questFlagAll",              new VariadicMin(2),    [Int, Int],      Bool),
    new("questFlagCount",            new VariadicMin(2),    [Int, Int],      Int),
    new("isQuestComplete",           new Fixed(1),          [Int],           Bool),
    new("isQuestAccepted",           new Fixed(1),          [Int],           Bool),
    new("isQuestAvailable",          new Fixed(1),          [Int],           Bool),
    new("playerZone",                new Fixed(0),          [],              Int),
    new("playerLevel",               new OptionalTail(0,1), [String],        Int),
    new("playerHasItem",             new OptionalTail(1,1), [Int, Int],      Bool),
    new("playerHasEquipped",         new OptionalTail(1,1), [Int, String],   Bool),
    new("playerAverageItemLevel",    new Fixed(0),          [],              Int),
    new("playerNear",                new Fixed(2),          [Position, Int], Bool),
    new("playerStartingClass",       new Fixed(0),          [],              String),
    new("currentJob",                new Fixed(0),          [],              String),
    new("inventoryFreeSlots",        new Fixed(0),          [],              Int),
    new("instanceKind",              new Fixed(0),          [],              String),
    new("playerInCombat",            new Fixed(0),          [],              Bool),
    new("playerDead",                new Fixed(0),          [],              Bool),
    new("interactableActive",        new Fixed(1),          [Int],           Bool),
    new("uiDialogueOpen",            new Fixed(0),          [],              Bool),
    new("gil",                       new Fixed(0),          [],              Int),
    new("playerLowestGearCondition", new Fixed(0),          [],              Int),
    new("gearsetExists",             new Fixed(1),          [String],        Bool),
    new("inNewGamePlus",             new Fixed(0),          [],              Bool),
};
```

`playerNear`'s second arg is declared `Int`. The parser accepts a `NumberFloat` in radius position **only if the fractional part is zero** (`5.0` → `IntLiteral(5)` is accepted; `2.5` → `predicate/parse-error`). No float literals are accepted anywhere else outside position literal keys.

### 1.5 Parser pseudocode (recursive descent)

```
ParsePredicate         := ParseOr
ParseOr                := ParseAnd ( 'or' ParseAnd )*
ParseAnd               := ParseNot ( 'and' ParseNot )*
ParseNot               := 'not' ParseNot | ParseComparisonOrAtom
ParseComparisonOrAtom  := ParseAtom comparison-op RhsLiteral
                        | ParseAtom
ParseAtom              := '(' ParsePredicate ')'
                        | 'default'
                        | identifier '(' ArgList? ')'   ← function call with args
                        | identifier                     ← bare identifier; see note below
                        | number-literal
                        | string-literal
                        | position-literal
                        | parameter-ref
RhsLiteral             := number-literal | string-literal
ArgList                := Arg ( ',' Arg )*
Arg                    := number-literal | string-literal
                        | position-literal | parameter-ref
```

`default` is not a legal `Arg`.

**Bare identifier handling:** a bare identifier (not followed by `(`) is parsed as `FunctionCall(name, [])` — a zero-arg call attempt. This is intentional: it routes the identifier through the semantic checker, which can then fire `predicate/unknown-function` with a Levenshtein suggestion ("did you mean 'isQuestComplete'?"). Emitting `predicate/parse-error` at the parser level instead would lose the suggestion. This is purely an error-reporting decision — valid predicates never produce a bare identifier.

### 1.6 Implementation order inside `QuestForge.Predicates`

1. AST records, `PredicateType`, `Arity`, `FunctionSignature`, `ParseError`, `ParseResult`
2. `FunctionRegistry` static table + Levenshtein helper + `SuggestSimilar`
   *(Levenshtein must exist before the semantic checker — `unknown-function` tests cannot go green without it)*
3. Lexer (tests first)
4. Parser recursive descent (tests covering grammar, precedence, recovery)
5. `PredicateChecker` semantic walk (tests for each error class)

---

## Task 2 — Integration into `QuestForge.Tools.Validator`

### 2.1 New `PredicateValidator : IValidator`

```csharp
public class PredicateValidator(IFragmentRegistry fragments) : IValidator
{
    // Key: (source string, scope ID). Scope ID is 0 for quest-level predicates
    // (no fragment parameters), or fragmentId.GetHashCode() for fragment predicates.
    // Using the fragment's string ID — not the IFragmentParameterScope object reference —
    // ensures cache hits across multiple calls with different scope object instances.
    private readonly Dictionary<(string Source, int ScopeId), ParseResult> _cache = new();

    public IEnumerable<ValidationError> Validate(QuestDefinition quest, ValidationContext ctx)
    {
        // CollectPredicates yields quest-level predicates (scopeId=0) and, for each
        // FragmentStep that resolves in the registry, the fragment's step predicates
        // with the fragment's parameter scope and scopeId=fragmentId.GetHashCode().
        foreach (var (predicate, location, stepId, scope, scopeId) in CollectPredicates(quest))
        {
            var key = (predicate, scopeId);
            if (!_cache.TryGetValue(key, out var result))
                _cache[key] = result = PredicateParser.Parse(predicate, scope);

            foreach (var err in result.Errors)
                yield return TranslateError(err, predicate, location, stepId, ctx.FilePath);

            if (result.Errors.Count == 0 && result.Ast is not null)
                foreach (var err in PredicateChecker.Check(result.Ast, scope))
                    yield return TranslateError(err, predicate, location, stepId, ctx.FilePath);
        }
    }
}
```

`PredicateValidator` receives `IFragmentRegistry` for the same reason `StructuralValidator` does: when a `FragmentStep` is encountered, it resolves the fragment and validates its step predicates using an `IFragmentParameterScope` built from the fragment's declared `Parameters`. If the fragment is not found in the registry, the step is skipped (the structural validator already reported `fragment-not-found` for that case).

### 2.2 Predicate collection — every site

| Site | Location format |
|---|---|
| `QuestSequence.SkipIf` | `seq:N` |
| `Step.Expect` (PredicateExpect) | `seq:N/step:<id>/expect` |
| `Step.Expect` (AllExpect/AnyExpect) | `seq:N/step:<id>/expect/all[i]` or `.../any[i]` |
| `Step.SkipIf` (same three forms) | `seq:N/step:<id>/skipIf` |
| `BranchStep.Branches[i].When` | `seq:N/step:<id>/branches[i]/when` |
| `Chain.Next[i].When` | `chain/next[i]/when` |

Fragment scope: when walking a `FragmentDefinition`'s steps, pass an `IFragmentParameterScope` built from the fragment's declared `Parameters`.

### 2.3 Pipeline wiring

```csharp
var pipeline = new ValidatorPipeline([
    new StructuralValidator(fragments),
    new PredicateValidator(fragments),
]);
```

### 2.4 Phase 1 deferred items now implemented

This phase fulfills SCHEMA.md §8.4 (predicate validity). It does NOT fulfill §8.5 goto loop detection, §8.2 game-data references, §8.3 chain bidirectionality, §8.6 patch verification, or §8.7 schema-version checks — those remain deferred.

---

## Task 3 — Validation rule table

### 3.1 Parse-time rules

| Rule | Code | Trigger example |
|---|---|---|
| Unrecognized character | `predicate/parse-error` | `questFlag(65, 1) & questFlag(65, 2)` |
| Unbalanced parentheses | `predicate/parse-error` | `questFlag(65, 1` |
| Unbalanced braces in position literal | `predicate/parse-error` | `playerNear({"x":1,"y":2,"z":3, 5)` |
| Missing argument after comma | `predicate/parse-error` | `questFlag(65,)` |
| Trailing operator | `predicate/parse-error` | `questSequence(65) >=` |
| Leading binary operator | `predicate/parse-error` | `and questFlag(65, 1)` |
| Empty predicate string | `predicate/parse-error` | `""` |
| Whitespace-only predicate | `predicate/parse-error` | `"   "` |
| Float literal outside legal context | `predicate/parse-error` | `questSequence(65) >= 3.5` |
| Position literal missing key | `predicate/parse-error` | `playerNear({"x":1,"y":2}, 5)` |
| Position literal with extra keys | `predicate/parse-error` | `playerNear({"x":1,"y":2,"z":3,"w":4}, 5)` |
| Position literal non-numeric value | `predicate/parse-error` | `playerNear({"x":"a","y":2,"z":3}, 5)` |
| Non-whole float in `playerNear` radius | `predicate/parse-error` | `playerNear({...}, 2.5)` |
| String literal unterminated | `predicate/parse-error` | `playerStartingClass() == "Gladiator` |
| Unary minus outside position literal keys | `predicate/parse-error` | `questSequence(65) >= -3` |

### 3.2 Semantic rules — function identity

| Rule | Code | Trigger example |
|---|---|---|
| Function name not in registry | `predicate/unknown-function` | `questSequnece(65) >= 3` |
| Identifier in atom position (not a known function) | `predicate/unknown-function` | bare `Gladiator` |

### 3.3 Semantic rules — arity

| Rule | Code | Trigger example |
|---|---|---|
| Fixed arity exceeded | `predicate/arity-mismatch` | `questSequence(65, 1) >= 3` |
| Fixed arity not met | `predicate/arity-mismatch` | `questFlag(65)` |
| OptionalTail exceeded | `predicate/arity-mismatch` | `playerHasItem(123, 1, 5)` |
| VariadicMin not met | `predicate/arity-mismatch` | `questFlagAny(65)` |
| Zero-arg call with args | `predicate/arity-mismatch` | `playerZone(132)` |

### 3.4 Semantic rules — types

| Rule | Code | Trigger example |
|---|---|---|
| Arg type wrong | `predicate/type-mismatch` | `questSequence("65")` |
| Compare Int to String | `predicate/type-mismatch` | `questSequence(65) >= "three"` |
| Compare Bool to Int | `predicate/type-mismatch` | `isQuestComplete(65) == 1` |
| Relational op on Bool | `predicate/type-mismatch` | `isQuestComplete(65) > isQuestAccepted(65)` |
| Relational op on String | `predicate/type-mismatch` | `currentJob() > "Gladiator"` |
| Bare expression returns non-Bool | `predicate/type-mismatch` | `questSequence(65)` (no comparison) |
| Int passed where Position expected | `predicate/type-mismatch` | `playerNear(132, 5)` |
| Position passed where Int expected | `predicate/type-mismatch` | `questFlag({"x":1,"y":2,"z":3}, 1)` |

### 3.5 Semantic rules — `default` keyword

| Rule | Code | Trigger example |
|---|---|---|
| `default` combined with other expressions | `predicate/default-not-composable` | `default and questFlag(65, 1)` |
| `default` used as function arg | `predicate/default-not-composable` | `playerNear(default, 5)` |

### 3.6 Semantic rules — fragment parameters

| Rule | Code | Trigger example |
|---|---|---|
| `${name}` references undeclared parameter | `predicate/unknown-parameter` | `playerNear(${notDeclared}, 5)` |
| `${name}` type mismatches expected | `predicate/type-mismatch` | fragment declares `count: itemId`; predicate uses `playerNear(${count}, 5)` |
| `${name}` outside a fragment file | `predicate/parse-error` | `${anything}` in a top-level quest file |

---

## Task 4 — Given-When-Then specifications

### 4.1 Lexer

- `"  questFlag( 65 ,  1 )  "` → `Identifier("questFlag"), LParen, NumberInt(65), Comma, NumberInt(1), RParen, EndOfInput`
- `"and"` → single `KeywordAnd` token
- `"andSomething"` → single `Identifier("andSomething")`
- `">="` → `GtEq` token
- `"> ="` → `Gt`, then `Error` token for `=`
- `"65"` → `NumberInt(65)`
- `"5.0"` → `NumberFloat(5.0)`
- `"-3"` → `Error` token for `-` (unary minus not supported outside position literals)

### 4.2 Parser — happy paths

- `"questSequence(65) >= 3"` → `Comparison(FunctionCall("questSequence", [IntLiteral(65)]), GtEq, IntLiteral(3))`
- `"playerZone() == 155 and not playerInCombat()"` → `And(Comparison(...), Not(FunctionCall("playerInCombat", [])))`
- `"questFlagAll(65, 1, 2, 3)"` → `FunctionCall("questFlagAll", [IntLiteral(65), IntLiteral(1), IntLiteral(2), IntLiteral(3)])`
- `"questFlagAny(65, 5, 6, 7) or questSequence(65) >= 5"` → `Or(FunctionCall(...), Comparison(...))`
- `"playerStartingClass() == \"Gladiator\""` → `Comparison(FunctionCall(...), Eq, StringLiteral("Gladiator"))`
- `"default"` → `DefaultLiteral`
- `"playerNear({\"x\":6.91,\"y\":-1.92,\"z\":47.29}, 5.0)"` → `FunctionCall("playerNear", [PositionLiteral(6.91f, -1.92f, 47.29f), IntLiteral(5)])`

### 4.3 Parser — operator precedence

- `"a() and b() or c()"` → `Or(And(a, b), c)` (`and` tighter than `or`)
- `"not a() and b()"` → `And(Not(a), b)` (`not` tighter than `and`)
- `"not (a() and b())"` → `Not(And(a, b))` (grouping overrides)
- `"a() == 1 and b() == 2"` → `And(Comparison(a, Eq, 1), Comparison(b, Eq, 2))`

### 4.4 Parser — error recovery

- `"questFlag(65, ) and questFlag(65, 2)"` → error for empty arg, but parser recovers at `and` and parses second call
- `"questSequnece(65) >= 3 and questFlag(65, 1)"` → one `predicate/unknown-function` with suggestion; right-side `And` branch parses successfully

### 4.5 Semantic checker — unknown function

- `"questSequnece(65) >= 3"` → `predicate/unknown-function`, suggestion `"questSequence"`
- `"isQuestcomplete(65)"` → suggestion `"isQuestComplete"`
- `"frobnicate(1)"` → unknown function, no suggestion (distance > 2)

### 4.6 Semantic checker — arity

- `"questFlag(65)"` → `predicate/arity-mismatch: function 'questFlag' requires exactly 2 arguments; got 1`
- `"questFlagAny(65)"` → `predicate/arity-mismatch: function 'questFlagAny' requires at least 2 arguments; got 1`
- `"questFlagAny(65, 1)"` → no error
- `"questFlagAny(65, 1, 2, 3, 4, 5)"` → no error
- `"playerHasItem(123)"` → no error
- `"playerHasItem(123, 5)"` → no error
- `"playerHasItem(123, 5, 7)"` → `predicate/arity-mismatch: function 'playerHasItem' accepts 1 or 2 arguments; got 3`
- `"playerLevel()"` → no error
- `"playerLevel(\"Gladiator\")"` → no error
- `"playerZone(132)"` → `predicate/arity-mismatch: function 'playerZone' accepts no arguments; got 1`

### 4.7 Semantic checker — types

- `"questSequence(\"65\")"` → `predicate/type-mismatch: argument 1 of 'questSequence' expects Int, got String`
- `"questSequence(65) >= \"three\""` → `predicate/type-mismatch: cannot compare Int to String`
- `"questSequence(65)"` (bare) → `predicate/type-mismatch: bare predicate expression must be Bool; 'questSequence' returns Int`
- `"isQuestComplete(65)"` (bare) → no error
- `"isQuestComplete(65) > isQuestAccepted(65)"` → `predicate/type-mismatch: relational operator '>' not supported on Bool`
- `"currentJob() != \"Pugilist\""` → no error
- `"playerNear(132, 5)"` → `predicate/type-mismatch: argument 1 of 'playerNear' expects Position, got Int`
- `"playerNear({\"x\":1,\"y\":2,\"z\":3}, 5)"` → no error

### 4.8 Semantic checker — `default`

- `"default"` (alone) → no error
- `"default and questFlag(65, 1)"` → `predicate/default-not-composable`
- `"questFlag(65, 1) and default"` → same error
- `"playerNear(default, 5)"` → `predicate/default-not-composable: 'default' is not a value`

### 4.9 Semantic checker — fragment parameters

Setup: fragment declares `Parameters = [{name: "finalPosition", type: "position"}, {name: "count", type: "itemId"}]`.

- `"playerNear(${finalPosition}, 3.0)"` → no error
- `"playerHasItem(${count}, 1)"` → no error
- `"playerHasItem(${notDeclared}, 1)"` → `predicate/unknown-parameter`
- `"playerNear(${count}, 3.0)"` → `predicate/type-mismatch: parameter 'count' has type itemId (Int); 'playerNear' expects Position at argument 1`
- `"${anything}"` parsed without a scope → `predicate/parse-error: parameter references are only legal inside fragment files`

### 4.10 Integration — `PredicateValidator` over a real quest

Setup: a quest file with a malformed predicate in one `expect`, a valid predicate in another, and `"default"` in a branch.

Then `PredicateValidator.Validate` yields one `ValidationError` for the malformed predicate, with `Location` pointing to `seq:N/step:<id>/expect`. The valid predicate and `"default"` yield no errors.

### 4.11 Cache behavior

Given a quest with the same predicate `"questSequence(65) >= 3"` appearing in 17 different `expect` fields — Then `PredicateParser.Parse` is invoked exactly once for that string.

---

## Task 5 — Done criteria

Phase 2 is done when:

1. PR to `questforge-data` with `"expect": "questSequnece(65) >= 3"` is caught by CI with `[predicate/unknown-function]` and suggestion "did you mean 'questSequence'?".
2. PR with `"expect": "questFlag(65)"` caught as `[predicate/arity-mismatch]`.
3. PR with `"expect": "questSequence(65) >= \"three\""` caught as `[predicate/type-mismatch]`.
4. PR with `"expect": "default"` in a branch `when` field passes validation.
5. PR with `"expect": "default and questFlag(65, 1)"` caught as `[predicate/default-not-composable]`.
6. Every predicate in SCHEMA.md §7.3 examples and the §10 worked example parses with zero errors.
7. `QuestForge.Predicates.Tests` has ≥ 80% line coverage and all suites from §4 pass.

---

## Implementation order

**Phase A — Foundation (1 day)**
1. Create `QuestForge.Predicates` + `QuestForge.Predicates.Tests` projects
2. Commit AST records, `PredicateType`, `Arity`, `FunctionSignature`, `ParseError`, `ParseResult`, `IFragmentParameterScope`
3. Commit `FunctionRegistry` static table with all 27 functions
4. Levenshtein helper + `SuggestSimilar`

**Phase B — Lexer + Parser (1 day)**
1. Write lexer tests (§4.1)
2. Implement lexer
3. Write parser tests for happy paths and precedence (§4.2, §4.3)
4. Implement recursive-descent parser
5. Write parser tests for error recovery (§4.4)
6. Add recovery sync points

**Phase C — Semantic checker (1 day)**
1. Write + implement: unknown-function with suggestions (§4.5)
2. Write + implement: arity checks (§4.6)
3. Write + implement: type checks and comparison-validity matrix (§4.7)
4. Write + implement: `default` composability (§4.8)
5. Write + implement: fragment parameter scope (§4.9)

**Phase D — Validator integration (half day)**
1. Implement `PredicateValidator : IValidator`
2. Implement `CollectPredicates` walking every site in §2.2
3. Add `PredicateValidator` to the pipeline in `qf-validate`
4. Integration test (§4.10) + cache hit test (§4.11)

**Phase E — CI proof (half day)**
1. Open PRs to `questforge-data` for each done-criterion predicate error
2. Verify CI annotates each with the correct error code
3. Fix predicates → CI green
4. Merge a PR with a quest containing all-valid predicates as the final gate

---

## What Phase 2 does NOT include

- Runtime predicate evaluation against game state (Phase 4, `QuestForge.Engine`)
- Goto infinite-loop detection (Phase 2+, separate)
- Game-data reference validation (requires Lumina)
- Chain bidirectionality and DAG-cycle detection
- Patch-verification warnings (§8.6)
- Schema-version compatibility checks (§8.7)
- Floating-point arithmetic or negative numbers as standalone literals (only inside position literals)
- Boolean literals `true`/`false` — reserved for v1.1
- Unary minus outside position literals — reserved for v1.1
- `xor`, bitwise ops — not in v1 grammar