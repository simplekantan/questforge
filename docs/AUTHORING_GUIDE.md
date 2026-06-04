# Quest Authoring Guide

A guide for contributors who want to author quests for QuestForge. No programming experience required.

---

## Getting started

1. Install the QuestForge plugin in Dalamud
2. Type `/qf author <questId>` in chat (the quest ID is the number from the game's quest journal — use `/qf quest <name>` to search for it)
3. Play through the quest normally — QuestForge watches what you do and drafts steps automatically
4. When you finish, use the Export button to save the quest file

---

## How recording works

When you enter Author mode, QuestForge opens a **Record modal** that watches for your next action. After you do something (talk to an NPC, use an item, equip gear, etc.), QuestForge detects what happened and drafts a step. You confirm or edit the step, then it's added to the quest draft.

The cycle is:
1. Click **+ Record Next Step** (or it opens automatically)
2. Perform the action in-game
3. QuestForge shows what it detected — review the step type, ID, and postcondition
4. Click **Confirm** to add the step to the draft
5. Repeat until the quest is complete

---

## Step types and how they get detected

### Automatically detected steps

These steps are detected when you perform the action in-game. QuestForge watches your game state and figures out what you did.

| Step type | What it does | How it's detected | Example |
|-----------|-------------|-------------------|---------|
| **talk** | Talk to an NPC | You target and interact with an NPC | Talk to the guild receptionist |
| **accept** | Accept a quest | A new quest appears in your journal | Accept "The Rematch" from Mylla |
| **turn-in** | Complete/turn in a quest | A quest is marked complete | Turn in quest items to Mylla |
| **travel** | Move to a location | Your position changes significantly | Walk to the Gladiators' Guild |
| **interact-object** | Click a world object | You interact with an EventObj (glowing marker, device, etc.) | Click the quest marker on the ground |
| **pickup-item** | Pick up a quest item | Same as interact-object (both use the same game mechanism) | Pick up the sparkling quest item |
| **attune** | Attune to an aetheryte | An aetheryte becomes unlocked | Attune to Camp Drybone aetheryte |
| **teleport** | Teleport to an aetheryte | Your zone changes via teleport | Teleport to Ul'dah |
| **use-item** | Use a key item or inventory item | The game's action system fires with an item type | Use the quest bell on an NPC |
| **use-action** | Use a combat ability or general action | The game's cast system completes an action | Use the "Examine" action on a target |
| **use-emote** | Perform an emote | The emote controller fires | /cheer at the NPC |
| **say-chat-message** | Type a message in /say chat | Your message appears in the chat log | /say Yes |
| **combat** | Fight enemies | Hostile targets appear and you engage | Defeat the 3 training dummies |
| **equip-gear-for-quest** | Equip specific gear | Your equipment slots change | Equip the Weathered Shortsword |
| **change-job** | Switch to a different job/class | Your ClassJob ID changes | Switch from Gladiator to Paladin |
| **register-gearset** | Save a gearset | The number of gearsets increases | Save your current gear as a gearset |
| **hand-over-item** | Give items to an NPC | Items leave your inventory during an NPC interaction | Hand over 3 wind crystals |
| **purchase-item** | Buy from a vendor | Gil/seals decrease during a shop interaction | Buy an Iron Shortsword from the vendor |

### Manually added steps (always select from the dropdown)

These steps are NOT auto-detected. You select them from the step type dropdown in the Record modal.

| Step type | What it does | Why not detected | When to use |
|-----------|-------------|-----------------|-------------|
| **duty** (spd) | Enter a Single Player Duty (solo battle) | SPD entry is via NPC interaction (detected as "talk") — the duty itself needs explicit authoring | After the NPC interaction that triggers the SPD |
| **duty** (dungeon/trial) | Enter a dungeon or trial | Dungeon entry is via Duty Finder — AutoDuty handles it | Before the dungeon/trial section of a quest |
| **equip-best-gear** | Equip the best available gear | Can't distinguish "equipped best" from "equipped specific items" | Before a duty or combat that needs good gear |
| **open-coffers** | Open treasure coffers in inventory | Can't distinguish coffer opening from regular item use | After a dungeon that gave coffer rewards |
| **cutscene** | Wait through a cutscene | Cutscenes are handled automatically by TextAdvance | Rarely needed — only for special cutscene handling |
| **wait** | Wait for a specific duration | No game event to detect | When the quest requires waiting (e.g., time-gated events) |
| **await-user** | Pause and wait for the player | No game event to detect | When manual player action is needed that can't be automated |

---

## Postconditions (Expect)

Every step needs to know when it's "done." This is the **Expect** field — a predicate that the engine checks each tick. When the predicate becomes true, the step is complete and the engine moves on.

QuestForge suggests an Expect based on what changed in the game state. Common patterns:

| Predicate | What it checks | Example |
|-----------|---------------|---------|
| `questSequence(65600) >= 2` | Quest sequence advanced | After talking to an NPC that advances the quest |
| `questFlag(65600, 3)` | A quest flag was set | After completing a sub-objective |
| `isQuestComplete(65600)` | Quest is marked complete | After the final turn-in |
| `playerZone() == 130` | Player is in a specific zone | After teleporting |
| `playerHasItem(4554)` | Player has an item | After picking up or receiving an item |
| `playerHasEquipped(12345)` | Player has an item equipped | After equipping gear |
| `isPlayerJob(19)` | Player is on a specific job | After changing jobs (19 = Paladin) |
| `isAetherCurrentAttuned(2818049)` | Aether current is unlocked | After attuning to an aether current |
| `not inventoryHasCoffers()` | No coffers left in inventory | After opening all coffers |
| `objectExists(1234)` | An object (NPC, EventObj, aetheryte) exists in the world | After an interaction triggers a spawn |
| `objectExistsInRange(1234, 10)` | An object exists within range (yalms) of the player | After warping near a specific NPC or object |
| `jobGearsetExists(19)` | A gearset exists for a job | After registering a gearset |

You can edit the Expect field if the suggestion is wrong. If no Expect is needed (the step has an implicit postcondition, like equip-gear-for-quest), leave it empty.

---

## Editing and reordering steps

### Edit a step
Click **edit** next to any step in the session panel. The edit modal shows the step's fields. Click **Re-record** to replace it with a fresh recording.

### Reorder steps
Use the **▲** and **▼** buttons next to each step to move it up or down within its sequence. Steps cannot be moved across sequence boundaries.

### Delete a step
Click **delete** next to any step. This is permanent (but the draft auto-saves, so you can re-record if needed).

---

## Sequences

Steps are grouped into **sequences** that match the game's quest sequence counter. You don't create sequences manually — they form automatically as the quest progresses.

When the game advances the quest sequence (e.g., from 0 to 1 after completing an objective), the next step you record automatically gets the new sequence number.

In the session panel, steps are displayed grouped by sequence:

```
Sequence 0 (4 steps)
  [1] accept | accept-quest
  [2] talk | talk-to-guild-master
  [3] travel | travel-to-training-grounds
  [4] combat | defeat-training-dummies
Sequence 1 (2 steps)
  [1] talk | report-to-guild-master
  [2] turn-in | complete-quest
```

---

## Tips

- **Let detection do the work.** Most steps are auto-detected. Only use the manual dropdown for steps that can't be inferred.
- **Check the Expect.** QuestForge's suggested Expect is usually right, but review it. If the game didn't advance the quest sequence, you may need to use a different predicate (like `questFlag`).
- **Use `/qf debug quest <id>`** to see quest state (sequence, variables, flags, availability) without opening game menus.
- **Use `/qf debug target`** to see information about your current target (NPC ID, position, object kind).
- **Save often.** The draft auto-saves after every confirmed step, but you can also export at any time.
- **Re-record > manual edit.** If a step is wrong, Re-record is usually faster than manually editing fields.
- **Test your quest.** After authoring, use `/qf run <questId>` to test the quest file. The engine will follow your steps and you can verify they work correctly.

---

## Exporting

When you're done authoring, click **Export** in the session panel. This saves the quest as a JSON file that QuestForge can run. The export includes:
- All steps organized by sequence
- Quest metadata (name, expansion, category, requirements)
- Expect predicates for each step

The exported file goes to `%appdata%/XIVLauncher/pluginConfigs/QuestForge/quests/<questId>.json`.
