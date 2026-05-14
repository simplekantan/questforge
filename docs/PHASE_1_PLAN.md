# Phase 1 Implementation Plan: Schema Validator + CI

**Status:** ready to implement
**Input docs:** docs/SCHEMA.md (full spec), docs/NEXT_STEPS.md §Phase 1
**Output:** broken PR → CI red. Fixed PR → CI green.
**Architect review:** incorporated (2026-05-14)

---

## Dependency graph

Three repos, strict build order:

```
1. questforge (this repo)
   └── QuestForge.Schema  ← C# types; no Dalamud dependency
       └── consumed by ↓

2. questforge-tools (new repo)
   ├── QuestForge.Schema (copied for Phase 1; NuGet in Phase 3)
   └── qf-validate CLI  ← structural validator

3. questforge-data (new repo)
   └── quests/, fragments/  ← validated by qf-validate in CI
```

**Build order:** Schema types first → tools repo with validator → data repo with CI.

---

## Architectural decisions (read before coding)

### Records vs classes for the Step hierarchy

Leaf value types (`Position3`, `DialogueChoice`, `NpcLocation`, etc.) use `record`. The `Step` base type and all 20 concrete step subtypes use **`class`**, not `record`. Reasons:

1. Positional records require every subtype to re-declare all inherited constructor parameters (9 fields on `Step` × 20 subtypes = 180 repeated declarations). Classes avoid this with properties.
2. `[JsonPolymorphic]` on a record base type has subtleties with source-gen ordering that classes avoid.
3. Mutability for default-value properties is cleaner with classes.

```csharp
// CORRECT — Step is a class
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public ExpectValue? Expect { get; init; }
    // ... etc
}

public class TravelStep : Step
{
    public TravelDestination Destination { get; init; } = default!;
    public RouteHint? RouteHint { get; init; }
}

// CORRECT — value types use record
public record Position3(float X, float Y, float Z);
public record DialogueChoice(string Type, string? Prompt = null, string? Answer = null);
```

### ValidationContext carries metadata only — scope lives in StructuralValidator

`ValidationContext` is immutable metadata (file path, quest ID). The scope stack (sequence number, branch depth) lives as a private field inside `StructuralValidator`. This prevents the scope state from leaking between validators in the pipeline.

```csharp
public record ValidationContext(string FilePath, uint QuestId);

// Scope tracking is private to StructuralValidator
private readonly Stack<ValidationScope> _scopes = new();
```

### StructuralValidator does exactly two passes

Pass 1 (pre-pass): Walk the entire step tree, collect `Dictionary<string, ValidationScope>` (step ID → scope). This is also where duplicate IDs are detected.

Pass 2 (validation pass): Walk again, validate all rules. `goto` target resolution and cross-branch checks use the pre-pass map.

This is intentional and must be implemented in this order. Tests for goto rules depend on the pre-pass having run.

### QuestLoader returns a homogeneous type through the pipeline

`QuestLoader` returns `(QuestDefinition? Quest, IReadOnlyList<ValidationError> Errors)` so everything in the pipeline speaks `IEnumerable<ValidationError>`. No `Result<T>` type needed.

```csharp
public static (QuestDefinition? Quest, IReadOnlyList<ValidationError> Errors) LoadQuest(string filePath)
```

If `Errors` is non-empty, `Quest` may be null (parse failure) or non-null (successfully parsed but with structural errors appended by validators). The CLI aggregates errors from all files.

### CLI accepts a root directory

```
qf-validate [rootDir] [--format text|json] [--fail-on-warning]
```

The CLI discovers `<rootDir>/quests/**/*.json` and `<rootDir>/fragments/**/*.json` automatically. The `rootDir` defaults to the current directory. This matches the GitHub Actions invocation: `qf-validate . --format json`.

---

## Task 1 — `questforge` repo: add QuestForge.Schema project

### 1.1 Repo skeleton changes

Add to `QuestForge.sln`:
- `QuestForge.Schema/` — schema types, `net10.0`, no Dalamud dependency
- `Directory.Build.props` — shared MSBuild properties
- `.editorconfig` — code style

