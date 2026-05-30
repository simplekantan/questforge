# UseItemStep Implementation Plan (Slice 2)

**Status:** ready for test creation

**Slice:** 2 (engine + schema + validator). Slice 1 = this spec. Slice 3 = `DalamudItemUser` + EngineHost dispatch arm + tooling catch-up (CapabilityInferrer, FilenameLookup, DistinguishingCapPriority, TraceConstants). Slice 4 = in-game smoke. Slice 5 = authoring inference (signal research required — see Decision UI21).

**Input docs:**
- `CLAUDE.md` — "Adding a New Step Type — Fixed Slice Order"
- `docs/USE_EMOTE_STEP_PLAN.md` — closest analog (UE1–UE19). The shape of this plan mirrors it 1:1, with extensions for the `Kind` enum and the per-target-mode validator rule.
- `docs/SAY_CHAT_MESSAGE_STEP_PLAN.md` — secondary analog (showed how to mirror UE without a pure-helper). Validator-rule numbering precedent (E11/E12/W9) — this plan picks up at E13/E14/E15/W10.
- `docs/USE_ACTION_STEP_PLAN.md` — origin of UA8 (UseItemStep and UseActionStep remain separate; this plan removes the temptation to fold KeyItem use into UseActionStep) and UA12 (optional ctor param) / UA13 (no `_lastResolvedStep`).
- `QuestForge.Schema/Step.cs:165-169` — current placeholder `UseItemStep { ItemId, Target: UseItemTarget? }` (being replaced)
- `QuestForge.Schema/SharedValueTypes.cs:6-15` — `ActionType` enum (the shape the new `ItemKind` enum mirrors)
- `QuestForge.Schema/SharedValueTypes.cs:151-159` — `UseItemTarget` class (being DELETED; no other consumers — verified via grep)
- `QuestForge.Schema/QuestForgeJsonContext.cs:22` — `[JsonSerializable(typeof(UseItemStep))]` already present; no change
- `QuestForge.Engine/EngineAction.cs:37-46` — `UseEmote` and `SayChatMessage` records (templates for the new `UseItem` record)
- `QuestForge.Engine/QuestEngine.cs:594-606` (step-dispatch async arms), `849-911` (`ResolveUseAction` / `ResolveUseEmote` / `ResolveSayChatMessage`) — templates for `ResolveUseItem`
- `QuestForge.Adapters/Emotes/IEmoteExecutor.cs` — focused-adapter precedent
- `QuestForge.Adapters/Chat/IChatSender.cs` — even more minimal focused-adapter precedent
- `QuestForge.Adapters.Fakes/Emotes/FakeEmoteExecutor.cs` — fake-with-recording-and-scripting precedent
- `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` — test layout template (UE1–UE9 → UI1–UI10)
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:57-59` (`EmoteExecutor` / `ChatSender` properties), `129-132` (ctor passthrough), `224-240` (`UseEmote` / `SayChatMessage` `RunToCompletion` arms)
- `QuestForge.Engine/Authoring/DraftValidator.cs:100-142` (E9 / E10 / E11 / E12) and `144-192` (W1 / W7 / W8 / W9 suppression pattern) — templates for the new E13–E15 / W10 + W1 extension
- `QuestForge.Plugin/EngineHost.cs:459-489` (`UseEmote` / `SayChatMessage` dispatch arms) — Slice 3 placement reference; NOT implemented here
- `QuestForge.Plugin/UI/Authoring/ExportDialog.cs:206` — `UseItemStep → "use-item"` discriminator mapping already present; no change required after the schema rewrite (the discriminator string is unchanged).
- `questforge-tools/QuestForge.Schema/Step.cs:133` + `SharedValueTypes.cs:94` + `QuestForgeJsonContext.cs:21` — **mirror copies** of the schema; the same `UseItemStep` rewrite + `UseItemTarget` deletion must be applied in this Slice 2 PR pair (see Task UIT-1 sub-step).
- `questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs:417-558` — the tools-side structural validator currently has `ValidateUseItemStep` keyed on the old `UseItemTarget` shape. This method MUST be rewritten in Slice 2 (paired-PR) to match the new schema; Decision UI11 covers the rewrite.
- `questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs:160-235` — the existing tools-side tests for the old `UseItemTarget` shape — REPLACE in Slice 2 (paired-PR).

**Output (CI behavior):** Adding a `{ "type": "use-item", "kind": "keyItem", "itemId": 2000123, "targetNpcId": 1000789 }` step (or any of the three target shapes — self, NPC, AoE position) to a quest dispatches a new `EngineAction.UseItem` from `QuestEngine`. Engine unit tests (xUnit, `QuestForge.Engine.Tests/Engine/UseItemStepTests.cs`) cover all dispatch arms against `FakeItemUser`. Round-trip tests in `QuestForge.Schema.Tests/RoundTripTests.cs` cover the rewritten field shape + the `ItemKind` enum round-trip. Validator tests in `QuestForge.Engine.Tests/Authoring/` cover the four new draft-validator rules. The Dalamud shell + `EngineHost` dispatch arm + tooling catch-up + tools-repo structural-validator rewrite are deferred to Slice 3 (paired-PR posture).

> **Note on the existing placeholder.** `QuestForge.Schema/Step.cs:165-169` currently defines `UseItemStep { ItemId, Target: UseItemTarget? }` (where `UseItemTarget` is a Kind-discriminated bundle of `{ NpcId, InteractableId, Zone, Position, Tolerance }`). **This plan replaces that shape** — `Target: UseItemTarget?` is dropped; `UseItemTarget` itself is deleted from `SharedValueTypes.cs`. Nothing in the engine references `UseItemStep`'s old shape today (the engine throws `NotSupportedException` for it via the default arm in `ResolveActionForStep`). The tools-repo structural validator references the old shape and is rewritten in the same Slice 2 PR pair (Decision UI11). The `ExportDialog` mapping (`ExportDialog.cs:206`) emits `"use-item"` and requires no change. The `RecordStepModal` (`RecordStepModal.cs:34`) lists `"use-item"` as an inferrable type and requires no change. No schema-version bump is needed — no shipped quest authors the old shape (verified by grepping `questforge-data/quests/` is out of scope here; the data team's first authored `use-item` quest will be on the new shape).

---

## Dependency graph

```
QuestForge.Schema
   ├── new ItemKind enum (in QuestForge.Schema/SharedValueTypes.cs alongside ActionType)
   ├── UseItemStep (rewritten) — { Kind, ItemId, TargetNpcId, TargetPosition }
   └── UseItemTarget — DELETED (zero consumers post-rewrite; verified by grep)
        └── consumed by ↓
QuestForge.Adapters
   └── new IItemUser (in QuestForge.Adapters/Items/IItemUser.cs)
        └── consumed by ↓
QuestForge.Adapters.Fakes
   └── FakeItemUser (in QuestForge.Adapters.Fakes/Items/FakeItemUser.cs)
        └── consumed by ↓
QuestForge.Engine
   ├── EngineAction.UseItem record (append to EngineAction.cs)
   ├── QuestEngine: optional IItemUser? ctor param + ResolveUseItem async pre-arm
   └── DraftValidator: E13 + E14 + E15 + W10 + extend W1 suppression list
        └── consumed by ↓
QuestForge.Engine.Tests
   ├── Engine/UseItemStepTests.cs (UI1–UI10)
   └── Authoring/DraftValidatorUseItemTests.cs (UI14–UI18) — or extend DraftValidatorTests.cs

QuestForge.Schema.Tests
   └── RoundTripTests.cs (UI11–UI13 + UI19 ItemKind enum round-trip) — replaces existing UseItemStep_NoTarget_RoundTrips / UseItemStep_WithTarget_RoundTrips

questforge-tools (PAIRED PR — schema mirror only, in Slice 2; full tooling catch-up in Slice 3)
   └── questforge-tools/QuestForge.Schema/Step.cs + SharedValueTypes.cs
       — same rewrite + UseItemTarget deletion (Decision UI11)
   └── questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs:417-558
       — ValidateUseItemStep rewritten to the new shape (Decision UI11)
   └── questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs:160-235
       — six UseItem* tests replaced (Decision UI11)
