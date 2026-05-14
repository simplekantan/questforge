# Phase 1 Implementation Plan: Schema Validator + CI

**Status:** ready to implement
**Input docs:** SCHEMA.md (full spec), NEXT_STEPS.md §Phase 1
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
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="*" />
  </ItemGroup>
</Project>
```

### 1.2 C# type definitions

All types use `record` (immutable) with source-generated System.Text.Json serialization.

#### 1.2.1 Root source-gen context

Register every type that participates in serialization — including all derived step and recover types. Missing registrations cause silent runtime failure, not compile errors.

```csharp
[JsonSerializable(typeof(QuestDefinition))]
[JsonSerializable(typeof(FragmentDefinition))]
// Explicit registration for all derived Step types (required for source-gen + JsonPolymorphic)
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
// Explicit registration for all derived RecoverAction types
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

**Mandatory gate:** before writing any validator tests, write a round-trip serialization test for every step type. If a type isn't correctly registered, the test fails at this gate — not mid-validator-development.

#### 1.2.2 Shared types

```csharp
public record Position3(float X, float Y, float Z);

public record NpcLocation(uint NpcId, int Zone, Position3 Position);
```

#### 1.2.3 QuestDefinition (root)

```csharp
public record QuestDefinition(
    string SchemaVersion,
    uint Id,
    string Name,
    string Expansion,        // "arr"|"heavensward"|"stormblood"|"shadowbringers"|"endwalker"|"dawntrail"
    string Category,         // "msq"|"class"|"job"
    bool Enabled,
    SupportStatus SupportStatus,         // required per schema
    string LastVerifiedPatch,            // required per schema
    Requirements Requirements,           // required per schema
    NpcLocation AcceptFrom,              // required per schema
    Chain? Chain = null,
    RewardOverride? RewardOverride = null,
    string[]? Contributors = null,
    string? Notes = null,
    QuestSequence[] Sequences = default! // see null-guard note below
);
```

**Note on `default!` arrays:** `default!` suppresses the nullable warning but the runtime value is `null` if the JSON key is absent. Every validator code path that iterates these arrays must null-check first. Failure to do so produces `NullReferenceException` instead of a clean validation error. Pattern: `foreach (var seq in quest.Sequences ?? [])`.

**Note on required fields:** `SupportStatus`, `LastVerifiedPatch`, `Requirements`, `AcceptFrom` are `required: yes` in the schema table (SCHEMA.md §2.1). They are non-nullable in the C# record but may arrive as null if absent from JSON. The validator must check these for null explicitly and emit `structural/required-field-missing` for each.

#### 1.2.4 Supporting metadata types

```csharp
public record SupportStatus(
    string Implementation,       // "complete"|"partial"|"none"
    string[] KnownIssues,
    bool? MinigameSkippable = null  // computed by validator from step contents; authors may omit or set
                                    // validator always recomputes and overwrites — never an error
);

public record Requirements(
    int? MinLevel = null,
    int? MaxLevel = null,
    string? RequiredJob = null,
    string? RequiredStartingClass = null,
    PrerequisiteRef[] Prereqs = default!
);

public record PrerequisiteRef(uint QuestId, string State);  // "complete"|"accepted"

public record Chain(
    uint[] Previous,
    ChainNext[] Next         // non-nullable; null if JSON key absent — must null-guard
);

public record ChainNext(string When, uint? QuestId);

public record RewardOverride(string Strategy, uint? ItemId = null);
```

#### 1.2.5 Sequences and steps

```csharp
public record QuestSequence(
    int Sequence,
    string? SkipIf = null,
    Step[] Steps = default!   // null-guard required
);
```

#### 1.2.6 Step — polymorphic discriminated union

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
public abstract record Step(
    string Id,
    string? Zone = null,
    ExpectValue? Expect = null,
    ExpectValue? SkipIf = null,
    float? StopDistance = null,
    RecoverConfig? Recover = null,
    RetryConfig? Retry = null,
    Preconditions? Preconditions = null,
    string? Notes = null
);
```

#### 1.2.7 ExpectValue — string OR structured (requires custom converter)

`expect` is either a bare string predicate or `{"all":[...]}` / `{"any":[...]}`.

```csharp
[JsonConverter(typeof(ExpectValueConverter))]
public abstract record ExpectValue;

