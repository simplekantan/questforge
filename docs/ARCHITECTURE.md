# QuestForge Architecture

**Status:** v1 draft — diagrams reflect the foundation design and will be revised as implementation surfaces what we got wrong
**Related:** [DESIGN.md](./DESIGN.md), [ADAPTERS.md](./ADAPTERS.md), [SCHEMA.md](./SCHEMA.md), [TRACE_FORMAT.md](./TRACE_FORMAT.md), [AUTHORING.md](./AUTHORING.md)

---

## About this document

C4-style architecture diagrams for QuestForge, at three levels of detail:

1. **System Context** — QuestForge and the world it lives in
2. **Container** — the three repos, CI, and how a plugin install fits together
3. **Component** — the engine, the adapter layer, and the supporting subsystems

Diagrams are in Mermaid, which renders natively on GitHub, GitLab, and most markdown viewers. To regenerate as images, see Mermaid Live Editor (https://mermaid.live).

This document is a navigational aid, not a contract. Where it conflicts with the prose specs, the prose specs win.

---

## Level 1: System Context

The widest view. Shows QuestForge in relation to the user, FFXIV, the dependent plugin ecosystem, and the data delivery infrastructure.

```mermaid
flowchart TB
    User(["Player<br/>(end user)"])
    Author(["Quest Author<br/>(contributor)"])

    subgraph Game["FINAL FANTASY XIV"]
        FFXIV["FFXIV Client<br/>(running game)"]
        Server["FFXIV Servers<br/>(authoritative state)"]
    end

    subgraph Dalamud["Dalamud Environment"]
        QF["QuestForge<br/>(this project)"]
        Deps["Dependency Plugins<br/>vnavmesh, Lifestream,<br/>TextAdvance, BossMod,<br/>WrathCombo, AutoDuty,<br/>Stylist"]
    end

    subgraph GitHub["GitHub"]
        DataRepo["questforge-data<br/>(quest JSON files,<br/>CC-BY-4.0)"]
        PluginRepo["questforge<br/>(plugin source,<br/>MIT)"]
        ToolsRepo["questforge-tools<br/>(validators, CLI,<br/>MIT)"]
    end

    User -->|plays, configures| QF
    Author -->|PRs new quest data,<br/>uses authoring mode| QF
    Author -->|submits PRs| DataRepo

    QF <-->|reads state,<br/>issues commands| FFXIV
    FFXIV <-->|game protocol| Server
    QF <-->|IPC| Deps
    Deps <-->|hook into| FFXIV

    QF -.->|opt-in delta updates<br/>via GitHub Releases| DataRepo
    PluginRepo -.->|distributes plugin via<br/>Dalamud plugin repo| Dalamud
```

### What this shows

- QuestForge sits inside Dalamud, talking to FFXIV through both Dalamud's hooks and the dependency plugins
- The player is the primary user; the quest author is a secondary user who contributes data
- Three GitHub repositories with different licenses and audiences
- The data repo updates separately from the plugin (the hybrid delivery model in `DESIGN.md` §4)

### What this hides

- Internal QuestForge structure (next level)
- Specific IPC protocols with each dependency plugin
- Server-side mechanics (FFXIV is treated as a black box state provider)

---

## Level 2: Containers

A closer view of QuestForge and its repositories. Shows what artifacts exist, where they live, and how they relate.

```mermaid
flowchart TB
    subgraph Player["Player's Machine"]
        subgraph PluginInstall["QuestForge Plugin Install"]
            PluginDLL["Plugin DLL<br/>(QuestForge.dll)"]
            BundledData["Bundled Quest Data<br/>(snapshot at release)"]
            UpdatedData["Updated Quest Data<br/>(delta from GitHub Releases,<br/>opt-in)"]
            LocalDrafts["Authoring Drafts<br/>(per-quest, local only)"]
            Traces["Trace Files<br/>(JSONL, rotating)"]
        end
    end

    subgraph QuestForgeRepo["questforge repo (MIT)"]
        EngineCode["Engine<br/>(QuestForge.Engine)"]
        AdaptersIface["Adapter Interfaces<br/>(QuestForge.Adapters)"]
        AdaptersImpl["Dalamud Adapters<br/>(QuestForge.Adapters.Dalamud)"]
        PluginCode["Plugin Entry<br/>(QuestForge.Plugin)"]
        UICode["UI<br/>(QuestForge.UI)"]
        SchemaTypes["Schema Types<br/>(C# source of truth)"]
    end

    subgraph DataRepo["questforge-data repo (CC-BY-4.0)"]
        QuestJSON["Quest JSON files<br/>(organized by<br/>expansion/category)"]
        Fragments["Fragments<br/>(reusable sub-sequences)"]
        Releases["GitHub Releases<br/>(versioned data snapshots,<br/>latest.json + ETag)"]
    end

    subgraph ToolsRepo["questforge-tools repo (MIT)"]
        Validator["Schema Validator<br/>(qf-validate)"]
        TraceCLI["Trace CLI<br/>(qf-trace)"]
        QuestCLI["Quest CLI<br/>(qf-quest)"]
        CIScripts["CI Workflow<br/>Definitions"]
    end

    subgraph CI["GitHub Actions"]
        PRChecks["PR Checks<br/>(validator, replay)"]
        ReleaseGen["Release Generation<br/>(latest.json, hashes)"]
    end

    SchemaTypes -.->|defines| QuestJSON
    SchemaTypes -.->|consumed by| Validator
    QuestJSON -->|packaged as| Releases
    Releases -->|fetched on update| UpdatedData
    QuestJSON -->|snapshot at release| BundledData
    Validator -->|runs in| PRChecks
    TraceCLI -->|consumes| Traces

    PluginCode -->|composes| EngineCode
    PluginCode -->|composes| AdaptersImpl
    PluginCode -->|composes| UICode
    EngineCode -->|references only| AdaptersIface
    AdaptersImpl -->|implements| AdaptersIface
    EngineCode -->|reads| BundledData
    EngineCode -->|reads| UpdatedData
    EngineCode -->|writes| Traces
    UICode <-->|reads/writes| LocalDrafts

    PRChecks -->|gates merge| QuestJSON
    PRChecks -->|gates merge| EngineCode
```

### What this shows

- The plugin install on the user's machine bundles a data snapshot and may also fetch deltas
- The plugin DLL composes three layers: engine (pure), adapters (Dalamud-bound), UI (Dalamud-bound)
- The engine references *only* adapter interfaces, never concrete Dalamud types — this is the testability boundary
- Three separate CI flows: data PRs validated, engine PRs replay-tested, releases generated
- The schema is defined by C# types and consumed by both the runtime and the validator (single source of truth)

### What this hides

- Engine internals (next level)
- Specific adapter implementations (each adapter wraps different Dalamud primitives and IPC contracts)
- The recording proxy layer (it sits between engine and adapter implementations — shown in level 3)

---

## Level 3: Component — Engine

Zooming in on the QuestForge plugin itself. Shows the engine's internal structure and how it interacts with the adapter layer.

```mermaid
flowchart LR
    subgraph EngineCore["Engine"]
        HSM["HSM Evaluator<br/>(state machine driver)"]
        Planner["Step Planner<br/>(expands composite steps<br/>into primitive sequences)"]
        Predicates["Predicate Evaluator<br/>(expect/skipIf parser + runtime)"]
        Decision["Decision Loop<br/>(Action | KeepObserving | Done)"]
        Timing["Timing Coordinator<br/>(seeded from runId)"]
        Recovery["Recovery Coordinator<br/>(default ladders + overrides)"]
        Counters["Failure Counters<br/>(step, duty, quest)"]
    end

    subgraph TraceSubsystem["Trace Subsystem"]
        Writer["Trace Writer<br/>(JSONL append, fsync)"]
        Redactor["Redactor<br/>(for shared traces)"]
        RecordingProxy["Recording Proxy<br/>(wraps adapters)"]
    end

    subgraph AdapterLayer["Adapter Layer (interfaces)"]
        IGameState["IGameStateProvider"]
        IQuest["IQuestState"]
        INav["INavigator"]
        ITel["ITeleporter"]
        IInter["IInteractor"]
        ICombat["ICombat"]
        IGear["IGearManager"]
        IMini["IMinigameSkipper"]
        IDialog["IDialogueResolver"]
        ITiming["ITimingProfile"]
    end

    subgraph QuestData["Quest Data"]
        Schema["Quest JSON<br/>(loaded at startup)"]
        FragmentReg["Fragment Registry"]
    end

    Schema -->|loaded by| Planner
    FragmentReg -->|expanded by| Planner
    Planner -->|produces step plan| HSM
    HSM -->|drives| Decision
    Decision -->|evaluates| Predicates
    Decision -->|consults| Counters
    Decision -->|triggers| Recovery
    Decision -->|requests timing for| Timing
    Predicates -->|reads via proxy| RecordingProxy
    Recovery -->|reads via proxy| RecordingProxy
    Timing --> ITiming

    Decision -->|commands| RecordingProxy
    RecordingProxy -->|wraps reads of| IGameState
    RecordingProxy -->|wraps reads of| IQuest
    RecordingProxy -->|emits observations to| Writer
    RecordingProxy -->|emits actions to| Writer
    RecordingProxy -->|passes through to| INav
    RecordingProxy -->|passes through to| ITel
    RecordingProxy -->|passes through to| IInter
    RecordingProxy -->|passes through to| ICombat
    RecordingProxy -->|passes through to| IGear
    RecordingProxy -->|passes through to| IMini
    RecordingProxy -->|passes through to| IDialog
    Writer -.->|on share| Redactor
```

### What this shows

- The engine is composed of focused subsystems with one responsibility each
- The recording proxy sits between the engine and the adapter layer, capturing reads as observations and actions as events — this is what enables trace replay
- Reads flow through the proxy; writes (commands to adapters) also flow through the proxy to capture them in the trace
- Quest data is loaded and expanded by the planner before the HSM evaluator sees it
- Predicates are parsed once, evaluated against the proxy at runtime
- Failure counters are consulted by the decision loop, not maintained inside it

### What this hides

- Specific adapter implementations (each wraps different Dalamud primitives — see `ADAPTERS.md` §16)
- UI subsystems (settings, support status display, authoring panels — those are separate from the engine)
- The exact tick rate and async cancellation flow (these are engine-internal details)

---

## Level 3: Component — Authoring Mode

Authoring mode is separable enough to warrant its own component view. It's lazy-loaded and runs largely independently of the engine.

```mermaid
flowchart LR
    Author(["Quest Author"])

    subgraph AuthoringMode["Authoring Mode (lazy-loaded)"]
        PlayerPanel["Player State Panel<br/>(zone, position, job, mount)"]
        QuestPanel["Quest State Panel<br/>(sequence, flags, changes)"]
        InteractPanel["Interaction Panel<br/>(target, dialog, sheet refs)"]
        Recorder["Recorder<br/>(infers step type from<br/>recent player action)"]
        Inference["Step Type Inference<br/>(rule-based)"]
        Suggester["Expect Predicate<br/>Suggester"]
        DraftMgr["Draft Manager<br/>(per-quest, versioned)"]
        Exporter["Exporter<br/>(validates + writes file)"]
        Browser["Quest Dialogue Browser<br/>(filtered Lumina view)"]
    end

    subgraph Sources["Data Sources"]
        GameState["Live Game State<br/>(via IGameStateProvider,<br/>read-only)"]
        Lumina["Lumina Sheets<br/>(NPC, item, dialogue refs)"]
        DraftStorage["Draft Storage<br/>(plugin config dir)"]
    end

    subgraph Output["Author's Output"]
        DraftFile["Quest Draft JSON<br/>(local, ready to PR)"]
    end

    Author -->|reads| PlayerPanel
    Author -->|reads| QuestPanel
    Author -->|reads| InteractPanel
    Author -->|browses| Browser
    Author -->|clicks Record| Recorder
    Author -->|reviews + accepts| Suggester
    Author -->|exports| Exporter

    PlayerPanel -->|polls 250ms +<br/>event-driven| GameState
    QuestPanel -->|polls 250ms +<br/>event-driven| GameState
    InteractPanel -->|polls 250ms +<br/>event-driven| GameState
    Browser -->|queries| Lumina

    Recorder -->|observes recent| GameState
    Recorder -->|feeds context to| Inference
    Inference -->|produces draft step| Suggester
    Suggester -->|reads diff against state| GameState
    Suggester -->|appends to| DraftMgr
    DraftMgr <-->|persists| DraftStorage
    Exporter -->|reads from| DraftMgr
    Exporter -->|validates against| Lumina
    Exporter -->|writes| DraftFile
```

### What this shows

- Three debug panels are read-only consumers of game state
- The recorder is the central authoring action — observing state, inferring a step type, suggesting `expect`
- Authoring is mutually exclusive with the engine (the recorder consumes events the engine would otherwise consume)
- Drafts live locally with version history; export is a manual step that produces a PR-ready file
- The dialogue browser is a Lumina view, decoupled from recording

### What this hides

- The exact step type inference rules (those are heuristics, documented in `AUTHORING.md` §5.1)
- The UI rendering layer (ImGui via Dalamud)

---

## Level 3: Component — Trace & Replay

The trace subsystem deserves its own view because it spans recording and replay testing.

```mermaid
flowchart TB
    subgraph Production["Production Run"]
        ProdEngine["Engine<br/>(production)"]
        ProdProxy["Recording Proxy"]
        ProdAdapters["Dalamud-backed<br/>Adapters"]
        ProdWriter["Trace Writer"]
        ProdTrace["trace.jsonl<br/>(append-only)"]
    end

    subgraph Sharing["Trace Sharing"]
        Redactor["Redactor<br/>(strips PII, wallclock)"]
        BugReport["Bug Report Package"]
        Fixture["Canonical Fixture<br/>(committed to repo)"]
    end

    subgraph Replay["Replay Test (CI)"]
        ReplayProxy["Replay Provider<br/>(reads observations<br/>from trace)"]
        ReplayEngine["Engine<br/>(current code)"]
        FakeAdapters["Fake Adapters<br/>(scripted from trace)"]
        Validator["Decision Validator<br/>(compares produced<br/>actions to recorded)"]
        Verdict["Pass / Diff"]
    end

    subgraph Tools["Tools (questforge-tools)"]
        TraceCLI["qf-trace CLI<br/>(view, validate, diff,<br/>migrate)"]
    end

    ProdEngine -->|reads via| ProdProxy
    ProdProxy -->|delegates to| ProdAdapters
    ProdProxy -->|records to| ProdWriter
    ProdWriter -->|writes| ProdTrace
    ProdAdapters <-->|Dalamud + IPC| Dalamud[("Dalamud /<br/>FFXIV")]

    ProdTrace -->|optional share| Redactor
    Redactor -->|user-attached| BugReport
    ProdTrace -->|hand-picked,<br/>after manual play| Fixture

    Fixture -->|input| ReplayProxy
    ReplayProxy --> ReplayEngine
    ReplayEngine -->|calls| FakeAdapters
    FakeAdapters -->|scripted from| Fixture
    ReplayEngine -->|produces actions| Validator
    Fixture -->|recorded actions| Validator
    Validator --> Verdict

    ProdTrace -.->|inspected with| TraceCLI
    Fixture -.->|maintained with| TraceCLI
```

### What this shows

- The same `Engine` code path runs in both production and replay — only the adapter layer differs
- Recording happens transparently via the proxy in production
- Fixtures are *hand-picked* from production traces, not auto-generated — quality matters more than volume
- Replay does not re-run adapters against the game; it scripts fakes from the trace
- Bug reports are a separate path with explicit redaction

### What this hides

- The structure of individual trace events (see `TRACE_FORMAT.md` §5)
- The crash-safety protocol for trace writing (`TRACE_FORMAT.md` §9)

---

## Diagram maintenance

These diagrams are intentionally smaller than the prose specs. Two implications:

- **They will lie sometimes.** When a diagram conflicts with prose, the prose wins. When you notice a conflict, fix the diagram or remove it — outdated diagrams are worse than no diagrams.
- **They should be revised in the same PR that revises the corresponding prose.** Keep them in sync.

If the diagrams stop being useful, drop them. They're a navigation aid, not a deliverable.

---

## Appendix: What's deliberately not diagrammed

Decisions made about what to leave out and why:

- **Class diagrams** — premature; no implementation exists yet. The C# types defined in `ADAPTERS.md` and `SCHEMA.md` substitute.
- **Sequence diagrams of specific quest runs** — `TRACE_FORMAT.md` §A worked example is more useful than a diagram.
- **State machine diagrams of the HSM** — the HSM is small enough that the diagram would either be trivial or hand-wave too much. Prose in `DESIGN.md` §5.4 explains it.
- **Deployment diagrams** — there's only one deployment (Dalamud user installs the plugin). The system context diagram already shows this.
- **Database schema** — there's no database. Trace files are append-only JSONL; quest data is JSON files.

These can be added if a specific pain point emerges. Premature diagramming is a real cost.
