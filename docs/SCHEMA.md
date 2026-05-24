# Quest Schema Specification

**Status:** v1 draft — additive changes accepted until implementation begins; will hard-lock at first plugin release
**Schema version:** `1.0.0` (semantic versioning)
**Owners:** QuestForge maintainers
**Related:** [DESIGN.md](./DESIGN.md), [ADAPTERS.md](./ADAPTERS.md), [TRACE_FORMAT.md](./TRACE_FORMAT.md), [AUTHORING.md](./AUTHORING.md)

---

## 1. Purpose

The quest schema defines the data format for quest definitions. The schema is the contract between quest authors and the engine.

This document specifies:

- The shape of a quest file
- The predicate language used in `expect`, `skipIf`, and branch conditions
- The taxonomy of step types
- Schema versioning and migration
- Semantic validation rules

Quest files are JSON. Quest data lives in the [`questforge-data`](../questforge-data) repository organized by expansion and category. The plugin loads quest definitions at startup and exercises them via the engine described in `DESIGN.md` §5.

---

## 2. Top-level structure

```json
{
  "schemaVersion": "1.0.0",
  "id": 65657,
  "name": "Close to Home",
  "expansion": "arr",
  "category": "msq",
  "enabled": true,

  "supportStatus": {
    "implementation": "complete",
    "knownIssues": []
  },

  "lastVerifiedPatch": "7.4",

  "requirements": {
    "minLevel": 1,
    "maxLevel": null,
    "requiredJob": null,
    "requiredStartingClass": null,
    "prereqs": [{ "questId": 65656, "state": "complete" }]
  },

  "acceptFrom": {
    "npcId": 1000789,
    "zone": 128,
    "position": { "x": 9.5, "y": 40.0, "z": 14.2 }
  },

  "chain": {
    "previous": [65656],
    "next": [
      { "when": "default", "questId": 65658 }
    ]
  },

  "rewardOverride": null,

  "contributors": ["@alice"],
  "notes": null,

  "sequences": [
    {
      "sequence": 0,
      "steps": [ ... ]
    },
    {
      "sequence": 1,
      "steps": [ ... ]
    }
  ]
}
```

### 2.1 Required vs optional fields

| Field | Required | Notes |
|-------|----------|-------|
| `schemaVersion` | Yes | SemVer string. Validator checks plugin compatibility. |
| `id` | Yes | FFXIV quest ID (uint). Must exist in game data. |
| `name` | Yes | Display name. Should match the in-game quest name. |
| `expansion` | Yes | `arr`, `heavensward`, `stormblood`, `shadowbringers`, `endwalker`, `dawntrail`, future-named values. |
| `category` | Yes | `msq`, `class`, `job`, `role`, `blue-urgent`, `blue`, `side`. Drives scheduler tier (see `NEXT_STEPS.md` §Phase 8). |
| `enabled` | No | Defaults `true`. Set `false` to temporarily disable a quest. |
| `supportStatus` | Yes | See §2.2. |
| `lastVerifiedPatch` | Yes | Patch version string. Used by support-status UI. |
| `requirements` | Yes | Prerequisites. See §2.3. |
| `acceptFrom` | Yes | Quest giver. See §2.4. |
| `chain` | No | Chain links. Omitted for standalone quests. See §2.5. |
| `rewardOverride` | No | Override `EngineDecisionConfig.DefaultRewardStrategy`. Rare. See §2.6. |
| `contributors` | No | GitHub usernames of authors. Optional, additive over time. |
| `notes` | No | Free-text context for contributors. ≤500 chars. |
| `sequences` | Yes | Quest content, grouped by sequence number. See §3. |

### 2.2 `supportStatus`

```json
{
  "supportStatus": {
    "implementation": "complete",
    "knownIssues": [],
    "minigameSkippable": false
  }
}
```

- **`implementation`** — `complete` / `partial` / `none`. Author's assessment of coverage.
- **`knownIssues`** — list of human-readable issue summaries, each typically linking to a GitHub issue.
- **`minigameSkippable`** — present only on quests containing `minigame` steps. Derived by the validator from step contents and `IMinigameSkipper` capability. Authors do not write this field; the validator computes it.

The support-status UI (per `DESIGN.md` §9) composes this with external signals (CI replay status, telemetry).

### 2.3 `requirements`

```json
{
  "requirements": {
    "minLevel": 15,
    "maxLevel": null,
    "requiredJob": "Gladiator",
    "requiredStartingClass": null,
    "prereqs": [
      { "questId": 65600, "state": "complete" },
      { "questId": 65601, "state": "complete" }
    ]
  }
}
```

- **`minLevel`** — minimum character level. Validator confirms ≤ current cap.
- **`maxLevel`** — rarely set; some early-leveling quests cap out.
- **`requiredJob`** — specific job required. `null` if any job works.
- **`requiredStartingClass`** — pinned starting class, for quests gated by character creation choices.
- **`prereqs`** — list of `{questId, state}` where `state` is `complete` or `accepted`.

The engine evaluates requirements before starting a quest. Failed requirements surface to the user via `AwaitUserCompletion` ("Quest requires level 15 Gladiator; you are Pugilist level 12").

### 2.4 `acceptFrom`

```json
{
  "acceptFrom": {
    "npcId": 1000789,
    "zone": 128,
    "position": { "x": 9.5, "y": 40.0, "z": 14.2 }
  }
}
```

Position is a hint for routing; the engine uses `IGameStateProvider.FindNpc(npcId)` to locate the actual current position. If recorded position drifts more than 50 units from current game data, validator warns.

For quests that auto-accept (e.g., post-completion follow-up quests), `acceptFrom` is the giver's location at the moment of post-turn-in dialogue. Engine handles this naturally — the acceptance happens during the previous quest's turn-in flow.

### 2.5 `chain`

```json
{
  "chain": {
    "previous": [65656],
    "next": [
      { "when": "playerStartingClass() == \"Gladiator\"", "questId": 65700 },
      { "when": "playerStartingClass() == \"Pugilist\"", "questId": 65710 },
      { "when": "default", "questId": 65720 }
    ]
  }
}
```

- **`previous`** — flat list of quest IDs that lead here. Validator enforces bidirectional consistency.
- **`next`** — conditional forward branches. Evaluated in order at chain-transition time. Last entry must be `"when": "default"` unless the quest is a terminus.

A chain terminus uses `null`:

```json
{
  "next": [
    { "when": "playerLevel(\"Gladiator\") >= 30", "questId": null },
    { "when": "default", "questId": 1101 }
  ]
}
```

