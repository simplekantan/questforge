# Phase 11A Plan — `qf-trace` CLI Wiring

**Status:** READY FOR TEST CREATION
**Related:** `docs/PHASE_10_PLAN.md` §11 (dispatch spec), `docs/BACKLOG.md` §6.1, `docs/FIXTURES.md` (output format)
**Target repo:** `questforge-tools`
**Target language:** C# / .NET 10
**Predecessor:** Phase 10 — library complete (`QuestForge.Tools.Trace`, 44 tests passing)
**Successor:** Phase 11B — corpus expansion, second canonical fixture

---

## 1. Scope and goals

Phase 11A wires argument parsing and output formatting onto the four `qf-trace` subcommands so they are usable from the command line. The library (`QuestForge.Tools.Trace`) is fully implemented and tested; this phase is purely the thin `Program.cs` shell that connects arguments to library calls to stdout / files.

### 1.1 In scope

1. Argument parsing for all four subcommands, extracted into a testable static class (`CliArgs`) that lives in `QuestForge.Tools.Trace` (not in the CLI project — keeps the CLI project a one-method shim).
2. Output formatters (`FormatIssues`, `FormatTodos`, `FormatFixtureList`) for stdout text.
3. Quest-data root auto-resolution (probe `./quests/` then `../questforge-data/quests/`).
4. `FixtureModel` JSON serialization options matching `FIXTURES.md` byte-for-byte (modulo `description`).
5. Exit-code routing per subcommand.
6. `--help` text listing all four subcommands and their arguments.

### 1.2 Out of scope

- Changes to `TraceToFixtureExtractor`, `FixtureValidator`, `TraceToQuestExtractor` behaviour — these are frozen.
- A new third-party CLI argument parser dependency. Manual parsing only, mirroring `qf-validate`.
- `qf-trace replay`, `qf-trace redact`, `qf-trace validate` (see `BACKLOG.md` §6.2–6.4).
- `--lumina` enrichment of `extract-quest` output (see `BACKLOG.md` §6.5–6.6).

**Note:** `ListFixturesCommand.Enumerate` and `.ComputeGaps` currently throw `NotImplementedException` from Phase 10. Phase 11A fills them in (§7) — these are in scope since `list-fixtures` cannot work without them. The `ListFixturesCommand` interface is unchanged.

---

## 2. Component layout

```
questforge-tools/
  qf-trace/
    Program.cs                                 (rewritten — argument routing + IO)
  QuestForge.Tools.Trace/
    Cli/
      CliArgs.cs                               (NEW — testable arg parsing)
      CliSubcommand.cs                         (NEW — enum)
      QuestDataRootResolver.cs                 (NEW — probe algorithm)
      OutputFormatters.cs                      (NEW — FormatIssues/FormatTodos/FormatList)
      FixtureModelSerializer.cs                (NEW — central JsonSerializerOptions)
    Fixture/
      ListFixturesCommand.cs                   (UPDATED — fill in Enumerate/ComputeGaps)
  QuestForge.Tools.Trace.Tests/
    Cli/
      CliArgsTests.cs                          (NEW — argument parsing tests)
      OutputFormattersTests.cs                 (NEW — formatter tests)
      QuestDataRootResolverTests.cs            (NEW — resolver probe tests)
```

No new project references required. `QuestForge.Tools.Trace` already references `QuestForge.Engine` (transitively bringing `QuestForge.Schema`), `QuestForge.Adapters`, and `QuestForge.Adapters.Tracing`.

---

## 3. `CliArgs` — testable argument parser

### 3.1 Purpose

Single responsibility: turn `string[] args` into a strongly-typed `CliArgs` record. No I/O, no side effects. Lives in `QuestForge.Tools.Trace.Cli` so tests can call it directly without invoking `Program.Main`.

### 3.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Cli;

public enum CliSubcommand
{
    None,            // no args at all → help
    Help,            // --help, -h, or "help"
    Unknown,         // first token is not a recognised subcommand
    ExtractFixture,
    ValidateFixture,
    ListFixtures,
    ExtractQuest,
}