**`Directory.Build.props`:**
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

**`QuestForge.Schema/QuestForge.Schema.csproj`:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

### 1.2 C# type definitions

#### 1.2.1 Root source-gen context

Register every type that participates in serialization. Missing registrations cause silent runtime failure at the point of first use, not at compile time.

```csharp
[JsonSerializable(typeof(QuestDefinition))]
[JsonSerializable(typeof(FragmentDefinition))]
[JsonSerializable(typeof(TravelStep))]
[JsonSerializable(typeof(TalkStep))]
[JsonSerializable(typeof(InteractObjectStep))]
[JsonSerializable(typeof(PickupItemStep))]
[JsonSerializable(typeof(AcceptStep))]
[JsonSerializable(typeof(TurnInStep))]
[JsonSerializable(typeof(CombatStep))]
[JsonSerializable(typeof(DutyStep))]
[JsonSerializable(typeof(CutsceneStep))]
[JsonSerializable(typeof(SayChatMessageStep))]
[JsonSerializable(typeof(UseEmoteStep))]
[JsonSerializable(typeof(UseItemStep))]
[JsonSerializable(typeof(UseActionStep))]
[JsonSerializable(typeof(EquipGearForQuestStep))]
[JsonSerializable(typeof(EquipBestGearStep))]
[JsonSerializable(typeof(ChangeJobStep))]
[JsonSerializable(typeof(MinigameStep))]
[JsonSerializable(typeof(AwaitUserStep))]
[JsonSerializable(typeof(BranchStep))]
[JsonSerializable(typeof(FragmentStep))]
[JsonSerializable(typeof(RetryRecoverAction))]
[JsonSerializable(typeof(GotoRecoverAction))]
[JsonSerializable(typeof(UseReturnRecoverAction))]
[JsonSerializable(typeof(UseTeleportRecoverAction))]
[JsonSerializable(typeof(AwaitUserRecoverAction))]
[JsonSerializable(typeof(AbandonRecoverAction))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
internal partial class QuestForgeJsonContext : JsonSerializerContext { }
```

**Mandatory gate:** write a round-trip serialization test for every step type before writing any validator tests. This gate catches registration gaps that would otherwise fail at runtime.

#### 1.2.2 Shared value types (records)

```csharp
public record Position3(float X, float Y, float Z);
public record NpcLocation(uint NpcId, int Zone, Position3 Position);
public record TravelDestination(int Zone, Position3? Position = null, uint? AetheryteId = null);
public record RouteHint(string? Aetheryte = null, string[]? Aethernet = null);
public record DialogueChoice(string Type, string? Prompt = null, string? Answer = null);
// Type: "list"|"yesno"|"talk" — validated structurally
public record DutyTrigger(string Kind, int Zone, Position3 Position,
    uint? NpcId = null, uint? InteractableId = null);
public record BranchCase(string When, Step[] Steps = default!);
public record ChainNext(string When, uint? QuestId);
public record PrerequisiteRef(uint QuestId, string State);  // "complete"|"accepted"
public record FragmentParameter(string Name, string Type, bool Required = true);
public record RetryConfig(int? MaxAttempts = null, int? Timeout = null, string? Backoff = null);
public record Preconditions(int? MinGearCondition = null);
public record RewardOverride(string Strategy, uint? ItemId = null);
```

#### 1.2.3 QuestDefinition (root)

```csharp
public class QuestDefinition
{
    public string SchemaVersion { get; init; } = default!;
    public uint Id { get; init; }
    public string Name { get; init; } = default!;
    public string Expansion { get; init; } = default!;   // "arr"|"heavensward"|...
    public string Category { get; init; } = default!;    // "msq"|"class"|"job"
    public bool Enabled { get; init; } = true;
    public SupportStatus? SupportStatus { get; init; }   // required — validator checks non-null
    public string? LastVerifiedPatch { get; init; }      // required — validator checks non-null
    public Requirements? Requirements { get; init; }     // required — validator checks non-null
    public NpcLocation? AcceptFrom { get; init; }        // required — validator checks non-null
    public Chain? Chain { get; init; }
    public RewardOverride? RewardOverride { get; init; }
    public string[]? Contributors { get; init; }
    public string? Notes { get; init; }
    public QuestSequence[] Sequences { get; init; } = [];
}
```

