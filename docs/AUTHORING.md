# Authoring Mode Specification

**Status:** v1 — Phase 9 delivered the core recording workflow; several sections describe planned future capability, noted below
**Owners:** QuestForge maintainers
**Related:** [DESIGN.md](./DESIGN.md), [SCHEMA.md](./SCHEMA.md), [ADAPTERS.md](./ADAPTERS.md), [TRACE_FORMAT.md](./TRACE_FORMAT.md)

---

## 1. Purpose

Authoring mode is in-plugin tooling that helps contributors author quest definitions without manually digging through game data files or Dalamud's developer tools.

This document specifies:

- The two authoring sub-modes (Inspect, Author) and when each is used
- The debug panels visible during authoring
- The "Record current action" workflow and step-type inference
- Draft management (per-quest scoping, version history, export)
- Resume vs. restart authoring flow for in-progress quests
- Lifecycle (lazy load, idle unload)

The goal: a contributor who has never edited JSON can produce a valid quest draft by playing the game with authoring mode active.

---

## 2. Sub-modes

Authoring mode has two distinct sub-modes:

### 2.1 Inspect mode

Passive observation. No recording happens. Debug panels (§4) are visible.

**Works alongside running automation.** The engine can be executing a quest while Inspect mode shows what it's observing. Useful for:

- Diagnosing why a step failed ("the engine sees questSequence(65) == 2 but the quest data expects ≥3")
- Spot-checking an NPC ID or sheet reference
- Verifying patch-day data freshness

Activated via `/qf inspect` or the plugin UI. No engine state mutation.

### 2.2 Author mode

Active recording. Debug panels plus the "Record current action" workflow.

**Mutually exclusive with engine execution.** The engine must be stopped before entering Author mode. Activated via `/qf author <questId>` or the plugin UI's "Start authoring" button.

Author mode scopes recording to a single quest at a time. Switching quests requires explicit action. Exit authoring with `/qf author stop` or the "Stop authoring" button in the Authoring Session panel. If you start authoring a new quest while another is active, a warning appears in the `InteractionPanel` with the stop command hint — your current draft is saved automatically before the switch.

### 2.3 Mode display

The plugin status bar shows the current mode unambiguously:

- `⚙ Engine running — Quest #1234 — Step 5`
- `🔍 Inspect mode — Engine running`
- `🔍 Inspect mode — Engine idle`
- `✏ Authoring Quest #1234 — Engine idle`

Transitions require explicit user action. No mode silently switches based on state.

---

## 3. Lifecycle

Authoring state (drafts, configuration) persists across plugin restarts via the standard config storage location.

> **Phase 9 note:** `AuthoringHost` is currently eager-loaded at plugin startup. Lazy-load (no overhead for users who never author) and idle-unload (30-minute timeout) are deferred to a future phase.

---

## 4. Debug panels

Three panels in v1. Each is independently toggleable and remembers its position across sessions.

### 4.1 Player state panel

```
┌─ Player ─────────────────────────────┐
│ Zone:     419 (Coerthas Central)     │
│ Position: (157.94, -19.48, 53.02)    │
│ [📋 Copy as JSON]                    │
│                                      │
│ Job:      Dark Knight, Lv 30         │
│ Mount:    Dismounted                 │
│ Combat:   No                         │
│ HP:       11420 / 11420              │
│                                      │
│ Instance: None                       │
└──────────────────────────────────────┘
```

Position and zone update on a 250ms heartbeat. Copy-as-JSON produces a position object ready to paste into quest data. The heartbeat interval is configurable via `PluginConfig.AuthoringHeartbeatMs` (default 250ms).

> **Phase 9 note:** Job, mount state, combat, HP, and instance kind are not yet shown — `GameStateSnapshot` doesn't capture those fields. They can be added when `SnapshotAggregator` is extended to poll `IGameStateProvider`.

### 4.2 Quest state panel

