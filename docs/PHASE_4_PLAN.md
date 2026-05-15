# Phase 4 Implementation Plan: Engine Skeleton (Travel + Talk)

**Status:** ready to implement
**Input docs:** docs/DESIGN.md §5 (engine architecture), docs/ARCHITECTURE.md (three-layer separation, HSM internals), docs/ADAPTERS.md §4–§10 (interface surfaces), docs/SCHEMA.md §3–§4.5 (sequence-grouped steps, travel, talk), docs/SPIKE_NOTES.md (quest 66130 ground truth), docs/NEXT_STEPS.md §Phase 4
**Output:** a unit test loads quest 66130 ("Coming to Ul'dah") from `questforge-data`, wires up fakes from `QuestForge.Adapters.Fakes`, runs the engine to completion, and verifies the expected sequence of `EngineAction` returns.
**Predates:** Phase 3 (`QuestForge.Adapters` + `QuestForge.Adapters.Fakes` + 105 tests, all done-criteria green).

---

## Dependency graph

`QuestForge.Engine` is currently an empty placeholder csproj. Phase 4 fills it. The engine becomes the third consumer of `QuestForge.Predicates` (after the validator and the engine-side tests).

```
questforge (this repo)
   QuestForge.Schema            ← Phase 1 (done)
   QuestForge.Adapters          ← Phase 3 (done): 10 interfaces, Result<T>, identifiers
   QuestForge.Adapters.Fakes    ← Phase 3 (done): in-memory test doubles for all 10
       │                          (engine tests consume these directly)
       ▼
   QuestForge.Engine            ← NEW Phase 4: HSM evaluator, PredicateEvaluator, EngineAction
       │
       ▼
   QuestForge.Engine.Tests      ← NEW Phase 4: xUnit, runs against fakes, no game

questforge-tools (separate repo, consumed via submodule)
   QuestForge.Predicates        ← Phase 2 (done): lexer, parser, PredicateChecker
       │                          (Engine references via ProjectReference through submodule)
       ▼
   referenced by QuestForge.Engine
```

**Build order:** submodule + project references → `EngineAction` + `QuestEngine` stub → tester writes failing tests → builder implements.

---

## Architectural decisions (read before coding)

### 1. `QuestForge.Predicates` is consumed via git submodule, not NuGet