public sealed record CliArgs(
    CliSubcommand Subcommand,
    string? TracePath,         // positional, extract-fixture / extract-quest
    string? FixturePath,       // positional, validate-fixture
    string? QuestDataRoot,     // --quest-data <dir>
    string? OutputPath,        // --out <file>
    bool Stdout,               // --stdout (extract-fixture only)
    bool FailOnWarning,        // --fail-on-warning (validate-fixture only)
    string Format,             // --format text|json (list-fixtures, default "text")
    string? UnknownToken,      // populated when Subcommand == Unknown
    string? ParseError);       // non-null when args are malformed (e.g. --out with no value)

public static class CliArgsParser
{
    public static CliArgs Parse(string[] args);
}
```

### 3.3 Parsing rules (precedence top-to-bottom)

1. **Zero args** → `Subcommand = None`. The shell shows help and exits `0`.
2. **First arg ∈ {`--help`, `-h`, `help`}** → `Subcommand = Help`. Exit `0`.
3. **First arg ∈ {`extract-fixture`, `validate-fixture`, `list-fixtures`, `extract-quest`}** → set `Subcommand` accordingly, continue parsing.
4. **First arg matches nothing above** → `Subcommand = Unknown`, `UnknownToken = args[0]`. Exit `1` with an error message.

After the subcommand token, walk the remaining args left-to-right:

| Token | Effect | Applies to subcommand(s) |
|---|---|---|
| `--quest-data <dir>` | `QuestDataRoot = <dir>` | all four |
| `--out <file>` | `OutputPath = <file>` | extract-fixture, extract-quest |
| `--stdout` | `Stdout = true` | extract-fixture |
| `--fail-on-warning` | `FailOnWarning = true` | validate-fixture |
| `--format <text\|json>` | `Format = <value>` | list-fixtures |
| any other `--flag` | `ParseError = "unknown flag: <flag>"` | — |
| positional (no `--` prefix), first one | `TracePath = <arg>` for extract-fixture / extract-quest; `FixturePath = <arg>` for validate-fixture | — |
| positional (no `--` prefix), second+ | `ParseError = "unexpected positional argument: <arg>"` | — |

### 3.4 Defaults

- `QuestDataRoot = null` — resolution happens at runtime via `QuestDataRootResolver`, not here.
- `Format = "text"`.
- All booleans default `false`.
- All string fields default `null`.

### 3.5 Validation note

`CliArgsParser.Parse` performs **only syntactic validation** (no missing-flag-value, no unknown-flag tokens). Semantic validation (e.g. "extract-fixture requires `TracePath`") happens in `Program.cs` after parsing, since some checks depend on what subcommand was chosen. Missing required positional → `Program.cs` prints an error and returns `1`.

### 3.6 Behaviours — Given/When/Then

**B-1 — `extract-fixture` with every flag.**
Given args `["extract-fixture", "run.jsonl", "--quest-data", "./qd", "--out", "out.json"]`,
When `Parse` is called,
Then `Subcommand == ExtractFixture`, `TracePath == "run.jsonl"`, `QuestDataRoot == "./qd"`, `OutputPath == "out.json"`, `Stdout == false`, `ParseError == null`.

**B-2 — `extract-fixture --stdout`.**
Given args `["extract-fixture", "run.jsonl", "--stdout"]`,
When `Parse`,
Then `Stdout == true`, `OutputPath == null`, `TracePath == "run.jsonl"`.

**B-3 — `validate-fixture --fail-on-warning`.**
Given args `["validate-fixture", "f.json", "--fail-on-warning"]`,
When `Parse`,
Then `Subcommand == ValidateFixture`, `FixturePath == "f.json"`, `FailOnWarning == true`.

**B-4 — `list-fixtures --format json`.**
Given args `["list-fixtures", "--format", "json"]`,
When `Parse`,
Then `Subcommand == ListFixtures`, `Format == "json"`.

**B-5 — `extract-quest` with all args.**
Given args `["extract-quest", "run.jsonl", "--quest-data", "./qd", "--out", "draft.json"]`,
When `Parse`,
Then `Subcommand == ExtractQuest`, `TracePath == "run.jsonl"`, `QuestDataRoot == "./qd"`, `OutputPath == "draft.json"`.

**B-6 — empty args.**
Given args `[]`,
When `Parse`,
Then `Subcommand == None`, all other fields are at defaults, `ParseError == null`.

**B-7 — unknown subcommand.**
Given args `["frob"]`,
When `Parse`,
Then `Subcommand == Unknown`, `UnknownToken == "frob"`.

**B-8 — flag with missing value.**
Given args `["extract-fixture", "run.jsonl", "--out"]` (trailing `--out` with no following token),
When `Parse`,
Then `ParseError == "flag --out requires a value"`.

**B-9 — unknown flag.**
Given args `["extract-fixture", "run.jsonl", "--frobnicate"]`,
When `Parse`,
Then `ParseError == "unknown flag: --frobnicate"`.

**B-10 — second positional.**
Given args `["extract-fixture", "a.jsonl", "b.jsonl"]`,
When `Parse`,
Then `ParseError == "unexpected positional argument: b.jsonl"`.

---

## 4. `QuestDataRootResolver`

### 4.1 Purpose

When the user does not pass `--quest-data`, probe a fixed sequence of candidate paths for a `quests/` subdirectory. Returns the first match.

### 4.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Cli;

public static class QuestDataRootResolver
{
    /// <summary>
    /// Resolve a quest-data root using the algorithm in §4.3.
    /// Returns the absolute, normalised path to the root, or null if no candidate
    /// directory contains a "quests/" subdirectory.
    /// </summary>
    /// <param name="workingDirectory">The directory used as the probe anchor. Defaults to Environment.CurrentDirectory.</param>
    public static string? Resolve(string? workingDirectory = null);
}
```

