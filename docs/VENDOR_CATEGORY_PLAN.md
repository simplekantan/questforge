# VendorCategory Field Plan: Optional Category Selection for PurchaseItemStep

**Status:** ready to implement
**Input docs:** `docs/SCHEMA.md`, `QuestForge.Schema/Step.cs`, `QuestForge.Engine/EngineAction.cs`, `QuestForge.Engine/QuestEngine.cs`, `QuestForge.Plugin/EngineHost.cs`
**Output:** PurchaseItemStep supports vendors that show a SelectIconString category menu before opening Shop. Engine dispatches `SelectStringOption` when `VendorCategory` is set.
**Scope:** Field addition to an existing step type -- NOT a new step type. Touches Schema, EngineAction, QuestEngine, EngineHost, DraftValidator, StepFactory, GameStateSnapshot, and round-trip tests.

---

## Dependency graph

Single repo (`questforge`), linear build order:

```
1. QuestForge.Schema       -- add VendorCategory property to PurchaseItemStep
2. QuestForge.Engine       -- add VendorCategory to EngineAction.Purchase + passthrough in ResolvePurchaseAction
3. QuestForge.Engine       -- DraftValidator E-rules for VendorCategory
4. QuestForge.Engine       -- StepFactory + GameStateSnapshot plumbing
5. QuestForge.Plugin       -- EngineHost dispatch: SelectStringOption between InteractWith and Purchase
6. QuestForge.Engine.Tests -- new tests for passthrough, validator, factory, round-trip
```

**Tools-repo catch-up** (paired PR): mirror `VendorCategory` in tools-repo `Step.cs`, add structural validator check, wire into `SnapshotState` if applicable.

---

## Architectural decisions

### VC1: VendorCategory is `int?` with `[JsonIgnore(WhenWritingNull)]`

**Decision:** Add `public int? VendorCategory { get; init; }` to `PurchaseItemStep`, placed after `GcRankTier`, with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.

**Rationale:** Mirrors the existing `GcCategory` and `GcRankTier` fields exactly. The value is a zero-based index into the SelectIconString option list. Null means "vendor opens Shop directly without a category menu."

**Alternatives considered:**
- `string VendorCategory` (category name lookup): Rejected. Category names are locale-dependent. Index is locale-stable and matches how `SelectStringOption` works (zero-based int).
- `uint VendorCategory` (non-nullable): Rejected. Most vendors have no category menu. Making it non-nullable would require a sentinel value (e.g., `uint.MaxValue`), which is worse than `null`.

**What breaks if violated:** Existing quest JSON that omits `vendorCategory` would fail deserialization if the field were non-nullable. The `[JsonIgnore(WhenWritingNull)]` attribute keeps existing JSON clean -- null fields are not emitted.

**Testability:** Round-trip tests verify null omission and value preservation.

### VC2: EngineAction.Purchase gains `int? VendorCategory = null`

**Decision:** Append `int? VendorCategory = null` to the `Purchase` record, after `GcRankTier`.

```csharp
public sealed record Purchase(
    NpcId Vendor, ItemId Item, int Quantity, PurchaseCurrency Currency,
    Step? Origin = null,
    int? GcCategory = null, int? GcRankTier = null,
    int? VendorCategory = null) : EngineAction;
```

**Rationale:** Mirrors the `GcCategory`/`GcRankTier` pattern. Default `null` preserves backward compatibility -- all existing `new EngineAction.Purchase(...)` call sites compile unchanged.

**Alternatives considered:**
- Separate `EngineAction.SelectVendorCategory` action: Rejected. The category selection is not a standalone engine concern -- it is a sub-step of the Purchase dispatch. Splitting it would require the engine to track multi-step purchase state across ticks, adding complexity for no testability gain. The engine's job is to emit `Purchase`; `EngineHost` handles the concrete addon sequencing.
- Putting the category index in `Origin` step: Rejected. `Origin` is `Step?` for trace/debug context, not for data extraction. EngineHost should not downcast `Origin` to read fields.

**What breaks if violated:** If VendorCategory lived only on the Step but not on the EngineAction, EngineHost would need to downcast `action.Origin` to `PurchaseItemStep` to read it. This violates the clean EngineAction-as-data-carrier pattern established by GcCategory/GcRankTier.

### VC3: QuestEngine.ResolvePurchaseAction is a pure passthrough

**Decision:** `ResolvePurchaseAction` copies `step.VendorCategory` into the `EngineAction.Purchase` constructor, alongside the existing `GcCategory` and `GcRankTier` passthrough.