public record PredicateExpect(string Predicate) : ExpectValue;
public record AllExpect(string[] All)           : ExpectValue;
public record AnyExpect(string[] Any)           : ExpectValue;
```

**`ExpectValueConverter` must handle all failure cases explicitly** (see Given-When-Then in §3):
- Unknown object key → `JsonException` with message naming the invalid keys
- Object with both `all` and `any` → `JsonException`
- Array token → `JsonException`
- Empty object `{}` → `JsonException`
- `all`/`any` array containing non-strings → `JsonException`
- `null` token → return null (field is `ExpectValue?`)

**Implementation sequencing note:** `ExpectValueConverter` must be implemented and fully tested *before* any `Step` subtypes are defined with `ExpectValue?` fields. You cannot write step deserialization tests until the converter works.

#### 1.2.8 Concrete step types

```csharp
public record TravelStep(
    string Id,
    TravelDestination Destination,
    RouteHint? RouteHint = null,
    // + inherited Step fields
) : Step(Id);

public record TravelDestination(int Zone, Position3? Position = null, uint? AetheryteId = null);
public record RouteHint(string? Aetheryte = null, string[]? Aethernet = null);

public record TalkStep(
    string Id,
    NpcLocation? Target = null,
    NpcLocation[]? Targets = null,
    string? TargetOrder = null,          // "sequential"|"any"|"nearest-first"
    DialogueChoice[] DialogueChoices = default!,
    // + inherited Step fields
) : Step(Id);

public record DialogueChoice(
    string Type,                         // "list"|"yesno"|"talk" — validated structurally
    string? Prompt = null,
    string? Answer = null
);

public record DutyStep(
    string Id,
    string Kind,                         // "regular"|"spd" — required; no default
    uint? DutyId = null,
    NpcLocation? EntryNpc = null,
    DutyTrigger? Trigger = null,
    string? FallbackOverride = null,
    // + inherited Step fields
) : Step(Id);

public record DutyTrigger(
    string Kind,                         // "npc"|"object"
    int Zone,
    Position3 Position,
    uint? NpcId = null,
    uint? InteractableId = null
);

public record BranchStep(
    string Id,
    BranchCase[] Branches,               // last entry's When must be "default"
    // + inherited Step fields
) : Step(Id);

public record BranchCase(string When, Step[] Steps);

public record FragmentStep(
    string Id,
    string Ref,
    Dictionary<string, JsonElement>? Params = null,
    // + inherited Step fields
) : Step(Id);

public record CutsceneStep(
    string Id,
    string Skip = "ifAllowed",           // "never"|"ifAllowed"
    // + inherited Step fields
) : Step(Id);

public record MinigameStep(
    string Id,
    string Kind,                         // "sniping"|"memory"|"aiming"|"rhythm"|"selection"|"other"
    string Skip = "ifAllowed",           // "never"|"ifAllowed"|"always"
    // + inherited Step fields
) : Step(Id);

public record AwaitUserStep(
    string Id,
    string Reason,                       // ≤200 chars
    // + inherited Step fields
) : Step(Id);

// Remaining step types follow the same pattern (InteractObjectStep, PickupItemStep,
// AcceptStep, TurnInStep, CombatStep, SayChatMessageStep, UseEmoteStep,
// UseItemStep, UseActionStep, EquipGearForQuestStep, EquipBestGearStep, ChangeJobStep)
```

**SCHEMA.md documentation bug (§10):** The worked example has a `duty` step with no `kind` field. This is incorrect — `Kind` is required. The example should have `"kind": "regular"`. Fix the example when writing the first quest file.

#### 1.2.9 Recovery types

```csharp
public record RecoverConfig(
    RecoverAction? OnTimeout = null,
    RecoverAction? OnObstacle = null,
    RecoverAction? OnAdapterError = null,
    RecoverAction? OnPostconditionFailed = null,
    RecoverAction? OnPlayerDefeated = null
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(RetryRecoverAction),       "retry")]
[JsonDerivedType(typeof(GotoRecoverAction),        "goto")]
[JsonDerivedType(typeof(UseReturnRecoverAction),   "useReturn")]
[JsonDerivedType(typeof(UseTeleportRecoverAction), "useTeleport")]
[JsonDerivedType(typeof(AwaitUserRecoverAction),   "awaitUser")]
[JsonDerivedType(typeof(AbandonRecoverAction),     "abandon")]
public abstract record RecoverAction;