**Why required fields are nullable in C#:** Making them nullable lets deserialization succeed even when absent, giving the validator a chance to emit a clean `structural/required-field-missing` error. Non-nullable required fields would throw `JsonException` first (caught as `structural/json-parse-error`), producing worse user messages. The validator explicitly null-checks each required field.

**Null-guard pattern for arrays:** `foreach (var seq in quest.Sequences)` — `Sequences` defaults to `[]` so no null-check needed. For arrays using `default!` elsewhere, use `foreach (var item in arr ?? [])`.

#### 1.2.4 Supporting metadata classes

```csharp
public class SupportStatus
{
    public string Implementation { get; init; } = default!;  // "complete"|"partial"|"none"
    public string[] KnownIssues { get; init; } = [];
    public bool? MinigameSkippable { get; init; }  // computed by validator; authors may set or omit
}

public class Requirements
{
    public int? MinLevel { get; init; }
    public int? MaxLevel { get; init; }
    public string? RequiredJob { get; init; }
    public string? RequiredStartingClass { get; init; }
    public PrerequisiteRef[] Prereqs { get; init; } = [];
}

public class Chain
{
    public uint[] Previous { get; init; } = [];
    public ChainNext[] Next { get; init; } = [];
}

public class QuestSequence
{
    public int Sequence { get; init; }
    public string? SkipIf { get; init; }
    public Step[] Steps { get; init; } = [];
}
```

#### 1.2.5 Step base class and subtypes

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TravelStep),            "travel")]
[JsonDerivedType(typeof(TalkStep),              "talk")]
[JsonDerivedType(typeof(InteractObjectStep),    "interact-object")]
[JsonDerivedType(typeof(PickupItemStep),        "pickup-item")]
[JsonDerivedType(typeof(AcceptStep),            "accept")]
[JsonDerivedType(typeof(TurnInStep),            "turn-in")]
[JsonDerivedType(typeof(CombatStep),            "combat")]
[JsonDerivedType(typeof(DutyStep),              "duty")]
[JsonDerivedType(typeof(CutsceneStep),          "cutscene")]
[JsonDerivedType(typeof(SayChatMessageStep),    "say-chat-message")]
[JsonDerivedType(typeof(UseEmoteStep),          "use-emote")]
[JsonDerivedType(typeof(UseItemStep),           "use-item")]
[JsonDerivedType(typeof(UseActionStep),         "use-action")]
[JsonDerivedType(typeof(EquipGearForQuestStep), "equip-gear-for-quest")]
[JsonDerivedType(typeof(EquipBestGearStep),     "equip-best-gear")]
[JsonDerivedType(typeof(ChangeJobStep),         "change-job")]
[JsonDerivedType(typeof(MinigameStep),          "minigame")]
[JsonDerivedType(typeof(AwaitUserStep),         "await-user")]
[JsonDerivedType(typeof(BranchStep),            "branch")]
[JsonDerivedType(typeof(FragmentStep),          "fragment")]
public class Step
{
    public string Id { get; init; } = default!;
    public string? Zone { get; init; }
    public ExpectValue? Expect { get; init; }
    public ExpectValue? SkipIf { get; init; }
    public float? StopDistance { get; init; }
    public RecoverConfig? Recover { get; init; }
    public RetryConfig? Retry { get; init; }
    public Preconditions? Preconditions { get; init; }
    public string? Notes { get; init; }
}

public class TravelStep : Step
{
    public TravelDestination Destination { get; init; } = default!;
    public RouteHint? RouteHint { get; init; }
}