```csharp
var purchaseAction = new EngineAction.Purchase(
    new NpcId(step.Target.NpcId),
    new ItemId(step.ItemId),
    step.Quantity,
    step.Currency,
    Origin: step,
    GcCategory: step.GcCategory,
    GcRankTier: step.GcRankTier,
    VendorCategory: step.VendorCategory);
```

**Rationale:** The engine does not interpret VendorCategory -- it is a value-passer. The EngineHost is the only consumer that knows how to translate the index into a `SelectStringOption` call. This mirrors exactly how GcCategory flows through the engine untouched.

**Testability:** Engine tests verify the emitted `EngineAction.Purchase` carries the correct `VendorCategory` value without transformation.

### VC4: EngineHost inserts SelectStringOption between InteractWith and Purchase

**Decision:** In `EngineHost.DispatchAction`, the `EngineAction.Purchase` case inserts a `SelectStringOption` call after `InteractWith` and before `_vendor.Purchase`:

```csharp
case EngineAction.Purchase p:
    // ... existing debounce, nav stop, cutscene skip ...
    await _interactor.InteractWith(p.Vendor, ct);
    if (p.VendorCategory is { } vendorCat)
        await _interactor.SelectStringOption(vendorCat, ct);
    await _vendor.Purchase(p.Vendor, p.Item, p.Quantity, p.Currency, ct,
        p.GcCategory, p.GcRankTier);
    _lastDispatchedActionWasPurchase = true;
    break;
```

**Rationale:** `SelectStringOption` already handles SelectIconString addon callbacks. If the addon is not yet visible (game frame delay after InteractWith), `SelectStringOption` returns `Fail("addonNotOpen")`. This is safe because the engine re-dispatches `Purchase` every tick until `_vendor.Purchase` returns success (the Shop addon opened and the buy completed). On the next tick:
1. `InteractWith` fires again (NPC is already targeted, dialog may already be open -- TextAdvance handles repeated interact gracefully).
2. By this tick, SelectIconString is likely visible. `SelectStringOption` succeeds, dismissing the category menu.
3. Shop addon opens. `_vendor.Purchase` proceeds.

This retry-on-next-tick behavior is the existing Purchase dispatch pattern -- no new retry logic is needed.

**Alternatives considered:**
- Polling loop inside EngineHost waiting for SelectIconString to appear: Rejected. The engine's tick-based retry is the standard recovery mechanism. Adding an inner poll loop would duplicate retry logic and could stall the engine tick.
- Separate tick for category selection vs purchase: Rejected. The engine emits a single `Purchase` action. EngineHost handles the multi-addon sequencing within one dispatch. This matches how GC purchases work (GrandCompanyExchange radio buttons are set within the same dispatch as the buy).

**What breaks if violated:** If `SelectStringOption` is called before `InteractWith`, the addon does not exist yet and the call is wasted. If called after `_vendor.Purchase`, the Shop may have already failed to open (the category menu was blocking it).

### VC5: Mutual exclusivity with GcCategory/GcRankTier (validator rule E23)

**Decision:** Add E23: if `VendorCategory` is non-null AND either `GcCategory` or `GcRankTier` is non-null, emit an error. These fields address different vendor types:
- `VendorCategory` targets SelectIconString-based vendors (general merchants with category tabs).
- `GcCategory`/`GcRankTier` targets GrandCompanyExchange-based vendors (radio button navigation).

Setting both is always a bug -- no vendor uses both UI patterns.

```csharp
// E23: PurchaseItemStep with both VendorCategory and GcCategory/GcRankTier set
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is PurchaseItemStep ps
        && ps.VendorCategory.HasValue
        && (ps.GcCategory.HasValue || ps.GcRankTier.HasValue))
    {
        errors.Add(new DraftValidationError("E23",
            $"Step '{steps[i].StepId}' has both VendorCategory and GcCategory/GcRankTier set. " +
            "These target different vendor UI types and are mutually exclusive.",
            [i]));
    }
}
```