public record RetryRecoverAction(int? MaxAttempts = null, string? Backoff = null)    : RecoverAction;
public record GotoRecoverAction(string StepId)                                       : RecoverAction;
public record UseReturnRecoverAction(bool ThenRetry = false)                         : RecoverAction;
public record UseTeleportRecoverAction(uint AetheryteId, bool ThenRetry = false)     : RecoverAction;
public record AwaitUserRecoverAction(string Reason)                                  : RecoverAction;
public record AbandonRecoverAction                                                   : RecoverAction;

public record RetryConfig(int? MaxAttempts = null, int? Timeout = null, string? Backoff = null);
public record Preconditions(int? MinGearCondition = null);
```

#### 1.2.10 FragmentDefinition

```csharp
public record FragmentDefinition(
    string SchemaVersion,
    string FragmentId,
    FragmentParameter[] Parameters,
    Step[] Steps
);

public record FragmentParameter(
    string Name,
    string Type,             // "position"|"npcId"|"itemId"|"string"
    bool Required = true
);
```

---

## Task 2 — `questforge-tools` repo: qf-validate CLI

### 2.1 Repo structure

```
questforge-tools/
  questforge-tools.sln
  Directory.Build.props
  QuestForge.Schema/           ← copied from questforge repo (Phase 1); NuGet ref in Phase 3
  QuestForge.Tools.Validator/  ← validation logic (testable library)
  qf-validate/                 ← CLI entry point (console app)
  QuestForge.Tools.Tests/      ← xUnit tests
```

### 2.2 `IFragmentRegistry` — required interface before writing validator tests

Fragment validation cannot be implemented without a corpus. `StructuralValidator` needs access to all fragment files to resolve `fragment-not-found` and validate parameters. This is injected at construction time:

```csharp
public interface IFragmentRegistry
{
    bool TryGetFragment(string fragmentRef, out FragmentDefinition? fragment);
    IReadOnlyCollection<string> AllFragmentRefs { get; }
}

// In-memory implementation for tests
public class InMemoryFragmentRegistry : IFragmentRegistry
{
    private readonly Dictionary<string, FragmentDefinition> _fragments;
    public InMemoryFragmentRegistry(IEnumerable<FragmentDefinition> fragments) { ... }
    public bool TryGetFragment(string fragmentRef, out FragmentDefinition? fragment) { ... }
    public IReadOnlyCollection<string> AllFragmentRefs => _fragments.Keys;
}

// File-backed implementation for the CLI
public class FileFragmentRegistry : IFragmentRegistry
{
    // Loads all .json files from the fragments/ directory on construction
}
```

**This interface must be designed and committed before any validator tests are written.** Tests that exercise fragment rules depend on `InMemoryFragmentRegistry`.

### 2.3 `ValidationContext` — scope tracking design

Every validation error must carry the step's location in the quest tree. This requires tracking scope during recursive descent:

```csharp
public class ValidationContext
{
    public string FilePath { get; }
    public uint QuestId { get; }

    // Scope stack: (sequenceNumber, branchStepId?, branchCaseIndex?)
    private readonly Stack<ValidationScope> _scopes = new();

    public void PushSequence(int sequenceNumber) { ... }
    public void PushBranch(string branchStepId, int caseIndex) { ... }
    public void Pop() { ... }

    public ValidationScope CurrentScope => _scopes.TryPeek(out var s) ? s : ValidationScope.Root;

    // Builds a human-readable location string: "seq:1/branch:fight-or-flee/case:0"
    public string Location => string.Join("/", _scopes.Select(s => s.ToString()));
}