### 4.3 Probe algorithm (in order, first match wins)

1. If `Path.Combine(cwd, "quests")` exists → return `cwd`.
2. If `Path.Combine(cwd, "..", "questforge-data", "quests")` exists → return `Path.GetFullPath(Path.Combine(cwd, "..", "questforge-data"))`.
3. Return `null`.

When `Program.cs` calls `Resolve` and the user did not pass `--quest-data`:
- Non-null result → log `"qf-trace: using quest-data root: <path>"` to stderr.
- Null result → log `"qf-trace: no quest-data root found; using placeholder paths"` to stderr.

When the user did pass `--quest-data`, `Program.cs` skips `Resolve` entirely and uses the supplied value verbatim (after `Path.GetFullPath`).

### 4.4 Behaviours

**B-11 — sibling repo layout (the common case).**
Given a temp directory `tmp/checkout` where `tmp/questforge-data/quests/` exists,
When `Resolve("tmp/checkout")`,
Then returns `Path.GetFullPath("tmp/questforge-data")`.

**B-12 — current directory is a checkout.**
Given a temp directory `tmp/qd` where `tmp/qd/quests/` exists,
When `Resolve("tmp/qd")`,
Then returns `Path.GetFullPath("tmp/qd")`.

**B-13 — no candidate exists.**
Given a temp directory `tmp/empty` containing neither `quests/` nor `../questforge-data/quests/`,
When `Resolve("tmp/empty")`,
Then returns `null`.

---

## 5. `FixtureModelSerializer`

### 5.1 Purpose

Provide a single `JsonSerializerOptions` configured to match the byte-level format in `FIXTURES.md`. Used only by `extract-fixture` when writing `FixtureModel` to disk or stdout. (`extract-quest` uses `QuestForgeJsonContext.QuestFileOptions` — the canonical Schema serializer — instead.)

### 5.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Cli;

public static class FixtureModelSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(FixtureModel fixture)
        => JsonSerializer.Serialize(fixture, Options);
}
```

### 5.3 Rationale for each option

- `WriteIndented` — fixtures are committed to git and read by humans. Pretty-printing matches `FIXTURES.md`.
- `PropertyNamingPolicy = CamelCase` — `FixtureModel`'s PascalCase C# properties (`SchemaVersion`, `ExpectedTransitions`, `TerminalOutcome`) become `schemaVersion`, `expectedTransitions`, `terminalOutcome` on the wire.
- `DefaultIgnoreCondition = WhenWritingNull` — `TerminalOutcome` is nullable; an absent `RunEnd` event should emit no `terminalOutcome` key rather than `"terminalOutcome": null`. **Note:** `FIXTURES.md` lists `terminalOutcome` as required. If a future fixture mandates the key be present-but-null, switch this to `JsonIgnoreCondition.Never`. For Phase 11A the current `extract-fixture` only emits null when `RunEnd` is missing (a draft state that the author is expected to fill in via the TODO list).
- `Encoder = UnsafeRelaxedJsonEscaping` — fixture descriptions and quest-file paths may contain apostrophes, em-dashes, and forward slashes; the default encoder over-escapes these into `'`, `—`, `\/`. Matches `QuestForgeJsonContext.QuestFileOptions` rationale (see `QuestForge.Schema`).