**Alternatives considered:**
- Warning instead of error: Rejected. Setting both is never valid. A warning would let invalid quest JSON pass validation and fail at runtime (EngineHost would fire SelectStringOption into a GC vendor's non-existent SelectIconString).
- No check at all: Rejected. The failure mode at runtime is silent (SelectStringOption returns addonNotOpen, engine retries forever).

**Testability:** Two validator tests: one with both set (error), one with each set independently (no error).

### VC6: VendorCategory negative value is an error (validator rule E24)

**Decision:** Add E24: if `VendorCategory` is non-null and negative, emit an error. SelectIconString indices are zero-based non-negative integers.

```csharp
// E24: PurchaseItemStep with VendorCategory < 0
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is PurchaseItemStep ps && ps.VendorCategory < 0)
    {
        errors.Add(new DraftValidationError("E24",
            $"Step '{steps[i].StepId}' has VendorCategory={ps.VendorCategory} which is negative.",
            [i]));
    }
}
```

No upper bound check -- vendor category counts vary by NPC. The adapter is the backstop for out-of-range indices.

### VC7: DalamudVendor.Close does NOT close SelectIconString

**Decision:** `DalamudVendor.Close()` currently closes `Shop` and `GrandCompanyExchange`. It should NOT also close `SelectIconString` because:

1. SelectIconString is a general-purpose dialog addon used by many NPCs (lift attendants, quest dialogue, etc.). Closing it in a vendor-specific close hook would dismiss non-vendor dialogs.
2. The lazy-close pattern (`_lastDispatchedActionWasPurchase` flag) fires on the next non-Purchase action. By that point, SelectIconString has already been dismissed (the category was selected and Shop opened). If the purchase failed before Shop opened, the engine retries and SelectIconString may still be visible -- but closing it prematurely would prevent the retry from selecting the category.

**What breaks if violated:** If `Close` dismissed SelectIconString, a failed purchase retry would find no SelectIconString to click, and the NPC interaction would need to restart from scratch. The current behavior is correct: SelectIconString is transient and self-dismisses once a category is selected.

### VC8: StepFactory and GameStateSnapshot plumbing in this PR (inference deferred)

**Decision:** This PR adds:
- `int? ActiveVendorCategory = null` to `PurchaseDetection` record.
- `VendorCategory = after?.PurchaseDetected?.ActiveVendorCategory` in `StepFactory.BuildPurchaseItemStep`.

These are trivial one-line additions that prevent a second PR from touching the same files later. However, the actual signal source (UIObserver detecting which SelectIconString option was selected before Shop opened) is NOT wired in this PR. `ActiveVendorCategory` will always be null until the inference slice lands.

**What this means for authors:** Until inference is wired, authors must manually set `vendorCategory` in the step edit modal or directly in quest JSON. The factory will draft `vendorCategory: null` and the author fills it in.

### VC9: EngineTestHarness mirrors EngineHost dispatch

**Decision:** The `EngineAction.Purchase` case in `EngineTestHarness.RunToCompletion` must mirror the EngineHost change:

```csharp
case EngineAction.Purchase purchase:
    actions.Add(action);
    EmitActionSubmitted("Purchase", ...);
    if (purchase.VendorCategory is { } vendorCat)
        await Interactor.SelectStringOption(vendorCat, ct);
    var purchaseResult = await Vendor.Purchase(...);
    // ... rest unchanged
```

This ensures fake-adapter tests exercise the same sequencing as the real plugin.

---

## Validation rule table

| Code | Severity | Condition | Message | Suppression |
|------|----------|-----------|---------|-------------|
| E23 | Error | `PurchaseItemStep` has `VendorCategory != null` AND (`GcCategory != null` OR `GcRankTier != null`) | "Step '{id}' has both VendorCategory and GcCategory/GcRankTier set. These target different vendor UI types and are mutually exclusive." | None |
| E24 | Error | `PurchaseItemStep` has `VendorCategory < 0` | "Step '{id}' has VendorCategory={value} which is negative." | None |

---

## Given-When-Then specs

### Round-trip tests (`QuestForge.Schema.Tests/RoundTripTests.cs`)

**V1 -- VendorCategory round-trips when set**

Given: `PurchaseItemStep` with `VendorCategory = 2`, `Currency = Gil`, `ItemId = 1601`, `Target = (NpcId:1001234, Zone:128, Position:(10.5,0,-20))`.
When: serialized as `Step` via `QuestForgeJsonContext.QuestFileOptions`, then deserialized.
Then: JSON contains `"vendorCategory":2`; deserialized step is `PurchaseItemStep` with `VendorCategory == 2`; `ItemId`, `Target.NpcId`, and `Currency` also preserved.

Exact JSON shape for the field (match the GcFields pattern -- bare integer, no quotes):
```json
"vendorCategory": 2
```

**V2 -- VendorCategory null omitted from JSON (WhenWritingNull)**

Given: `PurchaseItemStep` with `VendorCategory = null`.
When: serialized as `Step`.
Then: compact JSON does NOT contain the string `"vendorCategory"`. Deserialized step has `VendorCategory == null`.

**V3 -- Missing vendorCategory field defaults to null**

Given: JSON `{"type":"purchase-item","id":"x","target":{"npcId":1001234,"zone":128,"position":{"x":10.5,"y":0,"z":-20.0}},"itemId":1601}` (no `vendorCategory` field).
When: deserialized as `Step`.
Then: result is `PurchaseItemStep` with `VendorCategory == null`.

### Engine passthrough tests (`QuestForge.Engine.Tests/Engine/PurchaseItemStepTests.cs`)

**V4 -- In range, VendorCategory set, emits Purchase with VendorCategory**

Given: player at vendor position, `PurchaseItemStep` with `VendorCategory = 1`, `Currency = Gil`, `ItemId = 1601`, `Quantity = 1`, `Gil = 10000`, item count = 0.
When: engine ticks.
Then: emits `EngineAction.Purchase` with `VendorCategory == 1`, `Vendor`, `Item`, `Quantity`, `Currency` all correct.

**V5 -- In range, VendorCategory null, emits Purchase with VendorCategory null**

Given: same as V4 but `VendorCategory = null`.
When: engine ticks.
Then: emits `EngineAction.Purchase` with `VendorCategory == null` (backward compatible).

**V6 -- VendorCategory and GcCategory both set, engine still passes both through**

Given: player at vendor position, `PurchaseItemStep` with `VendorCategory = 1`, `GcCategory = 2`, `GcRankTier = 0`, `Currency = GcSeals`, `Seals = 2000`, item count = 0.
When: engine ticks.
Then: emits `EngineAction.Purchase` with `VendorCategory == 1` AND `GcCategory == 2` AND `GcRankTier == 0`.

Note: The engine is a value-passer. It does NOT enforce mutual exclusivity -- that is the validator's job. This test proves the engine does not silently drop fields.

### Validator tests (`QuestForge.Engine.Tests/Authoring/DraftValidatorPurchaseTests.cs`)

**V7 -- E23: VendorCategory + GcCategory both set -> error**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = 1` and `GcCategory = 2`.
When: `DraftValidator.Validate(draft)` is called.
Then: errors contains exactly one entry with `Code == "E23"` and `Message` containing "mutually exclusive".

**V8 -- E23: VendorCategory + GcRankTier both set -> error**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = 1` and `GcRankTier = 0`.
When: `DraftValidator.Validate(draft)` is called.
Then: errors contains exactly one entry with `Code == "E23"`.

**V9 -- E23: VendorCategory set, GcCategory/GcRankTier both null -> no E23 error**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = 1`, `GcCategory = null`, `GcRankTier = null`.
When: `DraftValidator.Validate(draft)` is called.
Then: no error with `Code == "E23"`.

**V10 -- E23: GcCategory set, VendorCategory null -> no E23 error**

Given: `QuestDraft` with one `PurchaseItemStep` having `GcCategory = 2`, `VendorCategory = null`.
When: `DraftValidator.Validate(draft)` is called.
Then: no error with `Code == "E23"`.

**V11 -- E24: VendorCategory negative -> error**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = -1`.
When: `DraftValidator.Validate(draft)` is called.
Then: errors contains exactly one entry with `Code == "E24"` and `Message` containing "-1".

**V12 -- E24: VendorCategory = 0 -> no error (zero-based index, 0 is valid)**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = 0`.
When: `DraftValidator.Validate(draft)` is called.
Then: no error with `Code == "E24"`.

**V13 -- E24: VendorCategory null -> no error**

Given: `QuestDraft` with one `PurchaseItemStep` having `VendorCategory = null`.
When: `DraftValidator.Validate(draft)` is called.
Then: no error with `Code == "E24"`.

### StepFactory tests (`QuestForge.Engine.Tests/Authoring/PurchaseStepFactoryTests.cs`)

**V14 -- Factory writes VendorCategory from PurchaseDetection.ActiveVendorCategory**

Given: `after.PurchaseDetected` with `ActiveVendorCategory = 2`, `ShopWasOpen = true`, `ItemDeltas = {1601:1}`, `GilDropped = 1000`, `SealsDropped = 0`.
When: `StepFactory.Build("purchase-item", "buy-cat-2", "playerHasItem(1601,1)", after)`.
Then: result is `PurchaseItemStep` with `VendorCategory == 2`.

**V15 -- Factory writes VendorCategory null when PurchaseDetection.ActiveVendorCategory is null**

Given: `after.PurchaseDetected` with `ActiveVendorCategory = null`.
When: `StepFactory.Build("purchase-item", ...)`.
Then: result is `PurchaseItemStep` with `VendorCategory == null`. No synthetic 0 default.

---

## Implementation order

### Phase A -- Schema + EngineAction (30 min)

1. Add `VendorCategory` property to `PurchaseItemStep` in `Step.cs` (VC1).
2. Add `int? VendorCategory = null` to `EngineAction.Purchase` record (VC2).
3. Update `ResolvePurchaseAction` in `QuestEngine.cs` to pass `VendorCategory: step.VendorCategory` (VC3).

Done-before-next: `dotnet build` succeeds. No tests added yet.

### Phase B -- Round-trip tests (20 min)

4. Add V1, V2, V3 round-trip tests to `RoundTripTests.cs`.

Done-before-next: `dotnet test QuestForge.Schema.Tests` passes.

### Phase C -- Engine passthrough tests (20 min)

5. Add V4, V5, V6 tests to `PurchaseItemStepTests.cs`.

Done-before-next: `dotnet test QuestForge.Engine.Tests` passes.

### Phase D -- Validator (30 min)

6. Add E23 and E24 rules to `DraftValidator.cs` (VC5, VC6).
7. Add V7-V13 validator tests (new file or appended to existing validator tests).

Done-before-next: `dotnet test QuestForge.Engine.Tests` passes with all validator tests green.

### Phase E -- StepFactory + GameStateSnapshot (20 min)

8. Add `int? ActiveVendorCategory = null` to `PurchaseDetection` record in `GameStateSnapshot.cs` (VC8).
9. Add `VendorCategory = after?.PurchaseDetected?.ActiveVendorCategory` to `BuildPurchaseItemStep` in `StepFactory.cs` (VC8).
10. Add V14, V15 factory tests to `PurchaseStepFactoryTests.cs`.

Done-before-next: `dotnet test QuestForge.Engine.Tests` passes.

### Phase F -- EngineHost dispatch + EngineTestHarness (20 min)

11. Insert `SelectStringOption` call in `EngineHost.DispatchAction` Purchase case (VC4).
12. Mirror the change in `EngineTestHarness.RunToCompletion` Purchase case (VC9).

Done-before-next: `dotnet build` succeeds. Manual in-game verification deferred to smoke test.

### Phase G -- Tools repo catch-up (paired PR)

13. Mirror `VendorCategory` field in tools-repo `Step.cs`.
14. Add structural validator negative-value check.
15. Any `SnapshotState` changes for `ActiveVendorCategory`.

---

## Done criteria

1. `dotnet test QuestForge.Schema.Tests` passes: V1-V3 round-trip tests green.
2. `dotnet test QuestForge.Engine.Tests` passes: V4-V15 tests green plus all existing tests unbroken.
3. `dotnet build QuestForge.Plugin` succeeds: EngineHost compiles with the new SelectStringOption dispatch.
4. Existing quest JSON without `vendorCategory` deserializes without error (backward compatibility).
5. Quest JSON with `vendorCategory: 2` round-trips correctly.
6. Draft with both `vendorCategory` and `gcCategory` set produces E23 error.
7. Draft with `vendorCategory: -1` produces E24 error.

---

## Exclusions

- **Authoring inference:** Detection of which SelectIconString option the player chose (UIObserver polling, SnapshotAggregator signal) is a separate future spec. Only the StepFactory/GameStateSnapshot plumbing (VC8) is included here because it touches the same files and is trivial.
- **UI (StepEditModal):** Adding a vendorCategory field editor to the step edit modal is not in scope for this spec. It can be added in a follow-up UI pass.
- **Upper bound validation for VendorCategory:** No maximum is enforced. Vendor category counts vary by NPC, and the adapter is the backstop for out-of-range indices.
- **SelectIconString close on failed purchase:** Per VC7, DalamudVendor.Close does not dismiss SelectIconString. This is intentional and documented.
- **Tools-repo trace extractor changes:** VendorCategory does not change the trace event shape -- it flows through the existing `EngineAction.Purchase` serialization. No `SnapshotState` multi-stage tracking is needed (single-tick signal, not a purchase-span delta).

---

```
READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 6 scenarios (V1, V3, V4, V5, V9, V14)
- Edge cases: 5 scenarios (V2, V6, V10, V12, V15)
- Error cases: 4 scenarios (V7, V8, V11, V13)
- Expected total: ~15 tests across RoundTripTests.cs, PurchaseItemStepTests.cs,
  DraftValidatorPurchaseTests.cs, PurchaseStepFactoryTests.cs
```