public class TalkStep : Step
{
    public NpcLocation? Target { get; init; }
    public NpcLocation[]? Targets { get; init; }
    public string? TargetOrder { get; init; }           // "sequential"|"any"|"nearest-first"
    public DialogueChoice[] DialogueChoices { get; init; } = [];
}

public class DutyStep : Step
{
    public string Kind { get; init; } = default!;       // "regular"|"spd"
    public uint? DutyId { get; init; }
    public NpcLocation? EntryNpc { get; init; }
    public DutyTrigger? Trigger { get; init; }
    public string? FallbackOverride { get; init; }
}

public class BranchStep : Step
{
    public BranchCase[] Branches { get; init; } = [];
}

public class FragmentStep : Step
{
    public string Ref { get; init; } = default!;
    public Dictionary<string, JsonElement>? Params { get; init; }
}

public class CutsceneStep : Step
{
    public string Skip { get; init; } = "ifAllowed";    // "never"|"ifAllowed"
}

public class MinigameStep : Step
{
    public string Kind { get; init; } = default!;       // "sniping"|"memory"|"aiming"|...
    public string Skip { get; init; } = "ifAllowed";    // "never"|"ifAllowed"|"always"
}

public class AwaitUserStep : Step
{
    public string Reason { get; init; } = default!;     // ≤200 chars
}

// Remaining types: InteractObjectStep, PickupItemStep, AcceptStep, TurnInStep,
// CombatStep, SayChatMessageStep, UseEmoteStep, UseItemStep, UseActionStep,
// EquipGearForQuestStep, EquipBestGearStep, ChangeJobStep — follow the same pattern.
```

#### 1.2.6 ExpectValue — custom JSON converter

```csharp
[JsonConverter(typeof(ExpectValueConverter))]
public abstract class ExpectValue { }

public class PredicateExpect : ExpectValue { public string Predicate { get; init; } = default!; }
public class AllExpect : ExpectValue       { public string[] All { get; init; } = []; }
public class AnyExpect : ExpectValue       { public string[] Any { get; init; } = []; }
```

`ExpectValueConverter` dispatches on the JSON token:
- String token → `PredicateExpect`
- Object with `"all"` key → `AllExpect`
- Object with `"any"` key → `AnyExpect`
- `null` token → `null` (field is nullable)
- Anything else → `JsonException` with a message describing the valid forms

Read path and write path must both be implemented (write path serializes `PredicateExpect` as a bare string, structured forms as objects).

**Implementation sequencing:** `ExpectValueConverter` must be implemented and fully tested *before* any `Step` subtypes are written. You cannot write step deserialization tests until the converter works.

#### 1.2.7 Recovery types

```csharp
public class RecoverConfig
{
    public RecoverAction? OnTimeout { get; init; }
    public RecoverAction? OnObstacle { get; init; }
    public RecoverAction? OnAdapterError { get; init; }
    public RecoverAction? OnPostconditionFailed { get; init; }
    public RecoverAction? OnPlayerDefeated { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(RetryRecoverAction),       "retry")]
[JsonDerivedType(typeof(GotoRecoverAction),        "goto")]
[JsonDerivedType(typeof(UseReturnRecoverAction),   "useReturn")]
[JsonDerivedType(typeof(UseTeleportRecoverAction), "useTeleport")]
[JsonDerivedType(typeof(AwaitUserRecoverAction),   "awaitUser")]
[JsonDerivedType(typeof(AbandonRecoverAction),     "abandon")]
public abstract class RecoverAction { }