---

## 6. `OutputFormatters`

### 6.1 Purpose

Pure functions that turn library result objects into strings. No I/O; the shell calls these and writes the strings to stdout or stderr.

### 6.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Cli;

public static class OutputFormatters
{
    /// <summary>
    /// Format a FixtureValidationResult as one block per issue plus a summary line.
    /// Mirrors the qf-validate output style.
    /// </summary>
    public static string FormatIssues(FixtureValidationResult result);

    /// <summary>
    /// Format a list of TODO strings as a labelled block. Returns "" when todos is empty.
    /// </summary>
    public static string FormatTodos(IReadOnlyList<string> todos);

    /// <summary>
    /// Format a fixture list as a text table.
    /// </summary>
    public static string FormatFixtureList(IReadOnlyList<FixtureListEntry> entries, IReadOnlyList<string> gaps);

    /// <summary>
    /// Format a fixture list as a JSON array.
    /// </summary>
    public static string FormatFixtureListJson(IReadOnlyList<FixtureListEntry> entries, IReadOnlyList<string> gaps);
}
```

### 6.3 `FormatIssues` exact output

For each error:
```
ERROR    <code>  <message>
```
For each warning:
```
WARNING  <code>  <message>
```
Two-space separator between columns. Severity left-padded to 7 characters so `ERROR` and `WARNING` align.

Summary line follows after a blank line:
```
N error(s), M warning(s). Validation passed.
```
or
```
N error(s), M warning(s). Validation failed.
```

Trailing newline omitted (the shell prints with `Console.WriteLine`).

### 6.4 `FormatTodos` exact output

Empty list → empty string.

Non-empty list:
```
TODOs (human author must fill these in):
  - <todo 1>
  - <todo 2>
  ...
```

### 6.5 `FormatFixtureList` exact output (text mode)

Header row, then one row per fixture, columns separated by two-spaces of padding (no fixed-width — recompute based on max content width):
```
fixture                          quest                                          capabilities
simple-linear-acceptance.json    quests/arr/msq/66130-coming-to-uldah.json      [OK] step:travel, step:talk, predicate:playerNear, ...
with-dungeon.json                [MISSING] quests/.../000000.json               [WARN] step:duty
```

`[OK]` / `[MISSING]` prefix is on the quest column. Capabilities column joins the list with `", "`.

If `gaps` is non-empty, append a blank line then:
```
Gaps (uncovered capabilities):
  step:cutscene
  predicate:inCombat
