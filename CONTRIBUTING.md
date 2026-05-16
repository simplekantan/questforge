# Contributing to QuestForge

## Ways to contribute

- **Code** — engine logic, adapter implementations, UI improvements
- **Bug reports** — open an issue with reproduction steps and any trace files
- **Documentation** — corrections and clarifications to `docs/`
- **Quest data** — see the [questforge-data](https://github.com/simplekantan/questforge-data) repo

---

## Development setup

**Prerequisites:**

- .NET 10 SDK
- Dalamud dev environment (required only for plugin-side changes; engine tests run without it)

**Clone both repos as siblings:**

```
parent/
  questforge/
  questforge-data/
```

```bash
git clone https://github.com/simplekantan/questforge
git clone https://github.com/simplekantan/questforge-data
```

---

## Running tests

```bash
dotnet test QuestForge.Engine.Tests/QuestForge.Engine.Tests.csproj
```

202 tests. No game instance required. Tests cover engine logic, predicate evaluation, step planning, failure recovery, and fixture-based regression tests against quest data.

Plugin-side changes (anything touching `QuestForge.Adapters.Dalamud` or `QuestForge.Plugin`) require manual in-game testing. The plugin project is excluded from CI.

---

## Architecture invariants

**The engine must never reference Dalamud types.** `QuestForge.Engine` and `QuestForge.Adapters` must compile without the Dalamud NuGet packages. Violations break testability — the engine runs in CI without a running game.

All testable logic belongs in `QuestForge.Engine` or `QuestForge.Adapters`. If you find yourself adding game-specific logic to `QuestForge.Adapters.Dalamud`, consider whether it belongs in the engine instead.

See `CLAUDE.md` and `docs/ARCHITECTURE.md` for the full rationale.

---

## Adding a new step type

1. Add the type to `QuestForge.Schema/Step.cs`
2. Add engine handling in `QuestEngine` (dispatch and postcondition verification)
3. Add a fake implementation in `QuestForge.Adapters.Fakes`
4. Add tests in `QuestForge.Engine.Tests`
5. Update `docs/SCHEMA.md` with the new step type's fields and semantics
6. Update `docs/FIXTURES.md` capability table

---

## PR process

- Open an issue first for large changes — design alignment before implementation
- All engine tests must pass (`dotnet test QuestForge.Engine.Tests/`)
- Keep commits clean; squash fixup commits before requesting review
- Reference the relevant issue in the PR description

---

## Code style

- Follow existing patterns in the codebase
- Comments explain *why*, not *what* — the code explains what
- No speculative abstractions — implement what the tests require