public class RetryRecoverAction       : RecoverAction { public int? MaxAttempts { get; init; } public string? Backoff { get; init; } }
public class GotoRecoverAction        : RecoverAction { public string StepId { get; init; } = default!; }
public class UseReturnRecoverAction   : RecoverAction { public bool ThenRetry { get; init; } }
public class UseTeleportRecoverAction : RecoverAction { public uint AetheryteId { get; init; } public bool ThenRetry { get; init; } }
public class AwaitUserRecoverAction   : RecoverAction { public string Reason { get; init; } = default!; }
public class AbandonRecoverAction     : RecoverAction { }
```

#### 1.2.8 FragmentDefinition

```csharp
public class FragmentDefinition
{
    public string SchemaVersion { get; init; } = default!;
    public string FragmentId { get; init; } = default!;
    public FragmentParameter[] Parameters { get; init; } = [];
    public Step[] Steps { get; init; } = [];
}
```

---

## Task 2 — `questforge-tools` repo: qf-validate CLI

### 2.1 Repo structure

```
questforge-tools/
  questforge-tools.sln
  Directory.Build.props
  QuestForge.Schema/               ← copied from questforge repo (Phase 1); NuGet in Phase 3
  QuestForge.Tools.Validator/      ← validation logic
  QuestForge.Tools.Validator.Tests/← xUnit tests
  qf-validate/                     ← CLI entry point
```

### 2.2 `IFragmentRegistry`

```csharp
public interface IFragmentRegistry
{
    bool TryGetFragment(string fragmentRef, out FragmentDefinition? fragment);
    IReadOnlyCollection<string> AllFragmentRefs { get; }
}

public class InMemoryFragmentRegistry(IEnumerable<FragmentDefinition> fragments) : IFragmentRegistry
{
    private readonly Dictionary<string, FragmentDefinition> _map =
        fragments.ToDictionary(f => f.FragmentId);

    public bool TryGetFragment(string fragmentRef, out FragmentDefinition? fragment)
        => _map.TryGetValue(fragmentRef, out fragment);

    public IReadOnlyCollection<string> AllFragmentRefs => _map.Keys;
}

// File-backed for the CLI
public class FileFragmentRegistry : IFragmentRegistry
{
    // Loads all *.json files under <rootDir>/fragments/ on construction
}
```

### 2.3 Validation types

```csharp
public record ValidationContext(string FilePath, uint QuestId);

public record ValidationError(
    string Code,
    string Message,
    string FilePath,
    string Location,    // e.g. "seq:1/branch:fight-or-flee/case:0"
    string? StepId = null,
    Severity Severity = Severity.Error
);

public enum Severity { Error, Warning }
```

### 2.4 Validator architecture

```csharp
public interface IValidator
{
    IEnumerable<ValidationError> Validate(QuestDefinition quest, ValidationContext ctx);
}

public class StructuralValidator(IFragmentRegistry fragments) : IValidator
{
    // Pass 1: build step-ID → scope map (also detects duplicates)
    // Pass 2: validate all rules using the map from pass 1
    // Scope stack is private — not shared with other validators
    private readonly Stack<ValidationScope> _scopes = new();

    public IEnumerable<ValidationError> Validate(QuestDefinition quest, ValidationContext ctx)
    {
        var (idMap, duplicates) = BuildStepIdMap(quest);  // Pass 1
        return ValidateAllRules(quest, ctx, idMap, duplicates);  // Pass 2
    }
}

// Push/Pop must always be paired with try/finally
// Example pattern used throughout StructuralValidator:
//   _scopes.Push(new ValidationScope(seq.Sequence));
//   try { /* validate steps */ }
//   finally { _scopes.Pop(); }