```

### 6.6 `FormatFixtureListJson` exact output

```json
{
  "fixtures": [
    {
      "fixtureFile": "fixtures/engine/simple-linear-acceptance.json",
      "capabilities": ["step:travel", "step:talk", "..."],
      "questFile": "quests/arr/msq/66130-coming-to-uldah.json",
      "questFileExists": true
    }
  ],
  "gaps": ["step:cutscene", "predicate:inCombat"]
}
```

Use `FixtureModelSerializer.Options` (same camelCase + indent + relaxed-encoding).

### 6.7 Behaviours

**B-14 — `FormatIssues` with one error and one warning.**
Given `FixtureValidationResult { Errors = [{Code="fixture/quest-file-missing", Message="quest file not found: x.json"}], Warnings = [{Code="fixture/capability-extra", Message="capabilities list includes 'step:combat' but quest does not use it"}] }`,
When `FormatIssues(result)`,
Then output contains a line matching `ERROR    fixture/quest-file-missing  quest file not found: x.json`,
And a line matching `WARNING  fixture/capability-extra  capabilities list includes 'step:combat' but quest does not use it`,
And a final summary line `1 error(s), 1 warning(s). Validation failed.`.

**B-15 — `FormatIssues` clean result.**
Given `FixtureValidationResult` with empty `Errors` and empty `Warnings`,
When `FormatIssues`,
Then output is just the summary `0 error(s), 0 warning(s). Validation passed.`.

**B-16 — `FormatTodos` non-empty.**
Given `["name (Lumina lookup)", "expansion", "category"]`,
When `FormatTodos`,
Then output starts with `TODOs (human author must fill these in):` on its own line,
And contains lines `  - name (Lumina lookup)`, `  - expansion`, `  - category`.

**B-17 — `FormatTodos` empty.**
Given an empty list,
When `FormatTodos`,
Then returns `""`.

---

## 7. `ListFixturesCommand` minimal implementation

The Phase 10 library left `Enumerate` and `ComputeGaps` as `NotImplementedException`. Phase 11A fills them in so `qf-trace list-fixtures` works end-to-end. Two methods, mechanical, behind the same interface — no contract change.

### 7.1 `Enumerate`

1. `dir = Path.Combine(_questDataRoot, "fixtures", "engine")`. If it does not exist → return `[]`.
2. For each `*.json` file in `dir`:
   - Read the file; deserialize as `FixtureModel` (use `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`).
   - Compute relative `fixtureFile = "fixtures/engine/<filename>"` (forward slashes).
   - Compute `questFileExists = File.Exists(Path.Combine(_questDataRoot, fixture.QuestFile))`.
   - Yield `new FixtureListEntry(fixtureFile, fixture.Capabilities, questFileExists, fixture.QuestFile)`.
3. Files that fail to deserialize are skipped silently (logged via `Debug.WriteLine`).

### 7.2 `ComputeGaps`

1. Walk `Path.Combine(_questDataRoot, "quests")` recursively for `*.json` files.
2. For each, deserialize with `QuestForgeJsonContext.QuestFileOptions`; call `CapabilityInferrer.Infer`; union into `allQuestCaps`.
3. Compute `coveredCaps = fixtures.SelectMany(f => f.Capabilities).ToHashSet()`.
4. Return `allQuestCaps.Except(coveredCaps).OrderBy(c => c, StringComparer.Ordinal).ToList()`.

### 7.3 Note for the Tester

Phase 10 explicitly marked `ListFixtures` as "integration-only — no unit tests required". Phase 11A keeps that posture: the implementation lives in the library but is exercised only through the smoke test in §10.4.

---

## 8. `Program.cs` shell

### 8.1 Top-level flow

```
Main(args):
    cliArgs = CliArgsParser.Parse(args)

    if cliArgs.Subcommand == None or Help:
        print help to stdout
        return 0

    if cliArgs.Subcommand == Unknown:
        stderr: "qf-trace: unknown subcommand '<token>'. Run 'qf-trace --help' for usage."
        return 1

    if cliArgs.ParseError != null:
        stderr: "qf-trace: " + cliArgs.ParseError
        return 1

    resolvedRoot = cliArgs.QuestDataRoot ?? QuestDataRootResolver.Resolve()
    if cliArgs.QuestDataRoot == null:
        stderr: log resolution result (per §4.3)
    else:
        resolvedRoot = Path.GetFullPath(cliArgs.QuestDataRoot)

    switch cliArgs.Subcommand:
        ExtractFixture   → return RunExtractFixture(cliArgs, resolvedRoot)
        ValidateFixture  → return RunValidateFixture(cliArgs, resolvedRoot)
        ListFixtures     → return RunListFixtures(cliArgs, resolvedRoot)
        ExtractQuest     → return RunExtractQuest(cliArgs, resolvedRoot)
```

### 8.2 `RunExtractFixture`

```
if cliArgs.TracePath is null:
    stderr "extract-fixture requires <trace.jsonl>"; return 1
if not File.Exists(cliArgs.TracePath):
    stderr "trace file not found: <path>"; return 1

events    = TraceEventParser.ReadFile(cliArgs.TracePath, Console.Error)
extractor = new TraceToFixtureExtractor(resolvedRoot)
result    = extractor.Extract(events)