`questId: null` means "no further quest." Validator recognizes this and doesn't require a corresponding `previous` entry elsewhere.

**Chain-branch evaluation is one-shot, not polling.** Engine evaluates predicates exactly once at chain transition. Failed evaluation (no predicate matches and no `default` provided) is a validator error.

### 2.6 `rewardOverride`

```json
{
  "rewardOverride": {
    "strategy": "SpecificItem",
    "itemId": 12345
  }
}
```

Defaults to `null`. When set, overrides `EngineDecisionConfig.DefaultRewardStrategy` for this quest only. Used for quests where a specific reward is required for the storyline. Strategies match the enum in `ADAPTERS.md` §5.3.

---

## 3. Sequence-grouped steps

Quest content is organized by **sequence number**, matching FFXIV's in-game quest sequence progression. The game advances sequence at specific points; steps within a sequence are "things to do before sequence advances."

```json
{
  "sequences": [
    {
      "sequence": 0,
      "steps": [ ... ]
    },
    {
      "sequence": 1,
      "skipIf": "questFlag(65, 10)",
      "steps": [ ... ]
    },
    {
      "sequence": 255,
      "steps": [ ... ]
    }
  ]
}
```

### 3.1 Sequence semantics

- Sequence 0 is the initial state (quest accepted, no steps complete).
- Sequence numbers are strictly increasing in the `sequences` array.
- Sequence 255 is conventionally the **completion sequence** — typically containing the `turn-in` step.
- Gaps in numbering are allowed (e.g., sequences 0, 1, 2, 5, 255).
- Optional `skipIf` predicate at the sequence level. When true on entry, the engine skips the entire sequence and proceeds to the next. Useful when an entire sequence becomes irrelevant based on earlier choices (e.g., conditional questline branches that bypass certain stages).

The engine on resume reads `questSequence(id)` and jumps to the matching sequence block. This makes resume-after-crash deterministic without needing to evaluate every prior step's `expect`.

### 3.2 Step ordering within a sequence

Steps within a sequence execute in declared order. Each step's `expect` is checked before execution (for resume) and after execution (for completion). If `expect` is already satisfied, the engine advances to the next step.

### 3.3 `type` must be the first property in every step object

The quest schema uses `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` for step deserialization. System.Text.Json requires the discriminator property to appear **before** all other properties in the JSON object or deserialization will fail with a metadata error.

**Correct:**
```json
{ "type": "travel", "id": "my-step", "destination": { ... } }
```

**Incorrect (will throw at load time):**
```json
{ "id": "my-step", "type": "travel", "destination": { ... } }
```

This applies to all step objects in quest files, fragment files, and anywhere else a `Step` is serialised.

### 3.3 Why not flat steps?

This structure is borrowed from a working implementation (Questionable). Aligning with the game's own state machine has three benefits:

- Resume becomes trivial — read game state, find matching sequence.
- Traces are easier to read — events are naturally grouped by sequence.
- Authoring mode can scope recording to the current sequence and reset between sequences.

The cost is one extra level of nesting in the JSON; well worth it.

---

## 4. Step types

Twenty step types, each with a clear single responsibility.

| Type | Purpose |
|------|---------|
| `travel` | Navigate to a position, possibly via teleport |
| `talk` | Talk to an NPC, optionally with dialogue choices |
| `interact-object` | Interact with an `EventObject` |
| `pickup-item` | Pick up a quest item (postcondition is usually a flag, not inventory) |
| `hand-over-item` | Hand one or more key items to an NPC via FFXIV's Request popup (`Target: NpcLocation`, `Items: uint[]` item IDs) |
| `accept` | Accept a quest from an NPC |
| `turn-in` | Hand in a quest, including reward selection |
| `attune` | Attune to an aetheryte or aethernet shard (`Target: AetheryteId`); use `skipIf: isAttuned(id)` for idempotency |
| `combat` | Engage a target or wave of targets |
| `duty` | Enter and complete a duty (full duty or single-player duty via `kind`) |
| `cutscene` | Watch (or skip) a cutscene |
| `say-chat-message` | Send a chat message (`say`/`yell`/`shout` only) |
| `use-emote` | Use an emote |
| `use-item` | Use a quest item from inventory, optionally on a target (NPC, object, or position) |
| `use-action` | Execute a specific player action against a specific target |
| `equip-gear-for-quest` | Equip specific items |
| `equip-best-gear` | Equip best available gear via game/Stylist |
| `change-job` | Switch jobs (stubbed in v1; relies on existing gearsets) |
| `minigame` | Quest minigame; skippable via `IMinigameSkipper` if available |
| `await-user` | Pause for user-initiated progress |
| `branch` | Conditional sub-sequence dispatch |
| `fragment` | Inline a reusable fragment |

### 4.1 Common step fields

Every step has these fields:

```json
{
  "id": "go-to-baderon",
  "type": "travel",
  "zone": 129,
  "requiredZone": null,
  "expect": "questSequence(65657) >= 1",
  "skipIf": null,
  "stopDistance": null,
  "recover": null,
  "retry": null,
  "preconditions": null,
  "notes": null
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `id` | Yes | Unique within the quest. Must match `^[a-z][a-z0-9-]*$`. Used for recovery `goto` and trace correlation. |
| `type` | Yes | One of the values in the table above. |
| `zone` | Optional | Zone the step occurs in. If `playerZone() != zone` on entry, engine inserts implicit travel. Inferred from `target.zone` or `destination.zone` when those fields are present. |
| `requiredZone` | Optional | Zone the player must be in **before** this step can execute (the source/start zone). Distinct from `zone`, which is the destination/after zone used for display and grouping. Set by the authoring factory from the pre-step position; authors may edit. Reserved for cold-resume positioning (consumed by resume logic); the engine does not gate on it today. |
| `expect` | Yes | Postcondition predicate. Doubles as resume check. |
| `skipIf` | Optional | Skip-this-step predicate, for "this step doesn't apply" cases. Distinct from `expect`. |
| `stopDistance` | Optional | Override default stopping distance for navigation. |
| `recover` | Optional | Override default recovery ladder. See §6. |
| `retry` | Optional | Override `InteractionRetryConfig` for this step. `{maxAttempts, timeout, backoff}`. |
| `preconditions` | Optional | Force-required preconditions that bypass user policy. Currently supports `{minGearCondition: int}` to force repair before the step regardless of `AutoRepair: false`. |
| `notes` | Optional | ≤500 chars. Author context. |

### 4.1.1 Multi-target steps

Steps that target a specific entity (`talk`, `interact-object`, `pickup-item`, `use-action`) support an optional `targets` array (plural) as an alternative to `target` (singular). Used when multiple entities all advance the same postcondition.

```json
{
  "id": "interview-witnesses",
  "type": "talk",
  "targets": [
    { "npcId": 1014879, "zone": 418, "position": {...} },
    { "npcId": 1014880, "zone": 418, "position": {...} },
    { "npcId": 1014881, "zone": 418, "position": {...} }
  ],
  "targetOrder": "any",
  "expect": {
    "all": [
      "questFlag(65, 1)",
      "questFlag(65, 2)",
      "questFlag(65, 3)"
    ]
  }
}
```

`target` and `targets` are mutually exclusive — validator enforces.

`targetOrder` values:
- `"sequential"` — visit in declared order (rarely useful; declare separate steps instead — validator warns)
- `"any"` (default) — engine picks order based on efficiency
- `"nearest-first"` — explicit: always go to closest first

Engine behavior:
1. Filter targets by which haven't been visited (using observable state via `expect`)
2. Pick next target per `targetOrder`
3. Navigate + interact
4. Re-check `expect`; advance if satisfied, otherwise pick next remaining target
5. Continue until `expect` satisfied or no targets remain

### 4.2 `expect` and `skipIf` semantics

- **`expect`** is the step's postcondition. The engine checks it on entry (skip if already met) and after each action (advance when met).
- **`skipIf`** is an explicit "this step doesn't apply right now" predicate. Checked once on entry. If true, the engine skips the step regardless of `expect`.

Example showing the distinction:

```json
{
  "id": "equip-relic-weapon",
  "type": "equip-gear-for-quest",
  "items": [{ "slot": "mainhand", "itemId": 10400 }],
  "expect": "playerHasEquipped(item:10400, slot:mainhand)",
  "skipIf": "not playerHasItem(item:10400, 1)"
}
```

If the player doesn't have the item, `skipIf` fires and the step is skipped entirely (e.g., a previous run consumed it). If they have it and have it equipped, `expect` is satisfied and the step is skipped. If they have it but haven't equipped it, the step runs.

### 4.3 `expect` structured form

For complex postconditions, `expect` may be an object with `all` or `any`:

```json
"expect": "questSequence(65) >= 3"

"expect": {
  "all": [
    "questFlag(65, 5)",
    "playerHasItem(item:12345, 1)"
  ]
}

"expect": {
  "any": [
    "questSequence(65) >= 3",
    "questFlag(65, 5)"
  ]
}
```

The structured form is syntactic sugar over `and`/`or` in the predicate language, with one diagnostic advantage: the engine reports which predicate of an `any:` set was satisfied. This shows up in trace `decision` events.

`skipIf` accepts the same forms.

### 4.4 `travel`

```json
{
  "id": "travel-to-ishgard",
  "type": "travel",
  "destination": {
    "zone": 419,
    "position": { "x": 157.94, "y": -19.47, "z": 53.02 }
  },
  "routeHint": {
    "aetheryte": "Ishgard",
    "aethernet": ["[Ishgard] Aetheryte Plaza", "[Ishgard] The Brume"]
  },
  "expect": "playerZone() == 419"
}
```

`destination` is required: `{zone, position}` or `{zone, aetheryteId}`.

`routeHint` is optional. When present, engine prefers the hinted route if feasible (attuned, affordable). When absent or infeasible, engine falls back to its computed travel strategy.

`stopDistance` overrides the default arrival distance (default 3.0 units; some destinations need more, e.g., 7 for NPCs with offset hitboxes).

### 4.5 `talk`

```json
{
  "id": "talk-to-baderon",
  "type": "talk",
  "target": {
    "npcId": 1000790,
    "zone": 129,
    "position": { "x": -10.2, "y": 40.0, "z": 14.5 }
  },
  "dialogueChoices": [
    { "type": "list", "prompt": "TEXT_JOBDRK301_02054_Q1_000_115", "answer": "TEXT_JOBDRK301_02054_A1_000_116" },
    { "type": "yesno", "prompt": "TEXT_JOBDRK301_02054_Q1_001_120", "answer": "yes" }
  ],
  "expect": "questSequence(65657) >= 1"
}
```

`dialogueChoices` is an ordered list of expected dialogue interactions. Each entry has:

- `type` — `"list"` (multi-option select), `"yesno"` (binary confirmation), `"talk"` (advance with no choice)
- `prompt` — game-data text sheet reference (the question)
- `answer` — for `list`, a text sheet reference (the option to pick); for `yesno`, the literal `"yes"` or `"no"`; omitted for `talk`

The dialogue references are direct pointers into FFXIV's text data sheets — stable across patches, localized by Lumina, defined by Square Enix. Authors do not invent keys; they record the references the game data already provides. See `AUTHORING.md` for how the authoring mode surfaces these.

`dialogueChoices` may be empty for "just advance dialogue, no choices expected."

### 4.6 `interact-object` and `pickup-item`

```json
{
  "id": "pickup-suspicious-letter",
  "type": "pickup-item",
  "target": {
    "interactableId": 2001234,
    "zone": 134,
    "position": { "x": 81.5, "y": 7.0, "z": 32.2 }
  },
  "expect": "questFlag(65657, 3)"
}
```

`pickup-item` semantically signals "this is a pickup" for diagnostics and authoring; functionally identical to `interact-object`. The author writes whatever postcondition actually proves pickup (usually a flag).

```json
{
  "id": "ring-the-bell",
  "type": "interact-object",
  "target": { "interactableId": 2001500, "zone": 134, "position": {...} },
  "expect": "questFlag(65657, 5)"
}
```

### 4.7 `hand-over-item`

Used when a quest requires the player to physically hand a key item to an NPC via FFXIV's Request addon popup (distinct from dialogue-based turn-in).

```json
{
  "id": "hand-over-sword-hilt",
  "type": "hand-over-item",
  "target": {
    "npcId": 1003987,
    "zone": 130,
    "position": { "x": 21.84, "y": 7.0, "z": -81.13 }
  },
  "items": [2002001],
  "expect": "questSequence(66104) >= 2"
}
```

For quests that require multiple items (the Request addon supports up to 5 slots):
```json
{ "items": [2002001, 2002002], ... }
```

The engine navigates to the NPC (implied navigation from `target.position`), interacts to open the Request addon, places each item from the KeyItems inventory into successive HandIn slots via `InventoryManager.MoveItemSlot`, then clicks the Hand Over button once all slots are filled.

`items` is an array of Lumina item IDs. Use `/qf debug quest <id>` to identify key items associated with a quest.

In authoring mode, handing over a key item is auto-inferred as a `hand-over-item` step (Rule 2.4) because the key item disappears from the KeyItems inventory container.

### 4.8 `accept` and `turn-in`

```json
{
  "id": "accept-quest",
  "type": "accept",
  "target": {
    "npcId": 1000789,
    "zone": 128,
    "position": {...}
  },
  "expect": "isQuestAccepted(65657)"
}