```

**Build order:**
1. Schema (`ItemKind` enum + `UseItemStep` rewrite + delete `UseItemTarget`). Round-trip tests gate it.
2. `IItemUser` interface in `QuestForge.Adapters/Items/`.
3. `FakeItemUser` in `QuestForge.Adapters.Fakes/Items/`.
4. `EngineAction.UseItem` record.
5. `QuestEngine`: field, optional ctor param, `ResolveUseItem` async pre-arm + dispatch wiring.
6. `EngineTestHarness`: wires `FakeItemUser` (property + ctor passthrough); `RunToCompletion` arm.
7. Engine tests UI1–UI10.
8. Round-trip tests UI11–UI13 + UI19.
9. Validator extension (E13 + E14 + E15 + W10 + W1 suppression) + tests UI14–UI18.
10. **Paired-PR**: tools-repo `questforge-tools/QuestForge.Schema` rewrite + `StructuralValidator.ValidateUseItemStep` rewrite + tests rewrite. Push both PRs before either merges (mirrors the tooling-catch-up posture in CLAUDE.md "Slice 3 — Dalamud impl + tooling catch-up").

(No pure-helper analog of `EmoteCommandResolver` is needed — see Decision UI10. The Slice 3 `DalamudItemUser` is a thin shell over `ActionManager.UseAction` / `ActionManager.UseActionLocation`; no formatting/lookup helper is required.)

---

## Architectural decisions (read before coding)

### Decision UI1 — `UseItemStep` schema is rewritten; flatten target into two optional fields

Existing placeholder (`QuestForge.Schema/Step.cs:165-169`):
```csharp
public class UseItemStep : Step
{
    public uint ItemId { get; init; }
    public UseItemTarget? Target { get; init; }   // Kind-discriminated bundle
}
```

…with `UseItemTarget` (`QuestForge.Schema/SharedValueTypes.cs:151-159`):
```csharp
public class UseItemTarget
{
    public string Kind { get; init; } = default!;   // "npc" | "object" | "position"
    public uint? NpcId { get; init; }
    public uint? InteractableId { get; init; }
    public int? Zone { get; init; }
    public Position3? Position { get; init; }
    public float? Tolerance { get; init; }
}
```

**Replace with:**
```csharp
// QuestForge.Schema/Step.cs (replaces existing UseItemStep)
public sealed class UseItemStep : Step
{
    /// <summary>
    /// Which game-side action mechanism to use:
    ///   - KeyItem        → ActionManager.UseAction(EventItem, itemId, ...)
    ///   - InventoryItem  → ActionManager.UseAction(Item, itemId, ...)
    /// The engine surface is unified; the Dalamud adapter discriminates on this enum
    /// to pick the right ActionType when calling ActionManager.
    /// </summary>
    public ItemKind Kind { get; init; }

    /// <summary>
    /// The item id. For Kind=KeyItem this is the EventItem row id; for Kind=InventoryItem
    /// this is the Item row id. Validator rejects ItemId == 0 (E13).
    /// </summary>
    public uint ItemId { get; init; }

    /// <summary>
    /// Optional NPC target. When set, the Dalamud adapter resolves this BaseId via
    /// ObjectTable and writes TargetManager.Target before invoking ActionManager.UseAction.
    /// Mutually exclusive with TargetPosition (validator E15).
    /// NpcId here is the BNpcBase / ENpcBase data-id, matching the convention used by
    /// InteractStep / TalkStep / UseActionStep / UseEmoteStep / SayChatMessageStep targets.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }

    /// <summary>
    /// Optional ground-target position (AoE-style item use — e.g. dropping a smoke bomb
    /// at coordinates). When set, the Dalamud adapter calls ActionManager.UseActionLocation
    /// directly (bypassing the ground-target reticle). Mutually exclusive with TargetNpcId
    /// (validator E15).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? TargetPosition { get; init; }
}
```

…and the new enum in `QuestForge.Schema/SharedValueTypes.cs` (alongside the existing `ActionType` at lines 6–15 — see Decision UI3 for the placement rationale):
```csharp
[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("keyItem")]
    KeyItem,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("inventoryItem")]
    InventoryItem
}
```

**Target-mode mapping (the three valid combinations):**

| Use case | `TargetNpcId` | `TargetPosition` | Engine action emitted | Dalamud-side mechanism (Slice 3) |
|---|---|---|---|---|
| Self-cast (drink elixir) | `null` | `null` | `UseItem(Kind, ItemId, null, null, …)` | `ActionManager.UseAction(actionType, itemId)` — no target |
| NPC target (give bell to NPC) | `<id>` | `null` | `UseItem(Kind, ItemId, NpcId(id), null, …)` | resolve `id` via ObjectTable → set `TargetManager.Target` → `ActionManager.UseAction(actionType, itemId, targetId)` |
| AoE position (drop smoke bomb at coords) | `null` | `(x, y, z)` | `UseItem(Kind, ItemId, null, Position3(x,y,z), …)` | `ActionManager.UseActionLocation(actionType, itemId, ..., position)` (signature verified in Slice 3) |
| **Invalid: both set** | `<id>` | `(x, y, z)` | — (validator E15 rejects at author time) | — |

**JSON samples:**

Self-cast inventory item (drink an elixir):
```json
{
  "type": "use-item",
  "id": "drink-the-elixir",
  "kind": "inventoryItem",
  "itemId": 4554,
  "expect": "playerHasBuff(50)"
}
```

KeyItem on NPC target (give the bell to the recruit):
```json
{
  "type": "use-item",
  "id": "ring-bell-at-recruit",
  "kind": "keyItem",
  "itemId": 2000456,
  "targetNpcId": 1000789,
  "expect": "questFlag(65657, 5)"
}
```

AoE position (drop smoke bomb at the bandit camp):
```json
{
  "type": "use-item",
  "id": "smoke-bomb-the-camp",
  "kind": "keyItem",
  "itemId": 2000123,
  "targetPosition": { "x": 123.4, "y": 0.0, "z": -45.6 },
  "expect": "questSequence(65657) >= 4"
}
```

**Why drop `UseItemTarget`:**
1. **The Kind discriminator was load-bearing.** `UseItemTarget` had four nullable fields, three of which were mutually exclusive depending on `Kind`. The validator (tools-side) had a state-machine of "if Kind=='npc' then NpcId required; if Kind=='object' then InteractableId required; if Kind=='position' then Position+Tolerance required." That logic is replaced by a single rule (E15): "at most one of TargetNpcId / TargetPosition may be set."
2. **`Zone` was misleading.** Carrying a zone on the target lured authors into believing UseItem performs cross-zone navigation. It does not (mirrors Decision UE3 in `USE_EMOTE_STEP_PLAN.md` and Decision SC1 in `SAY_CHAT_MESSAGE_STEP_PLAN.md`).
3. **`InteractableId` was unused.** No quest in the corpus uses UseItem to target an interactable (the "object" kind in `UseItemTarget.Kind`). Authors who need to interact with an object use `InteractObjectStep`. If a real quest ever needs UseItem on an interactable, add a third optional field `TargetInteractableId: uint?` and extend E15 to "at most one of the three" — but defer that until a real case appears.
4. **`Tolerance` was a navigation hint masquerading as a target field.** Step-level `StopDistance` already covers approach tolerance. The AoE position variant uses the position as a *target reticle*, not a navigation destination — there's no tolerance.

**What breaks if violated:** if `UseItemTarget` is kept, authors continue to set the `Kind` discriminator wrong (e.g. `"npc"` with no `NpcId` set), and the tools-side validator runs a four-rule state machine for what is in practice a binary distinction.

### Decision UI2 — `ItemKind` enum discriminates the game-side mechanism; engine surface is unified

The user-confirmed scope is both key items and regular inventory items. The mechanism differs:
- **Key items** (rare quest props — bells, lanterns, summoning stones) are submitted via `ActionManager.UseAction(ActionType.EventItem, itemId, …)`.
- **Inventory items** (consumables — potions, food, throwables) are submitted via `ActionManager.UseAction(ActionType.Item, itemId, …)`.

The engine surface is the same — both flow through `IItemUser.UseItem(ItemKind, ItemId, target, position, ct)`. The Dalamud adapter (Slice 3) picks the right `FFXIVClientStructs.FFXIV.Client.Game.ActionType` enum value based on `Kind`. The engine never imports FFXIVClientStructs (mirrors the engine-purity invariant in `CLAUDE.md`).

**Why a Schema-side `ItemKind` enum rather than reusing `Schema.ActionType`:**
- `Schema.ActionType` (`SharedValueTypes.cs:6-15`) has values `Action / GeneralAction / KeyItem`. Adding `Item` to that enum is exactly the move Decision UA8 in `USE_ACTION_STEP_PLAN.md` explicitly rejected: "`UseActionStep.ActionType` never carries `Item`. Inventory items go through `UseItemStep`." Mixing the two enums collapses the orthogonality.
- A focused `ItemKind` enum with two values (`KeyItem`, `InventoryItem`) is cheaper than overloading `ActionType` and forcing UseItemStep authors to reason about why `Action / GeneralAction` are even options for an item step.
- Symmetric to `PurchaseCurrency` (a tiny enum with two values, in the schema, used by exactly one step type).

**Why not boolean `IsKeyItem`:** booleans are anti-self-documenting. `"kind": "keyItem"` in JSON reads correctly; `"isKeyItem": true` reads as "you tell me." Same posture as `CombatSpawn` (a two-value enum where a bool would suffice — but doesn't read as well in JSON).

**What breaks if violated:** if `ItemKind` is folded into `ActionType`, the validator gains a new rule "`UseActionStep.ActionType` ∉ {Item}" (which Decision UA8 already said we wouldn't enforce because the enum doesn't have that value) and the symmetry of "one step type → one enum" is broken.

### Decision UI3 — `ItemKind` lives in `SharedValueTypes.cs` alongside `ActionType`, NOT in its own file

`ActionType` is in `QuestForge.Schema/SharedValueTypes.cs:6-15`. `PurchaseCurrency` is in `Step.cs:257-264`. `CombatSpawn` is in `Step.cs:127-134`. The convention is "tiny enums live with their kin." Pick `SharedValueTypes.cs` because:
1. `ActionType` is already there; placing `ItemKind` alongside it keeps the two "what-mechanism-am-I-using" enums together.
2. `Step.cs` is the polymorphic dispatch file; adding more enums there grows it past readable length.
3. A dedicated `ItemKind.cs` for a 6-line enum is ceremony.

**What breaks if violated:** if `ItemKind` lives in its own file, future maintainers grep `ActionType` and don't find `ItemKind` despite the two being siblings — minor but real friction.

### Decision UI4 — Target shape is "flat" (two nullable fields), not a discriminated union

Three valid target modes (self / NPC / AoE position) collapse to two optional fields with the convention:

| `TargetNpcId` | `TargetPosition` | Mode |
|---|---|---|
| `null` | `null` | Self-cast |
| set | `null` | NPC target |
| `null` | set | AoE position |
| set | set | **Invalid — validator E15** |

**Why not a discriminated union (`UseItemTarget` with `Kind` discriminator):** the old shape had four mutually-exclusive fields keyed off a Kind string. Replacing it with two mutually-exclusive fields halves the state space and replaces the state-machine validator with a single "not both" check.

**Why not three booleans:** would still require a "exactly one is set" validator rule (worse than the current "at most one" rule because self-cast requires *zero* set).

**Why not require explicit `Kind: "self" | "npc" | "position"` discriminator at the JSON level for the target:** the field combination already discriminates losslessly. An explicit Kind field would be duplicative information that the validator would then have to keep in sync with the actual fields (a new rule: "if Kind=='npc' then TargetNpcId must be set"). Strict shape inference is simpler.

**Side benefit:** the JSON is more terse — self-cast is just `{ "kind": "inventoryItem", "itemId": 4554 }`, with no `target: null` boilerplate.

**What breaks if violated:** if both target fields can be set, the Dalamud adapter (Slice 3) faces an ambiguity — does it call `UseAction(actionType, itemId, targetId)` or `UseActionLocation(actionType, itemId, ..., position)`? Decision UI8 routes this defensively (validator rejects at author time; engine does NOT re-check at runtime), so violating this means the validator misses a real authoring bug.

### Decision UI5 — No quantity field; engine fires once per dispatch and the postcondition gates retry

User-confirmed. Quantity is **not** a `UseItemStep` field. Each dispatch fires the item once. If the postcondition is unmet on the next tick, the engine re-emits `UseItem` (stateless retry — same posture as `UseEmoteStep` and `UseActionStep`).

**Why not a quantity field (e.g. "drink 3 elixirs"):**
1. No quest in the corpus today needs "use this item N times in a row." The pattern "use item until effect happens" is already covered by the stateless retry + Expect predicate ("until quest flag is set").
2. Quantity = N steps would shadow `RetryConfig.MaxAttempts` (the existing per-step retry knob).
3. If a real case ever requires "drink exactly 3 of these and don't stop at the first effect," the author writes three `UseItemStep`s in sequence, each with an Expect that ratchets a counter — clearer than a hidden inner loop.

**Why not a Repeated/AutoRepeat flag:** same answer — the stateless retry already covers "fire until Expect satisfies." A flag would just be a less-explicit way to say "no Expect — fire forever."

**What breaks if violated:** if a quantity field is added, the engine grows a per-step "remaining quantity" counter that has to be tracked across ticks; that's state in `QuestEngine`, which we've consistently kept stateless across ticks (`_lastResolvedStep` notwithstanding, which is per-tick scratch).

### Decision UI6 — `IItemUser` is the interface name (vs `IItemExecutor`, `IInventoryItemUser`)

Pick **`IItemUser`** in `QuestForge.Adapters/Items/IItemUser.cs`.

**Rejected: `IItemExecutor`** — `IActionExecutor` and `IEmoteExecutor` use `Executor` because they wrap `ActionManager.UseAction` / `Chat.SendMessage` (verbs are "execute action," "send message"). For items the verb is "use item" — `Use` is already in the method name, and `IItemUser` reads as "thing that uses items," matching `IInteractor` ("thing that interacts") / `INavigator` ("thing that navigates") / `ITeleporter` ("thing that teleports") — all noun-form interface names. `IItemExecutor` would be the odd one out.

**Rejected: `IInventoryItemUser`** — too narrow. The interface covers both key items and inventory items; the name should not lock in just one.

**Rejected: `IItemCommandSender`** — overloads "Command" with chat-command sender (`IChatSender`) and reads as "thing that sends commands about items" rather than "thing that uses items."

**Concrete shape:**
```csharp
// QuestForge.Adapters/Items/IItemUser.cs
namespace QuestForge.Adapters.Items;