if result.IsFailure:
    stderr "qf-trace: <result.FailureReason>: <result.FailureDetail>"; return 1

fixture = result.Value
json    = FixtureModelSerializer.Serialize(fixture)

if cliArgs.Stdout:
    Console.Out.Write(json)
    return 0

outputPath = cliArgs.OutputPath ?? extractor.SuggestFilename(fixture)
File.WriteAllText(outputPath, json)
Console.Out.WriteLine($"Written to {outputPath}")
Console.Out.WriteLine()
Console.Out.WriteLine("TODO: edit the 'description' field before committing.")
return 0
```

### 8.3 `RunValidateFixture`

```
if cliArgs.FixturePath is null:
    stderr "validate-fixture requires <fixture.json>"; return 1
if not File.Exists(cliArgs.FixturePath):
    stderr "fixture file not found: <path>"; return 1
if resolvedRoot is null:
    stderr "validate-fixture requires --quest-data or a resolvable sibling"; return 1

result = new FixtureValidator(resolvedRoot).ValidateFile(cliArgs.FixturePath)
Console.Out.Write(OutputFormatters.FormatIssues(result))

if result.HasErrors: return 1
if result.Warnings.Count > 0 and cliArgs.FailOnWarning: return 2
return 0
```

### 8.4 `RunListFixtures`

```
if resolvedRoot is null:
    stderr "list-fixtures requires --quest-data or a resolvable sibling"; return 1

cmd      = new ListFixturesCommand(resolvedRoot)
entries  = cmd.Enumerate()
gaps     = cmd.ComputeGaps(entries)

if cliArgs.Format == "json":
    Console.Out.Write(OutputFormatters.FormatFixtureListJson(entries, gaps))
else:
    Console.Out.Write(OutputFormatters.FormatFixtureList(entries, gaps))
return 0
```

### 8.5 `RunExtractQuest`

```
if cliArgs.TracePath is null:
    stderr "extract-quest requires <trace.jsonl>"; return 1
if not File.Exists(cliArgs.TracePath):
    stderr "trace file not found: <path>"; return 1

events = TraceEventParser.ReadFile(cliArgs.TracePath, Console.Error)
result = new TraceToQuestExtractor().Extract(events)

if result.IsFailure:
    stderr "qf-trace: <result.FailureReason>: <result.FailureDetail>"; return 1

draft  = result.Value
json   = JsonSerializer.Serialize(draft.Definition, QuestForgeJsonContext.QuestFileOptions)
output = cliArgs.OutputPath ?? $"{draft.Definition.Id}-draft.json"

File.WriteAllText(output, json)
Console.Out.WriteLine($"Written to {output}")
Console.Out.WriteLine()
Console.Out.Write(OutputFormatters.FormatTodos(draft.Todos))
return 0
```

### 8.6 `--help` text

```
qf-trace <subcommand> [options]

Subcommands:
  extract-fixture <trace.jsonl> [--quest-data <dir>] [--out <path>] [--stdout]
    Convert a .jsonl trace into a fixture JSON draft.

  validate-fixture <fixture.json> [--quest-data <dir>] [--fail-on-warning]
    Cross-check a fixture against its referenced quest file.

  list-fixtures [--quest-data <dir>] [--format text|json]
    Enumerate fixtures and show capability coverage / gaps.

  extract-quest <trace.jsonl> [--quest-data <dir>] [--out <path>]
    Convert a .jsonl trace into a QuestDefinition draft.

Quest-data root:
  --quest-data <dir>          Path to the questforge-data checkout root.
                              If omitted, qf-trace probes ./quests/, then
                              ../questforge-data/quests/, in that order.

Exit codes:
  0    success / clean validation
  1    usage error, fatal error, or validation errors
  2    validation warnings only with --fail-on-warning