{
  "id": "complete-quest",
  "type": "turn-in",
  "target": {
    "npcId": 1000790,
    "zone": 129,
    "position": {...}
  },
  "dialogueChoices": [ ... ],
  "expect": "isQuestComplete(65657)"
}
```

`accept` and `turn-in` are first-class step types (rather than dialogue sequences) because they're load-bearing for engine postconditions and have specific UX. `turn-in` handles reward selection internally based on `EngineDecisionConfig.DefaultRewardStrategy` and `rewardOverride`.

### 4.8 `combat`

```json
{
  "id": "engage-bandits",
  "type": "combat",
  "target": { "kind": "nearestHostile", "radius": 20 },
  "expect": "not playerInCombat()"
}
```

`target.kind` is one of:
- `"nearestHostile"` — engine queries `IGameStateProvider`, engages whatever's hostile within radius
- `"specificNpc"` — paired with `npcId`, engages a specific entity
- `"wave"` — engages all hostiles until none remain

The engine delegates to `ICombat`. Combat plugin rotation choices are out of scope.

### 4.9 `duty`

Duty steps cover both **full duties** (dungeons, trials, raids — queued via Duty Finder or Duty Support) and **single-player duties** (Quest Battles — directly entered through an NPC/object trigger). The `kind` field discriminates.

**Full duty:**

```json
{
  "id": "complete-the-dungeon",
  "type": "duty",
  "kind": "regular",
  "dutyId": 56,
  "entryNpc": {
    "npcId": 1014883,
    "zone": 419,
    "position": {...}
  },
  "fallbackOverride": null,
  "expect": "isQuestComplete(2054) or questSequence(2054) >= 5"
}
```

**Single-Player Duty (Quest Battle):**

```json
{
  "id": "complete-axe-trial",
  "type": "duty",
  "kind": "spd",
  "trigger": {
    "kind": "object",
    "interactableId": 2001234,
    "zone": 134,
    "position": { "x": 81.5, "y": 7.0, "z": 32.2 }
  },
  "expect": "questFlag(65849, 3)"
}
```

The `trigger.kind` field is `"npc"` (with `npcId`) or `"object"` (with `interactableId`). Matches the `InteractableOrNpc` discriminated union in the adapter.

**Differences:**

| Field | `kind: "regular"` | `kind: "spd"` |
|-------|------------------|---------------|
| `dutyId` | Required | Omitted |
| `entryNpc` | Required (Duty Finder trigger or Duty Support trigger) | — |
| `trigger` | — | Required (NPC or interactable that starts the SPD) |
| `fallbackOverride` | Optional — overrides `DutyFallbackPolicy` | Not applicable |
| Entry mechanism | `EnterDutyWithSupport` / `EnterDutyWithFinder` | `EnterSinglePlayerDuty` (adapter handles YesNo / difficulty SelectString) |
| Failure handling | Combat plugin handles wipes; player exits via game mechanics | Engine detects via `InstanceKind` → `None` without `expect`; retries via re-interacting with trigger |
| Difficulty selection | Not applicable | Engine picks per `PreferredDutyDifficulty` and `DutyFailurePolicy` from `DifficultyDialogState.AvailableDifficulties` |

**For both kinds:**

After triggering entry, the engine observes `InstanceKind` transitions. AutoDuty (full dungeons), BossMod (single-player duties), or the user manually executes the encounter. Engine resumes when `InstanceKind == None`, then evaluates `expect`.

**SPD postcondition note:** A successful SPD completion does **not** necessarily complete the quest. Most often it advances the quest sequence; sometimes it sets a flag. Quest authors write whatever `expect` postcondition actually proves the SPD outcome.

### 4.10 `cutscene`

```json
{
  "id": "watch-flashback",
  "type": "cutscene",
  "skip": "ifAllowed",
  "expect": "questSequence(65657) >= 4"
}
```

`skip` values:
- `"never"` — always watch
- `"ifAllowed"` (default) — skip if `EngineDecisionConfig.CutsceneSkipPolicy` allows

`CutsceneSkipPolicy` is `AlwaysWatch` / `AlwaysSkip` / `FollowQuestDefinition` (default).

### 4.11 `say-chat-message` and `use-emote`

```json
{
  "id": "shout-for-help",
  "type": "say-chat-message",
  "channel": "say",
  "message": "TEXT_..._SAY_000_001",
  "target": {
    "npcId": 1000789,
    "zone": 132,
    "position": {...}
  },
  "expect": "questFlag(65657, 2)"
}

{
  "id": "salute-the-commander",
  "type": "use-emote",
  "emoteId": 7,
  "target": {
    "npcId": 1000789,
    "zone": 132,
    "position": {...}
  },
  "expect": "questFlag(65657, 5)"
}
```

These are non-standard interactions, frequently directed at NPCs. `target` is optional — emotes/messages without a target run wherever the player is.

`channel` is one of `"say"`, `"yell"`, `"shout"`. Party/FC/linkshell channels are rejected by the adapter to prevent unintended spam.

### 4.12 `use-item`

Use a quest item from inventory. The target can be an NPC, a world object, a ground position (for AoE-placement items), or omitted entirely.

```json
{
  "id": "use-summoning-bell",
  "type": "use-item",
  "itemId": 12345,
  "expect": "questFlag(65657, 4)"
}

{
  "id": "use-letter-on-merchant",
  "type": "use-item",
  "itemId": 12347,
  "target": {
    "kind": "npc",
    "npcId": 1000789,
    "zone": 130,
    "position": {...}
  },
  "expect": "questSequence(65657) >= 3"
}

{
  "id": "use-key-on-door",
  "type": "use-item",
  "itemId": 12346,
  "target": {
    "kind": "object",
    "interactableId": 2001500,
    "zone": 134,
    "position": {...}
  },
  "expect": "questFlag(65657, 5)"
}