using QuestForge.Adapters.Types;
using QuestForge.Schema;   // ItemKind, Position3

/// <summary>
/// Uses a key item or inventory item, optionally on an NPC target or at a ground position.
/// The Dalamud adapter discriminates on ItemKind to pick the right ActionManager call:
///   - KeyItem        → ActionType.EventItem
///   - InventoryItem  → ActionType.Item
/// …and on target shape to pick the right ActionManager method:
///   - both null        → UseAction(actionType, itemId)
///   - targetNpcId set  → resolve via ObjectTable → set TargetManager.Target → UseAction(actionType, itemId, targetId)
///   - targetPosition set → UseActionLocation(actionType, itemId, ..., position)
///
/// Success means "the ActionManager call returned true." It does NOT mean "the item's
/// effect landed" or "the NPC reacted." The engine verifies outcome via the authored
/// Expect predicate.
/// </summary>
public interface IItemUser
{
    /// <summary>
    /// Uses the item. Returns Result.Failure if:
    ///   - targetNpcId is supplied and no matching object is in ObjectTable (kind: EventNpc / BattleNpc), or
    ///   - the underlying ActionManager call returns false (out of charges, on cooldown, not in inventory).
    ///
    /// Caller MUST NOT pass both targetNpcId and targetPosition (the validator rejects this
    /// combination at author time per Decision UI8; the adapter is not defensive against the
    /// violation — if it happens, behavior is undefined).
    /// </summary>
    Task<Result<Unit>> UseItem(
        ItemKind kind,
        uint itemId,
        NpcId? targetNpcId,
        Position3? targetPosition,
        CancellationToken ct);
}
```

**Why no `GetItemStatus`:** unlike `IActionExecutor`, items don't expose a clean Ready/OnCooldown/Unusable trichotomy.
- Inventory items: cooldowns exist but vary per-item; charge counts exist; the engine's stateless retry handles "ActionManager returned false → try again next tick" cleanly. Adding a status read would invent a category for "the item's gone from your bag" (which the engine can't distinguish from "the action is on cooldown" without per-item knowledge of cooldown duration vs charge gating).
- Key items: no cooldown, no charge; if the row is in the player's KeyItems collection, `ActionManager.UseAction(EventItem, …)` will succeed.

**Why no `GetItemChargeCount`:** would only be used in a Slice 5 (authoring) inference path or a custom validator rule. Out of scope.

**What breaks if violated:** if `IItemUser` exposes a status read, the engine pre-arm grows three more guards (Ready / OnCooldown / Unusable) and every test that exercises UseItem has to script the status. Cost outweighs benefit because the stateless retry already handles the failure mode.

### Decision UI7 — Pre-flight guards live in `ResolveUseItem`; single guard (casting)

Mirrors Decision UE5. The only meaningful pre-flight gate is the casting check (a mid-cast item-use is silently rejected by the game). No cooldown read, no status read.

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `EngineAction.Wait("player casting; deferring use-item")` |
| 2. (none — proceed to emit) | — | `EngineAction.UseItem(Kind, ItemId, TargetNpcId, TargetPosition, Origin: step)` |

**No InCombat guard:** some items (potions, food) work in combat by design. Combat is the player's problem; the engine should not refuse to dispatch.

**No "ambiguous target" runtime guard:** Decision UI8 routes ambiguous-target rejection to the validator only. The engine assumes the validator has run; if it hasn't (e.g. ad-hoc test setup), the engine emits whatever the step says and the Dalamud adapter behavior is undefined. Mirrors Decision UA in the runtime posture: "`actionId == 0` is a validator concern, not a runtime concern."

**Concrete shape:**
```csharp
private async Task<EngineAction> ResolveUseItem(UseItemStep step, CancellationToken ct)
{
    if (_itemUser is null)
        return new EngineAction.AwaitUser(
            "UseItemStep dispatched but no IItemUser wired — host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring use-item", Origin: step);

    var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
    return new EngineAction.UseItem(
        step.Kind, step.ItemId, target, step.TargetPosition, Origin: step);
}
```

### Decision UI8 — Ambiguous-target rejection is validator-only; engine and adapter trust the validator

If `TargetNpcId.HasValue && TargetPosition.HasValue`, the validator rejects at author time (E15). The engine does NOT re-check at runtime; the Dalamud adapter does NOT defensively pick one. Behavior on a malformed step that bypassed the validator is undefined.

**Why validator-only:**
1. **Mirrors UA posture.** `UseActionStep` has `ActionId == 0` checked only by the validator (E7); the engine does not re-check. Same principle applies here.
2. **The validator is the source of truth.** All quest data must pass the validator before merge; the engine is allowed to assume validated input.
3. **Defensive runtime checks erode the contract.** If the engine re-checks E15, authors will start treating the engine as a fallback validator and skip the actual validator step.

**Why not "engine picks one and emits a warning":** would mask a real authoring bug. The author meant to set exactly one target; "engine picks one" violates intent.

**What breaks if violated:** if the engine becomes defensive, the validator's E15 rule becomes documentation-only (authors stop trusting the validator's exclusivity claim because the engine "handles it anyway").

### Decision UI9 — AoE `TargetPosition` is NOT validated for terrain reasonableness

The validator does not know FFXIV terrain. Authors set `TargetPosition` from in-game observation (clicked location or recorded coords). The validator only rejects:
- `TargetNpcId == 0` (explicit zero, E14)
- BOTH targets set (E15)

It does NOT reject:
- Y outside `[-100, 100]` or any other "reasonable height" bound
- X / Z outside zone bounds
- Position too far from the previous step's TravelStep destination

These checks would require a Lumina map index and would produce false positives (e.g. cliffside-drop AoEs are valid).

**Recommendation:** if a real "author put coordinates on the wrong continent" bug appears, add it as a runtime warning (the engine logs a warning when `TargetPosition.Distance(player) > 50m`), not a validator rule.

**What breaks if violated:** if the validator gets terrain-aware, authoring a quest in a new expansion requires updating the validator's terrain map — coupling that we want to avoid.

### Decision UI10 — `EngineAction.UseItem` record shape

```csharp
// QuestForge.Engine/EngineAction.cs (append, after UseEmote / SayChatMessage)
public sealed record UseItem(
    ItemKind Kind,
    uint ItemId,
    QuestForge.Adapters.Types.NpcId? TargetNpcId,
    Position3? TargetPosition,
    Step? Origin = null) : EngineAction;