public record ValidationScope(int SequenceNumber, string? BranchStepId = null, int? BranchCaseIndex = null);
```

`goto` cross-branch detection works by comparing the scope of the step containing the `goto` with the scope of the target step ID. If the scopes differ (different `BranchStepId` or the target is in the outer sequence), the goto is cross-branch.

### 2.4 Validator architecture

```
QuestForge.Tools.Validator/
  ValidationError.cs         — record + Severity enum
  ValidationContext.cs       — scope tracking
  IFragmentRegistry.cs       — fragment corpus interface
  IValidator.cs              — Validate(QuestDefinition, ValidationContext) → IEnumerable<ValidationError>
  Validators/
    StructuralValidator.cs   ← Phase 1 (§8.1 rules)
    GameDataValidator.cs     ← Phase 2+ stub (returns empty)
    PredicateValidator.cs    ← Phase 2 stub (returns empty)
  ValidatorPipeline.cs       — composes validators, aggregates results
```

```csharp
public interface IValidator
{
    IEnumerable<ValidationError> Validate(QuestDefinition quest, ValidationContext ctx);
}

public record ValidationError(
    string Code,
    string Message,
    string FilePath,
    string Location,         // from ValidationContext (e.g., "seq:1/branch:fight-or-flee/case:0")
    string? StepId = null,
    Severity Severity = Severity.Error
);