```
┌─ Active Quests ──────────────────────┐
│ #2054 Ishgardian Justice             │
│   Sequence: 2                        │
│   Flags:    0x00 0x04 0x00 0x00 ...  │
│   [▼ Show all flags]                 │
│                                      │
│ #5678 Beast Tribe Daily              │
│   Sequence: 1                        │
│   Flags:    0x01 0x00 0x00 ...       │
│                                      │
│ Recent change (1.2s ago):            │
│   Quest #2054, flag bit 2 set        │
└──────────────────────────────────────┘
```

Lists all accepted quests with current sequence and flag values. The most recent state change is highlighted with a timestamp; this is the author's primary signal that "something just happened in response to what I did."

Flag display defaults to compact hex with click-to-expand for per-bit detail.

### 4.3 Interaction panel

```
┌─ Interaction ────────────────────────┐
│ Target:   Adelphel (NPC 1014875)     │
│   Position: (6.91, -1.92, 47.29)     │
│   Distance: 4.2                      │
│   [📋 Copy as JSON]                  │
│                                      │
│ Current UI: None                     │
│                                      │
│ Last interaction (3.4s ago):         │
│   Talked to Adelphel                 │
│   Dialogue: TEXT_JOBDRK301_02053_*   │
│                                      │
│ Last dialogue choice (3.1s ago):     │
│   Prompt: TEXT_..._Q1_000_115        │
│   Answer: TEXT_..._A1_000_116        │
└──────────────────────────────────────┘
```

Shows the current target (NPC or interactable), available quests from the targeted NPC, and the most recent interaction and dialogue choice.

**Available quests from this NPC** — when the player targets an NPC, the panel displays up to five quests filtered by `IsQuestAvailable`. This naturally handles class-specific quests (e.g. "Close to Home" for Gladiator vs Marauder — only the one valid for your current job appears). Each quest shows its name, ID, and a "Start Authoring" button. A "Show completed quests" toggle (default off) reveals already-finished quests for re-recording purposes.

> **Finding a quest ID without targeting the NPC:** two approaches:
> - `/qf quest <name>` — searches Lumina by name, returns up to 10 results with quest ID, level, and availability status. Best for general lookup.
> - `/qf debug offered-quest` — when the JournalAccept window is open (NPC is actively offering a quest), reads AtkValue[261] to identify the exact quest ID being offered *without* accepting it. Useful for class-specific quests where accepting determines which variant you get (e.g. "Close to Home" for GLA vs MRD vs CNJ). The JournalAccept AtkValue[261] stores the raw Lumina RowId without the `0x10000` flag; the command adds this automatically to return the full public quest ID.

Dialogue prompt and answer strings are shown with click-to-copy. Full sheet-reference browsing is deferred — see issue #11.

### 4.4 Allowlisted UI addons

The interaction panel only shows addons in a curated allowlist matching what the engine cares about: `Talk`, `SelectYesno`, `SelectString`, `JournalResult`, `ShopExchangeItem`, `ContentsFinderConfirm`, `CutSceneSelectString`, and similar quest-relevant addons.

Other addons (chat, inventory, character UI, etc.) are filtered out. Authors needing to inspect arbitrary addons have two options:

- `/qf debug addon <name>` — dumps the first 30 AtkValues of any open addon to chat and the Dalamud log. Useful for quick value lookups (e.g. `/qf debug addon JournalAccept` to inspect the quest accept window).
- Dalamud's `/xldata` — full structural inspector with all AtkValues, node tree, and collision data. Use this when you need more than 30 values or want to inspect the node hierarchy.

---

## 5. The "Record current action" workflow

Active recording (Author mode only) collects steps into a draft.

### 5.1 Single button, contextual inference

A single "Record current action" button. Pressing it captures the player's most recent action and infers the step type from context:

| Recent action | Inferred type | Phase 9 |
|---------------|---------------|---------|
| Quest completed | `turn-in` | ✅ Auto-inferred |
| Quest accepted | `accept` | ✅ Auto-inferred |
| Quest sequence advanced | `talk` | ✅ Auto-inferred |
| Zone changed | `travel` | ✅ Auto-inferred |
| Quest flags changed (no seq change) | `talk` (flag predicate) | ✅ Auto-inferred |
| Dialogue answer changed only | `talk` | ✅ Auto-inferred |
| NPC target changed only | `talk` (low confidence) | ✅ Auto-inferred |
| Interacted with an EventObject | `interact-object` or `pickup-item` | Override required |
| Key item appeared in inventory | `pickup-item` | ✅ Auto-inferred (Rule 2.3) |
| Key item disappeared from inventory (handed over via Request addon) | `hand-over-item` | ✅ Auto-inferred (Rule 2.4) |
| `AethernetShardTargeted` observation fires (aethernet shard targeted) | `attune` | ✅ Auto-inferred (Rule 2.5) |
| Inventory hash changes between before/after observations (`InventoryChangedEvent`) | `hand-over-item` using the items that disappeared | ✅ Auto-inferred (Rule 2.6) |
| Duty entry | `duty` | Override required |
| Used emote | `use-emote` | Override required |
| Sent chat message | `say-chat-message` | Override required |
| Equipped an item | `equip-gear-for-quest` | Override required |
| Cutscene start/end | `cutscene` | Override required |

The inference is shown in a preview modal:

```
┌─ Record Step ────────────────────────┐
│ Inferred type: talk                  │
│ Target:        NPC 1014879           │
│ Position:      (134.05, -20.02, ...) │
│ Zone:          418                   │
│                                      │
│ Dialogue observed:                   │
│  - List: TEXT_..._Q1_000_115         │
│           Answer: ..._A1_000_116     │
│                                      │
│ Suggested expect:                    │
│   questSequence(2054) >= 2           │
│                                      │
│ Step ID: [talk-to-witness-a______]   │
│ Notes:   [_______________________]   │
│                                      │
│ [✓ Record]  [✗ Cancel]  [Override]   │
└──────────────────────────────────────┘
```

The author can:

- Accept the inference as-is (`Record`)
- Override the inferred type (`Override` → dropdown of step types)
- Edit the suggested `expect` predicate
- Provide a step ID (auto-suggested from context)
- Add notes
- Cancel without recording

### 5.2 Suggested `expect` predicates

The recorder observes state changes during the action and suggests an `expect` based on the strongest signal:

- Sequence advanced → `questSequence(N) >= K`
- Flag changed but sequence didn't → `questFlag(N, bit)`
- Quest accepted → `isQuestAccepted(N)`
- Quest completed → `isQuestComplete(N)`
- Zone changed → `playerZone() == N`
- Inventory changed → `playerHasItem(item:N, count)`

`StepInferenceEngine` always emits a **single** predicate string (the strongest signal wins). If the author needs to combine predicates, they edit the expect field manually in the record modal. See `PHASE_9_PLAN.md §2.3` for the single-predicate policy rationale.

### 5.3 No observed change — author writes manually

If no state change was observed (rare; usually a step the engine should signal differently), the inference modal shows the author the captured action and an empty `expect` field. The author writes one manually, drawing from the panels' current state.

---

## 6. Draft management

### 6.1 Per-quest scoping

Drafts are identified by quest ID. The author selects "Start authoring quest 2054" from the UI; recording activity is scoped to that quest until they switch.

Multiple drafts can exist simultaneously (one per quest), but only one is the "active" draft accepting new recordings.

### 6.2 Storage location

```
%APPDATA%\XIVLauncher\pluginConfigs\QuestForge\drafts\
├── 2054.draft.json           # active draft
├── 2054.draft.json.bak.001   # version history
├── 2054.draft.json.bak.002
├── 2054.draft.json.bak.003
├── 2054.draft.json.bak.004
├── 2054.draft.json.bak.005
└── 5678.draft.json
```

Drafts are saved automatically:
- Immediately after each `Record` action
- On periodic auto-save (every 60 seconds if anything changed)
- On Author mode exit

Last 5 saves per quest are retained as numbered backups (`.bak.001` is most recent). Older backups are deleted automatically.

### 6.3 Authoring session panel