{
  "id": "throw-dynamite-on-rocks",
  "type": "use-item",
  "itemId": 12348,
  "target": {
    "kind": "position",
    "zone": 134,
    "position": { "x": 81.5, "y": 7.0, "z": 32.2 },
    "tolerance": 3.0
  },
  "expect": "questFlag(65657, 7)"
}
```

`target.kind` values:
- `"npc"` — use the item on an NPC (delivering a letter, etc.). Requires `npcId`.
- `"object"` — use the item on a world object (key on a door, etc.). Requires `interactableId`.
- `"position"` — use an AoE-placement item at a ground position (throwing dynamite, dropping flares, placing markers). Requires `position` and `tolerance` (navigation radius — engine arrives within `tolerance` units before placing).

If `target` is omitted, the item is used from inventory directly with no targeting (summoning bells, telephones, single-use consumables that trigger flags).

Each entry maps to one of `IInteractor.UseItem`, `UseItemOnTarget`, `UseItemOnObject`, `UseItemOnPosition`. Precondition `playerHasItem(itemId, 1)` is implicit; authors may add explicit `skipIf` if the item is consumed in earlier runs.

### 4.13 `use-action`

Execute a specific player action against a specific target. Used for quests that require combat skills against quest objects (e.g., the Marauder class quest "Axe in the Stone" requires Heavy Swing on rocks three times).

```json
{
  "id": "heavy-swing-rocks",
  "type": "use-action",
  "actionId": 31,
  "target": {
    "kind": "object",
    "interactableId": 2001234,
    "zone": 134,
    "position": {...}
  },
  "repeatUntilExpect": true,
  "expect": "questFlag(65849, 3)"
}

{
  "id": "use-special-ability",
  "type": "use-action",
  "actionId": 1234,
  "target": {
    "kind": "npc",
    "npcId": 1000789,
    "zone": 132,
    "position": {...}
  },
  "repeatUntilExpect": false,
  "expect": "questFlag(65849, 5)"
}
```

`actionId` is the FFXIV action ID (from game data — same ID space as combat skills).

`target.kind` is `"npc"` or `"object"` (no `"position"` for actions in v1).

`repeatUntilExpect`:
- `true` — engine repeats the action (respecting cooldowns and MP) until `expect` is satisfied. Common for "use Heavy Swing on these N rocks" patterns.
- `false` (default) — one execution. Engine retries on failure per recovery policy.

Maps to `ICombat.UseAction` or `ICombat.UseActionOnObject`. Requires a combat plugin to be configured (hard dependency).

### 4.14 `equip-gear-for-quest` and `equip-best-gear`

```json
{
  "id": "equip-borrowed-armor",
  "type": "equip-gear-for-quest",
  "items": [
    { "slot": "body", "itemId": 12345 },
    { "slot": "head", "itemId": 12346 }
  ],
  "expect": "playerHasEquipped(item:12345, slot:body) and playerHasEquipped(item:12346, slot:head)"
}

{
  "id": "gear-up-for-dungeon",
  "type": "equip-best-gear",
  "constraints": { "minItemLevel": 50 },
  "expect": "playerAverageItemLevel() >= 50"
}
```

`equip-gear-for-quest` specifies exact items. Engine equips via `IGearManager.EquipItem`.

`equip-best-gear` invokes the game's recommended-gear function (or Stylist if available per plugin setting). `constraints` are optional and apply post-equip — if not met, engine surfaces failure.

`slot` values match FFXIV equip slots: `mainhand`, `offhand`, `head`, `body`, `hands`, `legs`, `feet`, `earrings`, `necklace`, `bracelets`, `ringR`, `ringL`.

### 4.15 `change-job`

```json
{
  "id": "switch-to-gladiator",
  "type": "change-job",
  "job": "Gladiator",
  "expect": "currentJob() == \"Gladiator\""
}
```

V1 implementation requires an existing gearset matching the job. If absent, the step fails and surfaces via `AwaitUserCompletion`. V2 may auto-create gearsets.

### 4.16 `minigame`

```json
{
  "id": "sniper-target-practice",
  "type": "minigame",
  "kind": "sniping",
  "skip": "ifAllowed",
  "expect": "questFlag(65657, 7)"
}
```

`kind` categorizes the minigame for the skip handler dispatch:
- `"sniping"` — implemented in v1
- `"memory"`, `"aiming"`, `"rhythm"`, `"selection"`, `"other"` — stubbed; falls back to `AwaitUserCompletion`

`skip` values:
- `"never"` — always AwaitUserCompletion
- `"ifAllowed"` (default) — skip via `IMinigameSkipper` if both `EngineDecisionConfig.AllowMinigameSkipping == true` and `IsKindSkippable(kind)` returns true; otherwise AwaitUserCompletion
- `"always"` — require skip; fail if unavailable

`AllowMinigameSkipping` defaults to `false` (opt-in by user).

### 4.17 `await-user`

```json
{
  "id": "complete-the-jumping-puzzle",
  "type": "await-user",
  "reason": "Please complete the jumping puzzle, then return to the questgiver.",
  "expect": "questFlag(65657, 8)"
}
```

Surfaces a persistent notification to the user. Engine polls `expect` until satisfied; no timeout. See `ADAPTERS.md` §15.1.

### 4.18 `branch`

```json
{
  "id": "fight-or-flee",
  "type": "branch",
  "branches": [
    {
      "when": "playerLevel() >= 15",
      "steps": [ ... ]
    },
    {
      "when": "default",
      "steps": [ ... ]
    }
  ],
  "expect": "questSequence(65657) >= 4"
}
```

Branches evaluate in order; first matching `when` predicate wins. Last branch must be `"when": "default"`.

Recovery `goto` within a branch sub-sequence can only target steps in the same branch — cross-branch jumps are validator errors.

### 4.19 `fragment`

```json
{
  "id": "travel-uldah-to-gridania",
  "type": "fragment",
  "ref": "travel/uldah-to-gridania",
  "params": {
    "finalPosition": { "x": 81.5, "y": 7.0, "z": 32.2 }
  },
  "expect": "playerZone() == 132"
}
```

Fragments live in `fragments/` in the data repo. Reference syntax matches file path (without `.json`).

Fragments themselves look like quests minus the metadata; see §5.

---

## 5. Fragments

Reusable sub-sequences. Fragment files live in `fragments/`:

```
fragments/
├── travel/
│   ├── uldah-to-gridania.json
│   └── limsa-to-uldah.json
└── common/
    └── return-to-questgiver.json