```

`ItemKind` and `Position3` come from `QuestForge.Schema` (matching the other action records that reference schema enums — `UseAction` carries `ActionType`, `Purchase` carries `PurchaseCurrency`). The `using QuestForge.Schema;` at the top of `EngineAction.cs` already covers the namespace.

**Why carry `Position3` directly rather than translating to `WorldPosition`:** `WorldPosition` (in `QuestForge.Adapters.Types`) is the engine's internal position type used for navigation deltas and distance math. `Position3` is the schema-side authored type. For an AoE target the value is passed straight through to the Dalamud adapter, which converts to `Vector3` for the native call. Carrying `Position3` keeps the record one-to-one with the schema field; no conversion in the engine.

**What breaks if violated:** if the record carries `WorldPosition`, the engine has to translate `Position3 → WorldPosition` in the pre-arm, and the Dalamud adapter has to translate back to `Vector3`. Two unnecessary hops.

### Decision UI11 — Tools-repo schema mirror + StructuralValidator rewrite are in-scope for Slice 2 (paired-PR)

The tools repo (`questforge-tools`) carries a **copy** of `QuestForge.Schema/Step.cs` + `SharedValueTypes.cs` (because the validator CLI must run without depending on the plugin repo). The mirror copies must be updated in lockstep with this PR.

**Files to update in `questforge-tools` (paired PR, push both before either merges):**

1. `questforge-tools/QuestForge.Schema/Step.cs:133-136` — replace `UseItemStep { ItemId, Target: UseItemTarget? }` with the new shape (identical to the questforge-repo Step.cs change).
2. `questforge-tools/QuestForge.Schema/SharedValueTypes.cs:94` — delete `UseItemTarget`; add `ItemKind` enum.
3. `questforge-tools/QuestForge.Schema/QuestForgeJsonContext.cs:21` — verify `[JsonSerializable(typeof(UseItemStep))]` present (it already is); add `[JsonSerializable(typeof(ItemKind))]` (mirror the `ActionType` registration at line 24).
4. `questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs:417-558` — rewrite `ValidateUseItemStep`. Old rule was a state-machine on `Kind`. New rules (matching engine-side E13/E14/E15):
   - Error `structural/use-item-itemid-zero`: `step.ItemId == 0` (mirrors E13)
   - Error `structural/use-item-target-npc-id-zero`: `step.TargetNpcId == 0` (mirrors E14)
   - Error `structural/use-item-ambiguous-target`: `step.TargetNpcId.HasValue && step.TargetPosition.HasValue` (mirrors E15)
   - No equivalent of W10 (the tools-side StructuralValidator does not currently emit warnings; W10 is a DraftValidator-only rule by precedent — `UseActionStep` / `UseEmoteStep` / `SayChatMessageStep` follow the same split).
5. `questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs:160-235` — replace the six `UseItemStep*` tests:
   - `UseItemStep_NoTarget_IsValid` → `UseItemStep_SelfCast_IsValid` (both target fields null + non-zero ItemId)
   - `UseItemStep_TargetKindWithRequiredFields_IsValid` → split into `UseItemStep_NpcTarget_IsValid` + `UseItemStep_PositionTarget_IsValid`
   - `UseItemStep_NpcTargetMissingNpcId_ReportsError` → `UseItemStep_NpcTargetZero_ReportsError` (asserts `structural/use-item-target-npc-id-zero`)
   - `UseItemStep_ObjectTargetMissingInteractableId_ReportsError` → DELETE (object target removed)
   - `UseItemStep_PositionTargetMissingPosition_ReportsError` → DELETE (position is now structurally typed as `Position3?` — STJ rejects malformed JSON before the validator sees it)
   - Add new test `UseItemStep_BothTargetsSet_ReportsError` asserting `structural/use-item-ambiguous-target`.
   - Add new test `UseItemStep_ItemIdZero_ReportsError` asserting `structural/use-item-itemid-zero`.

**Why paired-PR not separate Slice:** the schema mirror is a hard constraint of the tools-repo design (it doesn't take a NuGet dep on the plugin repo's Schema project). The validator change is mechanical and tightly coupled to the schema change. Splitting them invites the lag-window where the tools repo can't be built. Tooling catch-up for Slice 3 (CapabilityInferrer is already present at `[typeof(UseItemStep)] = "step:use-item"`; FilenameLookup needs an entry; DistinguishingCapPriority needs an entry; TraceConstants needs `ActionUseItem`) is deferred to Slice 3 because it depends on `DalamudItemUser` existing.

**What breaks if violated:** if the tools-repo mirror is not updated, every PR to `questforge-data` that uses the new shape fails CI immediately (the tools-side validator can't deserialize the new fields). If the mirror is updated but the StructuralValidator is not, the tools-side validator silently passes invalid quests.

### Decision UI12 — Validator rules (added by this plan in `QuestForge.Engine/Authoring/DraftValidator.cs`)

The validator currently emits E1–E12 + W1–W9 (verified via Read of `DraftValidator.cs`). Next free codes: **E13, E14, E15, W10.**

| Rule | Code | Severity | Check | Suppressed when | Message contains |
|---|---|---|---|---|---|
| `itemId` non-zero | `E13` | Error | `UseItemStep.ItemId == 0` | — | "UseItemStep" + "ItemId == 0" |
| `targetNpcId` non-zero when present | `E14` | Error | `UseItemStep.TargetNpcId == 0` (null is allowed) | — | "UseItemStep" + "TargetNpcId == 0" |
| Mutually exclusive targets | `E15` | Error | `UseItemStep.TargetNpcId.HasValue && UseItemStep.TargetPosition.HasValue` | — | "UseItemStep" + "both TargetNpcId and TargetPosition" |
| `expect` authored | `W10` | Warning | `UseItemStep.Expect is null` | — | "UseItemStep" + "spin-loop" (per CLAUDE.md slice contract) |
| W1 ("step has no Expect") | `W1` | Warning | (existing rule) | extended exclusion: `not (UseActionStep or UseEmoteStep or SayChatMessageStep or UseItemStep)` | (existing) |

**Why E13 / E14 / E15 / W10 rather than continuing per-step grouping:** the validator uses flat global codes (per the established UE7 / SC pattern). Append-only numbering is the convention.

**W1 suppression — the exact pattern change:**

Current (`DraftValidator.cs:149`):
```csharp
if (step.Raw.Expect is null && step.Raw is not UseActionStep and not UseEmoteStep and not SayChatMessageStep)
```

New:
```csharp
if (step.Raw.Expect is null && step.Raw is not UseActionStep and not UseEmoteStep and not SayChatMessageStep and not UseItemStep)
```

Pinned by tests UI16, UI17, UI18.

**Why E15 is an error not a warning:** the Dalamud adapter behavior is undefined when both targets are set (Decision UI8). A warning would let the bad step ship; an error blocks the merge.

**Why W10 message says "spin-loop":** matches the CLAUDE.md "Adding a New Step Type" contract — "The W# message must contain 'spin-loop' so authors understand the runtime cost." Verified against the existing W7 / W8 / W9 messages, all of which include the word.

### Decision UI13 — `QuestEngine` constructor gains `IItemUser? itemUser = null` (after `IChatSender?`)

Mirrors Decision UE11 / SC's equivalent. Appended to preserve ordering for trace-replay:

```csharp
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
    ILogger<QuestEngine> logger,
    TimeProvider? clock = null,
    IVendor? vendor = null,
    IActionExecutor? actionExecutor = null,
    IEmoteExecutor? emoteExecutor = null,
    IChatSender? chatSender = null,
    IItemUser? itemUser = null)            // NEW