```
┌─ Authoring Quest #2054 — Ishgardian Justice ────────┐
│ Sequence 0:                                         │
│   ✓ equip-soul-of-drk          [edit] [✗ delete]   │
│   ✓ equip-recommended          [edit] [✗ delete]   │
│   ✓ travel-to-questgiver       [edit] [✗ delete]   │
│   ✓ accept-quest               [edit] [✗ delete]   │
│                                                     │
│ Sequence 1:                                         │
│   ✓ talk-to-witness-a          [edit] [✗ delete]   │
│                                                     │
│ Sequence 2:                                         │
│   ⚠ talk-to-witness-b (validation: no expect)      │
│                                                     │
│ ─────────────────────────────────────────────────── │
│ [+ Record next step]  [⚙ Validate now]              │
│ [📤 Export draft]      [📁 Open file location]      │
│ [⏸ Pause authoring]   [✗ Discard this quest]       │
└─────────────────────────────────────────────────────┘
```

Per-step controls:

- `[edit]` — opens a modal showing the step JSON; author can hand-edit before saving back
- `[✗ delete]` — removes the step from the draft
- Status icons: `✓` valid, `⚠` validation warning, `✗` validation error

Per-session controls:

- `[+ Record next step]` — the primary action button; opens the record modal
- `[⚙ Validate now]` — runs the local validator; results shown inline
- `[📤 Export draft]` — saves to a user-specified path (e.g., to a git clone of `questforge-data`)
- `[📁 Open file location]` — opens explorer at the draft directory
- `[⏸ Pause authoring]` — exits Author mode without deleting the draft; can be resumed later
- `[✗ Discard this quest]` — deletes the draft and all backups, with confirmation

### 6.4 Re-recording a step

Author selects an existing step and clicks `[edit]`, then a "Re-record" option in the edit modal. Plugin returns to record mode, scoped to replacing this step's entry. Author performs the action again; new recording replaces the old.

### 6.5 Validation feedback

`[⚙ Validate now]` invokes the local validator with the draft. Validation errors and warnings are shown inline on each step with hover-for-details. Examples:

- `✗ Step ID 'talk-x' is not unique within quest`
- `⚠ NPC 1014879 has moved 8.2 units since last verification`
- `✗ Predicate 'questSeq(...)' — unknown function 'questSeq', did you mean 'questSequence'?`
- `✗ Dialogue sheet reference 'TEXT_..._XYZ' does not resolve`

---

## 7. Resume vs. restart authoring

When the author selects "Start authoring quest N" and the quest is already partially complete in their journal, the plugin offers two options:

### 7.1 Resume from current state

Recording starts at the player's current quest sequence. Earlier sequences are left as gaps in the draft. The author can:

- Author the gap sequences later, in a different play session (with a fresh quest accept)
- Import a previous draft's earlier sequences if one exists
- Submit the partial draft as-is for collaborative completion

This is the common case for "I noticed step 5 is broken; let me re-record just that one."

### 7.2 Restart authoring

Author manually abandons the quest in-game (the engine never auto-abandons) and re-accepts it to record from sequence 0. The plugin confirms the choice explicitly:

```
┌─ Restart authoring? ────────────────────┐
│ This will discard your current quest    │
│ progress for #2054. You will need to    │
│ manually re-accept the quest after      │
│ abandoning it.                          │
│                                         │
│ The plugin does NOT abandon quests for  │
│ you. After clicking Continue, please    │
│ abandon the quest yourself via the      │
│ in-game journal.                        │
│                                         │
│ [✗ Cancel]  [Continue]                  │
└─────────────────────────────────────────┘
```

### 7.3 No abandon automation

The engine never abandons quests. This is per `ADAPTERS.md` §8 — `AbandonQuest` is an adapter method but only the user's explicit UI action triggers it. Authoring mode follows the same rule.

---

## 8. Sheet reference discovery

Two related discovery needs: finding the quest ID for a quest you want to author, and finding dialogue sheet references for quest steps.

### Finding a quest by name

Use `/qf quest <name>` — searches Lumina for quests matching the name (partial, case-insensitive) and returns up to 10 results:

```
[66104] Close to Home — Lv1, Gladiator — available ✓
[66105] Close to Home — Lv1, Marauder — locked (wrong job)
[66106] Close to Home — Lv1, Conjurer — locked (wrong job)
```