```

### 5.1 Fragment file shape

```json
{
  "schemaVersion": "1.0.0",
  "fragmentId": "travel/uldah-to-gridania",
  "parameters": [
    { "name": "finalPosition", "type": "position", "required": true }
  ],
  "steps": [
    {
      "id": "teleport-to-gridania",
      "type": "travel",
      "destination": { "zone": 132, "aetheryteId": 3 },
      "expect": "playerZone() == 132"
    },
    {
      "id": "walk-to-final",
      "type": "travel",
      "destination": { "zone": 132, "position": "${finalPosition}" },
      "expect": "playerNear(${finalPosition}, 3.0)"
    }
  ]
}
```

### 5.2 Parameter substitution

`${name}` placeholders are substituted at engine-load time. Parameter types:

- `position` — `{x, y, z}` object
- `npcId` — uint
- `itemId` — uint
- `string` — string

Validator confirms:
- Referenced fragments exist
- Required parameters are provided
- Provided parameter values match declared types
- No fragment recursion (direct or transitive)

### 5.3 No nested fragments in v1

Fragments cannot reference other fragments. Keeps validation tractable and resolution shallow. May be revisited in v2 if patterns demand it.

---

## 6. Recovery overrides

The default recovery ladders in `ADAPTERS.md` §15 cover most cases. Per-step overrides handle exceptions.

```json
{
  "id": "step-5",
  "type": "travel",
  "destination": {...},
  "expect": "playerZone() == 155",
  "recover": {
    "onTimeout": { "action": "useReturn", "thenRetry": true },
    "onObstacle": { "action": "goto", "stepId": "step-4" },
    "onAdapterError": { "action": "awaitUser", "reason": "Navigation broken, please help" }
  }
}
```

### 6.1 Recovery action types

| Action | Parameters | Meaning |
|--------|-----------|---------|
| `retry` | `maxAttempts`, `backoff` | Retry the current step |
| `goto` | `stepId` | Jump to another step in the same sequence (or same branch sub-sequence) |
| `useReturn` | `thenRetry` | Invoke `/return`, optionally retry the current step after |
| `useTeleport` | `aetheryteId`, `thenRetry` | Teleport to a specific aetheryte, optionally retry |
| `awaitUser` | `reason` | Surface to user via `AwaitUserCompletion` |
| `abandon` | — | Fail the quest with `CompletionFailure` |

### 6.2 Trigger keys

| Key | Triggered on |
|-----|--------------|
| `onTimeout` | Step exceeded its timeout |
| `onObstacle` | `NavigationOutcome.StoppedByObstacle` |
| `onAdapterError` | Any `adapter.error` during step execution |
| `onPostconditionFailed` | After action completed but `expect` not satisfied |
| `onPlayerDefeated` | Player died during step |

Unspecified triggers fall through to default recovery ladders.

### 6.3 Default `useTeleport` fallback ladder

When the engine triggers `useTeleport` (whether from default or override), it tries aetherytes in order:

1. Aetheryte in the destination zone, if attuned and affordable
2. Aetheryte in an adjacent zone, if attuned and affordable
3. Major-city aetheryte (Ul'dah, Limsa, Gridania), if any is attuned and affordable
4. `useReturn` to home aetheryte
5. `awaitUser`

---

## 7. Predicate language

Predicates appear in `expect`, `skipIf`, branch `when`, chain branch `when`, and recovery triggers.

### 7.1 Grammar

```
predicate     := atom | binary | unary | grouped
atom          := state-fn '(' args? ')' | comparison | 'default'
comparison    := state-fn '(' args? ')' op literal
op            := '==' | '!=' | '>' | '<' | '>=' | '<='
binary        := predicate boolean-op predicate
boolean-op    := 'and' | 'or'   -- C-style && and || are NOT lexed; authors must use keywords
unary         := 'not' predicate
grouped       := '(' predicate ')'
args          := arg (',' arg)*
arg           := number | string | identifier
literal       := number | string | identifier | enum-value
```

### 7.2 State functions

Drawn from `IGameStateProvider`, `IQuestState`, and a few derived helpers.

| Function | Returns | Description |
|----------|---------|-------------|
| `questSequence(questId)` | int | Current sequence value |
| `questFlag(questId, bit)` | bool | Specific bit set |
| `questFlags(questId)` | uint | Full flag value |
| `questFlagAny(questId, bit, ...)` | bool | Any of the specified bits set |
| `questFlagAll(questId, bit, ...)` | bool | All of the specified bits set |
| `questFlagCount(questId, bit, ...)` | int | Count of set bits among those specified |
| `isQuestComplete(questId)` | bool | Quest is complete |
| `isQuestAccepted(questId)` | bool | Quest is accepted |
| `isQuestAvailable(questId)` | bool | Quest can be accepted now |
| `playerZone()` | int | Current zone ID |
| `playerLevel(job?)` | int | Job level; defaults to current job |
| `playerHasItem(itemId, count?)` | bool | Inventory contains item (≥ count) |
| `isAttuned(id)` | bool | Aetheryte or aethernet shard with the given `AetheryteId` is attuned |
| `playerHasEquipped(itemId, slot?)` | bool | Item equipped in slot |
| `playerAverageItemLevel()` | int | Average iLvl of equipped gear |
| `playerNear(position, radius)` | bool | Player within radius of position |
| `playerStartingClass()` | string | Class at character creation |
| `currentJob()` | string | Current job |
| `inventoryFreeSlots()` | int | Free inventory slots |
| `instanceKind()` | string | `InstanceKind` enum value |
| `playerInCombat()` | bool | In combat |
| `playerDead()` | bool | HP zero or death prompt open |
| `interactableActive(id)` | bool | Interactable currently usable |
| `uiDialogueOpen()` | bool | Dialogue UI shown |
| `gil()` | long | Current gil |
| `playerLowestGearCondition()` | int | Minimum durability percentage (0-100) across equipped gear |
| `gearsetExists(jobName)` | bool | Player has an existing gearset for the named job |
| `inNewGamePlus()` | bool | Currently in NG+ mode |
| `default` | bool | Always true. Used as the fallback in branches. |

### 7.3 Examples

```
questSequence(65) >= 3
playerZone() == 155 and not playerInCombat()
questFlagAll(65, 1, 2, 3)
questFlagAny(65, 5, 6, 7) or questSequence(65) >= 5
playerStartingClass() == "Gladiator"
instanceKind() == "None"
```

### 7.4 Versioning

The predicate language version is implicit in the quest schema version. Schema `1.x.y` includes predicate-language v1. Schema `2.0.0` may make breaking predicate changes. The same migration rules apply (see §9).

Within v1:
- Adding state functions: minor bump (`1.0.0` → `1.1.0`)
- Removing or renaming state functions: major bump (`1.0.0` → `2.0.0`)
- Changing parser rules: major bump

### 7.5 Validator parsing

The validator parses every predicate in every quest file at PR time:

- Malformed syntax → `predicate-parse-error`
- Unknown state function → `predicate-unknown-function`
- Wrong arity → `predicate-arity-mismatch`
- Type mismatch on comparison → `predicate-type-mismatch`

The plugin runtime uses the same parser, ensuring author-time and runtime parsing agree.

---

## 8. Semantic validation rules

Tier 2 validation per `DESIGN.md` §6.2. Runs on every PR.

### 8.1 Structural integrity

- Step IDs match `^[a-z][a-z0-9-]*$` (kebab-case, lowercase)
- Step IDs unique within a quest
- Recovery `goto` references existing step IDs in the same sequence or branch
- Recovery `goto` cannot cross branch boundaries (no jumping into or out of branch sub-sequences from outside)
- Branch sub-sequences have at least one step
- Branch nesting depth ≤ 3 (warn at depth 2)
- Sequence numbers are strictly increasing
- Fragment references resolve to existing fragment files
- Fragment required parameters are provided and well-typed
- No nested fragments (fragment files cannot reference other fragments)
- Last chain `next` entry is `"when": "default"` (or quest is a terminus via `null`)
- `target` and `targets` fields are mutually exclusive on a single step
- `duty` step with `kind: "regular"` requires `dutyId` and `entryNpc`; `kind: "spd"` requires `trigger` (no `dutyId`)
- `use-item` step's `target.kind` field matches the structure provided (`"npc"` requires `npcId`; `"object"` requires `interactableId`; `"position"` requires `position` and `tolerance`)
- `notes` fields ≤ 500 characters

### 8.1.1 Fragment scope reporting

On any PR that modifies a fragment file, the validator reports the full list of consuming quests in the PR comment so reviewers can assess blast radius:

```
Fragment travel/uldah-to-gridania modified.
Affected quests (12):
  - 65657 Close to Home
  - 65658 Going My Way
  - ... (10 more)