public enum Severity { Error, Warning }
```

### 2.5 Deserialization error handling

Deserialization failures (wrong token type, missing required field, unknown `type` discriminator) throw `JsonException` before the validator runs. The CLI must catch these and report them as structured errors — not unhandled exceptions:

```csharp
public static class QuestLoader
{
    public static Result<QuestDefinition> LoadQuest(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var quest = JsonSerializer.Deserialize(json, QuestForgeJsonContext.Default.QuestDefinition);
            return quest is null
                ? Result.Error(new ValidationError("structural/json-null", "File produced null on deserialization", filePath, "root"))
                : Result.Ok(quest);
        }
        catch (JsonException ex)
        {
            return Result.Error(new ValidationError(
                "structural/json-parse-error",
                $"JSON parse failure: {ex.Message}",
                filePath,
                $"line:{ex.LineNumber}"));
        }
    }
}
```

Deserialization errors are reported alongside, not instead of, validation errors. A file with both a parse error and a structural issue (possible in partially-valid JSON) reports both.

### 2.6 Structural validation rules (Phase 1 — complete list)

Each rule is its own private method in `StructuralValidator` for independent testability.

#### Required field checks

| Rule | Code |
|---|---|
| `schemaVersion` present and non-empty | `structural/required-field-missing` |
| `supportStatus` present | `structural/required-field-missing` |
| `lastVerifiedPatch` present and non-empty | `structural/required-field-missing` |
| `requirements` present | `structural/required-field-missing` |
| `acceptFrom` present | `structural/required-field-missing` |
| `sequences` non-null and non-empty | `structural/sequences-empty` |

#### Sequence rules

| Rule | Code |
|---|---|
| At least one sequence with `sequence: 0` | `structural/sequence-zero-missing` |
| Sequence numbers strictly increasing (adjacent pair check) | `structural/sequence-not-increasing` |
| No duplicate sequence numbers | `structural/sequence-duplicate` |

*Note: sequence 255 being the turn-in sequence is a convention only — not enforced.*

#### Step ID rules

| Rule | Code |
|---|---|
| Step IDs match `^[a-z][a-z0-9-]*$` (all steps, including branch sub-steps) | `structural/step-id-invalid-format` |
| Step IDs unique across entire quest (all levels of nesting) | `structural/step-id-duplicate` |

*Step IDs must be globally unique within a quest — this includes steps nested inside branch sub-sequences at all depths.*

#### Recovery rules

| Rule | Code |
|---|---|
| Recovery `goto` targets an existing step ID | `structural/recovery-goto-unresolved` |
| Recovery `goto` does not cross scope boundary (same sequence, same branch sub-sequence) | `structural/recovery-goto-cross-branch` |
| `awaitUser` reason ≤ 200 characters | `structural/recover-reason-too-long` |

**Implementation note for goto rules:** Build a complete `Dictionary<string, ValidationScope>` (step ID → scope) in a pre-pass before evaluating any `goto` targets. This pre-pass also enables duplicate-ID detection. Goto checks must be suppressed for any step ID that is duplicated (to avoid spurious `goto-unresolved` errors when the ID exists but is ambiguous).

#### Branch rules

| Rule | Code |
|---|---|
| Branch sub-sequences have ≥ 1 step | `structural/branch-empty` |
| Branch nesting depth ≤ 3; warn at depth ≥ 2 | `structural/branch-nesting-too-deep` |
| Last `BranchCase.When` is `"default"` | `structural/branch-missing-default` |

#### Fragment rules

| Rule | Code | Dependency |
|---|---|---|
| Fragment reference resolves in registry | `structural/fragment-not-found` | IFragmentRegistry |
| Fragment required params are provided | `structural/fragment-missing-param` | Suppressed if fragment-not-found |
| Fragment param values match declared types | `structural/fragment-param-type-mismatch` | Suppressed if fragment-not-found |
| No nested fragments (fragment files cannot `ref` other fragments) | `structural/fragment-nested` | |

#### Step-type-specific rules

| Rule | Code |
|---|---|
| `target` and `targets` mutually exclusive | `structural/step-target-conflict` |
| `duty` with `kind: "regular"` requires `dutyId` and `entryNpc` | `structural/duty-missing-required-field` |
| `duty` with `kind: "spd"` requires `trigger`, must not have `dutyId` | `structural/duty-invalid-field-for-kind` |
| `use-item` `target.kind` matches the fields provided | `structural/use-item-target-mismatch` |
| `dialogueChoices[*].type` is `"list"`, `"yesno"`, or `"talk"` | `structural/dialogue-choice-type-invalid` |
| `yesno` choice `answer` is `"yes"` or `"no"` | `structural/dialogue-choice-answer-invalid` |
| `cutscene.skip` is `"never"` or `"ifAllowed"` | `structural/cutscene-skip-invalid` |
| `minigame.kind` is a known value | `structural/minigame-kind-invalid` |
| `requirements.prereqs[*].state` is `"complete"` or `"accepted"` | `structural/prereq-state-invalid` |
| `chain.next` last entry is `"when": "default"` (or quest is terminus) | `structural/chain-missing-default` |
| `chain.next[*].when` is non-empty string | `structural/chain-when-empty` |

#### Notes rules

| Rule | Code |
|---|---|
| Quest `notes` ≤ 500 characters | `structural/notes-too-long` |
| Step `notes` ≤ 500 characters | `structural/notes-too-long` |
| Fragment `notes` (if added) ≤ 500 characters | `structural/notes-too-long` |

#### minigameSkippable

Not a validation rule. The validator computes this field from step contents and overwrites any author-provided value. No error is emitted regardless of what the author writes.

#### Error suppression dependency graph

Some rules must be suppressed when upstream rules fire:
- `structural/recovery-goto-unresolved` and `structural/recovery-goto-cross-branch` are suppressed for any step ID that triggered `structural/step-id-duplicate`
- `structural/fragment-missing-param` and `structural/fragment-param-type-mismatch` are suppressed for any fragment ref that triggered `structural/fragment-not-found`
- Branch depth errors should suppress branch-scope goto checks for the oversized branch (redundant errors)

#### Deferred to Phase 2+

Do NOT implement in Phase 1:
- §8.2 Game-data references (Lumina required)
- §8.3 Chain `previous`/`next` bidirectionality (requires loading corpus)
- §8.3 Prerequisite DAG cycle detection
- §8.4 Predicate validity (Phase 2 parser)
- §8.5 Recovery `goto` infinite loop detection (graph analysis)
- §8.6 Patch verification warnings
- §8.7 Schema version compatibility check

### 2.7 CLI entry point (`qf-validate`)

```
qf-validate [path] [--format text|json] [--fail-on-warning]

Arguments:
  path    Directory (validates all .json files recursively) or single file.
          Defaults to current directory.

Exit codes:
  0   All files valid (no errors, no warnings — or warnings present but --fail-on-warning not set)
  1   One or more errors
  2   One or more warnings with --fail-on-warning
```

**Text output:**
```
ERROR  quests/arr/msq/65657-close-to-home.json  seq:0
  [structural/step-id-duplicate] Step ID "talk-to-baderon" is not unique

WARNING  quests/arr/msq/65657-close-to-home.json  seq:1/branch:fight-or-flee/case:0
  [structural/branch-nesting-too-deep] Branch nesting depth 2 approaches limit of 3