```

---

## 9. Exit code matrix

| Subcommand | Condition | Exit code |
|---|---|---|
| any | `--help`, `help`, or no args | `0` |
| any | unknown subcommand | `1` |
| any | parse error (unknown flag, missing flag value, extra positional) | `1` |
| any | required positional missing | `1` |
| any | input file does not exist | `1` |
| extract-fixture | extractor returns `Result.Failure` | `1` |
| extract-fixture | success | `0` |
| validate-fixture | result has errors | `1` |
| validate-fixture | result has warnings only, `--fail-on-warning` set | `2` |
| validate-fixture | result has warnings only, `--fail-on-warning` not set | `0` |
| validate-fixture | clean | `0` |
| validate-fixture | resolved root is null | `1` |
| list-fixtures | success | `0` |
| list-fixtures | resolved root is null | `1` |
| extract-quest | extractor returns `Result.Failure` | `1` |
| extract-quest | success | `0` |

---

## 10. Test scenarios

All tests live in `QuestForge.Tools.Trace.Tests/Cli/`. They exercise the testable units (`CliArgsParser`, `OutputFormatters`, `QuestDataRootResolver`) — **not** `Program.Main`. The shell itself is exercised via the §10.4 smoke test only.

### 10.1 `CliArgsTests`

**T-1 — `ParseArgs_ExtractFixture_AllArgs`**
Pre: `args = ["extract-fixture", "run.jsonl", "--quest-data", "./qd", "--out", "out.json"]`.
Call: `CliArgsParser.Parse(args)`.
Assert: `Subcommand == ExtractFixture`, `TracePath == "run.jsonl"`, `QuestDataRoot == "./qd"`, `OutputPath == "out.json"`, `Stdout == false`, `ParseError == null`.

**T-2 — `ParseArgs_ExtractFixture_Stdout`**
Pre: `args = ["extract-fixture", "run.jsonl", "--stdout"]`.
Call: `Parse`.
Assert: `Stdout == true`, `OutputPath == null`, `TracePath == "run.jsonl"`.

**T-3 — `ParseArgs_ValidateFixture_FailOnWarning`**
Pre: `args = ["validate-fixture", "f.json", "--fail-on-warning"]`.
Call: `Parse`.
Assert: `Subcommand == ValidateFixture`, `FixturePath == "f.json"`, `FailOnWarning == true`.

**T-4 — `ParseArgs_ListFixtures_JsonFormat`**
Pre: `args = ["list-fixtures", "--format", "json"]`.
Call: `Parse`.
Assert: `Subcommand == ListFixtures`, `Format == "json"`.

**T-5 — `ParseArgs_ExtractQuest_AllArgs`**
Pre: `args = ["extract-quest", "run.jsonl", "--quest-data", "./qd", "--out", "draft.json"]`.
Call: `Parse`.
Assert: `Subcommand == ExtractQuest`, `TracePath == "run.jsonl"`, `QuestDataRoot == "./qd"`, `OutputPath == "draft.json"`.

**T-6 — `ParseArgs_MissingSubcommand`**
Pre: `args = []`.
Call: `Parse`.
Assert: `Subcommand == None`, `TracePath == null`, `ParseError == null`.

**T-7 — `ParseArgs_UnknownSubcommand`**
Pre: `args = ["frob"]`.
Call: `Parse`.
Assert: `Subcommand == Unknown`, `UnknownToken == "frob"`.

### 10.2 `OutputFormattersTests`

**T-8 — `FormatIssues_ErrorsAndWarnings`**
Pre: a `FixtureValidationResult` with one `FixtureValidationIssue(Code: "fixture/quest-file-missing", Message: "quest file not found: x.json")` in `Errors` and one `FixtureValidationIssue(Code: "fixture/capability-extra", Message: "capabilities list includes 'step:combat' but quest does not use it")` in `Warnings`.
Call: `OutputFormatters.FormatIssues(result)`.
Assert: result contains the literal substring `"ERROR"` followed by `"fixture/quest-file-missing"` followed by `"quest file not found: x.json"`,
And contains `"WARNING"` followed by `"fixture/capability-extra"`,
And contains the summary `"1 error(s), 1 warning(s). Validation failed."`.

**T-9 — `FormatTodos_NonEmpty`**
Pre: `todos = ["name (Lumina lookup)", "expansion"]`.
Call: `OutputFormatters.FormatTodos(todos)`.
Assert: result starts with `"TODOs (human author must fill these in):"`,
And contains `"  - name (Lumina lookup)"`,
And contains `"  - expansion"`.

**T-11 — `FormatIssues_CleanResult`**
Pre: a `FixtureValidationResult` with empty `Errors` and empty `Warnings`.
Call: `OutputFormatters.FormatIssues(result)`.
Assert: result equals `"0 error(s), 0 warning(s). Validation passed."` (no issue lines, no blank line prefix).

**T-12 — `FormatTodos_Empty`**
Pre: `todos = []` (empty list).
Call: `OutputFormatters.FormatTodos(todos)`.
Assert: returns `""` (empty string, no output).

### 10.3 `QuestDataRootResolverTests`

**T-10 — `ResolveQuestDataRoot_FindsSibling`**
Pre: create temp directory layout:
  - `tmp/checkout/` (the "working directory")
  - `tmp/questforge-data/quests/` (the sibling)
Call: `QuestDataRootResolver.Resolve(Path.Combine(tmp, "checkout"))`.
Assert: returns `Path.GetFullPath(Path.Combine(tmp, "questforge-data"))`.
Cleanup: delete `tmp/`.

### 10.4 Smoke test (optional, integration-style)

A single test that spawns `dotnet run --project qf-trace -- --help` and asserts:
- Exit code is `0`.
- stdout contains the four subcommand names: `extract-fixture`, `validate-fixture`, `list-fixtures`, `extract-quest`.

This is permitted to be skipped on CI runners that cannot execute `dotnet run` from inside a test (mark with `[Trait("Category", "smoke")]`).

### 10.5 Estimated counts

- Happy paths: 5 (T-1 through T-5)
- Edge cases: 6 (T-6, T-7, T-9, T-10, T-11, T-12)
- Error / mixed: 1 (T-8)
- **Total: 12 tests** + 1 optional smoke test in §10.4.

---

## 11. Implementation order (for the Builder)

1. **Add types**: `CliSubcommand` enum, `CliArgs` record, empty `CliArgsParser.Parse` stub. Compile.
2. **Implement `CliArgsParser`** per §3.3. Run T-1 through T-7; iterate until green.
3. **Add `FixtureModelSerializer`** per §5.2. No tests yet — the format is asserted by the §10.4 byte-equality acceptance criterion in §12.
4. **Add `OutputFormatters`** per §6. Run T-8 and T-9.
5. **Add `QuestDataRootResolver`** per §4. Run T-10.
6. **Fill in `ListFixturesCommand.Enumerate` / `.ComputeGaps`** per §7. No new tests required.
7. **Rewrite `qf-trace/Program.cs`** per §8. Manual end-to-end smoke: run each subcommand against the committed quest 66130 trace.
8. **Acceptance verification** per §12.

---

## 12. Done criteria

- All 10 named tests in §10 pass.
- `qf-trace --help` lists all four subcommands with their arguments, exit code `0`.
- `qf-trace frob` exits `1` with `"qf-trace: unknown subcommand 'frob'..."` on stderr.
- `qf-trace extract-fixture <trace>` against the canonical quest 66130 trace, with no `--quest-data` flag and the cwd being the `questforge-tools` root (so the sibling `questforge-data` resolves), produces JSON byte-identical to the committed `fixtures/engine/simple-linear-acceptance.json` except for `description`.
- `qf-trace validate-fixture fixtures/engine/simple-linear-acceptance.json --quest-data ../questforge-data` exits `0` and prints `0 error(s), 0 warning(s). Validation passed.`.
- `qf-trace list-fixtures --quest-data ../questforge-data` prints the single committed fixture as `[OK]` and lists known uncovered capability tags as gaps.
- `qf-trace extract-quest <trace> --out 66130-draft.json` writes a file that re-deserialises via `QuestForgeJsonContext.QuestFileOptions` without throwing, and the printed TODO list includes at least `name`, `expansion`, `category`, `lastVerifiedPatch`, and `requirements`.
- Validation in CI (the `questforge-data` workflow) can replace `qf-validate` smoke with `qf-trace validate-fixture fixtures/engine/*.json` and still pass.

---

## READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.
- Happy paths: 5 scenarios (T-1, T-2, T-3, T-4, T-5)
- Edge cases: 4 scenarios (T-6, T-7, T-9, T-10)
- Error / mixed: 1 scenario (T-8)
- Expected total: 12 tests, optionally +1 smoke test in §10.4 → 13