`QuestForge.Predicates` lives in the `questforge-tools` repository. The engine needs it for runtime predicate evaluation, but NuGet publishing is not yet done (Phase 3 plan deferred NuGet to a later date; Phase 1's "Phase 3 NuGet timeline" reference was aspirational, not committed).

**Decision: add `questforge-tools` as a git submodule of `questforge`, then `<ProjectReference>` into the submodule.**

This matches the pattern `questforge-data` already uses for `questforge-tools` (submodule → ProjectReference into the validator). It is also the same shape Phase 1's `questforge-data` CI uses to invoke `qf-validate`. Consistent across all three repos.

**Commands run once during Phase 4 Phase A:**

```bash
# From the questforge root
git submodule add https://github.com/<owner>/questforge-tools.git questforge-tools
git submodule update --init --recursive
```

**`QuestForge.Engine.csproj` change:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\QuestForge.Schema\QuestForge.Schema.csproj" />
    <ProjectReference Include="..\QuestForge.Adapters\QuestForge.Adapters.csproj" />
    <ProjectReference Include="..\questforge-tools\QuestForge.Predicates\QuestForge.Predicates.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

**`QuestForge.sln` change:** add `questforge-tools\QuestForge.Predicates\QuestForge.Predicates.csproj` to the solution as an existing project. The schema validator project from the tools repo is NOT added — only `QuestForge.Predicates` is needed.

**CI implication (called out, not built in Phase 4):** when the plugin repo gets a CI workflow (no workflow exists today; one will appear in Phase 6 or 7), it must check out submodules:

```yaml
- uses: actions/checkout@v4
  with:
    submodules: true
```

Phase 4 itself ships no GitHub Actions workflow. Local `dotnet build` and `dotnet test` are the entire CI surface during this phase.

**No NuGet packaging in Phase 4.** When the NuGet timeline is set (post-Phase 7 is the working guess), the ProjectReference becomes a PackageReference and the submodule goes away. That migration is out of scope.

### 2. The engine returns `EngineAction`, never directly calls adapters

DESIGN.md §5.2 specifies the engine returns `Action | KeepObserving | Done` per tick. Phase 4 collapses this to a single discriminated union: `EngineAction`. The engine reads state, evaluates predicates, decides what step is next, and returns a record describing the action. **The plugin layer (Phase 6) is what actually calls `INavigator.NavigateTo` / `IInteractor.InteractWith`.**

This is the inverted-control boundary that lets the engine be replay-tested: a replay harness can read recorded observations from a trace, hand them to the engine, and compare the returned `EngineAction` to the recorded one — without anything ever moving the player.

```csharp
namespace QuestForge.Engine;

public abstract record EngineAction
{
    public sealed record Navigate(WorldPosition Destination, NavigationOptions Options) : EngineAction;
    public sealed record Interact(NpcId Target) : EngineAction;
    public sealed record Wait(string Reason) : EngineAction;
    public sealed record AwaitUser(string Reason) : EngineAction;
    public sealed record Done : EngineAction;
}
```

**`Wait` semantics (clarified):** travel and talk steps in Phase 4 do not produce a `Wait` state. The engine returns `Navigate` until `playerZone()` (or whatever the expect predicate is) becomes satisfied; on each tick the plugin reissues the navigate command and vnavmesh deduplicates. There is no engine-side "I already issued this — wait." Each tick is independent.

`Wait` exists in the discriminated union for completeness — Phase 5+ adds it for situations like "cutscene playing, do nothing until it ends" — but Phase 4 never returns it.

`AwaitUser` is returned only when the engine cannot proceed: the current quest sequence has no matching block, the current sequence number is unknown, or no step within the current sequence is actionable. The Reason string is diagnostic.

### 3. `QuestEngine` takes all 10 adapter interfaces in its constructor, even though Phase 4 uses only 5

ADAPTERS.md §14.1 (constructor surface) is fixed by the Phase 3 done-criteria test (`QuestForge.Adapters.Tests/Engine/QuestEngineConstructorTest.cs`, which currently fakes a constructor that will become real here). Adding adapters later means changing the signature and breaking that pin.

```csharp
public sealed class QuestEngine
{
    public QuestEngine(
        IGameStateProvider gameState,
        IQuestState questState,
        INavigator navigator,
        ITeleporter teleporter,
        IInteractor interactor,
        ICombat combat,
        IGearManager gear,
        IMinigameSkipper minigames,
        IDialogueResolver dialogue,
        ITimingProfile timing,
        ITraceWriter trace,
        ILogger<QuestEngine> logger)
    {
        // store all references; assert non-null
    }
}
```

**Phase 4 actively uses:** `IGameStateProvider`, `IQuestState`, `INavigator`, `IInteractor`, `ITimingProfile`.

**Phase 4 stores but never calls:** `ITeleporter`, `ICombat`, `IGearManager`, `IMinigameSkipper`, `IDialogueResolver`. They are constructor-injected, null-guarded, and dormant.

`ITraceWriter` is stored but not yet written to in Phase 4 — that is Phase 5. The reference is held so the Phase 5 trace integration is purely additive.

`ILogger<QuestEngine>` is `Microsoft.Extensions.Logging.Abstractions`. Tests pass `NullLogger<QuestEngine>.Instance`.

### 4. The HSM decision algorithm is stateless per tick

The engine does not track "I issued an action last tick, am I still waiting on it." Every `Tick()` call:

1. Reads `IQuestState.GetQuestSequence(questId)` to find the current sequence number.
2. Reads `IQuestState.IsQuestComplete(questId)` — if true, returns `Done`.
3. Finds the `QuestSequence` in the loaded `QuestDefinition` whose `Sequence` field matches the current number.
4. Evaluates `QuestSequence.SkipIf` (if non-null) — if true, **the engine cannot itself advance sequences** (sequences are advanced by the game in response to interactions, which the plugin layer executes). Skip-if at the sequence level becomes a Phase 7+ feature; in Phase 4 the engine logs and returns `AwaitUser("sequence skipped by skipIf — engine cannot self-advance in Phase 4")`. Tests that exercise sequence-level skipIf are out of scope; no quest in Phase 4's worked example uses it.
5. Walks steps in declared order:
   a. Evaluate `step.Expect`. If satisfied, advance to the next step.
   b. Evaluate `step.SkipIf` (if non-null). If satisfied, advance to the next step.
   c. Determine action for the step type (see §5 below) and **return immediately**.
6. If the walk falls off the end (every step's expect is satisfied) without the game having advanced the sequence — return `Wait("all steps in current sequence satisfied; awaiting game sequence advance")`. This handles the brief window between "I just talked to the turn-in NPC" and "the game has updated `questSequence` to a state where `isQuestComplete` is true."

**Critical:** the engine never advances quest state. Sequence transitions happen because the plugin called the adapter, the adapter called Dalamud, the game updated its state, and the next `IQuestState.GetQuestSequence` read returns a new value. The engine just observes.

This is the fundamental discipline from DESIGN.md §5.2: pure with respect to state, not time.

### 5. `PredicateEvaluator` lives in `QuestForge.Engine` and walks `PredicateAst` against fake state

`QuestForge.Predicates` already provides:

- `PredicateLexer` / `PredicateParser` — produces a `PredicateAst`
- `PredicateChecker` — type-checks an AST against the function registry
- `FunctionRegistry` — known state functions, arity, return types

What it does **not** provide: actual evaluation against live game state. The validator does not need that; it only checks "is this a valid predicate." The engine does.

**Phase 4 adds `QuestForge.Engine.Predicates.PredicateEvaluator`:**

```csharp
namespace QuestForge.Engine.Predicates;

public sealed class PredicateEvaluator
{
    private readonly IGameStateProvider _gameState;
    private readonly IQuestState _questState;

    public PredicateEvaluator(IGameStateProvider gameState, IQuestState questState)
    {
        _gameState = gameState;
        _questState = questState;
    }

    public async Task<bool> Evaluate(PredicateAst ast, CancellationToken ct);
    public Task<bool> Evaluate(ExpectValue expectValue, CancellationToken ct);
}
```

The second overload accepts a schema-level `ExpectValue` (`PredicateExpect | AllExpect | AnyExpect`), parses its string(s) into `PredicateAst` via `PredicateParser`, and dispatches to the AST evaluator. The convenience overload memoizes parsed ASTs per evaluator instance — predicate strings are re-evaluated on every tick, but parsed only once.

**Supported state functions in Phase 4 (the minimum the worked quest needs):**

| Function | Arity | Reads | Used by 66130? |
|---|---|---|---|
| `questSequence(questId)` | 1 | `IQuestState.GetQuestSequence` | yes |
| `isQuestAccepted(questId)` | 1 | `IQuestState.IsQuestAccepted` | implicit (engine precondition) |
| `isQuestComplete(questId)` | 1 | `IQuestState.IsQuestComplete` | yes (Done detection) |
| `questFlag(questId, bit)` | 2 | `IQuestState.IsQuestFlagSet` | reserved (not in 66130) |
| `playerZone()` | 0 | `IGameStateProvider.GetPlayerZone` | yes |
| `playerNear(position, radius)` | 2 | `IGameStateProvider.GetPlayerPosition` + Euclidean distance | optional (position-form only) |
| `playerInCombat()` | 0 | `IGameStateProvider.IsPlayerInCombat` | reserved |

Any other state function encountered in a Phase 4 quest produces an `EngineEvaluationException` (an *exception*, not a Result — this is a quest data bug, not a routine failure). The validator catches these at PR time; the engine asserts at runtime as a defense in depth.

**Comparison and logical operators** (`==`, `!=`, `>=`, `<=`, `>`, `<`, `and`, `or`, `not`, parenthesization) are handled by walking the AST. Integer comparisons compare integer values; boolean comparisons reduce identical truth values to `true`.

**`AllExpect` evaluates as all-true short-circuit AND. `AnyExpect` evaluates as any-true short-circuit OR.** Identical to the predicate-language `and`/`or` operators, but flattened — see SCHEMA.md §4.3.

**`default` keyword:** parsed as `PredicateAst.DefaultLiteral`, always evaluates to `true`. Phase 4 quest predicates do not use `default` (it appears only in `chain.next[].when`, which is out of scope), but the evaluator handles it for completeness.

### 6. The worked quest is 66130 "Coming to Ul'dah," from `questforge-data`

Phase 0's spike completed this quest end-to-end. SPIKE_NOTES.md pins:

| Constant | Value | Use in Phase 4 quest file |
|---|---|---|
| Quest ID | 66130 | `id: 66130` |
| Zone | 182 (NOT 130 — new-player instance) | `acceptFrom.zone: 182` |
| Wymond NPC ID | 1003987 | First talk target (`talk.target.npcId`) |
| Wymond position | (35.56, 4.0, -151.18) | Travel destination + `playerNear` literal in expect |
| Momodi NPC ID | 1003988 | Second talk target |
| Momodi position | (21.84, 7.0, -81.13) | Travel destination + `playerNear` literal in expect |
| Sequence progression | 0 → 255 (no intermediate) | Two `sequences` blocks |

The quest content (paraphrased, full JSON written during Phase 4 Phase A as a Phase-4 test fixture in `QuestForge.Engine.Tests/Fixtures/66130.json`):

```json
{
  "schemaVersion": "1.0.0",
  "id": 66130,
  "name": "Coming to Ul'dah",
  "expansion": "arr",
  "category": "msq",
  "supportStatus": { "implementation": "complete", "knownIssues": [] },
  "lastVerifiedPatch": "7.4",
  "requirements": { "minLevel": 1, "prereqs": [] },
  "acceptFrom": { "npcId": 1003987, "zone": 182, "position": {"x": 35.56, "y": 4.0, "z": -151.18} },
  "sequences": [
    {
      "sequence": 0,
      "steps": [
        {
          "type": "travel",
          "id": "travel-to-wymond",
          "destination": { "zone": 182, "position": {"x": 35.56, "y": 4.0, "z": -151.18} },
          "expect": "playerNear({\"x\":35.56,\"y\":4.0,\"z\":-151.18}, 3)"
        },
        {
          "type": "talk",
          "id": "talk-to-wymond",
          "target": { "npcId": 1003987, "zone": 182, "position": {"x": 35.56, "y": 4.0, "z": -151.18} },
          "expect": "questSequence(66130) >= 255"
        }
      ]
    },
    {
      "sequence": 255,
      "steps": [
        {
          "type": "travel",
          "id": "travel-to-momodi",
          "destination": { "zone": 182, "position": {"x": 21.84, "y": 7.0, "z": -81.13} },
          "expect": "playerNear({\"x\":21.84,\"y\":7.0,\"z\":-81.13}, 3)"
        },
        {
          "type": "talk",
          "id": "turn-in-to-momodi",
          "target": { "npcId": 1003988, "zone": 182, "position": {"x": 21.84, "y": 7.0, "z": -81.13} },
          "expect": "isQuestComplete(66130)"
        }
      ]
    }
  ]
}
```

**Note on step types:** schema-strict Phase 4 uses `talk` for the turn-in step, not `turn-in`. The Phase 4 engine handles only `travel` and `talk`. The `turn-in` step type exists in the schema (SCHEMA.md §4.7) but is unimplemented in Phase 4; using `talk` with `expect: "isQuestComplete(66130)"` is the documented Phase 4 workaround. SCHEMA.md classifies `turn-in` as semantically `talk` plus reward selection; Phase 4 quests with no reward selection collapse to bare `talk`.

The full quest file is generated during Phase A from this template and committed to `QuestForge.Engine.Tests/Fixtures/`. **It is a Phase 4 test fixture, not a real `questforge-data` contribution** — the engine-tests own this file. When `questforge-data` gets its real 66130.json (likely in Phase 6/7 with the canonical trace), the test fixture remains a separate copy with possibly stub-friendly tweaks. Authoring the canonical `questforge-data` file is Phase 7's job, not this phase's.

**Implication for the `acceptFrom` zone:** SPIKE_NOTES.md confirms the quest is accepted in zone 182 (new-player instance), not zone 130 (regular Ul'dah). The test fixture must use 182 throughout.

### 7. The engine never advances quest state — the fake's transitions are scripted via callbacks

Engine tests use `FakeInteractor` callbacks to simulate the game's response to interaction:

```csharp
// Setup: when the engine asks the plugin to InteractWith(Wymond), advance the fake's quest sequence
interactor.OnInteractQuest(QuestId(66130), () => {
    questState.SetQuestSequence(QuestId(66130), 255);
});
```

**But the engine in Phase 4 returns `EngineAction.Interact(Wymond)` — it does NOT call `interactor.InteractWith` itself.** So who calls the fake?

**The test harness, between ticks.** The test loop is:

```csharp
while (true) {
    var action = await engine.Tick(ct);
    switch (action) {
        case EngineAction.Navigate nav:
            await navigator.NavigateTo(nav.Destination, nav.Options, ct);  // updates fake state
            break;
        case EngineAction.Interact talk:
            await interactor.InteractWith(talk.Target, ct);  // fires the OnInteract callback → advances sequence
            break;
        case EngineAction.Done:
            return;
        case EngineAction.AwaitUser au:
            Assert.Fail($"unexpected AwaitUser: {au.Reason}");
            break;
    }
}
```

This is the Phase 4 stand-in for the Phase 6 plugin loop. The plugin layer in Phase 6 will execute the action against real Dalamud adapters; the test harness here executes it against fakes. The engine is identical in both worlds.

The `FakeInteractor` callback mechanism (`OnAcceptQuest`, `OnCompleteQuest` per Phase 3 fakes, plus new `OnInteractWith(NpcId, Action)` if needed) is how tests inject state transitions. Phase 4 may need to extend `FakeInteractor` with `OnInteractWith` — that is an additive Phase-3-fake extension, owned by Phase 4.

### 8. `Tick(CancellationToken)` is the single per-tick API

```csharp
public sealed class QuestEngine
{
    public async Task<EngineAction> Tick(CancellationToken ct);

    public void StartQuest(QuestDefinition quest);  // loads the quest; engine now tracks this quest
    // Phase 5 adds: SetTraceWriter, GetEngineConfig, BeginRun(runId), EndRun
}
```

- `StartQuest` is called once at the beginning. The engine validates that the schema version is supported and the quest passes basic shape checks (Phase 4 trusts the validator did its job; minimal asserts in this method).
- `Tick(ct)` is called repeatedly. Each call returns one `EngineAction`. The caller executes it (or doesn't, e.g., for `Done`/`AwaitUser`/`Wait`).
- Cancellation: every adapter call inside `Tick` flows the same `ct`. A cancelled `Tick` throws `OperationCanceledException`.

There is no `Run` method (no internal loop). Tick-driven inversion of control is what makes the engine fit cleanly inside both the eventual plugin frame loop and the unit-test harness.

### 9. Basic recovery: retry, no per-step override

NEXT_STEPS.md scope says "Basic recovery: retry on failure (no per-step override)." Phase 4's interpretation:

- If an adapter call inside the engine's evaluator path returns `Result<T>.Failure` (e.g., `GetQuestSequence` fails for some reason), the engine does NOT crash. It returns `AwaitUser($"adapter failure: {reason}")` and lets the next tick try again. The `MaxConsecutiveStepFailures` counter from DESIGN.md is **not** implemented in Phase 4 — that is Phase 7+.
- For action `Result` failures: those happen in the plugin layer's `await navigator.NavigateTo(...)` call, not inside the engine. If the navigator returns `NavigationOutcome.StoppedByObstacle`, the next tick re-reads state, sees the player is not yet at destination, and returns `Navigate` again. vnavmesh / fake-nav decide whether retry succeeds. This is the "stateless per-tick" discipline doing recovery work for us.
- One explicit retry test required in Phase 4: configure `FakeNavigator.ScriptNextResult(NavigationOutcome.StoppedByObstacle)` for the first call; verify the engine's *next* tick still returns `Navigate` (i.e., the engine treats the failure as "step not complete, try again next tick").

No retry counter, no backoff, no escalation. That all comes in Phase 7.

### 10. Trace recording is not in Phase 4

`ITraceWriter` is constructor-injected, stored, and unused. Phase 5 adds the recording proxy and the per-tick observation flush. Phase 4 engine code must not write to the trace, because the event types are not yet defined. Tests pass `NullTraceWriter` (a one-line implementation living in `QuestForge.Engine.Tests/Fakes/`).

### 11. No `EngineDecisionConfig` in Phase 4

DESIGN.md and ADAPTERS.md reference `EngineDecisionConfig` (timing profile name, duty fallback policy, reward strategy, failure counters, etc.). Phase 4 does not need any of these — `travel` and `talk` have no decision points that consult policy. The engine constructor takes no config object in Phase 4. The class is introduced when Phase 5 wires the trace recorder (which records the config in `run.start`).

### 12. Engine project must not reference Dalamud, even transitively

Test:

```bash
dotnet list QuestForge.Engine reference
```

must show only `QuestForge.Schema`, `QuestForge.Adapters`, `QuestForge.Predicates`, and `Microsoft.Extensions.Logging.Abstractions`. No `Dalamud`, no `vnavmesh`, no Lifestream. If a future change introduces such a reference, the engine has crossed the testability boundary and the architecture is wrong.

A CI gate that asserts this is Phase 6+; Phase 4 enforces it by code review and by the engine-tests project building without Dalamud installed.

---

## Task 1 — `questforge` repo: add submodule + engine project structure

### 1.1 Submodule

```bash
git submodule add https://github.com/<owner>/questforge-tools.git questforge-tools
git submodule update --init --recursive
git add .gitmodules questforge-tools
```

The maintainer fills in the actual URL; the placeholder above is for the plan.

**`.gitmodules` should look like:**

```
[submodule "questforge-tools"]
    path = questforge-tools
    url = https://github.com/<owner>/questforge-tools.git
```

### 1.2 Engine project structure

```
QuestForge.Engine/
  QuestForge.Engine.csproj
  EngineAction.cs                       ← discriminated union
  QuestEngine.cs                        ← public API
  Hsm/
    SequenceWalker.cs                   ← finds active step within current sequence
    StepActionResolver.cs               ← maps Step → EngineAction
  Predicates/
    PredicateEvaluator.cs               ← walks PredicateAst against live state
    ExpectEvaluator.cs                  ← evaluates ExpectValue (parse + cache + dispatch)
    UnknownStateFunctionException.cs    ← thrown on unsupported function
```

```
QuestForge.Engine.Tests/
  QuestForge.Engine.Tests.csproj        ← references Engine, Adapters, Adapters.Fakes
  Fakes/
    NullTraceWriter.cs                  ← ITraceWriter no-op
  Fixtures/
    66130.json                          ← worked-example quest definition (Phase 4 owned)
  EngineActionTests.cs                  ← record equality, exhaustive switch
  Predicates/
    PredicateEvaluatorTests.cs          ← happy path per supported function
  Engine/
    QuestEngineConstructorTests.cs      ← all 10 adapters required, null guards
    Quest66130FlowTests.cs              ← the worked quest end-to-end
    QuestResumeTests.cs                 ← starting mid-quest
    RecoveryTests.cs                    ← single retry test
```

### 1.3 `QuestForge.Engine.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\QuestForge.Engine\QuestForge.Engine.csproj" />
    <ProjectReference Include="..\QuestForge.Adapters\QuestForge.Adapters.csproj" />
    <ProjectReference Include="..\QuestForge.Adapters.Fakes\QuestForge.Adapters.Fakes.csproj" />
    <ProjectReference Include="..\QuestForge.Schema\QuestForge.Schema.csproj" />
    <PackageReference Include="xunit" Version="2.9.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Fixtures\66130.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

### 1.4 `QuestForge.sln` updates

Add to the solution:
- `QuestForge.Engine.Tests/QuestForge.Engine.Tests.csproj` (new)
- `questforge-tools\QuestForge.Predicates\QuestForge.Predicates.csproj` (submodule, existing on disk)

---

## Task 2 — `EngineAction` definition

### 2.1 Discriminated union

```csharp
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Engine;

public abstract record EngineAction
{
    public sealed record Navigate(WorldPosition Destination, NavigationOptions Options) : EngineAction;
    public sealed record Interact(NpcId Target) : EngineAction;
    public sealed record Wait(string Reason) : EngineAction;
    public sealed record AwaitUser(string Reason) : EngineAction;
    public sealed record Done : EngineAction;
}
```

`NavigationOptions` is the existing record from `QuestForge.Adapters.Movement` (stopping distance, mount, flight, timeout). The engine constructs it from the step's `stopDistance` (or defaults) when emitting `Navigate`.

### 2.2 Why a discriminated union and not interface + classes

Records give value equality for free, which simplifies test assertions:

```csharp
Assert.Equal(new EngineAction.Navigate(new WorldPosition(35.56f, 4.0f, -151.18f), new NavigationOptions()), action);
```

A `sealed record : EngineAction` hierarchy plays nicely with `switch` expression exhaustiveness in C# 11+ (warning if a case is missing — useful when Phase 5 adds new action types).

---

## Task 3 — `PredicateEvaluator`

### 3.1 Public surface

```csharp
namespace QuestForge.Engine.Predicates;

public sealed class PredicateEvaluator
{
    public PredicateEvaluator(IGameStateProvider gameState, IQuestState questState);

    /// <summary>Evaluate a parsed AST against live state.</summary>
    public Task<bool> Evaluate(PredicateAst ast, CancellationToken ct);
}

public sealed class ExpectEvaluator
{
    public ExpectEvaluator(PredicateEvaluator inner);

    /// <summary>Parse (with caching) and evaluate any ExpectValue form.</summary>
    public Task<bool> Evaluate(ExpectValue? expect, CancellationToken ct);
    // Null expect evaluates to true (some Step subtypes' SkipIf may be null).
}
```

### 3.2 Walking the AST

The AST node types are defined in `QuestForge.Predicates.PredicateAst`. The evaluator walks each using pattern matching:

| Node | Behavior |
|---|---|
| `Comparison(Left, Op, Right)` | Evaluate operands to int or string, apply `==`, `!=`, `>=`, `<=`, `>`, `<`. |
| `And(Left, Right)` | Evaluate left; short-circuit if false; evaluate right. Returns bool. |
| `Or(Left, Right)` | Evaluate left; short-circuit if true; evaluate right. Returns bool. |
| `Not(Inner)` | Evaluate inner, negate. |
| `FunctionCall(Name, Args)` | Dispatch to the named function (table-driven). |
| `IntLiteral(Value)` | Returns the long value as an evaluation result. |
| `StringLiteral(Value)` | Returns the string. |
| `PositionLiteral(X, Y, Z)` | Returns a WorldPosition. Legal only as arg to `playerNear`. |
| `DefaultLiteral` | Returns `true`. |

**No `Grouped` node:** grouping is consumed during parsing and expressed as tree shape. **No `BoolLiteral`:** boolean literals `true`/`false` are reserved for predicate language v1.1; the evaluator need not handle them.

`Evaluate(ast, ct)` returns `Task<bool>` because state functions are async (the adapters are async). Comparisons and logical operations produce `bool`; integer-returning functions like `questSequence` are valid only as operands of comparison operators, never as the top-level result.

**Top-level shape rule:** an `ExpectValue.PredicateExpect` is required to evaluate to `bool`. The validator (Phase 2) ensures this via the type checker. The engine asserts at runtime as a defense in depth: if `Evaluate` produces an `int` at the top level, throw `EnginePredicateShapeException`.

### 3.3 State function table

```csharp
private static readonly Dictionary<string, Func<PredicateEvaluator, IReadOnlyList<object>, CancellationToken, Task<object>>> _functions =
    new()
    {
        // IntLiteral.Value is long — cast to uint for QuestId, int for flag bit index.
        ["questSequence"]   = async (e, args, ct) => (long)(await e._questState.GetQuestSequence(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
        ["isQuestAccepted"] = async (e, args, ct) => (await e._questState.IsQuestAccepted(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
        ["isQuestComplete"] = async (e, args, ct) => (await e._questState.IsQuestComplete(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
        ["questFlag"]       = async (e, args, ct) => (await e._questState.IsQuestFlagSet(new QuestId((uint)(long)args[0]), (int)(long)args[1], ct)).ValueOrThrow,
        ["playerZone"]      = async (e, args, ct) => (long)(await e._gameState.GetPlayerZone(ct)).ValueOrThrow.Value,
        ["playerNear"]      = async (e, args, ct) => await PlayerNear(e, (WorldPosition)args[0], (long)args[1], ct),
        ["playerInCombat"]  = async (e, args, ct) => (await e._gameState.IsPlayerInCombat(ct)).ValueOrThrow,
    };
```

`playerNear` takes a `PositionLiteral` (first arg) and an integer radius (second arg), matching the Phase 2 function registry (`Fixed(2), [Position, Int]`). The `PositionLiteral(X, Y, Z)` node is evaluated to a `WorldPosition`. `PlayerNear` calls `GetPlayerPosition` and checks Euclidean distance against the radius — it does NOT take an NPC ID. To check proximity to a named NPC, quest authors use the NPC's known position as a literal: `playerNear({"x":35.56,"y":4.0,"z":-151.18}, 3)`.

Note: `playerNear(npc:XXXX, radius:Y)` is **not** valid in our predicate grammar — the `npc:` prefix notation was confirmed invalid during Phase 2's SCHEMA.md §10 review. The correct form is `playerNear({"x":X,"y":Y,"z":Z}, radius)` using a position literal as the first argument, which is what the Phase 4 fixture uses. The evaluator receives a `PositionLiteral` AST node, constructs a `WorldPosition`, and computes Euclidean distance from the player's current position.

Unsupported function name throws `UnknownStateFunctionException` immediately. Arity mismatches do not happen at runtime — the parser already enforced them.

### 3.4 Caching parsed ASTs

`ExpectEvaluator` keeps a `Dictionary<string, PredicateAst>` of parsed predicates. Per tick, the same predicate string parses once (across all ticks of one engine instance). This is a memory-cheap optimization that keeps Phase 4 honest about not re-parsing on every observation.

---

## Task 4 — `QuestEngine`

### 4.1 Public surface

```csharp
namespace QuestForge.Engine;

public sealed class QuestEngine
{
    public QuestEngine(
        IGameStateProvider gameState,
        IQuestState questState,
        INavigator navigator,
        ITeleporter teleporter,
        IInteractor interactor,
        ICombat combat,
        IGearManager gear,
        IMinigameSkipper minigames,
        IDialogueResolver dialogue,
        ITimingProfile timing,
        ITraceWriter trace,
        ILogger<QuestEngine> logger);

    public void StartQuest(QuestDefinition quest);

    public Task<EngineAction> Tick(CancellationToken ct);
}
```

### 4.2 Decision algorithm (one tick)

Pseudocode for `Tick(ct)`:

```
if (no quest loaded)
    return AwaitUser("no quest loaded")

questId = quest.Id
seqResult = await questState.GetQuestSequence(questId, ct)
if (seqResult is Failure)
    return AwaitUser($"adapter failure reading sequence: {reason}")

completeResult = await questState.IsQuestComplete(questId, ct)
if (completeResult is Success { Value: true })
    return Done

currentSeq = seqResult.ValueOrThrow
matchingBlock = quest.Sequences.FirstOrDefault(s => s.Sequence == currentSeq)
if (matchingBlock is null)
    return AwaitUser($"no sequence block matches current sequence {currentSeq}")

if (matchingBlock.SkipIf is not null)
    if (await expectEvaluator.Evaluate(matchingBlock.SkipIf, ct))
        return AwaitUser("sequence skipped by skipIf — engine cannot self-advance in Phase 4")

foreach (step in matchingBlock.Steps)
{
    if (step.Expect is not null && await expectEvaluator.Evaluate(step.Expect, ct))
        continue;
    if (step.SkipIf is not null && await expectEvaluator.Evaluate(step.SkipIf, ct))
        continue;
    return ResolveActionForStep(step);
}

return Wait("all steps in current sequence satisfied; awaiting game sequence advance")
```

### 4.3 `ResolveActionForStep`

```csharp
private EngineAction ResolveActionForStep(Step step) => step switch
{
    TravelStep travel when travel.Destination.Position is { } pos => new EngineAction.Navigate(
        new WorldPosition(pos.X, pos.Y, pos.Z),
        new NavigationOptions(StoppingDistance: step.StopDistance ?? 3.0f)),

    // TravelDestination.Position is Position3? — null means aetheryte-only travel (no walk target).
    // Aetheryte-based travel is out of scope for Phase 4; the engine cannot resolve it.
    TravelStep travel when travel.Destination.Position is null =>
        throw new NotSupportedException("Phase 4 does not support aetheryte-only travel steps"),

    TalkStep talk when talk.Target is not null => new EngineAction.Interact(
        new NpcId(talk.Target.NpcId)),

    TalkStep talk when talk.Target is null && talk.Targets is { Length: > 0 } =>
        throw new NotSupportedException("Phase 4 does not support multi-target talk steps"),

    _ => throw new NotSupportedException($"Phase 4 does not support step type {step.GetType().Name}"),
};
```

The Phase 4 engine has exactly two step-type branches. Anything else is an exception — the Phase 4 worked quest does not contain other step types, and the validator catches it elsewhere.

For `TalkStep`, dialogue choices are ignored in Phase 4. The `FakeInteractor` in tests doesn't need dialogue choices to advance state; that fidelity is for Phase 5+. The engine emits `Interact(NpcId)`; whether the plugin layer also calls `SelectDialogueOption` is a Phase-6 plugin concern, not a Phase-4 engine concern.

### 4.4 Why no `talk` action subtype?

`EngineAction.Interact` covers the `talk` step. The plugin layer, on receiving `Interact(NpcId)`, knows to call `IInteractor.InteractWith(NpcId)`. There is no separate "Talk vs Interact" action because the underlying adapter call is the same — both NPCs and quest-NPCs use `InteractWith`. Object interaction will get a separate `EngineAction.InteractObject(InteractableId)` when the `interact-object` step type is added (post-Phase 4).

---

## Task 5 — Given-When-Then specifications

These are the behaviors the Tester must cover. Each maps to one or more xUnit tests. Counts at the end inform the Builder when "done."

### 5.1 `EngineAction` (record value semantics)

**5.1.1** Given two `Navigate` records with equal `Destination` and equal `Options`, When compared, Then `Equals` returns true and hash codes match.

**5.1.2** Given two `Navigate` records with different `Destination`, When compared, Then `Equals` returns false.

**5.1.3** Given an exhaustive switch over all five `EngineAction` subtypes (`Navigate`, `Interact`, `Wait`, `AwaitUser`, `Done`), When each case is handled, Then the switch compiles and all cases are reachable. This is a structural test — write a helper method in the test project that switches on an `EngineAction` and returns a string label for each case; verify each label is distinct. This documents the complete set of cases and will fail to compile if a new subtype is added without updating the switch.

### 5.2 `PredicateEvaluator` — supported state functions

**5.2.1 `questSequence` (integer comparison):**
- Given `FakeQuestState` with `SetQuestSequence(QuestId(66130), 0)`, When evaluating `"questSequence(66130) >= 255"`, Then result is false.
- Given the same, When evaluating `"questSequence(66130) >= 0"`, Then result is true.
- Given `SetQuestSequence(QuestId(66130), 255)`, When evaluating `"questSequence(66130) >= 255"`, Then result is true.
- Given `SetQuestSequence(QuestId(66130), 255)`, When evaluating `"questSequence(66130) == 255"`, Then result is true.

**5.2.2 `isQuestComplete`:**
- Given `FakeQuestState` with `SetQuestStatus(QuestId(66130), QuestStatus.Accepted)`, When evaluating `"isQuestComplete(66130)"`, Then result is false.
- Given `SetQuestStatus(QuestId(66130), QuestStatus.Complete)`, When evaluating `"isQuestComplete(66130)"`, Then result is true.

**5.2.3 `playerZone`:**
- Given `FakeGameStateProvider.SetZone(new ZoneId(182))`, When evaluating `"playerZone() == 182"`, Then result is true.
- Given `SetZone(new ZoneId(130))`, When evaluating `"playerZone() == 182"`, Then result is false.

**5.2.4 `playerNear`:**
`playerNear` takes a position literal and radius — it checks whether the player's current position is within `radius` units of the given coordinates. It does NOT take an NPC ID.

- Given `FakeGameStateProvider.SetPosition(new WorldPosition(35.56f, 4.0f, -151.18f))`, When evaluating `"playerNear({\"x\":35.56,\"y\":4.0,\"z\":-151.18}, 3)"`, Then result is true (distance ≈ 0).
- Given `SetPosition(new WorldPosition(0f, 0f, 0f))`, When evaluating same predicate, Then result is false (distance >> 3).
- Given `SetPosition(new WorldPosition(35.57f, 4.0f, -151.18f))` (just over 3 units from the literal), Then result depends on exact distance — use a position clearly inside or outside the radius for deterministic tests.

**5.2.5 `questFlag`:**
- Given `FakeQuestState.SetQuestFlagBit(QuestId(66130), 1, true)`, When evaluating `"questFlag(66130, 1)"`, Then result is true.
- Given the bit unset, When evaluating same, Then result is false.

**5.2.6 Logical operators:**
- Given `playerZone() == 182` is true and `questSequence(66130) >= 255` is false, When evaluating `"playerZone() == 182 and questSequence(66130) >= 255"`, Then result is false (AND).
- Given the same, When evaluating `"playerZone() == 182 or questSequence(66130) >= 255"`, Then result is true (OR).
- Given the same, When evaluating `"not (questSequence(66130) >= 255)"`, Then result is true (NOT).

**5.2.7 `ExpectValue.AllExpect`:**
- Given two predicates both true, When evaluating `AllExpect { All = [p1, p2] }`, Then result is true.
- Given one true and one false, When evaluating `AllExpect`, Then result is false.

**5.2.8 `ExpectValue.AnyExpect`:**
- Given two predicates both false, When evaluating `AnyExpect { Any = [p1, p2] }`, Then result is false.
- Given one true, When evaluating `AnyExpect`, Then result is true.

**5.2.9 Errors:**
- Given a predicate referencing an unknown function `"foo()"`, When evaluating, Then `UnknownStateFunctionException` is thrown with the function name in the message.
- Given a predicate that evaluates to `int` at the top level (e.g., bare `"questSequence(66130)"`), When evaluating via `ExpectEvaluator`, Then `EnginePredicateShapeException` is thrown.

### 5.3 `QuestEngine` constructor

**5.3.1** Given all 10 adapters + trace + logger are non-null, When constructed, Then construction succeeds and no exception is thrown.

**5.3.2** Given `gameState = null`, When constructed, Then `ArgumentNullException` is thrown with parameter name `gameState`. Repeat for each of the 12 parameters.

(12 separate tests — these are mechanically simple but explicit.)

### 5.4 Quest 66130 — full flow

The full-flow tests load the fixture and drive the engine to completion via the test harness loop described in §7 above. Several sub-cases:

**5.4.1 — Cold start, sequence 0, player elsewhere:**
- Given: `questState.SetQuestSequence(QuestId(66130), 0)`; `gameState.SetZone(new ZoneId(182))`; `gameState.SetPosition((0,0,0))` (far from Wymond); `gameState.AddNpc(NpcReference(1003987, (35.56, 4, -151.18), 200f))` (out of range).
- When: engine `Tick`.
- Then: returns `EngineAction.Navigate(Destination=(35.56, 4, -151.18), Options=NavigationOptions{...})`.

**5.4.2 — Travel step complete, player near Wymond:**
- Given: same as 5.4.1 but `gameState.SetPosition((35.56, 4, -151.18))` and the NPC's `DistanceToPlayer = 0.5f`.
- When: engine `Tick`.
- Then: returns `EngineAction.Interact(NpcId(1003987))` (travel step's expect is satisfied; engine advances to the talk step).

**5.4.3 — Talk step complete, sequence advanced to 255, player still at Wymond:**
- Given: `questState.SetQuestSequence(QuestId(66130), 255)`; player near Wymond but Momodi is far away.
- When: engine `Tick`.
- Then: returns `EngineAction.Navigate(Destination=(21.84, 7, -81.13), Options=NavigationOptions{...})`. The engine has detected sequence 255 is now active and is processing its first step.

**5.4.4 — Talk to Momodi, quest complete:**
- Given: `questState.SetQuestStatus(QuestId(66130), QuestStatus.Complete)`.
- When: engine `Tick`.
- Then: returns `EngineAction.Done`.

**5.4.5 — End-to-end loop:**
- Given: fixture loaded, full state setup, callbacks wired (`FakeNavigator` updates player position on `Arrived`; `FakeInteractor.OnInteractWith(Wymond, () => questState.SetQuestSequence(255))`; `FakeInteractor.OnInteractWith(Momodi, () => questState.SetQuestStatus(Complete))`).
- When: tick → execute → tick → execute, loop until `Done`.
- Then: exactly 4 actions emitted before `Done`: `Navigate(Wymond)`, `Interact(Wymond)`, `Navigate(Momodi)`, `Interact(Momodi)`, then `Done`. The loop terminates within 10 ticks (safety bound — should be 5).

### 5.5 Resume (mid-quest start)

**5.5.1 — Start with sequence already at 255:**
- Given: `questState.SetQuestSequence(QuestId(66130), 255)`; player at zone 182 but far from Momodi; quest accepted, not complete.
- When: engine `StartQuest(quest)`; engine `Tick`.
- Then: first action is `Navigate(Destination=(21.84, 7, -81.13))`. The engine never returns Wymond-related actions.

**5.5.2 — Start with talk-to-Momodi expect already satisfied (resume after partial completion):**
- Given: sequence 255; player at Momodi position (`(21.84, 7, -81.13)`); NPC Momodi present with `DistanceToPlayer < 3.5`; quest not yet complete.
- When: `Tick`.
- Then: returns `Interact(NpcId(1003988))` (travel step expect satisfied; talk step is next).

### 5.6 Done detection

**5.6.1** Given `isQuestComplete(66130)` returns true while the engine is mid-tick, When `Tick`, Then `Done` is returned (this is checked BEFORE walking sequence steps).

### 5.7 AwaitUser cases

**5.7.1 — No quest loaded:**
- Given: engine constructed but `StartQuest` not yet called.
- When: `Tick`.
- Then: returns `AwaitUser("no quest loaded")`.

**5.7.2 — Sequence not in quest definition:**
- Given: `questState.SetQuestSequence(QuestId(66130), 42)` (no matching block in the fixture).
- When: `Tick`.
- Then: returns `AwaitUser` whose Reason mentions sequence 42.

**5.7.3 — Adapter failure on sequence read:**
- Given: a fake `IQuestState` that returns `Result<int>.Failure("adapter-broken")` from `GetQuestSequence`.
- When: `Tick`.
- Then: returns `AwaitUser` whose Reason includes "adapter-broken".

### 5.8 Single retry (NEXT_STEPS.md "at least one realistic failure case")

**5.8.1** Given: cold-start setup as in 5.4.1; `FakeNavigator.ScriptNextResult(NavigationOutcome.StoppedByObstacle)` for the first NavigateTo call. The test harness loop: tick → engine returns `Navigate` → harness calls `navigator.NavigateTo` → outcome is `StoppedByObstacle` → harness does NOT update player position → next tick.

- When: tick is called the second time.
- Then: engine returns `Navigate` again (the travel-step expect is still unsatisfied; engine treats this as "step not yet complete, do same thing").

Then on the third tick after the harness fakes successful arrival:
- Then: engine returns `Interact(Wymond)`.

This single test validates the "stateless retry" model for Phase 4.

### 5.9 Discipline: engine never references Dalamud

**5.9.1** Compile-time check (manual): `QuestForge.Engine` MSBuild output's `*.deps.json` contains no `Dalamud.*` package. This is verified by code review in the PR for this phase, not by an automated test.

### 5.10 Test count summary

| Section | Tests |
|---|---|
| 5.1 `EngineAction` value semantics | 3 |
| 5.2 PredicateEvaluator | ~20 (8 function-coverage groups × 2-3 cases each + 2 error cases) |
| 5.3 Constructor null-guard | 12 |
| 5.4 Full quest flow | 5 |
| 5.5 Resume | 2 |
| 5.6 Done detection | 1 |
| 5.7 AwaitUser cases | 3 |
| 5.8 Retry | 1 |
| **Total** | **~47 tests** |

---

## Task 6 — Done criteria (matches NEXT_STEPS.md phase 4 done-criteria)

1. `dotnet build` of the full solution succeeds with `TreatWarningsAsErrors=true`.
2. `dotnet test QuestForge.Engine.Tests` runs green. All ~47 tests pass.
3. The end-to-end flow test (5.4.5) drives the worked 66130 quest from cold start to `Done` using fakes only, with the exact action sequence `Navigate, Interact, Navigate, Interact, Done`.
4. The mid-quest resume test (5.5.1) starts with sequence already 255 and returns the Momodi travel action immediately, never producing Wymond-related actions.
5. `Done` is returned when `isQuestComplete(66130)` is true, ahead of any step walking.
6. `dotnet list QuestForge.Engine reference` shows no Dalamud reference (manual verification, recorded in PR description).
7. The Phase 0 spike's findings (zone 182, NPC IDs 1003987 / 1003988, positions per SPIKE_NOTES.md) are encoded in the fixture and the engine drives them correctly.

---

## Implementation order (strict TDD)

### Phase A — Scaffolding (architect → builder, no tests yet)

1. Add the `questforge-tools` submodule and verify `git submodule update --init` works.
2. Edit `QuestForge.Engine.csproj` to add the three `ProjectReference`s and the `Microsoft.Extensions.Logging.Abstractions` PackageReference.
3. Add `QuestForge.Predicates` to `QuestForge.sln`.
4. Create `QuestForge.Engine.Tests/` project with csproj, fakes folder, fixtures folder; add to solution.
5. Write `Fixtures/66130.json` containing the worked-example quest definition exactly as in §6 of the architectural decisions.
6. Write `NullTraceWriter`.
7. Create `EngineAction.cs` with the discriminated union (Navigate, Interact, Wait, AwaitUser, Done).
8. Create `QuestEngine.cs` with the full constructor signature and `Tick(ct)` and `StartQuest(quest)` methods, all throwing `NotImplementedException`.
9. Create `PredicateEvaluator.cs` and `ExpectEvaluator.cs` with public surfaces from §3.1, all throwing `NotImplementedException`.
10. `dotnet build` succeeds. The solution compiles. No tests yet.

**Gate:** `dotnet build` is green. Move to Phase B.

### Phase B — Tester writes failing tests

1. Write all tests per §5.1 through §5.9 above.
2. Wire `FakeGameStateProvider`, `FakeQuestState`, `FakeNavigator`, `FakeInteractor`, `FakeTeleporter`, `FakeCombat`, `FakeGearManager`, `FakeMinigameSkipper`, `FakeDialogueResolver`, and `FakeTimingProfile` into test setup helpers (a `EngineTestHarness` class in `QuestForge.Engine.Tests/Helpers/`).
3. Extend `FakeInteractor` with `OnInteractWith(NpcId, Action)` if needed for callback-driven state transitions during full-flow tests. This is an additive change to the Phase 3 fake.
4. `dotnet test QuestForge.Engine.Tests` runs and **every test is red** (NotImplementedException). This is the expected state at the end of Phase B.

**Gate:** all ~47 tests exist, all are red, all are red for the right reason (NotImplementedException, not compile errors). Move to Phase C.

### Phase C — Builder implements engine to make tests pass

Implement in this order (lowest-risk to highest-risk):

1. **`EngineAction`** — already done in Phase A scaffolding; tests in §5.1 turn green for free (the records exist).
2. **`PredicateEvaluator`** — implement the function table, AST walking, integer/bool comparison, logical operators. §5.2 tests turn green incrementally.
3. **`QuestEngine` constructor** — null-guard each parameter. §5.3 tests turn green.
4. **`QuestEngine.StartQuest`** — store the quest, validate it has at least one sequence. (Minimal — the validator did the deep work.)
5. **`QuestEngine.Tick` decision loop** — implement the algorithm in §4.2 of this plan. §5.4 / §5.5 / §5.6 / §5.7 tests turn green.
6. **Retry behavior** — none of the engine code changes; §5.8 turns green because the engine is stateless per tick. (This is the test that validates the design rather than testing new code.)

**Gate:** all tests green. Builder hands off to Reviewer.

### Phase D — Review

1. Reviewer (or main agent) reads the engine code, verifies it matches the spec, runs `dotnet test`, confirms green.
2. Reviewer verifies `dotnet list QuestForge.Engine reference` shows no Dalamud reference.
3. Reviewer verifies the .gitmodules entry is committed and the submodule is at a known-good commit.
4. Phase 4 is closed; Phase 5 begins.

---

## What Phase 4 deliberately does NOT include

- Step types other than `travel` and `talk` (`interact-object`, `pickup-item`, `accept`, `turn-in`, `combat`, `duty`, `cutscene`, `use-emote`, `say-chat-message`, `use-item`, `use-action`, `equip-gear-for-quest`, `equip-best-gear`, `change-job`, `minigame`, `await-user`, `branch`, `fragment`). Phase 7+ adds them as quests demand.
- `BranchStep` and `FragmentStep` — composite step types that change the HSM evaluator. Phase 7+.
- Multi-target talk/interact steps. Phase 7+.
- `EngineDecisionConfig` and related configuration. Phase 5+.
- Trace recording / `ITraceWriter.Write` calls inside the engine. Phase 5.
- The recording proxy that wraps `IGameStateProvider` and `IQuestState`. Phase 5.
- `MaxConsecutiveStepFailures`, `MaxDutyRetries`, `MaxConsecutiveQuestFailures` counters. Phase 7+.
- Death recovery, `InstanceKind`-routed recovery, AwaitUser indefinite-poll for SPDs. Phase 7+.
- Real Dalamud adapter implementations. Phase 6.
- Reward selection logic. Phase 7+ (the `turn-in` step type brings it).
- Dialogue choice selection inside `EngineAction.Interact`. Phase 5+ (the action becomes structured: `Interact(NpcId, DialogueChoices[])`).
- Sequence-level `skipIf` advancement. The engine cannot self-advance sequences in Phase 4; the `skipIf` case becomes `AwaitUser` for now. Phase 7+ revisits this when more step types reveal whether self-advancement is needed or whether the game's state transitions cover it.
- A CI workflow for the plugin repo. Phases 6+ introduce it; Phase 4 enforces correctness through local `dotnet test`.
- NuGet packaging of `QuestForge.Predicates`. Post-Phase 7.

---

## Risks specific to Phase 4

**Risk 1: `QuestForge.Predicates` API has surprises.**
The Phase 2 plan said the AST node types and parser entry points would be stable, but Phase 4 is the first non-validator consumer. If `PredicateParser.Parse(string)` doesn't return a clean `PredicateAst` root or if the AST node hierarchy differs from what's assumed in §3.2, the Builder will discover this in Phase C and either (a) adapt the evaluator to the real shape or (b) revise `QuestForge.Predicates` in the tools repo and update the submodule pin. Either is acceptable.

**Risk 2: `FakeInteractor.OnInteractWith` doesn't exist in Phase 3.**
Phase 3 fakes have `OnAcceptQuest` and `OnCompleteQuest` callbacks, but not a per-NPC interaction callback. The full-flow test (§5.4.5) needs the latter — when the engine returns `Interact(Wymond)` and the harness calls `interactor.InteractWith(Wymond)`, the test needs that to advance the quest sequence. Phase 4 adds this method to `FakeInteractor` as an additive Phase-3-fake change, owned by Phase 4. Phase 3's tests remain unchanged because they don't use it.

**Risk 3: The schema's `ExpectValue` shape doesn't match the parser's input expectations.**
`ExpectValue.PredicateExpect.Predicate` is a string — fine for the parser. `AllExpect.All` and `AnyExpect.Any` are arrays of strings. The `ExpectEvaluator` parses each string in those arrays separately and evaluates them as a boolean AND or OR. If the schema parsing wraps these differently (e.g., as an already-parsed AST), the Builder revises during Phase C.

**Risk 4: The Phase 0 spike notes are slightly wrong about NPC IDs or positions.**
Phase 4 doesn't actually drive the real game — fakes only. If a coordinate is off by a few units in the fixture, no test fails. The Phase 6 / Phase 7 real-quest integration will surface any spike-data errors then. Phase 4 trusts SPIKE_NOTES.md as written.

**Risk 5: TDD discipline breaks down.**
The TDD workflow (Phase A scaffold → Phase B red tests → Phase C green implementation) is new to this codebase. If a phase blurs the line — implementing things in Phase A that should wait for Phase C, or stubbing tests in Phase B that "happen" to pass — the gate criteria above (e.g., "every test is red for the right reason at end of Phase B") are how the architect / reviewer catches this. Strict.

---

## A note on the test fixture vs the eventual `questforge-data` file

`QuestForge.Engine.Tests/Fixtures/66130.json` is owned by Phase 4. It exists to drive the engine in unit tests. It is not a contribution to `questforge-data` and is not validated by `qf-validate` in this repo (the validator lives in the tools repo, which the engine project doesn't reference for validation — only for predicate parsing).

When Phase 7 authors the real 66130 quest for `questforge-data`, that file may have additional fields (`turn-in` step type with reward selection, more accurate dialogue choices, possibly `acceptFrom` revisions if the spike's coordinates were off). The Phase 4 fixture remains a separate file inside the test project. Drift between the two is acceptable so long as the Phase 4 fixture remains a valid `QuestDefinition` per `QuestForgeJsonContext` (i.e., it loads).

The Phase 4 fixture deliberately uses `talk` for the Momodi turn-in step instead of the schema's `turn-in` step type, because the Phase 4 engine implements `talk` but not `turn-in`. This is the documented Phase 4 workaround and is called out in §6 of the architectural decisions.