```

`itemUser` is optional. Absence yields `AwaitUser("UseItemStep dispatched but no IItemUser wired — host must supply one")` (per Decision UI7). `EngineHost.BeginRun` (in Slice 3) MUST pass a non-null `DalamudItemUser`. `EngineTestHarness` MUST pass `ItemUser`.

### Decision UI14 — `_lastResolvedStep` is NOT updated by `ResolveUseItem`

Mirrors Decision UE12 / UA13. `UseItemStep` does not carry `DialogueChoices`, so `ExtractYesNo` returns null for it. Do NOT set `_lastResolvedStep` in the async pre-arm — the `Origin: step` field on `EngineAction.UseItem` carries the necessary context for downstream trace consumers.

### Decision UI15 — Dispatch arm insertion in `QuestEngine.ResolveAction` step-dispatch switch

Insert after the `SayChatMessageStep` arm (currently at `QuestEngine.cs:600-606`). Concrete placement:

```csharp
// 6a3. SayChatMessageStep async arm (existing)
if (step is SayChatMessageStep sayChatMessageStep)
{
    var sayChat = await ResolveSayChatMessage(sayChatMessageStep, ct);
    return (sayChat, step.Id);
}

// 6a4. UseItemStep async arm — step-gated so GetPlayerState is only read when the
//      cursor is on a UseItemStep.  (NEW)
if (step is UseItemStep useItemStep)
{
    var useItem = await ResolveUseItem(useItemStep, ct);
    return (useItem, step.Id);
}

// 6b. TeleportStep async arm (existing, unchanged)
if (step is TeleportStep teleportStep) { ... }
```

### Decision UI16 — `EngineTestHarness` wires `FakeItemUser`

`EngineTestHarness` gains:
```csharp
public FakeItemUser ItemUser { get; } = new FakeItemUser();
```
…and passes `itemUser: ItemUser` to the `QuestEngine` constructor (last positional kwarg).

`RunToCompletion` gains an arm mirroring the UseEmote arm at line 224:
```csharp
case EngineAction.UseItem ui:
    actions.Add(action);
    EmitActionSubmitted("UseItem", JsonSerializer.SerializeToElement(
        new {
            kind = ui.Kind.ToString(),
            itemId = ui.ItemId,
            targetNpcId = ui.TargetNpcId?.Value,
            targetPosition = ui.TargetPosition is { } p
                ? new { x = p.X, y = p.Y, z = p.Z }
                : (object?)null
        },
        _jsonOpts));
    var uiResult = await ItemUser.UseItem(
        ui.Kind, ui.ItemId, ui.TargetNpcId, ui.TargetPosition, ct);
    EmitActionCompleted("UseItem", uiResult.IsSuccess ? "Done" : "Failed");
    break;
```

### Decision UI17 — `FakeItemUser` exposes `RecordedCalls` + `ScriptNextFailure` + `Reset`

Mirrors `FakeEmoteExecutor` exactly. Concrete shape:

```csharp
// QuestForge.Adapters.Fakes/Items/FakeItemUser.cs
namespace QuestForge.Adapters.Fakes.Items;

using QuestForge.Adapters.Items;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