```

Reviewers can then decide whether to spot-check the consuming quests' replay traces.

### 8.2 Game-data references

- Quest IDs (this quest, prereqs, chain links) exist in current game data
- NPC IDs exist
- Aetheryte IDs and aethernet shortcut names exist
- Item IDs exist
- Zone IDs exist
- Interactable IDs exist (where present)
- Duty IDs exist
- Emote IDs exist
- Dialogue sheet references resolve via Lumina in all four shipped languages
- Positions fall within the declared zone's bounding box (warn if drift >50 units from current NPC position)

### 8.3 Logical consistency

- Quest prerequisites form a DAG (no cycles)
- `requirements.minLevel` ≤ current level cap
- `requirements.requiredJob` is a real job name
- Chain `previous`/`next` consistency: if A says `next` includes B, then B's `previous` includes A
- Chain branches must be reachable (predicates can be satisfied for some character state)

### 8.4 Predicate validity

- Every predicate parses
- Every state function call uses an allowlisted function
- Arities match
- Comparison types match (no comparing a string to a number)

### 8.5 Recovery consistency

- `useTeleport` references an attunable aetheryte
- `goto` doesn't create static infinite loops (each step can reach completion via some path; cycle detection on `goto` graph)
- Recovery `awaitUser` reasons are ≤200 characters

### 8.6 Patch verification (warnings, not failures)

- `lastVerifiedPatch` is a known patch version
- If `supportStatus.implementation == "complete"` but `lastVerifiedPatch` is two or more patches behind current, validator warns

### 8.7 Schema version compatibility

- `schemaVersion` is a valid SemVer string
- Plugin version supports the declared schema range

---

## 9. Schema versioning

Quest schema follows semantic versioning.

| Change kind | Bump | Migration |
|-------------|------|-----------|
| Add new optional field | Patch (`1.0.0` → `1.0.1`) | None — old plugins ignore unknown fields. |
| Add new step type | Minor (`1.0.0` → `1.1.0`) | None — old plugins reject unknown step types loudly. |
| Add new predicate function | Minor | Same as above. |
| Rename field | Major (`1.0.0` → `2.0.0`) | `qf-quest migrate` rewrites quest corpus. |
| Change field semantics | Major | Same as above. |
| Remove field or step type | Major | Same as above. |

Plugin declares supported schema range (e.g., `>=1.0.0 <2.0.0`). Mismatched quest files are loaded with a clear error in the support-status UI.

Major bumps are rare. First-year expected cadence: zero.

---

## 10. Worked ARR example

Adapting the Questionable example (DRK class quest #2054 "Ishgardian Justice", Heavensward) into this schema:

```json
{
  "schemaVersion": "1.0.0",
  "id": 2054,
  "name": "Ishgardian Justice",
  "expansion": "heavensward",
  "category": "class",
  "enabled": true,

  "supportStatus": {
    "implementation": "complete",
    "knownIssues": []
  },

  "lastVerifiedPatch": "7.4",

  "requirements": {
    "minLevel": 30,
    "requiredJob": "DarkKnight",
    "prereqs": [{ "questId": 2053, "state": "complete" }]
  },

  "acceptFrom": {
    "npcId": 1014875,
    "zone": 418,
    "position": { "x": 6.91, "y": -1.92, "z": 47.29 }
  },

  "chain": {
    "previous": [2053],
    "next": [{ "when": "default", "questId": 2055 }]
  },

  "contributors": ["@liza"],

  "sequences": [
    {
      "sequence": 0,
      "steps": [
        {
          "id": "equip-soul-of-drk",
          "type": "equip-gear-for-quest",
          "items": [{ "slot": "soul", "itemId": 10400 }],
          "skipIf": "not playerHasItem(item:10400, 1)",
          "expect": "playerHasEquipped(item:10400)"
        },
        {
          "id": "equip-recommended",
          "type": "equip-best-gear",
          "expect": "playerAverageItemLevel() > 0"
        },
        {
          "id": "travel-to-questgiver",
          "type": "travel",
          "destination": {
            "zone": 418,
            "position": { "x": 6.91, "y": -1.92, "z": 47.29 }
          },
          "routeHint": {
            "aetheryte": "Ishgard",
            "aethernet": ["[Ishgard] Aetheryte Plaza", "[Ishgard] The Brume"]
          },
          "expect": "playerNear({\"x\":6.91,\"y\":-1.92,\"z\":47.29}, 5.0)"
        },
        {
          "id": "accept-quest",
          "type": "accept",
          "target": {
            "npcId": 1014875,
            "zone": 418,
            "position": { "x": 6.91, "y": -1.92, "z": 47.29 }
          },
          "expect": "isQuestAccepted(2054)"
        }
      ]
    },
    {
      "sequence": 1,
      "steps": [
        {
          "id": "talk-to-witness-a",
          "type": "talk",
          "target": {
            "npcId": 1014879,
            "zone": 418,
            "position": { "x": 134.05, "y": -20.02, "z": 73.75 }
          },
          "expect": "questSequence(2054) >= 2"
        }
      ]
    },
    {
      "sequence": 2,
      "steps": [
        {
          "id": "talk-to-witness-b",
          "type": "talk",
          "target": {
            "npcId": 1014880,
            "zone": 418,
            "position": { "x": 136.98, "y": -20.02, "z": 69.84 }
          },
          "stopDistance": 7,
          "expect": "questSequence(2054) >= 3"
        }
      ]
    },
    {
      "sequence": 3,
      "steps": [
        {
          "id": "walk-to-coerthas",
          "type": "travel",
          "destination": {
            "zone": 419,
            "position": { "x": 157.95, "y": -19.48, "z": 53.02 }
          },
          "expect": "playerZone() == 419"
        },
        {
          "id": "talk-to-coerthas-witness",
          "type": "talk",
          "target": {
            "npcId": 1014882,
            "zone": 419,
            "position": { "x": 244.10, "y": -13.73, "z": -95.51 }
          },
          "expect": "questSequence(2054) >= 4"
        }
      ]
    },
    {
      "sequence": 4,
      "steps": [
        {
          "id": "complete-solo-duty",
          "type": "duty",
          "dutyId": 56,
          "entryNpc": {
            "npcId": 1014883,
            "zone": 419,
            "position": { "x": 151.63, "y": -9.19, "z": -64.96 }
          },
          "expect": "questSequence(2054) >= 5"
        }
      ]
    },
    {
      "sequence": 5,
      "steps": [
        {
          "id": "talk-to-post-duty-npc",
          "type": "talk",
          "target": {
            "npcId": 1014888,
            "zone": 419,
            "position": { "x": 40.39, "y": 16.08, "z": -85.95 }
          },
          "expect": "questSequence(2054) >= 255"
        }
      ]
    },
    {
      "sequence": 255,
      "steps": [
        {
          "id": "turn-in-quest",
          "type": "turn-in",
          "target": {
            "npcId": 1014889,
            "zone": 419,
            "position": { "x": 43.38, "y": 16.08, "z": -86.05 }
          },
          "dialogueChoices": [
            {
              "type": "list",
              "prompt": "TEXT_JOBDRK301_02054_Q1_000_115",
              "answer": "TEXT_JOBDRK301_02054_A1_000_116"
            }
          ],
          "expect": "isQuestComplete(2054)"
        }
      ]
    }
  ]
}
```

Walking through:

- Quest 2054 requires Dark Knight (level 30), previous quest 2053 complete, leads to 2055
- Sequence 0: equip soul crystal (skipped if not in inventory — handles re-run cases), equip recommended gear, travel to Ishgard with explicit aetheryte hint, accept quest
- Sequences 1-3: talk to NPCs, one with a non-default stop distance for hitbox reasons, one that crosses a zone boundary
- Sequence 4: enter a single-player duty (BossMod takes over execution)
- Sequence 5: post-duty conversation
- Sequence 255: turn in, with a specific dialogue option referencing the game's text sheet directly

If anything goes wrong at any step, default recovery ladders apply. No explicit `recover` blocks are needed in this quest.

---

## Appendix A: Comparison with Questionable

Key differences in our schema vs. the example we adapted:

| Concern | Questionable | QuestForge |
|---------|-------------|------------|
| Step grouping | By sequence | By sequence (same) |
| Dialogue references | Game text sheets directly | Game text sheets directly (same) |
| Chain forward | `NextQuestId` | `chain.next[]` with predicates |
| Chain backward | Not tracked | `chain.previous[]`, validator-enforced consistency |
| Skip conditions | `SkipConditions.StepIf.*` | `skipIf` predicate string |
| Postcondition | Implicit (sequence advance) | Explicit `expect` predicate |
| Recovery | Implicit | Explicit `recover` per-step, with default ladders |
| Travel routing | Author-specified shortcuts | Author hint, engine fallback |
| Step types | Enum-style `InteractionType` | Explicit step-type-per-record |
| Support status | None | First-class `supportStatus` object |
| Reward strategy | Per-step `ItemReward` | Plugin setting + optional quest override |

The structural similarity is intentional. Questionable's approach is battle-tested for FFXIV's quest model; we adopt their core decisions and add the rigor (explicit postconditions, recovery, validation, branching, predicate language) that supports our reliability and contributor goals.

---

## Appendix B: Glossary additions

- **Sequence** — FFXIV's per-quest progression marker. The game advances it at specific points; steps within a sequence run before advancement.
- **Sheet reference** — Direct identifier into Square Enix's text data sheets (e.g., `TEXT_JOBDRK301_02054_Q1_000_115`). Stable across patches, localized by Lumina.
- **Skip condition** — Predicate that, when true on entry, causes the engine to skip a step entirely (distinct from postcondition-satisfied skipping).
- **Sequence 255** — Conventional value for the completion sequence in FFXIV quest data.

---

## Appendix C: Design decisions and rationale

| Decision | Alternative considered | Why |
|----------|----------------------|-----|
| Sequence-grouped steps | Flat step list | Aligns with FFXIV's quest state machine; trivial resume |
| Sheet references for dialogue | Invented namespace + lookup table | Use SE's own data; no translation layer; no CI multi-lang verification |
| Explicit `expect` | Implicit (sequence advance) | Allows non-sequence postconditions (flags, inventory); supports resume |
| `skipIf` distinct from `expect` | Single `expect` doubles for both | Some steps need "doesn't apply" semantics distinct from "already done" |
| Composite step primitives | Fully flat primitives | Human-readable; engine handles expansion uniformly |
| Twelve step types | Generalized step type | Explicitness aids both authors and validator |
| Route hints with engine fallback | Author-specified routing only | Combines determinism with flexibility |
| `null` for chain terminus | Magic string sentinel | Standard JSON; no special parsing |
| Predicate language as string | Structured AST | Readability for authors; parser lives in tools |
| `questFlagAny`/`All`/`Count` helpers | Bitwise operators in grammar | Readable without bit-twiddling |
| `routeHint` as hint, not constraint | Author-specified routing as ground truth | Authors don't always have current cost data; engine can compute |
| `RewardStrategy` as plugin setting | Per-quest field | Reward preferences are user-level, not quest-level |
| `enabled` field | Removing broken quests from corpus | Preserves quest data while disabling automation |