Use the ID from the results with `/qf author <id>`. For class-specific quests the `InteractionPanel`'s NPC quest list (§4.3) handles this automatically when you're standing next to the quest-giver — only the valid class variant appears.

### Finding the quest ID from an open accept window

When an NPC is offering you a quest and the JournalAccept window is open, use `/qf debug offered-quest` — this reads AtkValue[261] on the `JournalAccept` addon to identify the exact quest being offered without you having to accept it. Useful when:

- Multiple variants of a same-named quest exist (class/job-specific quests) and you want to confirm which ID the game will assign before committing
- A quest cannot be easily abandoned and re-accepted once accepted

The command prints the public quest ID and name to both chat and the Dalamud log.

### Finding dialogue sheet references

The `InteractionPanel` (§4.3) shows the most recently observed dialogue prompt and answer strings. Full sheet-reference browsing is deferred — see issue #11.

### 8.1 Quest dialogue browser (planned)

Available via a `[📚 Browse dialogue]` button when a quest is the authoring target (not yet implemented):

```
┌─ Quest #2054 Dialogue ───────────────────────┐
│ Filter: [______________________]             │
│                                              │
│ TEXT_JOBDRK301_02054_Q1_000_115              │
│   EN: "What say you, Adelphel?"              │
│   DE: "Was sagt Ihr dazu, Adelphel?"         │
│   FR: "Qu'en dites-vous, Adelphel?"          │
│   JA: "アデルフェルよ、いかがか?"               │
│   [📋 Copy reference]                        │
│                                              │
│ TEXT_JOBDRK301_02054_A1_000_116              │
│   EN: "I will see this through."             │
│   ...                                        │
└──────────────────────────────────────────────┘
```

This is read-only browsing of Lumina data filtered to the quest's text sheets. No authoring decisions; just discovery.

### 8.2 Cross-language verification

When a sheet reference is selected (in any panel), the plugin can show all four shipped languages side-by-side. This helps authors confirm "yes, this is the option I meant to pick" especially for languages they don't speak.

This is also useful for the validator's tier-2 checks — sheet references that resolve in EN but not DE/FR/JA produce a validation warning.

---

## 9. Trace integration

Author mode and trace recording are complementary but separate.

### 9.1 Author mode produces drafts

The draft is a JSON quest file under development. Output goes to `drafts/<questId>.draft.json`.

### 9.2 Trace recording produces traces

The trace recorder (per `TRACE_FORMAT.md`) records engine execution. Author mode doesn't run the engine, so authoring sessions do not produce traces.

### 9.3 Verifying authored quests

After a draft is exported and committed, the standard CI replay test workflow applies:

1. Run the engine against the new quest data (in a play session)
2. Trace recorder produces a trace file
3. Author commits the trace as the canonical fixture via PR
4. CI replays the trace against subsequent engine builds

Authoring mode does not produce trace fixtures directly. Authors play through their own draft via the engine to generate the trace.

---

## 10. Export and PR submission

### 10.1 Export draft

`[📤 Export draft]` saves the draft to a user-specified path:

```
┌─ Export draft ──────────────────────────┐
│ Save to:                                │
│ [C:\dev\questforge-data\quests\hw\... ] │
│ [📁 Browse]                             │
│                                         │
│ Filename: 2054_Ishgardian-Justice.json  │
│                                         │
│ Options:                                │
│ [✓] Run validator before export         │
│ [ ] Open file after export              │
│                                         │
│ [✗ Cancel]  [📤 Export]                 │
└─────────────────────────────────────────┘
```

The default filename and folder structure match `questforge-data` conventions. Authors with a local clone export directly into the appropriate folder; authors without a clone save somewhere they can attach to a PR or paste into the web editor.

### 10.2 PR submission workflow

For v1, PR submission is manual — author exports to a clone, commits, pushes, opens a PR. Plugin doesn't automate git or GitHub interactions.

Future enhancement: a `[📤 Submit PR]` flow that opens a fork-and-branch creation in the browser via GitHub's web flow. Considered for v2 once authoring patterns stabilize.

### 10.3 Validation before export

By default, validation runs before export. Drafts with errors warn the author:

```
┌─ Export blocked — 2 errors ─────────────┐
│ ✗ Step 'talk-to-witness-b' has no       │
│   `expect` field                        │
│ ✗ Predicate 'questSeq(2054) >= 3'       │
│   has unknown function 'questSeq'       │
│                                         │
│ [✗ Cancel]  [Export anyway]             │
└─────────────────────────────────────────┘
```

Drafts with warnings (only) export by default but display the warning summary.

---

## 11. What authoring mode does NOT do

Explicit scope boundaries to prevent feature creep:

- **No git/GitHub interaction in v1.** Author exports a file; everything else is manual.
- **No live editing of running automation.** Author mode and engine execution are mutually exclusive.
- **No support for general plugin development.** Other Dalamud plugins have their own developer tools.
- **No quest data validation against game state mid-authoring.** The author may be on a different patch, character, or job than the eventual user; mid-authoring validation against current state would produce false positives. Validation runs against game data (Lumina), not the current player's state.
- **No automated multi-language testing.** Authors record in their own language; CI handles multi-lang verification post-PR.
- **No support for fragments or branches in v1's recording workflow.** These are advanced authoring concerns; v1 records flat per-sequence steps. Authors who want to use fragments or branches edit the exported JSON manually after recording.

---

## 12. Performance and observability

Authoring mode's overhead during active use:

- Three UI panels, each on event-driven updates plus a 250ms heartbeat for continuous fields
- Event subscription tap on quest state changes, zone transitions, dialogue events
- Draft buffer in memory (≤1 MB per draft)
- Auto-save every 60 seconds

For most users this is fine. On Steam Deck or other constrained hardware, the heartbeat is configurable in plugin settings (down to 1000ms or off entirely).

Authoring mode emits its own structured log entries (separate from trace recording) for debugging the authoring workflow itself. Tagged `authoring.*` and filtered out of normal plugin logs by default.

---

## Appendix A: Open questions for revision

- **Cross-quest fragment authoring.** No defined workflow yet for authoring a new fragment. v1 expects fragments to be hand-written.
- **Branch authoring.** Same — the recording workflow doesn't yet handle branches. v1 expects branches to be hand-added to drafts post-export.
- **Bulk operations.** No way to record multiple steps in sequence without modal confirmation between each. May add a "batch mode" if it becomes a friction point.
- **Multi-character authoring.** No defined behavior when the author switches characters mid-session. v1 assumes single character per authoring session.
- **Authoring on Linux/Steam Deck.** ImGui rendering works identically. No platform-specific concerns expected, but UI scaling on Steam Deck's smaller screen needs testing.

## Appendix B: Glossary

- **Inspect mode** — Passive authoring sub-mode. Debug panels visible, no recording.
- **Author mode** — Active authoring sub-mode. Debug panels plus recording workflow.
- **Draft** — A quest file under development in authoring mode, stored locally.
- **Sheet reference** — Direct identifier into Square Enix's text data sheets, used in dialogue choices.
- **Heartbeat** — Periodic UI refresh for continuously-changing fields (default 250ms).

## Appendix C: Design decisions and rationale

| Decision | Alternative considered | Why |
|----------|----------------------|-----|
| Two sub-modes (Inspect, Author) | Single authoring mode | Inspect serves debugging during automation; combining them invites mode confusion |
| Single "Record" button with inference | Type-specific buttons | Authors often don't know what they just did; inference + preview is better UX |
| Sheet references read from game data | Authored dialogue keys | Use SE's own data; no key conventions to maintain |
| Per-quest draft scoping | Single global draft | Multiple quests in flight; clear ownership |
| Manual git/PR workflow | Automated PR submission | Scope creep for v1; revisit in v2 |
| 250ms heartbeat + events | Pure events or pure polling | Events miss position drift; polling alone is wasteful |
| Author mode mutually exclusive with engine | Both running simultaneously | Recording activity during automation would conflate user and engine actions |
| Engine never abandons quests | Auto-abandon on restart authoring | User retains control over quest journal state |
| Validation runs against game data | Validation against current player state | Player-state validation produces false positives for authors playing on different characters |