public sealed class FakeItemUser : IItemUser
{
    public record UseItemCall(
        ItemKind Kind,
        uint ItemId,
        NpcId? TargetNpcId,
        Position3? TargetPosition,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseItemCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    /// <summary>Forces UseItem to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> UseItem(
        ItemKind kind, uint itemId, NpcId? targetNpcId, Position3? targetPosition,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseItemCall(
            kind, itemId, targetNpcId, targetPosition, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
```

### Decision UI18 — Mount handling: `UseItem` is NOT in the dismount-exemption list

Mirrors Decision UE6. Most item-use animations require the player to be dismounted (the game silently rejects mid-mount item use for many items). The lazy-dismount hook fires before `EngineAction.UseItem` is returned to the dispatch loop when the previous tick dispatched `Navigate`.

**No code change required** — the existing `HarnessEngine.Tick` / `EngineHost.DispatchAction` exemption check (`is not EngineAction.Navigate and not EngineAction.Teleport`) already covers `UseItem` (it is NOT navigate, NOT teleport, therefore lazy-dismount applies). Pinned by tests:
- UI7 (mounted + prior Navigate) — dismount fires before UseItem.
- UI8 (standalone, no prior Navigate) — dismount does NOT fire.

**Why not exempt:** unlike Teleport (the game auto-dismounts on aetheryte arrival), there's no game-side auto-dismount for item use. Adding `UseItem` to the exemption list would mean `Navigate → UseItem` while mounted leaves the player mounted, and many items (potions, key items, AoE throwables) will be silently rejected.

### Decision UI19 — Do NOT bundle Dalamud impl in this slice; pure helper is NOT needed

Mirrors Decision UE19 / SC8.

- **No pure helper.** Unlike `EmoteCommandResolver` (which formats slash commands), `IItemUser`'s implementation is direct: dispatch on `ItemKind` to pick the FFXIVClientStructs `ActionType`, dispatch on target shape to pick `UseAction` vs `UseActionLocation`, ObjectTable lookup for NPC targets. No formatting / lookup logic to extract.
- **No `QuestForge.Adapters.Tests/Items/` test project entry.** Engine-side tests against `FakeItemUser` are the testable surface for the engine layer.
- **`DalamudItemUser` lives in Slice 3.** Not in this PR.

### Decision UI20 — Recording-proxy decision: `UseItem` does NOT need a `RecordingItemUser`

Per CLAUDE.md Slice 3 contract: "Write-only adapters (the typical action-executor shape) don't need a `RecordingXxxExecutor` wrapper — `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` already capture writes." `IItemUser` is write-only (one async write method, no observable reads, no multi-stage operations). No `RecordingItemUser` is needed.

Document this in the Slice 3 plan; do not implement here.

### Decision UI21 — Authoring inference is OUT OF SCOPE for this slice (Slice 5 concern; signal research required)

Per CLAUDE.md Slice 5 contract: "Authoring inference is a crucial feature — without it, every use of the new step in a quest requires manual authoring. This slice is mandatory for every step type, no exceptions."

For UseItemStep the inference signal is **non-obvious** and requires explicit research in Slice 5 (do not silently defer). Candidates surveyed at architecture time:

| Signal candidate | For Kind | Pros | Cons | Verdict |
|---|---|---|---|---|
| `KeyItemsRemoved` snapshot delta (already tracked by SnapshotAggregator for HandOverItemStep) | KeyItem | Already plumbed; no new probe needed | Fires for any key-item removal — including hand-overs and quest progression. False positives mid-quest. | Candidate (needs disambiguation against HandOverItemStep) |
| `InventoryItemsRemoved` delta (would need new SnapshotAggregator field; precedent: PurchaseItemStep gil/seal delta) | InventoryItem | Symmetric to existing snapshot pattern | Inventory deltas fire constantly during quests (rewards in, consumables out); needs strong disambiguation | Candidate (needs polling infrastructure) |
| FFXIVClientStructs `ActionManager` last-used signal (similar shape to `CastInfo.ResponseGlobalSequence` for actions) | both | Captures both kinds uniformly | Would require new probe field; unclear which exact field exposes "last item used" | Best candidate; signal research required |
| TargetManager observed when player target-clicked an item | AoE position | — | TargetManager doesn't expose "the ground reticle was set to (x,y,z)" cleanly | NOT feasible without game hooks (which we don't do per CLAUDE.md) |

**For AoE-position targets specifically**: capturing the player's clicked ground position is hard. Likely workaround: if the player target-clicks an NPC first then uses an item, infer the target as the NPC (degraded mode — AoE-position drops cannot be authoring-inferred in v1). Note as a Phase 5 known limitation.

**Action for Slice 5 architect:**
1. Spend the FIRST task of Slice 5 doing signal research against FFXIVClientStructs `ActionManager` — look for "last action used" / "last item used" fields, monotonic counters, and timestamps.
2. If `ActionManager` exposes a clean signal: prefer that (covers both KeyItem and InventoryItem uniformly).
3. If not: fall back to the snapshot-delta approach with strong disambiguation rules (e.g. "key item removed AND step cursor is currently on a UseItemStep" — but that's a poor rule because authoring is by definition out-of-engine).
4. If no signal found after honest research: surface to the user, do not silently defer.

**Out of scope for Slice 2:** the inference plumbing itself. Document the research requirement here so the Slice 5 architect doesn't start cold.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Schema/SharedValueTypes.cs` | MODIFY | Add `ItemKind` enum (alongside `ActionType`); DELETE `UseItemTarget` |
| `QuestForge.Schema/Step.cs` | MODIFY | Replace `UseItemStep` per Decision UI1 |
| `QuestForge.Schema/QuestForgeJsonContext.cs` | MODIFY | Add `[JsonSerializable(typeof(ItemKind))]` (mirror line 24 `ActionType` entry) |
| `QuestForge.Schema.Tests/RoundTripTests.cs` | MODIFY | Replace existing `UseItemStep_NoTarget_RoundTrips` + `UseItemStep_WithTarget_RoundTrips`; add UI11–UI13 + UI19 |
| `QuestForge.Adapters/Items/IItemUser.cs` | NEW | Adapter interface |
| `QuestForge.Adapters.Fakes/Items/FakeItemUser.cs` | NEW | Fake with recording + scripting |
| `QuestForge.Engine/EngineAction.cs` | MODIFY | Append `UseItem` record (after `SayChatMessage`) |
| `QuestForge.Engine/QuestEngine.cs` | MODIFY | Field `_itemUser`, ctor param `itemUser`, `ResolveUseItem` method, dispatch arm |
| `QuestForge.Engine/Authoring/DraftValidator.cs` | MODIFY | E13 + E14 + E15 + W10 + extend W1 exclusion |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | MODIFY | `ItemUser` property + ctor passthrough + `RunToCompletion` arm |
| `QuestForge.Engine.Tests/Engine/UseItemStepTests.cs` | NEW | UI1–UI10 |
| `QuestForge.Engine.Tests/Authoring/DraftValidatorUseItemTests.cs` | NEW | UI14–UI18 |
| **questforge-tools (paired-PR):** | | |
| `questforge-tools/QuestForge.Schema/Step.cs` | MODIFY | Mirror the `UseItemStep` rewrite |
| `questforge-tools/QuestForge.Schema/SharedValueTypes.cs` | MODIFY | Add `ItemKind`, delete `UseItemTarget` |
| `questforge-tools/QuestForge.Schema/QuestForgeJsonContext.cs` | MODIFY | Add `[JsonSerializable(typeof(ItemKind))]` |
| `questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs` | MODIFY | Rewrite `ValidateUseItemStep` per Decision UI11 |
| `questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs` | MODIFY | Replace six `UseItemStep*` tests |

---

## Validation rule table

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| `itemId` non-zero | `E13` | Error | `UseItemStep.ItemId == 0` | — |
| `targetNpcId` non-zero when present | `E14` | Error | `UseItemStep.TargetNpcId == 0` (null is allowed) | — |
| Mutually exclusive targets | `E15` | Error | `UseItemStep.TargetNpcId.HasValue && UseItemStep.TargetPosition.HasValue` | — |
| `expect` authored (spin-loop warning) | `W10` | Warning | `UseItemStep.Expect is null` | — |
| W1 ("step has no Expect") | `W1` | Warning | (existing rule) | extended: `not (UseActionStep or UseEmoteStep or SayChatMessageStep or UseItemStep)` |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/UseItemStepTests.cs`)

All tests follow the `UseEmoteStepTests` pattern. For each scenario:
- `harness.QuestState.SetQuestSequence(new QuestId(<questId>), 0)`.
- The quest contains exactly one UseItemStep in sequence 0 (unless noted).
- The step has an authored `Expect` (PredicateExpect using a predicate that does NOT auto-satisfy from default fake state, unless the test specifically tests Expect satisfaction).

#### UI1 — Happy path, self-cast (no target) → emits UseItem with both targets null

**Given:**
- Player not casting.
- UseItemStep `{ Kind = ItemKind.InventoryItem, ItemId = 4554, TargetNpcId = null, TargetPosition = null, Expect = PredicateExpect("playerHasBuff(50)") }` (predicate false).

**When:** `harness.Engine.Tick(CancellationToken.None)`.

**Then:**
- Returns `EngineAction.UseItem` with: `Kind == ItemKind.InventoryItem`, `ItemId == 4554u`, `TargetNpcId == null`, `TargetPosition == null`, `Origin != null`.
- `harness.ItemUser.RecordedCalls.Count == 0` (engine returns the action; harness `Tick` does not call the adapter — `RunToCompletion` does).

#### UI2 — Happy path, NPC target → emits UseItem with TargetNpcId set, TargetPosition null

**Given:**
- Player not casting.
- UseItemStep `{ Kind = ItemKind.KeyItem, ItemId = 2000456, TargetNpcId = 1000789u, TargetPosition = null, Expect = PredicateExpect("questFlag(82102, 3)") }`.

**When:** `harness.Engine.Tick(CancellationToken.None)`.

**Then:**
- Returns `EngineAction.UseItem` with: `Kind == ItemKind.KeyItem`, `ItemId == 2000456u`, `TargetNpcId == new NpcId(1000789)`, `TargetPosition == null`, `Origin != null`.

#### UI3 — Happy path, AoE position target → emits UseItem with TargetPosition set, TargetNpcId null

**Given:**
- Player not casting.
- UseItemStep `{ Kind = ItemKind.KeyItem, ItemId = 2000123, TargetNpcId = null, TargetPosition = new Position3(123.4f, 0f, -45.6f), Expect = PredicateExpect("questSequence(82103) >= 4") }`.

**When:** `harness.Engine.Tick(CancellationToken.None)`.

**Then:**
- Returns `EngineAction.UseItem` with: `Kind == ItemKind.KeyItem`, `ItemId == 2000123u`, `TargetNpcId == null`, `TargetPosition` is a `Position3` with `X == 123.4f`, `Y == 0f`, `Z == -45.6f`.

#### UI4 — Player casting → Wait; no UseItem emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- UseItemStep as UI1.

**When:** `harness.Engine.Tick(CancellationToken.None)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains the substring `"player casting"`.
- `harness.ItemUser.RecordedCalls.Count == 0`.

#### UI5 — Adapter UseItem returns Result.Failure → stateless retry on next tick

**Given:**
- UseItemStep as UI1 (self-cast inventory item, Expect false).
- `harness.ItemUser.ScriptNextFailure("adapter-error", "ActionManager.UseAction returned false")`.

**When:**
1. Tick 1 → returns `EngineAction.UseItem(...)`.
2. Manually call `await harness.ItemUser.UseItem(ItemKind.InventoryItem, 4554u, null, null, ct)` (consumes the scripted failure; returns `Result.Failure`; recorded call appended).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseItem(...)` again — stateless retry.
- `harness.ItemUser.RecordedCalls.Count == 1` (only the manual call; engine emits the action but does not invoke the adapter).

#### UI6 — Cancellation propagates from dispatch arm

**Given:**
- UseItemStep as UI1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### UI7 — Mounted + prior Navigate: lazy-dismount fires before UseItem

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep navigating to position `(200, 0, 0)` in zone `130` with `Expect = "playerZone() == 130"`.
  2. UseItemStep `{ Kind = ItemKind.KeyItem, ItemId = 2000456, TargetNpcId = null, TargetPosition = null, Expect = PredicateExpect("questFlag(82106, 3)") }` (predicate false).
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 → `EngineAction.Navigate`. After this tick `_lastDispatchedWasNavigate = true`.
2. Advance state: `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect now satisfies).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseItem(...)`.
- `harness.Mount.DismountCallCount >= 1` (lazy-dismount fired — UseItem is NOT in the exemption list per Decision UI18).

Pins Decision UI18.

#### UI8 — Standalone UseItem + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: UseItemStep as UI1.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.UseItem(...)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount is bound to *prior Navigate*).

#### UI9 — Authored Expect already satisfied → step skipped → no UseItem emitted

**Given:**
- UseItemStep `{ Kind = ItemKind.InventoryItem, ItemId = 4554, TargetNpcId = null, TargetPosition = null, Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(8), true)` so the predicate is true *before* the step runs.

**When:** `harness.Engine.Tick(CancellationToken.None)`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits dispatch; step confirmed; no more steps).
- `harness.ItemUser.RecordedCalls.Count == 0`.

#### UI10 — Integration two-tick: UseItem fires, Expect satisfies, step completes

**Given:**
- UseItemStep `{ Kind = ItemKind.KeyItem, ItemId = 2000456, TargetNpcId = 1000789u, TargetPosition = null, Expect = PredicateExpect("questFlag(82110, 3)") }`.

**When:**
1. Tick 1 → `EngineAction.UseItem(...)`.
2. Mimic the harness dispatch:
   - `await harness.ItemUser.UseItem(ItemKind.KeyItem, 2000456u, new NpcId(1000789), null, ct)`.
   - `harness.QuestState.SetQuestFlagBit(new QuestId(82110), 3, true)` (the established setter used by UE9; Tester picks the matching helper).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Wait` (Expect now satisfies; step confirmed; no more steps).
- `harness.ItemUser.RecordedCalls.Count == 1`.
- The recorded call's fields: `Kind == ItemKind.KeyItem`, `ItemId == 2000456u`, `TargetNpcId == new NpcId(1000789)`, `TargetPosition == null`.

### Round-trip tests (`QuestForge.Schema.Tests/RoundTripTests.cs`)

#### UI11 — JSON round-trip self-cast (no target fields written)

**Replaces** the existing `UseItemStep_NoTarget_RoundTrips` test (currently at line 341, references the old `Target: UseItemTarget?` shape).

**Given:** A `UseItemStep { Id = "drink-elixir", Kind = ItemKind.InventoryItem, ItemId = 4554, TargetNpcId = null, TargetPosition = null, Expect = PredicateExpect("playerHasBuff(50)") }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, deserialize as `Step`.

**Then:**
- Deserialized value is a `UseItemStep`.
- `result.Kind == ItemKind.InventoryItem`.
- `result.ItemId == 4554u`.
- `result.TargetNpcId == null`.
- `result.TargetPosition == null`.
- The serialized JSON does NOT contain `"targetNpcId"` (pins `[JsonIgnore(WhenWritingNull)]`).
- The serialized JSON does NOT contain `"targetPosition"` (pins `[JsonIgnore(WhenWritingNull)]`).

#### UI12 — JSON round-trip with NPC target

**Replaces** the existing `UseItemStep_WithTarget_RoundTrips` test (currently at line 356, references `UseItemTarget { Kind = "npc", ... }`).

**Given:** A `UseItemStep { Id = "ring-bell", Kind = ItemKind.KeyItem, ItemId = 2000456, TargetNpcId = 1000789u, TargetPosition = null, Expect = PredicateExpect("questFlag(65657, 5)") }`.

**When:** Serialize, deserialize.

**Then:**
- `result.Kind == ItemKind.KeyItem`.
- `result.ItemId == 2000456u`.
- `result.TargetNpcId == 1000789u`.
- `result.TargetPosition == null`.
- The serialized JSON contains `"targetNpcId": 1000789`.
- The serialized JSON contains `"kind": "keyItem"` (pins `[JsonStringEnumMemberName("keyItem")]`).

#### UI13 — JSON round-trip with AoE position target

**Given:** A `UseItemStep { Id = "smoke-bomb-camp", Kind = ItemKind.KeyItem, ItemId = 2000123, TargetNpcId = null, TargetPosition = new Position3(123.4f, 0f, -45.6f), Expect = PredicateExpect("questSequence(65657) >= 4") }`.

**When:** Serialize, deserialize.

**Then:**
- `result.Kind == ItemKind.KeyItem`.
- `result.ItemId == 2000123u`.
- `result.TargetNpcId == null`.
- `result.TargetPosition` is a `Position3` with `X == 123.4f`, `Y == 0f`, `Z == -45.6f`.
- The serialized JSON contains `"targetPosition"` with sub-fields `"x"`, `"y"`, `"z"`.
- The serialized JSON does NOT contain `"targetNpcId"`.

#### UI19 — `ItemKind` enum round-trips as the configured camelCase strings

**Given:** Two `UseItemStep`s, one with `Kind = ItemKind.KeyItem`, one with `Kind = ItemKind.InventoryItem`. Both with `ItemId = 1u`, no targets, Expect set.

**When:** Serialize each, then re-deserialize.

**Then:**
- The KeyItem step's JSON contains the literal `"kind": "keyItem"` (not `"KeyItem"`, not `"Key_Item"`).
- The InventoryItem step's JSON contains the literal `"kind": "inventoryItem"` (not `"InventoryItem"`).
- Both round-trips preserve `Kind` value.

Pins Decision UI2's `[JsonStringEnumMemberName]` choice.

### Validator tests (`QuestForge.Engine.Tests/Authoring/DraftValidatorUseItemTests.cs`)

Tester decides whether to add to the existing `DraftValidatorTests.cs` or split into a new file; both are acceptable. The four tests are independent.

#### UI14 — Validator E13 (ItemId == 0)

**Given:** A `QuestDraft` containing a `UseItemStep` with `Kind = ItemKind.KeyItem, ItemId = 0, TargetNpcId = null, TargetPosition = null, Expect = PredicateExpect("questFlag(82114, 3)")`. AcceptStep present (so E4 does not fire).

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E13"`.
- The error message mentions the step id and `"ItemId == 0"`.

#### UI15 — Validator E14 (TargetNpcId == 0)

**Given:** A `QuestDraft` containing a `UseItemStep` with `Kind = ItemKind.InventoryItem, ItemId = 4554u, TargetNpcId = 0u, TargetPosition = null, Expect = PredicateExpect("questFlag(82115, 3)")`. AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E14"`.
- The error message mentions the step id and `"TargetNpcId == 0"`.

**Defensive sub-case** (Tester may add in the same test): a UseItemStep with `TargetNpcId = null` and the same other fields MUST NOT trigger E14.

#### UI16 — Validator E15 (both TargetNpcId and TargetPosition set → ambiguous target)

**Given:** A `QuestDraft` containing a `UseItemStep` with `Kind = ItemKind.KeyItem, ItemId = 2000456u, TargetNpcId = 1000789u, TargetPosition = new Position3(1f, 2f, 3f), Expect = PredicateExpect("questFlag(82116, 3)")`. AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E15"`.
- The error message mentions the step id and "both TargetNpcId and TargetPosition" (or equivalent — message phrasing is Builder's choice but MUST be unambiguous to author).

**Defensive sub-case**: a UseItemStep with EXACTLY one of `TargetNpcId` or `TargetPosition` set (and the other null) MUST NOT trigger E15.

#### UI17 — Validator W10 (missing Expect) + W1 suppression

**Given:** A `QuestDraft` containing a `UseItemStep` with `Kind = ItemKind.InventoryItem, ItemId = 4554u, TargetNpcId = null, TargetPosition = null, Expect = null` (missing). AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `warnings` contains exactly one entry with `Code == "W10"`.
- The W10 message contains the substring `"spin-loop"` (per Decision UI12).
- `warnings` does NOT contain an entry with `Code == "W1"` referencing the same step (W1 is suppressed for UseItemStep per Decision UI12).

#### UI18 — Validator clean baseline (well-formed UseItemStep → zero errors, zero warnings)

**Given:** A `QuestDraft` containing a well-formed `UseItemStep { Kind = ItemKind.KeyItem, ItemId = 2000456u, TargetNpcId = 1000789u, TargetPosition = null, Expect = PredicateExpect("questFlag(82118, 3)"), Notes = "uses the bell" }`. AcceptStep and TurnInStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `errors.Count == 0`.
- `warnings.Count == 0`.

Pins "no rule fires unexpectedly on a clean step" (the negative-for-everything posture used by `DraftValidatorTests.Validation_PassesForValidDraft`).

---

## Implementation order

**Phase A — Schema (15 min)**
1. Add `ItemKind` enum to `QuestForge.Schema/SharedValueTypes.cs` (alongside `ActionType`).
2. Delete `UseItemTarget` from `QuestForge.Schema/SharedValueTypes.cs` lines 151-159.
3. Replace `UseItemStep` in `QuestForge.Schema/Step.cs:165-169` per Decision UI1.
4. Add `[JsonSerializable(typeof(ItemKind))]` to `QuestForgeJsonContext.cs` (after the existing `[JsonSerializable(typeof(ActionType))]` at line 24).
5. **Tester writes UI11, UI12, UI13, UI19** in `RoundTripTests.cs` (red — old tests reference deleted `UseItemTarget`). Delete the two old `UseItemStep_*` tests in the same edit.
6. `dotnet build QuestForge.Schema` — must compile.
7. `dotnet test QuestForge.Schema.Tests --filter UseItemStep` — must be green.

**Phase B — Adapter surface (5 min)**
1. Create `QuestForge.Adapters/Items/IItemUser.cs` per Decision UI6.

**Phase C — Fake (5 min)**
1. Create `QuestForge.Adapters.Fakes/Items/FakeItemUser.cs` per Decision UI17.

**Phase D — Engine (25-30 min, TDD)**
1. Append `EngineAction.UseItem` record per Decision UI10 to `EngineAction.cs` (after `SayChatMessage`).
2. Add `_itemUser` field + constructor param to `QuestEngine` per Decision UI13.
3. **Tester writes UI1, UI4, UI6** (single-tick dispatch shape; cheapest). Red.
4. Insert async pre-arm `ResolveUseItem` in `QuestEngine.cs` (after `ResolveSayChatMessage` at line 911) + insert dispatch switch case (after `SayChatMessageStep` arm at line 606) per Decisions UI7, UI15. Green.
5. **Tester writes UI2, UI3** (NPC-target + AoE position variants). Green (no engine change).
6. **Tester writes UI9** (Expect short-circuit). Green (no engine change).

**Phase E — Harness wiring (10 min)**
1. `EngineTestHarness` gains `ItemUser` property + constructor passthrough.
2. `RunToCompletion` gains the `UseItem` arm per Decision UI16.
3. **Tester writes UI5** (stateless retry via manual two-tick).
4. **Tester writes UI10** (integration two-tick).
5. **Tester writes UI7** (lazy-dismount with prior Navigate) and **UI8** (standalone, no dismount).
6. Make them green.

**Phase F — Validator (15 min, TDD)**
1. **Tester writes UI14, UI15, UI16, UI17, UI18** in `DraftValidatorUseItemTests.cs` (or extends `DraftValidatorTests.cs`). Red.
2. Add E13 + E14 + E15 + W10 to `QuestForge.Engine/Authoring/DraftValidator.cs` per Decision UI12.
3. Extend W1 suppression list to include `UseItemStep` (one-line change at `DraftValidator.cs:149`).
4. Green.

**Phase G — Tools-repo paired PR (15 min)**
1. Mirror the schema rewrite in `questforge-tools/QuestForge.Schema/Step.cs` + `SharedValueTypes.cs` + `QuestForgeJsonContext.cs` per Decision UI11 step 1-3.
2. Rewrite `questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs:536-558` `ValidateUseItemStep` per Decision UI11 step 4.
3. Rewrite `questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs:160-235` per Decision UI11 step 5.
4. `dotnet build` + `dotnet test` in the tools repo — both green.
5. Push the tools-repo branch BEFORE the questforge-repo PR merges (paired-PR posture).

**Total dev time: ~1.5-2 hours code. No in-game smoke in Slice 2 (deferred to Slice 4).**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseItemStepTests` reports all 10 engine tests green.
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidatorUseItem` (or the equivalent filter for the chosen test file) reports all 5 validator tests green.
3. `dotnet test QuestForge.Schema.Tests --filter FullyQualifiedName~UseItemStep` reports UI11, UI12, UI13, UI19 green; the legacy `UseItemStep_NoTarget_RoundTrips` / `UseItemStep_WithTarget_RoundTrips` tests no longer exist.
4. A quest JSON with `{ "type": "use-item", "kind": "keyItem", "itemId": 2000456, "targetNpcId": 1000789 }` round-trips losslessly through `QuestForgeJsonContext.QuestFileOptions`.
5. A quest JSON with `{ "type": "use-item", "kind": "inventoryItem", "itemId": 4554 }` (self-cast) round-trips losslessly and the serialized form contains neither `"targetNpcId"` nor `"targetPosition"`.
6. A quest JSON with `{ "type": "use-item", "kind": "keyItem", "itemId": 2000123, "targetPosition": {"x": 1.0, "y": 0.0, "z": 2.0} }` round-trips losslessly.
7. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos (no `TreatWarningsAsErrors` regressions in any project).
8. `UseItemTarget` no longer exists in `QuestForge.Schema/SharedValueTypes.cs` or `questforge-tools/QuestForge.Schema/SharedValueTypes.cs` (grep verification).
9. `questforge-tools` validator CLI rejects a quest with `UseItemStep { ItemId = 0 }` with error code `structural/use-item-itemid-zero`.
10. `questforge-tools` validator CLI rejects a quest with `UseItemStep { TargetNpcId = 1, TargetPosition = {...} }` with error code `structural/use-item-ambiguous-target`.
11. The engine's `ResolveUseItem` returns the `AwaitUser("...no IItemUser wired...")` fallback in any test/host that omits the adapter — but `EngineTestHarness` wires `FakeItemUser` so engine tests never hit that branch.
12. No regression in `UseActionStepTests`, `UseEmoteStepTests`, `SayChatMessageStepTests`, `TeleportStepTests`, `PurchaseItemStepTests`, `AttunementStepTests`, or any existing test.

---

## Exclusions (what this plan does NOT include)

- **`DalamudItemUser` shell.** Slice 3. Will wrap `ActionManager.UseAction(EventItem|Item, itemId, targetId)` for self/NPC and `ActionManager.UseActionLocation(EventItem|Item, itemId, ..., position)` for AoE. The exact ActionManager method names + parameter order MUST be verified against FFXIVClientStructs during Slice 3 build (the names above are conventional but should not be trusted blindly). The ObjectTable lookup mirror's `DalamudEmoteExecutor`'s pattern.
- **`EngineHost` dispatch arm.** Slice 3. Placement: between the `SayChatMessageStep` arm (`EngineHost.cs:482-489`) and `Wait` (`:491`). Debounced log, `_navigator.Stop` if `IsNavigating`, then `await _itemUser.UseItem(...)`.
- **`EngineHost` field construction + `BeginRun` arg passthrough.** Slice 3.
- **Tooling catch-up (CapabilityInferrer / FilenameLookup / DistinguishingCapPriority / TraceConstants).** Slice 3 (paired with `DalamudItemUser` per CLAUDE.md "Slice 3" contract).
  - `CapabilityInferrer.StepCapabilities` already has `[typeof(UseItemStep)] = "step:use-item"` (verified via Read; no change).
  - `FilenameLookup` needs `(["step:talk", "step:travel", "step:use-item"], "with-use-item.json")` exact-shape entry + `("step:use-item", "with-use-item.json")` fallback. Mirrors the use-action / use-emote entries.
  - `DistinguishingCapPriority` needs `"step:use-item"` placed in the existing priority order (action > emote > item > teleport > purchase-item is a sensible default, but Slice 3 architect picks based on shape-defining-ness review).
  - `TraceConstants` needs `ActionUseItem = "useitem"` constant (per `EngineAction.UseItem` → `.GetType().Name.ToLowerInvariant()`).
- **Authoring-mode inference.** Slice 5 (REQUIRED, NEVER DEFER). Signal research required — Decision UI21 documents the candidate signals; the Slice 5 architect must pick one and validate it works for both KeyItem and InventoryItem.
- **`RecordingItemUser` wrapper.** Not needed per Decision UI20 (write-only adapter; trace coverage via `action.submitted` / `action.completed`).
- **Pure helper (analog of `EmoteCommandResolver`).** Not needed per Decision UI19 (no formatting / lookup logic to extract).
- **Quantity / repeated-use semantics.** Not in this slice per Decision UI5; the stateless retry handles "fire until Expect satisfies."
- **`InteractableId` target.** Removed from schema with `UseItemTarget` deletion. If a real quest needs UseItem on an interactable, add `TargetInteractableId: uint?` and extend E15 to "at most one of three."
- **`Tolerance` field.** Removed with `UseItemTarget` deletion. Step-level `StopDistance` covers approach; AoE position is a reticle target, not a navigation destination.
- **Terrain validation of `TargetPosition`.** Not in this slice per Decision UI9 (validator does not know FFXIV terrain).
- **Runtime check of E15 (ambiguous-target).** Not in this slice per Decision UI8 (validator-only; mirrors UA posture on `actionId == 0`).
- **Quest data file with a UseItemStep.** Authored by the data team; this plan only proves the engine + schema + validator surface.
- **In-combat behavior of items.** Some items (potions, food) work in combat by design. The engine does not gate on combat (Decision UI7).

---

## Open questions / decisions called out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| Schema discriminates KeyItem vs InventoryItem how? | `ItemKind` enum in `SharedValueTypes.cs` | Symmetric to `ActionType`; orthogonal to `UseActionStep` per Decision UA8. | UI2, UI3 |
| Target shape — discriminated union or flat? | **Flat** (two nullable fields) | Halves state space vs `UseItemTarget`; single "not both" validator rule replaces state-machine. | UI1, UI4 |
| AoE-position dispatch mechanism? | Direct `ActionManager.UseActionLocation` in Slice 3 (engine passes position through). | Bypasses ground-target reticle. Slice 3 verifies the exact ActionManager API. | UI1 (table row), exclusion section |
| Quantity field? | **No** | Stateless retry already covers "fire until Expect satisfies"; quantity duplicates `RetryConfig`. | UI5 |
| Adapter interface name? | **`IItemUser`** | Noun-form like `INavigator` / `IInteractor`; rejected `IItemExecutor` (asymmetric with siblings) and `IInventoryItemUser` (too narrow). | UI6 |
| Pre-flight guards? | Casting-only (no InCombat, no Status) | Mirrors UE5; items don't expose a clean status trichotomy. | UI7 |
| Ambiguous-target runtime check? | **Validator-only** | Mirrors UA posture; defensive runtime check erodes validator contract. | UI8 |
| `TargetPosition` terrain validation? | **No** | Validator doesn't know FFXIV terrain; would produce false positives. | UI9 |
| Engine surface for AoE position — `Position3` vs `WorldPosition`? | `Position3` (carry schema type through) | No translation cost; one-to-one with schema field. | UI10 |
| Tools-repo paired-PR scope? | Schema mirror + StructuralValidator rewrite in Slice 2; tooling catch-up in Slice 3. | Schema mirror is a hard constraint; tooling catch-up depends on `DalamudItemUser` existing. | UI11, exclusions |
| Validator codes? | **E13 / E14 / E15 / W10** | Continues flat global numbering established by UE7 / SC. | UI12 |
| Recording proxy needed? | **No** | Write-only adapter; trace events from EngineHost cover writes. | UI20 |
| Authoring inference signal? | Slice 5 must do signal research; candidate: FFXIVClientStructs `ActionManager` last-used field. | KeyItem snapshot delta exists but disambiguates poorly against HandOverItem; AoE-position capture is hard. | UI21 |

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 4 scenarios (UI1 self-cast, UI2 NPC target, UI3 AoE position, UI10 integration two-tick)
- Edge cases: 3 scenarios (UI7 lazy-dismount, UI8 standalone-no-dismount, UI9 expect-short-circuits)
- Error / wait cases: 3 scenarios (UI4 player casting, UI5 stateless retry, UI6 cancellation)
- Validator: 5 scenarios (UI14 E13, UI15 E14, UI16 E15, UI17 W10+W1-suppression, UI18 clean-baseline)
- Serialization: 4 scenarios (UI11 self-cast round-trip, UI12 NPC target round-trip, UI13 AoE position round-trip, UI19 ItemKind enum round-trip)
- Expected total:
  - `QuestForge.Engine.Tests/Engine/UseItemStepTests.cs`: 10 tests (UI1–UI10)
  - `QuestForge.Engine.Tests/Authoring/DraftValidatorUseItemTests.cs` (or extension of DraftValidatorTests.cs): 5 tests (UI14–UI18)
  - `QuestForge.Schema.Tests/RoundTripTests.cs`: 4 tests (UI11, UI12, UI13, UI19) — replaces the two existing `UseItemStep_*` tests
  - Grand total: ~19 tests across three projects (plus a paired-PR rewrite of 6 tests in `questforge-tools/QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs` per Decision UI11).