public class ValidatorPipeline(IEnumerable<IValidator> validators)
{
    public IEnumerable<ValidationError> Validate(QuestDefinition quest, ValidationContext ctx)
        => validators.SelectMany(v => v.Validate(quest, ctx));
}
```

### 2.5 QuestLoader — homogeneous return type

```csharp
public static class QuestLoader
{
    public static (QuestDefinition? Quest, IReadOnlyList<ValidationError> Errors)
        LoadQuest(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var quest = JsonSerializer.Deserialize(json, QuestForgeJsonContext.Default.QuestDefinition);
            if (quest is null)
                return (null, [new ValidationError("structural/json-null",
                    "File produced null on deserialization", filePath, "root")]);
            return (quest, []);
        }
        catch (JsonException ex)
        {
            return (null, [new ValidationError("structural/json-parse-error",
                $"JSON parse failure: {ex.Message}", filePath, $"line:{ex.LineNumber}")]);
        }
    }
}
```

The CLI aggregates across files:
```csharp
var allErrors = new List<ValidationError>();
foreach (var file in questFiles)
{
    var (quest, loadErrors) = QuestLoader.LoadQuest(file);
    allErrors.AddRange(loadErrors);
    if (quest is not null)
        allErrors.AddRange(pipeline.Validate(quest, new ValidationContext(file, quest.Id)));
}
```

### 2.6 Structural validation rules (Phase 1 — complete list)

Each rule is its own private method in `StructuralValidator`.

#### Required field checks

| Rule | Code |
|---|---|
| `schemaVersion` non-null and non-empty | `structural/required-field-missing` |
| `supportStatus` non-null | `structural/required-field-missing` |
| `lastVerifiedPatch` non-null and non-empty | `structural/required-field-missing` |
| `requirements` non-null | `structural/required-field-missing` |
| `acceptFrom` non-null | `structural/required-field-missing` |
| `sequences` non-empty | `structural/sequences-empty` |

#### Sequence rules

| Rule | Code |
|---|---|
| At least one sequence with `sequence: 0` | `structural/sequence-zero-missing` |
| Sequence numbers strictly increasing | `structural/sequence-not-increasing` |
| No duplicate sequence numbers | `structural/sequence-duplicate` |

*Sequence 255 is a convention for the turn-in sequence — not enforced.*

#### Step ID rules (built during Pass 1)

| Rule | Code |
|---|---|
| Step IDs match `^[a-z][a-z0-9-]*$` at all nesting levels | `structural/step-id-invalid-format` |
| Step IDs globally unique within the quest (all nesting levels) | `structural/step-id-duplicate` |

#### Recovery rules (Pass 2, uses Pass 1 map)

| Rule | Code |
|---|---|
| `goto` targets an existing step ID | `structural/recovery-goto-unresolved` |
| `goto` stays within the same scope boundary | `structural/recovery-goto-cross-branch` |
| `awaitUser` reason ≤ 200 characters | `structural/recover-reason-too-long` |

**goto suppression:** if a step ID triggered `structural/step-id-duplicate`, suppress both goto rules for that ID.

#### Branch rules

| Rule | Code |
|---|---|
| Branch sub-sequences have ≥ 1 step | `structural/branch-empty` |
| Branch nesting depth ≤ 3; warn at depth ≥ 2 | `structural/branch-nesting-too-deep` |
| Last `BranchCase.When` is `"default"` | `structural/branch-missing-default` |

#### Fragment rules

| Rule | Code | Suppressed when |
|---|---|---|
| Fragment reference resolves in registry | `structural/fragment-not-found` | — |
| Fragment required params provided | `structural/fragment-missing-param` | fragment-not-found |
| Fragment param values match declared types | `structural/fragment-param-type-mismatch` | fragment-not-found |
| Fragment files cannot reference other fragments | `structural/fragment-nested` | — |

#### Step-type-specific rules

| Rule | Code |
|---|---|
| `target` and `targets` mutually exclusive | `structural/step-target-conflict` |
| `duty` `kind: "regular"` requires `dutyId` and `entryNpc` | `structural/duty-missing-required-field` |
| `duty` `kind: "spd"` requires `trigger`, no `dutyId` | `structural/duty-invalid-field-for-kind` |
| `use-item` `target.kind` matches provided fields | `structural/use-item-target-mismatch` |
| `dialogueChoices[*].type` is `"list"`, `"yesno"`, or `"talk"` | `structural/dialogue-choice-type-invalid` |
| `yesno` `answer` is `"yes"` or `"no"` | `structural/dialogue-choice-answer-invalid` |
| `cutscene.skip` is `"never"` or `"ifAllowed"` | `structural/cutscene-skip-invalid` |
| `minigame.kind` is a known value | `structural/minigame-kind-invalid` |
| `requirements.prereqs[*].state` is `"complete"` or `"accepted"` | `structural/prereq-state-invalid` |
| Last `chain.next` entry is `"when": "default"` or quest is terminus | `structural/chain-missing-default` |
| `chain.next[*].when` is non-empty | `structural/chain-when-empty` |

#### Notes/length rules

| Rule | Code |
|---|---|
| Quest `notes` ≤ 500 chars | `structural/notes-too-long` |
| Step `notes` ≤ 500 chars | `structural/notes-too-long` |

#### minigameSkippable

Not a validation rule. The validator computes this from step contents and overwrites any author-provided value. No error emitted.

#### Deferred to Phase 2+

- §8.2 Game-data references (Lumina)
- §8.3 Chain bidirectionality, DAG cycle detection
- §8.4 Predicate validity (Phase 2 parser)
- §8.5 Goto infinite loop detection
- §8.6 Patch verification warnings
- §8.7 Schema version compatibility

### 2.7 CLI

```
qf-validate [rootDir] [--format text|json] [--fail-on-warning]

  rootDir   Root of the quest data repo. Discovers quests/**/*.json and
            fragments/**/*.json automatically. Defaults to current directory.

