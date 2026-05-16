# QuestForge

A Dalamud plugin that automates FFXIV quest completion.

[![CI](https://github.com/simplekantan/questforge/actions/workflows/ci.yml/badge.svg)](https://github.com/simplekantan/questforge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-blueviolet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Dalamud](https://img.shields.io/badge/plugin-Dalamud-7b4fa3)](https://github.com/goatcorp/Dalamud)

---

## Status

Active development. All 10 phases of the implementation roadmap are complete. Not yet released for general use — not available in the Dalamud plugin installer.

---

## What it does

Press Start in the plugin UI and QuestForge runs MSQ, class quests, and side quests in priority order. It handles:

- **Navigation** — vnavmesh for in-zone pathfinding
- **Teleportation** — Lifestream for aetheryte travel
- **Dialogue** — TextAdvance for NPC interaction and cutscene skipping
- **Combat** — WrathCombo or RSR (fully delegated; QuestForge does not manage rotations)

QuestForge works on FFXIV for Windows with the [Dalamud](https://goatcorp.github.io/) mod framework.

---

## Plugin commands

| Command | Effect |
|---------|--------|
| `/qf run <questId>` | Run a single quest by ID |
| `/qf start` | Start fully automated questing |
| `/qf stop` | Stop automation |
| `/qf ui` | Open the main window |
| `/qf inspect` | Open authoring inspect panels |
| `/qf author <questId>` | Start authoring a quest |
| `/qf config trace on\|off` | Enable or disable trace recording |
| `/qf debug quest <id>` | Print Lumina data for a quest (authoring aid) |

---

## Required Dalamud plugins

Install these before using QuestForge:

- [vnavmesh](https://github.com/awgil/ffxiv_navmesh) — in-zone navigation
- [Lifestream](https://github.com/NightmareXIV/Lifestream) — aetheryte teleportation
- [TextAdvance](https://github.com/NightmareXIV/TextAdvance) — dialogue and cutscene skipping
- WrathCombo or RSR — combat (at least one required)

---

## Developer setup

Clone `questforge` and `questforge-data` as siblings in the same parent directory:

```
parent/
  questforge/
  questforge-data/
```

```bash
git clone https://github.com/simplekantan/questforge
git clone https://github.com/simplekantan/questforge-data
```

Run the engine tests (no game required):

```bash
dotnet test QuestForge.Engine.Tests/QuestForge.Engine.Tests.csproj
```

Build the plugin DLL:

```bash
dotnet publish QuestForge.Plugin -c Release
```

See `docs/` for architecture documentation. `QuestForge.Plugin` is excluded from CI because it depends on the Dalamud NuGet feed, which is not available in GitHub Actions. Plugin builds are tested locally.

---

## Repository structure

| Directory | Purpose |
|-----------|---------|
| `QuestForge.Engine/` | Pure C# engine — no Dalamud dependency. All testable logic lives here. |
| `QuestForge.Adapters/` | Adapter interface definitions (`INavigator`, `IInteractor`, etc.). |
| `QuestForge.Adapters.Dalamud/` | Concrete Dalamud-backed adapter implementations. |
| `QuestForge.Adapters.Fakes/` | In-memory fakes and recording proxies for engine tests. |
| `QuestForge.Plugin/` | Dalamud entry point, `EngineHost`, ImGui UI, `/qf` commands. |
| `QuestForge.Schema/` | Quest file schema types with source-generated STJ serialization. |
| `docs/` | Design, architecture, adapter specs, schema reference, trace format. |

---

## Quest data

Quest definitions live in the separate [questforge-data](https://github.com/simplekantan/questforge-data) repository (CC-BY-4.0). See that repo to contribute quests — no programming experience required.

---

## License

MIT. See [LICENSE](LICENSE).