2 error(s), 1 warning(s). Validation failed.
```

**JSON output** (`--format json`):
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

## Task 3 — `questforge-data` repo: placeholder structure

### 3.1 Directory layout

```
questforge-data/
  README.md
  quests/
    arr/
      msq/
        66104-close-to-home-gladiator.json   ← first real quest file
      class/
    heavensward/
    stormblood/
    shadowbringers/
    endwalker/
    dawntrail/
  fragments/
    travel/
    common/
  .github/
    workflows/
      validate.yml
```

### 3.2 First quest file

Write `66104-close-to-home-gladiator.json` (Gladiator variant of "Close to Home", Ul'dah). This is a short quest: accept from Wymond → visit Gladiators' Guild → turn in. Use actual values from the spike (`acceptFrom.npcId: 1003987`, zone 182 for the new-player instance).

### 3.3 GitHub Actions workflow

**Phase 1 approach: `dotnet run` via submodule.** Do not publish a NuGet package as a prerequisite — that adds scope and will stall Phase 1. Use a checked-out `questforge-tools` submodule and `dotnet run` directly. Switch to the global tool approach when the NuGet feed is set up in Phase 3.

```yaml
name: Validate quest data

on:
  pull_request:
    paths:
      - 'quests/**'
      - 'fragments/**'

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: true   # questforge-tools as a submodule

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'

      - name: Validate
        run: |
          dotnet run --project questforge-tools/qf-validate -- quests/ fragments/ --format json > results.json
        continue-on-error: true

      - name: Annotate PR
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const results = JSON.parse(fs.readFileSync('results.json', 'utf8'));
            for (const err of results.results) {
              if (err.severity === 'error') {
                core.error(err.message, { file: err.file, title: `[${err.code}]` });
              } else {
                core.warning(err.message, { file: err.file, title: `[${err.code}]` });
              }
            }
            if (results.summary.errors > 0)
              core.setFailed(`${results.summary.errors} validation error(s)`);