Exit codes: 0 = clean, 1 = errors, 2 = warnings with --fail-on-warning
```

**Text output:**
```
ERROR  quests/arr/msq/65657-close-to-home.json  seq:0
  [structural/step-id-duplicate] Step ID "talk-to-baderon" is not unique

WARNING  quests/arr/msq/65657-close-to-home.json  seq:1/branch:fight-or-flee/case:0
  [structural/branch-nesting-too-deep] Branch nesting depth 2 approaches limit of 3

2 error(s), 1 warning(s). Validation failed.
```

**JSON output (`--format json`):**
```json
{
  "results": [
    {
      "code": "structural/step-id-duplicate",
      "message": "Step ID \"talk-to-baderon\" is not unique",
      "file": "quests/arr/msq/65657-close-to-home.json",
      "location": "seq:0",
      "stepId": "talk-to-baderon",
      "severity": "error"
    }
  ],
  "summary": { "errors": 2, "warnings": 1 }
}
```

---

## Task 3 — `questforge-data` repo

### 3.1 Directory layout

```
questforge-data/
  README.md
  quests/
    arr/
      msq/
        66104-close-to-home-gladiator.json   ← first quest file (verify NPC IDs in-game)
      class/
    heavensward/ stormblood/ shadowbringers/ endwalker/ dawntrail/
  fragments/
    travel/
    common/
  .github/workflows/validate.yml
```

**Note:** `acceptFrom.npcId` for "Close to Home" (66104) must be verified in-game — Wymond (1003987) was the NPC for "Coming to Ul'dah" (66130) and may differ for this quest.

### 3.2 GitHub Actions workflow

Phase 1 uses `dotnet run` + submodule. NuGet publishing is Phase 3.

```yaml
name: Validate quest data

on:
  pull_request:
    paths: ['quests/**', 'fragments/**']

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: true

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'

      - name: Validate
        run: dotnet run --project questforge-tools/qf-validate -- . --format json > results.json
        continue-on-error: true

      - name: Annotate PR
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const { results, summary } = JSON.parse(fs.readFileSync('results.json', 'utf8'));
            for (const err of results) {
              const fn = err.severity === 'error' ? core.error : core.warning;
              fn(err.message, { file: err.file, title: `[${err.code}]` });
            }
            if (summary.errors > 0) core.setFailed(`${summary.errors} error(s)`);
