# UseItemStep — Slice 3 Fix-Forward Spec

**Status:** ready for test creation (fix-forward, not a restart)

**Slice:** 3 correction. Slice 2 (engine/schema/validator/fakes) merged. Slice 3 (Dalamud impl + EngineHost wiring + tools-repo trace catch-up) was implemented; the tools-repo half is committed and clean. This spec corrects three questforge-side defects a reviewer found against checked-out FFXIVClientStructs source.

**Branch:** `feat/use-item-dalamud` (uncommitted working tree).

**Input docs:**
- `docs/USE_ITEM_STEP_PLAN.md` — Slice 2 spec, esp. decision UI19 (native-int mapping belongs in the Dalamud layer, not the engine assembly).
- `CLAUDE.md` — "Adding a New Step Type — Fixed Slice Order", Slice 3 section (EngineHost dispatch-arm recipe; pure-logic-in-Dalamud-assembly testing guidance).
- `feedback_tdd_even_for_adapters.md` — run TDD for Dalamud adapters too; extract pure logic and unit-test it.

**Ground truth (verified by reading source, not decompiling):**
`C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs`, `enum ActionType : uint` (lines ~395-417):
```
None=0, Action=1, Item=2, EventItem=3, EventAction=4, GeneralAction=5,
BuddyAction=6, MainCommand=7, Companion=8, CraftAction=9, Unk10=10,
PetAction=11, Unk12=12, Mount=13, PvPAction=14, FieldMarker=15, ...
```
- There is **no** `ActionType.KeyItem` member.
- Inventory items → `ActionType.Item` = **2**.
- Quest key items → `ActionType.EventItem` = **3**.
- `14` is `PvPAction` (the prompt's "Mount=14" was off by one; Mount is 13). Either way the deleted test's `KeyItem == 14` assertion is wrong on two counts — wrong member name AND wrong number — which is exactly why the engine assembly must assert **no** native ints at all.

---

## 1. Summary

Three defects, one fix-forward correction (no restart, no re-architecture):

1. **Bogus native-int mapping in the Dalamud-free engine assembly.** `QuestForge.Adapters/Items/ItemUseInterpreter.cs` carries a second method, `ToNativeActionType(ItemKind) -> int` (InventoryItem→2, KeyItem→3), plus a comment block (file lines 10-25). It duplicates the canonical Dalamud-layer mapper and bakes brittle native ints into an assembly that must stay Dalamud-free. This violates decision UI19 and the engine-purity invariant. **Delete it** (method + comment block). Keep `ItemTargetMode` + `ResolveTargetMode`.

2. **The duplicated tests assert a value that is wrong twice over.** `QuestForge.Adapters.Tests/Items/ItemUseInterpreterTests.cs` asserts `ToNativeActionType(KeyItem) == 14` (the `[InlineData(ItemKind.KeyItem, 14)]` row, file line 31). There is no `ActionType.KeyItem`; key items are `EventItem = 3`; `14` is `PvPAction`. **Delete both `ToNativeActionType_*` tests.** The engine assembly must not assert native ints, so these tests do not get "corrected to 3" — they get removed. Keep the four `ResolveTargetMode_*` tests.

3. **EngineHost UseItem dispatch arm omits `TryCutsceneSkipConfirm()`.** Confirmed: in `QuestForge.Plugin/EngineHost.cs` the `case EngineAction.UseItem ui:` arm (lines 497-506) does the debounced log + `_navigator.Stop()` guard then jumps straight to `await _itemUser.UseItem(...)`, with no `TryCutsceneSkipConfirm()`. Every sibling content arm — UseAction (line 472), UseEmote (line 484), Purchase (line 455) — calls it before its `await`. **Decision already made by the user: add it** to the UseItem arm, mirroring the UseEmote arm.

The canonical mapping is already correct and already lives in the right place: `QuestForge.Adapters.Dalamud/Items/ItemActionTypeMapper.cs` maps by **named** enum (`ItemKind.InventoryItem -> NativeActionType.Item`, `ItemKind.KeyItem -> NativeActionType.EventItem`). It is `internal static` and references `FFXIVClientStructs.FFXIV.Client.Game.ActionType`. It currently has no test coverage — see Decision A.

After this fix the 3-vs-14 contradiction becomes structurally impossible: there is exactly one mapping, expressed in named enum members, in the one assembly that is allowed to know native action types.

---

## 2. Decision A — ItemActionTypeMapper coverage

**Chosen: Option (a) — note the gap, add NO new test project.**

### Investigation (done — not deferred to the builder)

- **The precedent mapper lives in the Dalamud-free assembly and IS tested there.** `QuestForge.Adapters/Actions/ActionTypeMapper.cs` (note: `QuestForge.Adapters`, not `.Dalamud`) is a *pure* helper — it returns the **schema** `ActionType?` from a raw `uint`, deliberately taking the wire value as a plain integer "to keep this assembly Dalamud-free" (its own doc comment). It has a dedicated unit test at `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs` (UAI-MAP-1..5). That is the project's established pattern: the *pure* half of the type-mapping concern is what gets a unit test, and it lives in the pure assembly.
- **The Dalamud-side mapper is the one that is NOT unit-tested — by precedent.** `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` (`ToFFXIVActionType`, schema `ActionType` → named `ClientStructsActionType`) is the direct structural twin of `ItemActionTypeMapper.ToNative`. It is `internal static`, maps by **named** enum members, has an `ArgumentOutOfRangeException` default arm, and has **no test of any kind**. `ItemActionTypeMapper` being untested is therefore consistent with the established precedent for the exact same concern, not an anomaly.
- **There is no `QuestForge.Adapters.Dalamud.Tests` project.** Confirmed by enumerating every `.csproj`: the only test projects are `QuestForge.Schema.Tests`, `QuestForge.Adapters.Tests`, `QuestForge.Engine.Tests`, `QuestForge.Plugin.Tests`. The Dalamud-backed assembly has never had a test project.
- **`ItemActionTypeMapper.ToNative` genuinely requires the FFXIVClientStructs dependency.** Its return type is the aliased `NativeActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType`. A test asserting `ToNative(ItemKind.KeyItem) == NativeActionType.EventItem` must reference FFXIVClientStructs to name the enum member; that reference resolves only from a Dalamud-SDK project (`Dalamud.NET.Sdk/15.0.0.0`, `net10.0-windows7.0`, x64).

### Why (a), not (b)

- **Matches precedent exactly.** The pure half of mapping is unit-tested; the named-enum Dalamud-side half is not (see `ActionExecutorLogic`, untested). `ItemActionTypeMapper` is the named-enum Dalamud-side half. Treating it differently from its own twin would be inconsistent.
- **`feedback_tdd_even_for_adapters` is satisfied.** That guidance says *extract pure logic and unit-test it*. The pure logic for UseItem is the Direct/Npc/Ground target decision, already extracted into `ItemUseInterpreter.ResolveTargetMode` and kept under test (four `ResolveTargetMode_*` tests retained, §3). The named-enum mapper is the irreducible adapter binding to a native type — not extractable pure logic.
- **Named enum members give compile-time protection stronger than an int test.** `ItemActionTypeMapper` cannot name a non-existent member: `NativeActionType.KeyItem` would not compile (no such member). The `_ => throw new ArgumentOutOfRangeException(...)` default arm guards future `ItemKind` growth. The original defect was caused by a *duplicated int-asserting* mapping (the deleted engine method); the structural fix — one named-enum mapping — is what removes the class of bug, not a second test.
- **Standing up `QuestForge.Adapters.Dalamud.Tests` is disproportionate to a three-defect fix.** It needs a new Dalamud-SDK test csproj, FFXIVClientStructs references, solution + CI wiring, and a `TreatWarningsAsErrors` posture against the Dalamud SDK's uncontrollable `CS0618` emissions (CLAUDE.md: the reason `TreatWarningsAsErrors` is per-project). That is a separate, deliberate infrastructure decision — out of scope here.

### Resulting action for Decision A

- **No new test project, no new mapper test.** The coverage gap is recorded here as a deliberate, visible decision (consistent with the untested `ActionExecutorLogic` twin).
- The protection for `ItemActionTypeMapper` is the compile-time named-enum guarantee + the `ArgumentOutOfRangeException` default arm — already present, unchanged by this fix.
- **For posterity only (explicitly OUT OF SCOPE — see §6):** if `QuestForge.Adapters.Dalamud.Tests` is ever created for other reasons, the canonical first tests for this mapper would be `ToNative(ItemKind.InventoryItem) == NativeActionType.Item`; `ToNative(ItemKind.KeyItem) == NativeActionType.EventItem`; `ToNative((ItemKind)999)` throws `ArgumentOutOfRangeException`. That project would use `Sdk="Dalamud.NET.Sdk/15.0.0.0"`, `<TargetFramework>net10.0-windows7.0</TargetFramework>`, `<Platforms>x64</Platforms>`, reference `QuestForge.Adapters.Dalamud` (+ transitively `QuestForge.Schema` and the Dalamud/FFXIVClientStructs reference set), and keep `TreatWarningsAsErrors` off or narrowly scoped to dodge the SDK `CS0618` noise. Do **not** create it in this branch.

---

## 3. Tester tasks (RED)

All changes are in `QuestForge.Adapters.Tests/Items/ItemUseInterpreterTests.cs`.

**T1 — Delete the two duplicated native-int tests:**
- Delete `ToNativeActionType_KnownKind_ReturnsExpectedNativeValue` (the `[Theory]` with `[InlineData(ItemKind.InventoryItem, 2)]` and `[InlineData(ItemKind.KeyItem, 14)]`, UI-NAT-1 / UI-NAT-2; file lines ~25-41).
- Delete `ToNativeActionType_UndefinedEnumValue_ThrowsArgumentOutOfRangeException` (UI-NAT-3; file lines ~43-56).
- Delete the `// ToNativeActionType — maps ItemKind ...` section banner comment and the UI-NAT explanatory comments above the deleted tests so no dangling reference to `ToNativeActionType` remains in the test file.

**T2 — Keep the four target-mode tests unchanged:**
- `ResolveTargetMode_BothNull_ReturnsDirect` (UI-TGT-1)
- `ResolveTargetMode_OnlyNpcId_ReturnsNpc` (UI-TGT-2)
- `ResolveTargetMode_OnlyPosition_ReturnsGround` (UI-TGT-3)
- `ResolveTargetMode_BothSet_ThrowsArgumentException` (UI-TGT-4)

**T3 — Fix up the class/file doc comment:** the class summary currently says "Tests for ItemUseInterpreter.ToNativeActionType and ItemUseInterpreter.ResolveTargetMode." Reduce it to `ResolveTargetMode` only. Remove the now-false "ALL tests in this file will fail to compile until Builder adds ItemUseInterpreter" line, or rewrite it truthfully — but it must not reference `ToNativeActionType`.

**No new tests.** Per Decision A there is no new mapper test and no new test project.

**Expected RED state after T1–T3, before the builder runs B1:**
- This fix is a *deletion + adapter-wiring add*, so there is no new failing assertion to author. The four retained `ResolveTargetMode_*` tests still compile and pass; the production method `ItemUseInterpreter.ToNativeActionType` is now **dead code with zero references**.
- The RED gate for defect #1 is therefore the reviewer-enforced criterion (§5.1): "zero references to `ToNativeActionType` and zero native ActionType ints in `QuestForge.Adapters/**`." It is RED (the method still exists) until the builder completes B1.
- The RED gate for defect #3 is reviewer criterion §5.4: `TryCutsceneSkipConfirm()` is absent from the UseItem arm. EngineHost dispatch arms have no unit test in this repo (they are exercised by Slice 4 in-game smoke), so there is no xUnit RED signal for it — this is expected.

> Note to tester: your concrete deliverable is the deletions T1–T3. There is no new failing assertion. Correctness is gated by the §5 reviewer grep/inspection criteria that the builder turns green.

---

## 4. Builder tasks (GREEN)

**B1 — Delete `ToNativeActionType` from the engine assembly.**
File: `QuestForge.Adapters/Items/ItemUseInterpreter.cs`.
- Delete the entire `public static int ToNativeActionType(ItemKind kind) => ...` method (currently lines 18-25).
- Delete its leading comment block (currently lines 10-17, the "CANONICAL MAPPING ... pure-layer tests that cannot reference FFXIVClientStructs" block). That comment is now false — the canonical mapping lives only in `ItemActionTypeMapper`.
- Keep `public enum ItemTargetMode { Direct, Npc, Ground }` and the `ResolveTargetMode` method (and its `<summary>`) exactly as-is.
- The `using QuestForge.Schema;` is still required (`ResolveTargetMode` neighbors use schema types via `Position3`); the `using QuestForge.Adapters.Types;` is still required (`NpcId`, `Position3`). Verify both are still referenced after the deletion rather than blind-deleting; do not remove a using that is still needed.

**B2 — Add `TryCutsceneSkipConfirm()` to the EngineHost UseItem dispatch arm.**
File: `QuestForge.Plugin/EngineHost.cs`.
- **Re-read the arms before editing; do not trust these line numbers blindly.** As checked out today:
  - UseEmote arm (lines 476-486) is the template:
    ```
    case EngineAction.UseEmote ue:
        DebounceLog( ... );
        if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
            await _navigator.Stop(ct);
        TryCutsceneSkipConfirm();                                   // <-- this line
        await _emoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
        break;
    ```
  - UseItem arm (lines 497-506) is missing exactly that one line:
    ```
    case EngineAction.UseItem ui:
        DebounceLog( ... );
        if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
            await _navigator.Stop(ct);
        // <-- INSERT: TryCutsceneSkipConfirm();
        await _itemUser.UseItem(ui.Kind, ui.ItemId, ui.TargetNpcId, ui.TargetPosition, ct);
        break;
    ```
- **Insert a bare `TryCutsceneSkipConfirm();` statement** between the `_navigator.Stop()` guard and the `await _itemUser.UseItem(...)` call — exactly the position UseEmote uses. The siblings call it as a void statement (no discard, no await); mirror that. Make no other change to the arm.

**B3 — Decision A: nothing to build.** No new test project, no new mapper test. `ItemActionTypeMapper.cs` is already correct — do not modify it.

**B4 — Confirm no dangling references.** Grep the whole `questforge` repo for `ToNativeActionType`. Expected after B1: **zero** matches in any `.cs` file (production or test). Any remaining match means B1 or a T1 deletion is incomplete.

**B5 — Build + test green.**
- `dotnet build` clean (the engine assembly no longer exposes `ToNativeActionType`; nothing references it).
- `dotnet test QuestForge.Adapters.Tests` green (four `ResolveTargetMode_*` tests pass; the two `ToNativeActionType_*` tests are gone).
- `dotnet build QuestForge.Plugin` clean (EngineHost change compiles).

---

## 5. Reviewer acceptance criteria

1. **Zero native ActionType ints in the engine assembly.** Grep `QuestForge.Adapters/**` (excluding `obj/`/`bin/`): no `ToNativeActionType`, no integer literal `2`/`3`/`14` standing in for an action type, no comment claiming to map `ItemKind` to a native int. The only target-mode logic in `ItemUseInterpreter.cs` is the `ItemTargetMode` enum + `ResolveTargetMode`.
2. **Canonical mapping lives only in `ItemActionTypeMapper`, by named enum.** `QuestForge.Adapters.Dalamud/Items/ItemActionTypeMapper.cs` maps `ItemKind.InventoryItem -> NativeActionType.Item` and `ItemKind.KeyItem -> NativeActionType.EventItem` using named members (unchanged by this fix).
3. **The 3-vs-14 contradiction is structurally impossible.** There is exactly one `ItemKind -> native` mapping in the codebase, and it cannot name a non-existent member without a compile error.
4. **Cutscene-skip present.** The EngineHost `case EngineAction.UseItem:` arm calls `TryCutsceneSkipConfirm()` as a void statement between the `_navigator.Stop()` guard and the `await _itemUser.UseItem(...)` call — same relative position as the UseEmote arm. No other behavioral change to the arm.
5. **Tests green.** `dotnet test QuestForge.Adapters.Tests` passes; the two `ToNativeActionType_*` tests are deleted; the four `ResolveTargetMode_*` tests pass.
6. **Scope limited.** Only the files listed in §3/§4 changed (`ItemUseInterpreter.cs`, `ItemUseInterpreterTests.cs`, `EngineHost.cs`). No new project, no tools-repo change, no change to verified-correct adapter files.

---

## 6. Scope guard — files that MUST NOT change

- **Anything in the `questforge-tools` repo.** The trace catch-up half (CapabilityInferrer, TraceToFixtureExtractor, TraceConstants, their tests) is already committed and clean.
- **Engine / schema / validator / predicates:** `QuestForge.Schema/Step.cs` and the `UseItemStep` type, `QuestForge.Schema/QuestForgeJsonContext.cs`, `QuestForge.Engine/QuestEngine.cs`, `QuestForge.Engine/EngineAction.cs`, `QuestForge.Engine/Authoring/DraftValidator.cs` (E13/E14/E15/W10), the predicate layer, `IGameStateProvider` / `GameStateProvider`.
- **Fakes:** `QuestForge.Adapters.Fakes/` (`FakeItemUser`, etc.).
- **Verified-correct Dalamud impl:** `QuestForge.Adapters.Dalamud/Items/DalamudItemUser.cs` and `QuestForge.Adapters.Dalamud/Items/ItemActionTypeMapper.cs` — both confirmed correct by the reviewer; do not touch.
- **`QuestForge.Adapters/Items/IItemUser.cs`** — the adapter interface is unaffected.
- **No new `QuestForge.Adapters.Dalamud.Tests` project** (Decision A, option (a)).
- **`docs/FIXTURES.md`** is already modified on this branch from the original Slice 3 work; this fix requires no further FIXTURES.md edits (the capabilities/actionType rows are correct). Leave the existing diff alone.

The retained `ItemUseInterpreter.ResolveTargetMode` is the pure-logic surface that keeps this concern unit-tested in the Dalamud-free assembly; the named-enum `ItemActionTypeMapper` is the irreducible adapter binding whose correctness is guaranteed at compile time.

---

✅ READY FOR TEST CREATION

Tester: Apply the deletions in §3 (T1–T3) to `QuestForge.Adapters.Tests/Items/ItemUseInterpreterTests.cs`.
- Happy paths: 0 new scenarios (4 `ResolveTargetMode_*` retained, unchanged)
- Edge cases: 0 new scenarios
- Error cases: 0 new scenarios
- Deletions: 2 tests removed (`ToNativeActionType_KnownKind_ReturnsExpectedNativeValue`, `ToNativeActionType_UndefinedEnumValue_ThrowsArgumentOutOfRangeException`)
- Expected total after fix: 4 tests in `QuestForge.Adapters.Tests/Items/ItemUseInterpreterTests.cs` (down from 6). No new test project (Decision A, option a).