```

---

## Task 4 — Given-When-Then specifications

### 4.1 `ExpectValueConverter`

**Happy paths:**

Given JSON `"questSequence(65) >= 3"` (string token) → `PredicateExpect { Predicate = "questSequence(65) >= 3" }`

Given JSON `{"all": ["questFlag(65, 1)", "questFlag(65, 2)"]}` → `AllExpect { All = ["questFlag(65, 1)", "questFlag(65, 2)"] }`

Given JSON `{"any": ["questSequence(65) >= 3", "questFlag(65, 5)"]}` → `AnyExpect { Any = ["questSequence(65) >= 3", "questFlag(65, 5)"] }`

Given JSON `null` → returns null (field is `ExpectValue?`)

**Error cases:**

Given JSON `{"foo": ["something"]}` → `JsonException` with message containing "Expected 'all' or 'any'"

Given JSON `{"all": ["a"], "any": ["b"]}` → `JsonException` indicating ambiguous form

Given JSON `["questFlag(65, 1)"]` (array token) → `JsonException` indicating arrays are not valid

Given JSON `{}` (empty object) → `JsonException` indicating missing `all` or `any` key

Given JSON `{"all": [42, "questFlag(65, 1)"]}` → `JsonException` indicating array elements must be strings

---

### 4.2 `StructuralValidator` — hard cases

**Step ID duplicate suppresses goto check:**

Given: sequence 0 has two steps both with id `"talk-a"`, and a third step has `recover.onObstacle: {action: "goto", stepId: "talk-a"}`

Then: exactly one `structural/step-id-duplicate` for `"talk-a"`. NO `structural/recovery-goto-unresolved`. NO `structural/recovery-goto-cross-branch`.

---

**goto cross-branch (invalid):**

Given: sequence 0 has outer step `"outer-step"`. Sequence 0 also has a branch step, and inside branch-case-0 is step `"inner-step"` with `recover.onTimeout: {action: "goto", stepId: "outer-step"}`.

Then: exactly one `structural/recovery-goto-cross-branch` with `StepId == "inner-step"`. NO `structural/recovery-goto-unresolved`.

---

**goto within same branch (valid):**

Given: branch-case-0 has steps `"step-a"` and `"step-b"`, where `"step-a"` has `recover.onTimeout: {action: "goto", stepId: "step-b"}`.

Then: no errors.

---

**fragment-not-found suppresses param checks:**

Given: a `FragmentStep` with `ref: "travel/nonexistent"` that is not in the registry, and provides `params: {finalPosition: {...}}`.

Then: exactly one `structural/fragment-not-found`. NO `structural/fragment-missing-param`. NO `structural/fragment-param-type-mismatch`.

---

**duty kind validation:**

Given: a `DutyStep` with `kind: "regular"` and no `dutyId` field (deserialized with `DutyId = null`).

Then: one `structural/duty-missing-required-field` identifying `dutyId`.

Given: a `DutyStep` with `kind: "spd"` and `dutyId: 56`.

Then: one `structural/duty-invalid-field-for-kind` identifying `dutyId`.

---

**branch nesting:**

Given: branch depth 2 (branch inside branch).

Then: one WARNING `structural/branch-nesting-too-deep` (not error).

Given: branch depth 4 (exceeds limit of 3).

Then: one ERROR `structural/branch-nesting-too-deep`.

---

**notes length boundary:**

Given: quest `notes` exactly 500 characters → no error.

Given: quest `notes` 501 characters → one `structural/notes-too-long`.

---

**sequence ordering:**

Given: sequences `[0, 5, 3, 255]` → one `structural/sequence-not-increasing` for pair (5, 3).

Given: sequences `[0, 5, 255]` (gap allowed) → no sequence errors.

Given: sequences `[0, 0, 255]` (duplicate) → one `structural/sequence-duplicate` for value 0 AND one `structural/sequence-not-increasing`.

---

**sequence: 0 required:**

Given: sequences `[1, 255]` (no sequence 0).

Then: one `structural/sequence-zero-missing`.

---

**target + targets conflict:**

Given: a `TalkStep` with both `Target` (single NPC) and `Targets` (array).

Then: one `structural/step-target-conflict` with that step's id.

---

## Task 5 — Done criteria

1. Open a PR to `questforge-data` adding a quest file with a deliberate error (e.g., duplicate step ID `"talk-a"` appearing twice)
2. CI runs `qf-validate` → exits 1 → PR check fails → inline annotation points at the offending line with `[structural/step-id-duplicate]`
3. Fix the error in a follow-up commit → CI reruns → exits 0 → PR check passes
4. Merge

---

## Implementation order within Phase 1

**Phase A: Types (in sub-order)**
1. Define `ExpectValue` abstract record (no converter yet)
2. Write `ExpectValueConverter` tests (from Given-When-Then §4.1)
3. Implement `ExpectValueConverter` until tests pass
4. Define all `Step` subtypes and remaining schema types
5. Write round-trip serialization tests for every step type — this is the source-gen gate
6. All round-trip tests must pass before proceeding to Phase B

**Phase B: Validator (TDD)**
1. Design and commit `IFragmentRegistry` and `InMemoryFragmentRegistry`
2. Design and commit `ValidationContext` with scope tracking
3. Specify `QuestLoader` deserialization error handling
4. Write tests for each structural rule (Given-When-Then §4.2, plus all rules in §2.6)
5. Implement `StructuralValidator` rule by rule until all tests pass
6. Wire `StructuralValidator` into `ValidatorPipeline` with stub validators

**Phase C: CLI + CI**
1. Implement `qf-validate` CLI (argument parsing, output formatting, exit codes)
2. Write integration tests: invoke CLI as subprocess, verify exit codes and output format
3. Create `questforge-data` repo with directory structure
4. Wire up GitHub Actions with `dotnet run` + submodule approach
5. Prove CI red (broken quest file) → CI green (fixed quest file)

---

## What Phase 1 explicitly does NOT include

- Predicate parser (Phase 2)
- Lumina game-data validation (Phase 2+)
- Chain bidirectionality across corpus (Phase 2+)
- Recovery goto infinite loop detection (Phase 2+)
- JSON Schema generation
- Engine projects (`QuestForge.Engine`, `QuestForge.Adapters`, etc.) — Phase 3+
- NuGet package publishing — Phase 3
- Dalamud plugin — Phase 6