```

---

## Task 4 — Given-When-Then specifications

### 4.1 `ExpectValueConverter`

Given `"questSequence(65) >= 3"` → `PredicateExpect { Predicate = "questSequence(65) >= 3" }`

Given `{"all": ["questFlag(65, 1)", "questFlag(65, 2)"]}` → `AllExpect { All = [...] }`

Given `{"any": ["questSequence(65) >= 3"]}` → `AnyExpect { Any = [...] }`

Given `null` token → returns null

Given `{"foo": ["x"]}` → `JsonException` mentioning "Expected 'all' or 'any'"

Given `{"all": ["a"], "any": ["b"]}` → `JsonException` indicating ambiguous form

Given `["questFlag(65, 1)"]` (array token) → `JsonException`

Given `{}` (empty object) → `JsonException`

Given `{"all": [42, "questFlag(65, 1)"]}` → `JsonException` (non-string element)

### 4.2 `StructuralValidator` — hard cases

**Duplicate ID suppresses goto rules:**
Given: two steps with id `"talk-a"` in seq 0; third step gotos `"talk-a"`.
Then: one `structural/step-id-duplicate`. NO goto errors.

**goto cross-branch (invalid):**
Given: `"inner-step"` in branch-case-0 gotos `"outer-step"` which is in seq 0 (outer scope).
Then: one `structural/recovery-goto-cross-branch` (StepId=`"inner-step"`). NO goto-unresolved.

**goto within same branch (valid):**
Given: `"step-a"` in branch-case-0 gotos `"step-b"` also in branch-case-0.
Then: no errors.

**fragment-not-found suppresses param checks:**
Given: FragmentStep refs `"travel/nonexistent"` (not in registry), provides params.
Then: one `structural/fragment-not-found`. NO param errors.

**duty kind validation:**
Given: DutyStep `kind: "regular"`, `DutyId = null` → one `structural/duty-missing-required-field`.
Given: DutyStep `kind: "spd"`, `DutyId = 56` → one `structural/duty-invalid-field-for-kind`.

**branch nesting:**
Given: depth 2 → one WARNING `structural/branch-nesting-too-deep`.
Given: depth 4 → one ERROR `structural/branch-nesting-too-deep`.

**notes boundary:**
Given: 500-char notes → no error. Given: 501-char notes → one `structural/notes-too-long`.

**sequence ordering:**
Given: `[0, 5, 3, 255]` → one `structural/sequence-not-increasing` for pair (5,3).
Given: `[0, 5, 255]` (gap OK) → no sequence errors.
Given: `[0, 0, 255]` → one `structural/sequence-duplicate` AND one `structural/sequence-not-increasing`.
Given: `[1, 255]` (no seq 0) → one `structural/sequence-zero-missing`.

**target + targets conflict:**
Given: TalkStep with both Target and Targets → one `structural/step-target-conflict`.

---

## Task 5 — Done criteria

1. PR to `questforge-data` with deliberate duplicate step ID
2. CI fails → inline annotation `[structural/step-id-duplicate]`
3. Fix in follow-up commit → CI passes
4. Merge

---

## Implementation order

**Phase A — Types**
1. Define `ExpectValue` hierarchy (no converter yet)
2. Write and pass `ExpectValueConverter` tests (§4.1)
3. Implement `ExpectValueConverter`
4. Define all `Step` subtypes and schema types
5. Write round-trip serialization test for every step type ← **mandatory gate**
6. All round-trip tests green before proceeding

**Phase B — Validator (TDD)**
1. Commit `IFragmentRegistry` + `InMemoryFragmentRegistry`
2. Commit `ValidationContext` record
3. Commit `QuestLoader` with tuple return
4. Write tests for every rule (§4.2 + §2.6)
5. Implement `StructuralValidator` (Pass 1 then Pass 2) rule by rule
6. Wire into `ValidatorPipeline` with stub validators

**Phase C — CLI + CI**
1. Implement `qf-validate` (argument parsing, output, exit codes)
2. Integration tests: subprocess invocation, verify exit codes and JSON output
3. Create `questforge-data` repo with directory structure
4. Wire GitHub Actions (dotnet run + submodule)
5. Prove CI red → CI green

---

## What Phase 1 does NOT include

- Predicate parser (Phase 2)
- Lumina game-data validation (Phase 2+)
- Chain bidirectionality (Phase 2+)
- Goto infinite loop detection (Phase 2+)
- JSON Schema generation
- Engine projects (`QuestForge.Engine`, etc.) — Phase 3+
- NuGet publishing — Phase 3
- Dalamud plugin — Phase 6