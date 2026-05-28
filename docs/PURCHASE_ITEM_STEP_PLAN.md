# PurchaseItemStep — Testable Specification (`purchase-item`)

**Status:** ready for test creation (awaiting user go-ahead before any implementation)
**Phase:** Phase 11 corpus expansion — new step type (after AttunementStep / HandOverItemStep / playerHasItem)
**Position in TDD workflow:** Architect (you are here) → Tester → Builder → Reviewer
**Input docs:** this plan; `CLAUDE.md` (architecture invariants); `docs/ACCEPT_TURNIN_PLAN.md` + `docs/SELECT_YESNO_RESPONDER_PLAN.md` (format/precedent); `docs/COMBAT_AUTHORING_DETECTION_PLAN.md` (the detection-mirror precedent — Slice E follows it); `docs/SCHEMA.md`; `docs/ADAPTERS.md`
**Output (behavior change):** Quests can author a `purchase-item` step that buys an item from a gil vendor (GilShop) or a Grand Company quartermaster (GrandCompanyExchange). The engine navigates to the vendor, opens the shop, buys until the player's inventory count of the item reaches the authored `Quantity`, and verifies success by re-reading `GetItemCount`. Insufficient funds or repeated buy failure cleanly pauses the run with `AwaitUser`. Tomestone/MGP/scrip vendors are detected and fall back to `AwaitUser`. **Authoring mode now infers `purchase-item` steps** (mirroring combat-step inference): performing a shop purchase during recording surfaces a best-effort `purchase-item` draft (vendor, item id, quantity, pre-filled currency) in `RecordStepModal`, and `qf-trace extract-quest` mirrors the same detection offline. Author-declared `Currency` stays authoritative; detection only pre-fills it.

---

## 0. Scope

Add a new step type `PurchaseItemStep` (discriminator `"purchase-item"`) that encapsulates: navigate to vendor NPC → interact → open shop → select item by id → set quantity → confirm → verify by inventory count.

In scope for v1:
- **Currencies: gil and Grand Company seals.** Gil shops use the `GilShop` addon/`AgentInterface`; the GC quartermaster seal exchange is a *distinct* addon/agent (`GrandCompanyExchange`). Both must be supported by the adapter.
- **Configurable quantity (default 1).** The engine drives quantity / repeats until the inventory-count target is met.
- **Affordability gate → `AwaitUser`.** No gil/seal farming. If the player cannot afford the item, or the purchase fails after bounded retries, the step emits `AwaitUser` with a clear reason.
- **Authoring auto-detection (NOW IN SCOPE).** Purchases performed during a recording session are inferred into a best-effort `purchase-item` draft step, both live (`StepInferenceEngine` → `RecordStepModal`) and offline (`qf-trace extract-quest`), mirroring the combat-step detection pattern (`COMBAT_AUTHORING_DETECTION_PLAN`). The signal is a shop-open observation correlated with a regular-inventory count increase and a currency (gil / GC-seal) decrease in the same window. Author-declared `Currency` remains the authoritative field; detection pre-fills it from the currency that dropped. See **§13 (detection design)** and **Slice E (§9)**.

Out of scope for v1 (see §11):
- Tomestone, MGP, scrip, and other special currencies — detected and routed to `AwaitUser` ("unsupported currency"). Detection likewise only pre-fills `gil`/`gcSeals`; a currency drop with no matching gil/seal delta yields a draft with `Currency` left for the author (§13.4).
- Selling items, vendor "buy-back", and reward/voucher exchanges.

---

## 1. Pre-existing state (audit)

A walk through both repos confirms the following building blocks already exist:

| Item | Location | Status |
| --- | --- | --- |
| `IGameStateProvider.GetItemCount(ItemId, ct)` → `Result<int>` | `QuestForge.Adapters\State\IGameStateProvider.cs:41` | EXISTS — the postcondition read (same read behind `playerHasItem`) |
| `IGameStateProvider.GetGil(ct)` → `Result<long>` | `IGameStateProvider.cs:42` | EXISTS — gil affordability read |
| `UiState.ShopOpen` | `IGameStateProvider.cs:108` | EXISTS — already plumbed into the per-tick `UiState` read |
| `FakeGameStateProvider.SetItemCount / SetGil` | `QuestForge.Adapters.Fakes\State\FakeGameStateProvider.cs:71-72` | EXISTS — fakes for count + gil |
| Implied-navigation helper `ResolveInteractOrNavigate` | `QuestForge.Engine\QuestEngine.cs:639` | EXISTS — the navigate-then-act pattern |
| `EngineAction` records (`Interact`, `HandOver`, `Navigate`, `AwaitUser`, …) | `QuestForge.Engine\EngineAction.cs` | EXISTS — we add one variant |
| `[JsonPolymorphic]` discriminator list | `QuestForge.Schema\Step.cs:11-34` | EXISTS — we add `"purchase-item"` |
| STJ source-gen registration list | `QuestForge.Schema\QuestForgeJsonContext.cs:10-33` | EXISTS — we add `PurchaseItemStep` |
| Per-step validator switch | `questforge-tools\…\StructuralValidator.cs:400-464` | EXISTS — we add a `PurchaseItemStep` case |
| Engine test harness + single-step quest factory | `QuestForge.Engine.Tests\Helpers\EngineTestHarness.cs`, `…\HandOverItemStepTests.cs:322` | EXISTS — mirror for new tests |
| **Authoring inference engine** | `QuestForge.Engine\Authoring\StepInferenceEngine.cs` | EXISTS — combat is Rule 2.2; we add the purchase rule (Slice E) |
| **Live correlation aggregator** | `QuestForge.Engine\Authoring\SnapshotAggregator.cs` | EXISTS — combat span correlation lives here; purchase correlation mirrors it (Slice E) |
| **Snapshot + StepFactory** | `QuestForge.Engine\Authoring\GameStateSnapshot.cs`, `StepFactory.cs` | EXISTS — combat fields + "combat" case; we add purchase fields + "purchase-item" case (Slice E) |
| **Authoring observer** | `QuestForge.Plugin.Tracing\UIObserver.cs` (`PollCombat`, `ICombatProbe`) | EXISTS — purchase observation pollers mirror `PollCombat`/`ICombatProbe` (Slice E) |
| **Record modal seeding** | `QuestForge.Plugin\UI\Authoring\RecordStepModal.cs` | EXISTS — combat seeds `_editKillEnemyDataIds` from `KillCorrelatedTargets`; purchase mirrors with editable fields (Slice E) |
| **Offline mirror** | `questforge-tools\…\SnapshotState.cs`, `Quest\TraceToQuestExtractor.cs`, `Capabilities\CapabilityInferrer.cs` | EXISTS — combat offline mirror (`InCombat`/`EnemyKilled`/`GetQuestVariables` correlation, combat branch); purchase mirror added the same way (Slice E offline sub-slice) |

**What is missing (the work of this feature):**
1. **A GC-seal read.** There is **no** `GetGrandCompanySeals` on `IGameStateProvider` (grep confirms only `GetGil`). The affordability check for GC vendors needs one. (D4)
2. **A vendor capability.** No adapter can open a shop, select an item, set quantity, or confirm a buy. (D3)
3. **`PurchaseItemStep`** schema type + JSON registration. (D1)
4. **`EngineAction.Purchase`** variant. (D2)
5. **`QuestEngine.ResolveActionForStep` arm** for `PurchaseItemStep` + the affordability/postcondition logic. (D5, D6)
6. **`EngineHost.DispatchAction` arm** to execute `Purchase`. (D8)
7. **Validator rules** for the new step (cross-repo). (D9)
8. **Authoring detection** — new shop-open / inventory-count / currency-balance authoring observations, live correlation, an inference rule, modal seeding, and the offline mirror. (D10, §13, Slice E)

---

## 2. Architectural decisions (read before coding)

### D1 — Schema shape: `PurchaseItemStep` with an explicit `Currency` enum (author-declared)

```csharp
// QuestForge.Schema/Step.cs
public class PurchaseItemStep : Step
{
    /// <summary>The vendor NPC and its world position (implied-navigation target). Required.</summary>
    public NpcLocation Target { get; init; } = default!;

    /// <summary>Row id of the item to purchase. Required, must be > 0.</summary>
    public uint ItemId { get; init; }

    /// <summary>How many to own after the step. Default 1, must be >= 1.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Which currency the vendor charges. Author-declared. Default Gil.</summary>
    public PurchaseCurrency Currency { get; init; } = PurchaseCurrency.Gil;
}

[JsonConverter(typeof(JsonStringEnumConverter<PurchaseCurrency>))]
public enum PurchaseCurrency
{
    [JsonStringEnumMemberName("gil")]      Gil,
    [JsonStringEnumMemberName("gcSeals")]  GcSeals
}
```

**Field style** mirrors `HandOverItemStep`/`UseItemStep`: `NpcLocation Target`, `uint ItemId`, plain `int Quantity`. The `Currency` enum mirrors the camelCase `CombatSpawn` enum precedent (`Step.cs:118-125`) — `JsonStringEnumConverter<T>` + `[JsonStringEnumMemberName]`.

**Author-declared currency (NOT inferred at engine runtime).** Rationale:
- **Determinism / engine purity.** The engine must decide *which adapter path and which affordability read* (`GetGil` vs `GetGrandCompanySeals`) **before** any I/O, inside the synchronous `ResolveActionForStep` switch. Inferring "from which shop the NPC opens" at engine runtime requires opening the shop first and inspecting addon state — a Dalamud-only observation the engine cannot make and tests cannot easily fake deterministically.
- **Validator leverage.** A declared `Currency` is statically checkable (`gil` | `gcSeals`; anything else → error). Engine-runtime inference would push currency correctness to runtime, defeating the "catch 60-70% of bugs at validation" goal.
- **Author intent is unambiguous.** The drafter already knows whether they are sending the player to a gil merchant or a GC quartermaster; encoding it is cheap and self-documenting.
- **Safe failure for unsupported currencies.** A future tomestone vendor is authored with… nothing valid — the enum has no member for it, so the file fails *validation* (closed enum) rather than silently mis-buying.

**This is distinct from AUTHORING-TIME detection (D10 / §13).** Authoring detection runs at *recording* time, not engine-decision time, and only *pre-fills* the `Currency` field in the draft so the author starts from the right value. The recorded/serialized step still carries an explicit author-declared `Currency`, so D1's engine-purity and validator guarantees are unchanged: the engine never infers currency from a live shop. Detection observes which balance dropped during recording and writes that into the draft as a *suggestion* the author confirms or corrects.

**Rejected: infer currency from the opened shop addon at engine runtime.** Rejected because it forces an async, Dalamud-only probe into the engine's pure decision path and is untestable without a live game. Inference is also strictly less safe — a GC quartermaster who *also* sells gil items would be ambiguous.

**What breaks if violated:** if `Currency` is omitted from the schema and inferred *by the engine at runtime*, the engine cannot pick the affordability read synchronously, `QuestForge.Engine` gains a Dalamud-shaped dependency (shop-addon inspection), and the validator loses a cheap correctness check.

### D2 — `EngineAction.Purchase` variant (new)

```csharp
// QuestForge.Engine/EngineAction.cs
public sealed record Purchase(
    NpcId Vendor,
    ItemId Item,
    int Quantity,
    PurchaseCurrency Currency,
    Step? Origin = null) : EngineAction;
```

**Why a new variant (not reuse `Interact`).** `Interact` fans out into the quest-lifecycle button chain (`AcceptQuest`/`CompleteQuest`) in `EngineHost.DispatchAction` (`EngineHost.cs:295-322`). A purchase must instead open a shop and drive the GilShop/GrandCompanyExchange addon — a completely different dispatch path. Overloading `Interact` would force the lifecycle chain to run on every purchase tick (harmless-but-noisy at best, racy at worst). A dedicated variant keeps the dispatch arms cohesive, mirroring how `HandOver` is its own variant despite also being "interact then do a thing."

**Carries `Currency` and `Quantity`** so the plugin dispatch arm can pick the adapter overload (gil vs seals) and pass the target quantity without re-reading the step. `Origin` mirrors `Interact`/`HandOver` for trace/debug symmetry.

**Testability implication:** the engine's decision is a pure value (`Purchase` record) asserted directly in `QuestForge.Engine.Tests`, exactly like `HandOverItemStepTests` asserts `EngineAction.HandOver`.

### D3 — Adapter surface: a NEW `IVendor` adapter (NOT extend `IInteractor`)

```csharp
// QuestForge.Adapters/Interaction/IVendor.cs
namespace QuestForge.Adapters.Interaction;

public interface IVendor
{
    /// <summary>
    /// Drive one tick of purchasing <paramref name="quantity"/> of <paramref name="item"/>
    /// from <paramref name="vendor"/> using <paramref name="currency"/>. Idempotent &amp;
    /// re-entrant: the engine calls this every tick until the inventory-count postcondition
    /// is met. The implementation opens the correct shop addon if not already open, sets the
    /// quantity, and confirms — performing at most one game-state-advancing action per call.
    /// </summary>
    Task<Result<PurchaseOutcome>> Purchase(
        NpcId vendor, ItemId item, int quantity, PurchaseCurrency currency, CancellationToken ct);
}

public enum PurchaseOutcome
{
    Purchased,          // a buy was confirmed this tick (count should rise next read)
    AlreadyOwned,       // shop reports nothing to do / target already satisfied at the shop layer
    ShopOpening,        // shop not yet open; interaction issued, retry next tick (NOT a failure)
    InsufficientFunds,  // player cannot afford (gil/seals too low) — engine routes to AwaitUser
    ItemNotSold,        // this vendor does not sell the item id
    ShopNotOpen,        // shop expected open but addon absent after interaction (transient)
    UnsupportedCurrency,// currency this adapter cannot handle (defensive; should be gated upstream)
    Failed              // generic buy failure (addon callback rejected, etc.)
}
```

**Recommendation: a new `IVendor` adapter, not methods on `IInteractor`.** Rationale (cohesion vs interface bloat):
- **Distinct concern.** `IInteractor` is about dialogue, prompts, quest lifecycle, item hand-over, and duty entry. Shopping (GilShop / GrandCompanyExchange agents, quantity spinner, buy confirm) is a separable concern with its own outcome vocabulary. `IInteractor` is already 30+ methods; adding purchase methods worsens the "god interface" smell.
- **Smaller fake surface.** A focused `FakeVendor` is trivial to script (queue of `PurchaseOutcome`, inventory-mutating callback) without touching the already-large `FakeInteractor`.
- **Independent evolution.** When the GilShop/GrandCompanyExchange IPC or ClientStructs layout changes on a patch, only `DalamudVendor` changes — `IInteractor` and its many call sites are untouched.
- **Consistent with the project's adapter taxonomy.** `CLAUDE.md` lists ten single-responsibility adapters; a vendor capability is naturally the eleventh rather than a bolt-on.

**Why a single per-tick `Purchase` method (not `OpenShop`/`SelectItem`/`Confirm`).** The engine is a per-tick re-entrant loop; a multi-call protocol would require the engine to track shop sub-state, which belongs in the Dalamud shell (addon lifecycle), not the pure engine. One idempotent method that "does the next necessary thing and reports an outcome" matches `HandOverItem` (which also returns `ItemPlaced`/`HandedOver`/`NoDialog` for a multi-tick interaction). The engine's authoritative success signal is **not** `PurchaseOutcome` — it is the re-read `GetItemCount` (postcondition discipline, D6).

**What breaks if violated:** putting purchase on `IInteractor` couples shop addon churn to the busiest adapter and bloats every `IInteractor` fake/mock in the test suite.

### D4 — Add `GetGrandCompanySeals` to `IGameStateProvider`

```csharp
// QuestForge.Adapters/State/IGameStateProvider.cs — Inventory section
Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct);   // EXISTS
Task<Result<long>> GetGil(CancellationToken ct);                     // EXISTS
Task<Result<int>> GetGrandCompanySeals(CancellationToken ct);        // NEW
```

**Why `int`:** GC seal caps are well under `int.MaxValue` (≤ ~90,000), so `int` is sufficient and matches `GetItemCount`. (`GetGil` is `long` because gil can exceed `int.MaxValue`.) The fake gets `SetGrandCompanySeals(int)`.

**Affordability is read by the ENGINE, not the adapter.** The engine reads `GetGil` or `GetGrandCompanySeals` (chosen by `step.Currency`) to decide whether to even attempt the purchase. This keeps the affordability *policy* (no farming → AwaitUser) in the testable engine and out of the Dalamud shell. The adapter still defensively reports `InsufficientFunds` if the shop rejects a buy, but the engine's pre-check is the primary gate (D5).

**Note — no per-item price read in v1.** The engine does **not** know the unit price; it cannot compute "can I afford N?" precisely. v1 affordability is a coarse gate: "do I have *any* of this currency above a floor of 0?" The precise gate is the adapter's `InsufficientFunds` outcome (the shop knows the price). See §11 (a `GetShopItemPrice` read is a future enhancement). The coarse engine pre-check exists mainly to short-circuit a GC vendor when the player has literally 0 seals (common: unaligned/low-rank players).

### D5 — Engine resolve arm: navigate → affordability gate → Purchase

```csharp
// QuestEngine.ResolveActionForStep — new arm, grouped with HandOverItemStep.
// NOTE: this arm needs an async affordability read, which ResolveActionForStep cannot do
// (it is sync). See D6 for the structural choice: the affordability read happens in the
// ASYNC ResolveAction body via a dedicated step-gated branch (like the CombatStep/WaitStep
// arms at QuestEngine.cs:500-547), NOT inside the sync switch.
```

The `PurchaseItemStep` is handled by a **step-gated async branch** in `ResolveAction` (mirroring the `CombatStep` and `WaitStep` arms that already live there, `QuestEngine.cs:500-547`) rather than the sync `ResolveActionForStep` switch, because it must (a) read player position for implied navigation and (b) read the currency balance. The branch logic:

1. **Implied navigation first.** Reuse `ResolveInteractOrNavigate(step, target.Position, playerPos, <purchaseAction>, DefaultStopDistance)`. If beyond `StopDistance` → emit `Navigate`. Player position is already read once per tick (`playerPos`, `QuestEngine.cs:384`).
2. **Affordability pre-check.** Read the balance for `step.Currency` (`GetGil` or `GetGrandCompanySeals`). On a **failed** read → fail-open (proceed to Purchase; the adapter's `InsufficientFunds` is the backstop). On a successful read of `0` → emit `AwaitUser($"cannot afford item {ItemId}: 0 {currency}")`.
3. **Emit `Purchase`.** `new EngineAction.Purchase(new NpcId(target.NpcId), new ItemId(ItemId), Quantity, Currency, Origin: step)`.

The affordability read MUST be **step-gated** (only performed when a `PurchaseItemStep` is the active, unconfirmed, not-skipped step) — exactly like `GetHostileActors` is gated to `CombatStep` (D6 in the combat plan) to avoid starving non-purchase fixtures with spurious reads.

### D6 — Postcondition: ABSOLUTE target (`GetItemCount(item) >= Quantity`) — RESOLVED

**Decision: ABSOLUTE.** Completion is `GetItemCount(ItemId) >= Quantity`. The engine buys only enough to *reach* `Quantity`; if the player already holds `>= Quantity`, the step is immediately complete and **no purchase fires**.

**This is encoded as the step's default `Expect`.** When the author does not supply `Expect`, the engine treats the implicit postcondition as `playerHasItem(ItemId, Quantity)` (the existing predicate, backed by the same `GetItemCount` read). The standard per-step loop (`QuestEngine.cs:442`) checks `Expect` *first*, so an already-satisfied count confirms-and-skips the step before any navigation or purchase — idempotent and resume-safe by construction. An authored `Expect` OPTIONALLY overrides this default (e.g. a quest that wants `playerHasItem(ItemId, 1)` even though `Quantity` is set higher for buffer, or a quest-flag postcondition).

**Why absolute over delta-from-baseline:**
- **Idempotent / resume-safe.** A replay or resume that re-enters the step when the player already holds enough will not re-buy. A delta baseline captured at step entry would re-trigger a buy on every fresh entry (resume after crash, NG+ replay), over-purchasing.
- **No hidden state.** Absolute needs no per-step captured baseline persisted across ticks/resumes; it is a pure function of current inventory. Delta requires storing "count at entry," which must survive resume and is a new state-persistence surface (the engine deliberately keeps minimal cross-tick state).
- **Matches existing predicate.** `playerHasItem(item, n)` is already an absolute `>=` check used by `HandOverItemStep` and others; reusing it keeps the model uniform and the validator/predicate layer unchanged.
- **Trace determinism.** Absolute postconditions replay identically; a baseline delta depends on *when* the baseline was captured, which is sensitive to tick alignment.

**Cost of absolute (accepted):** if the player already owns the item for an *unrelated* reason, the step is a no-op even though the quest "intended" a purchase. This is the correct behavior for a quest-automation tool (the goal is "own N," not "spend money"), and is the same semantics `HandOverItemStep` relies on.

**Default-Expect mechanism (concrete):** the engine, when resolving a `PurchaseItemStep` whose `Expect is null`, evaluates a synthesized `playerHasItem(ItemId, Quantity)` predicate via the existing `ExpectEvaluator` to gate completion. Implementation choice for the Builder (either is acceptable, Tester asserts behavior not mechanism):
  - (a) In `StartQuest`/expansion, if `PurchaseItemStep.Expect is null`, set `Expect = new PredicateExpect { Predicate = $"playerHasItem({ItemId},{Quantity})" }`; or
  - (b) In the per-step loop, special-case `PurchaseItemStep` with null `Expect` to evaluate the synthesized predicate.
  Recommendation: **(a)** — it reuses the existing `Expect`-first loop with zero new branching and makes the postcondition visible in any serialized/expanded form.

### D7 — Unaffordable / repeated-failure → `AwaitUser`

The engine routes to `AwaitUser` (terminating the run, per existing `Tick` semantics at `QuestEngine.cs:288-298`) when:
- **Pre-check zero balance** (D5 step 2): `AwaitUser($"cannot afford item {item}: 0 {currency}")`.
- **Adapter reports `InsufficientFunds`**: `AwaitUser($"cannot afford item {item} ({currency})")`.
- **Adapter reports `ItemNotSold`**: `AwaitUser($"vendor {vendor} does not sell item {item}")`.
- **Adapter reports `UnsupportedCurrency`**: `AwaitUser($"unsupported currency for item {item}")` (defensive; closed enum should prevent reaching here).
- **Repeated `Failed`/`ShopNotOpen`** beyond the step's failure budget: routes through the existing `MaxConsecutiveStepFailures` recovery ladder. v1 may simply `AwaitUser($"purchase failed for item {item}")` since the recovery-ladder integration is a separate, already-deferred concern across step types (see ACCEPT_TURNIN_PLAN §7.6).

`ShopOpening`/`Purchased`/`AlreadyOwned` are **forward progress, not failures** — the engine simply ticks again; the postcondition (`Expect`) will confirm completion once the count reaches `Quantity`.

### D8 — Plugin dispatch arm + shop confirm

```csharp
// EngineHost.DispatchAction — new case, modeled on the HandOver arm (EngineHost.cs:324)
case EngineAction.Purchase p:
    DebounceLog($"purchase:{p.Vendor.Value}:{p.Item.Value}",
        $"[Purchase] vendor={p.Vendor.Value} item={p.Item.Value} qty={p.Quantity} cur={p.Currency}");
    TryCutsceneSkipConfirm();
    await _vendor.Purchase(p.Vendor, p.Item, p.Quantity, p.Currency, ct);
    break;
```

**SelectYesno confirm reuse.** A gil purchase raises a `SelectYesno` ("Purchase X for Y gil?"). The existing `SelectYesnoResponder` (`QuestForge.Plugin/Interaction/SelectYesnoResponder.cs`) **already answers it**: it fires on `SelectYesno` `PostSetup` whenever a run is active, defaulting to **Yes** when the active step authors no yesno (`SelectYesnoDecider.Decide` → `Yes`). A `PurchaseItemStep` authors no yesno choice, so the responder confirms the buy automatically. **No new responder is needed and the vendor adapter does NOT drive SelectYesno itself** — the single-owner rule from SELECT_YESNO_RESPONDER_PLAN D4 stays intact (only the responder fires SelectYesno).

The **quantity spinner** and the **buy-confirm button** inside the GilShop/GrandCompanyExchange addons are NOT SelectYesno and ARE the vendor adapter's responsibility (D3). The adapter sets the quantity and clicks buy; the resulting SelectYesno (if any) is dismissed by the responder.

### D9 — Validator rules (cross-repo: `questforge-tools`)

Add a `PurchaseItemStep` case to `StructuralValidator.CheckStepTypeRules` (`StructuralValidator.cs:400`). Cross-repo impact is **flagged, not designed in depth** here (it is its own slice, §10 Slice D):

| Rule (error code) | Condition | Severity |
| --- | --- | --- |
| `structural/purchase-item-id-zero` | `ItemId == 0` | Error |
| `structural/purchase-quantity-nonpositive` | `Quantity < 1` | Error |
| `structural/purchase-npc-id-zero` | `Target.NpcId == 0` | Error |
| `structural/purchase-currency-invalid` | `Currency` not in `{gil, gcSeals}` (enforced by closed enum at deserialize; a belt-and-suspenders check for raw-JSON paths) | Error |

The `questforge-tools` repo also needs the schema mirror updated so the new type deserializes there (it consumes the same `QuestForge.Schema` types — confirm the validator references the Schema project; if it vendors a copy, mirror `PurchaseItemStep` + `PurchaseCurrency`). `CapabilityInferrer` (qf-trace) should map `[typeof(PurchaseItemStep)] = "step:purchase-item"` for capability tagging, mirroring the `AcceptStep`/`TurnInStep` entries (`CapabilityInferrer.cs:14-38`).

### D10 — Authoring stance: INFER `purchase-item` during recording (was: hand-authored only) — REVISED

**Decision (REVISED, now in scope): `purchase-item` IS inferred during authoring**, mirroring how combat is inferred (`COMBAT_AUTHORING_DETECTION_PLAN`, Rule 2.2). The previous stance ("hand-authored only, WaitStep precedent") is withdrawn at the user's direction. A purchase performed in a recording session surfaces a best-effort `purchase-item` draft step in `RecordStepModal` (live) and in `qf-trace extract-quest` (offline), with the same correlation algorithm running in both consumers.

**Why this is now worth it (the previous deferral reasons re-examined):**
- *"Purchases are rare."* True, but rarity does not justify making the author hand-write four fields when the act of buying leaves a perfectly attributable trace (a shop opened, a specific item count rose, a specific currency dropped). Combat is also a minority of steps yet is inferred because the signal is clean — the same is true here.
- *"Vendor/item/quantity are hard to infer."* On closer analysis they are NOT, given the right observations: the interacted vendor NPC is already captured (`LastNpcInteracted`), the item id + quantity come from the inventory-count delta, and the currency is disambiguated by which balance dropped. This is a *more* deterministic signal than combat (no ±500ms kill-correlation guesswork; the shop-open event brackets the window precisely).
- *"Disambiguate currency is risky."* Detection only *pre-fills* `Currency`; the author confirms it in the modal (D1 keeps the field authoritative). A wrong pre-fill is a one-click correction, never a silent mis-buy.

**The full detection design — signals, correlation rule, integration points, the fixture-cascade risk and its mitigation, and ambiguity handling — is specified in §13.** The slice that implements it is **Slice E (§9)**. The cross-repo offline mirror (`qf-trace extract-quest`) is a sub-slice of Slice E, following the combat offline-mirror precedent (`CapabilityInferrer` tag + `SnapshotState` apply-cases + `TraceToQuestExtractor` branch).

**What still does NOT change:** the engine never infers currency at *runtime* (D1). Detection is a *recording-time* convenience that writes a draft; the serialized step always carries an explicit author-declared `Currency`.

---

## 3. Schema contract — `PurchaseItemStep`

```csharp
public class PurchaseItemStep : Step
{
    public NpcLocation Target { get; init; } = default!;   // vendor NPC + position (implied-nav target)
    public uint ItemId { get; init; }                      // item to buy, > 0
    public int Quantity { get; init; } = 1;                // own-this-many target, >= 1
    public PurchaseCurrency Currency { get; init; } = PurchaseCurrency.Gil;
    // Inherited: Id, Zone, RequiredZone, Expect, SkipIf, StopDistance, Recover, Retry, Preconditions, Notes
}
```

`NpcLocation` = `record NpcLocation(uint NpcId, int Zone, Position3 Position)` (`SharedValueTypes.cs:16`).

**Registration:**
- `Step.cs`: add `[JsonDerivedType(typeof(PurchaseItemStep), "purchase-item")]` to the discriminator list.
- `QuestForgeJsonContext.cs`: add `[JsonSerializable(typeof(PurchaseItemStep))]` and `[JsonSerializable(typeof(PurchaseCurrency))]`.

### Expected authoring usage

```jsonc
// gil vendor — buy 1, default currency
{
  "type": "purchase-item",
  "id": "buy-bronze-cesti",
  "target": { "npcId": 1001234, "zone": 128, "position": { "x": 10.5, "y": 0, "z": -20.0 } },
  "itemId": 1601,
  "quantity": 1
  // currency omitted → defaults to "gil"
  // expect omitted → engine synthesizes playerHasItem(1601, 1)
}

// GC quartermaster — buy 3 of a seal item
{
  "type": "purchase-item",
  "id": "buy-gc-issued-aetherpool",
  "target": { "npcId": 1002000, "zone": 128, "position": { "x": 5.0, "y": 0, "z": 5.0 } },
  "itemId": 6141,
  "quantity": 3,
  "currency": "gcSeals",
  "skipIf": { "predicate": "playerHasItem(6141,3)" }
}
```

---

## 4. Validation rule table (Slice D — `questforge-tools`)

| Error code | Suppression / condition | Severity |
| --- | --- | --- |
| `structural/purchase-item-id-zero` | suppressed when `ItemId > 0` | Error |
| `structural/purchase-quantity-nonpositive` | suppressed when `Quantity >= 1` | Error |
| `structural/purchase-npc-id-zero` | suppressed when `Target.NpcId > 0` | Error |
| `structural/purchase-currency-invalid` | suppressed when `Currency ∈ {gil, gcSeals}` (closed enum normally enforces) | Error |

(Semantic checks — item id exists in game data, NpcId/zone valid — reuse the existing reference-resolution layer applied to other steps; no new semantic rule design here.)

---

## 5. Given-When-Then specifications

Targets `QuestForge.Engine.Tests` (engine arm), `QuestForge.Schema.Tests` (round-trip), and `QuestForge.Adapters.Tests` (FakeVendor) unless noted. Mirror `HandOverItemStepTests.cs` harness usage. Use a `ConditionQuestId` distinct from the running quest id so marking conditions complete never returns `Done` prematurely (precedent: `HandOverItemStepTests.cs:33`).

(Authoring-detection GWT specs for the live aggregator/inference/modal and the offline mirror are in **§13.5 / Slice E**, modeled on `COMBAT_AUTHORING_DETECTION_PLAN §Task 4`.)

Test constants: `TestQuestId = 12345`, `TestVendorNpc = 1001234`, `TestItem = 1601`, `TestSeq = 0`, `TargetPos = (10.5, 0, -20.0)`.

### Group A — engine resolve arm (navigation + emit Purchase)

**A1 — in range, count below target → emits `Purchase`.**
Given a quest seq=0 with `PurchaseItemStep { Target=(1001234, 128, (10.5,0,-20)), ItemId=1601, Quantity=1, Currency=Gil }`,
and player at `(10.5,0,-20)` (XZ distance 0),
and `GetItemCount(1601)=0`, `GetGil()=10000`,
When `Engine.Tick`,
Then `EngineAction.Purchase` with `Vendor==NpcId(1001234)`, `Item==ItemId(1601)`, `Quantity==1`, `Currency==Gil`.

**A2 — out of range → emits `Navigate`.**
Given A1 but player at `(0,0,0)` (XZ distance >> 3.0 default),
When `Engine.Tick`,
Then `EngineAction.Navigate` whose `Destination` X/Z match `TargetPos` (precision 2).

**A3 — custom `StopDistance` honored.**
Given A1 with `StopDistance=8.0` and player ~7.0 away on XZ,
When `Engine.Tick`,
Then `EngineAction.Purchase` (within custom radius), not `Navigate`.

**A4 — GC-seal currency emits Purchase carrying `GcSeals`.**
Given A1 in range but `Currency=GcSeals`, `GetGrandCompanySeals()=2000`, `GetItemCount=0`,
When `Engine.Tick`,
Then `EngineAction.Purchase` with `Currency==GcSeals`.

**A5 — position read failure fails open → `Purchase`.**
Given A1 but `GameState.SetPositionFailure(...)`,
When `Engine.Tick`,
Then `EngineAction.Purchase` (fail-open, skip distance check) — mirrors `HandOverItemStep_PositionFailure_FailsOpen`.

### Group B — postcondition (ABSOLUTE, default + override)

**B1 — already owns target (no authored Expect) → step complete, no Purchase.**
Given A1 with `Quantity=2` and `GetItemCount(1601)=2` (>= target), no authored `Expect`,
When `Engine.Tick`,
Then `EngineAction.Wait` (default `playerHasItem(1601,2)` confirms the step; no `Purchase`/`Navigate` emitted).

**B2 — owns more than target → step complete (idempotent).**
Given `Quantity=1`, `GetItemCount(1601)=5`,
When `Engine.Tick`,
Then `EngineAction.Wait` (absolute `>=` semantics; never re-buys).

**B3 — count rises to target across ticks → completes.**
Given A1 `Quantity=1`, initial `GetItemCount=0`; tick 1 returns `Purchase`; test then `GameState.SetItemCount(ItemId(1601),1)`,
When `Engine.Tick` again,
Then `EngineAction.Wait` (Expect now satisfied).

**B4 — authored `Expect` overrides default.**
Given A1 `Quantity=3` but authored `Expect = playerHasItem(1601,1)`, and `GetItemCount=1`,
When `Engine.Tick`,
Then `EngineAction.Wait` (authored expect met at 1, even though Quantity=3).

**B5 — `SkipIf` true → skipped.**
Given A1 with `SkipIf = isQuestComplete(99999)` and `QuestState.SetQuestStatus(99999, Complete)`,
When `Engine.Tick`,
Then `EngineAction.Wait` (step skipped; precedent `HandOverItemStep_SkipIf_True`).

### Group C — affordability gate → AwaitUser

**C1 — zero gil pre-check → `AwaitUser`.**
Given A1 in range, `GetItemCount=0`, `GetGil()=0`,
When `Engine.Tick`,
Then `EngineAction.AwaitUser` whose `Reason` contains `"afford"` and `"1601"`.

**C2 — zero GC seals pre-check → `AwaitUser`.**
Given A4 in range, `Currency=GcSeals`, `GetGrandCompanySeals()=0`, `GetItemCount=0`,
When `Engine.Tick`,
Then `EngineAction.AwaitUser` whose `Reason` contains `"afford"`.

**C3 — balance read failure → fail open → `Purchase`.**
Given A1 in range, `GetItemCount=0`, and `GetGil` returns a failed `Result`,
When `Engine.Tick`,
Then `EngineAction.Purchase` (fail-open; adapter `InsufficientFunds` is the backstop).

**C4 — positive balance → proceeds to `Purchase`.**
Given A1, `GetGil()=1`, `GetItemCount=0`,
When `Engine.Tick`,
Then `EngineAction.Purchase` (coarse gate passes on any >0).

### Group D — FakeVendor + adapter contract (`QuestForge.Adapters.Tests`)

**D1 — FakeVendor records the call and returns scripted outcome.**
Given a `FakeVendor` scripted `Purchase → PurchaseOutcome.Purchased`,
When `Purchase(NpcId(1001234), ItemId(1601), 1, Gil, ct)`,
Then result is `Ok(Purchased)` and the call is recorded (vendor, item, quantity, currency).

**D2 — FakeVendor inventory-mutating callback simulates the buy.**
Given a `FakeVendor` wired to increment `FakeGameStateProvider` item count on `Purchased`,
When `Purchase` is called once,
Then `GetItemCount` rises by the per-tick increment (supports B3-style flow tests).

**D3 — FakeVendor default outcome when unscripted.**
Given an unscripted `FakeVendor`,
When `Purchase` is called,
Then it returns a benign default (`ShopOpening`) and records the call.

### Group E — schema round-trip (`QuestForge.Schema.Tests`)

**E1 — `PurchaseItemStep` round-trips with `"type": "purchase-item"` discriminator.**
Given a `PurchaseItemStep { Target, ItemId=1601, Quantity=2, Currency=Gil }` serialized via `QuestForgeJsonContext.QuestFileOptions`,
When inspected,
Then JSON contains `"type": "purchase-item"`; deserializing yields a `Step` whose runtime type is `PurchaseItemStep`; `ItemId`, `Quantity`, `Target.NpcId` preserved.

**E2 — `Currency` serializes camelCase and round-trips both members.**
Given `Currency=GcSeals`,
When serialized,
Then JSON contains `"currency": "gcSeals"`; deserialization yields `PurchaseCurrency.GcSeals`. (Repeat for `Gil` → `"gil"`.)

**E3 — `Quantity` defaults to 1 when omitted.**
Given JSON without a `quantity` field,
When deserialized,
Then `Quantity == 1`.

### Group F — validator (`questforge-tools`, Slice D)

**F1 — `ItemId == 0` → `structural/purchase-item-id-zero` (Error).**
**F2 — `Quantity < 1` → `structural/purchase-quantity-nonpositive` (Error).**
**F3 — `Target.NpcId == 0` → `structural/purchase-npc-id-zero` (Error).**
**F4 — valid step (ItemId>0, Quantity>=1, NpcId>0, Currency gil) → no purchase-* errors.**

---

## 6. Error handling strategy

| Condition | Engine behavior | Recovery |
| --- | --- | --- |
| `ItemId == 0` / `Quantity < 1` / `Target.NpcId == 0` | Malformed file; caught by validator (F1-F3) upstream. Engine would emit a degenerate `Purchase`/`AwaitUser`; not the engine's job to lint. | Authoring-time (validator). |
| Player far from vendor | Engine emits `Navigate` (implied nav). | Self-correcting per tick. |
| Zero currency (pre-check) | `AwaitUser` (D7). | Manual. |
| Balance read fails | Fail-open → `Purchase`; adapter backstops with `InsufficientFunds`. | Per-tick retry. |
| Shop not yet open | Adapter returns `ShopOpening`/`ShopNotOpen`; engine ticks again (forward progress, not failure). | Self-correcting. |
| Adapter `InsufficientFunds` / `ItemNotSold` / `UnsupportedCurrency` | `AwaitUser` with specific reason (D7). | Manual. |
| Repeated `Failed` | v1: `AwaitUser("purchase failed…")`. Recovery-ladder integration deferred (consistent with other steps). | Manual / future ladder. |

The engine does **not** throw for any runtime condition. Null `Target`/`Quest` propagate as exceptions per the project convention.

---

## 7. Integration points and seam locations

| Production code | Test seam |
| --- | --- |
| `QuestEngine` purchase branch (async, step-gated) | `EngineTestHarness.Engine.Tick`, assert `EngineAction.Purchase` / `Navigate` / `AwaitUser` / `Wait`. |
| `FakeGameStateProvider.SetItemCount/SetGil/SetGrandCompanySeals(NEW)` | flip affordability + count between ticks. |
| `FakeVendor` (NEW) | scripts `PurchaseOutcome`; optional inventory-mutating callback. |
| `QuestForgeJsonContext.QuestFileOptions` | E1-E3 serialization. |
| `StructuralValidator.CheckStepTypeRules` (cross-repo) | F1-F4. |
| `EngineTestHarness.RunToCompletion` | add a `Purchase` case that calls `FakeVendor.Purchase` (mirrors the `HandOver` case at `EngineTestHarness.cs:151`). |
| **`SnapshotAggregator` purchase correlation (NEW, Slice E)** | feed `OnShopOpened`/`OnVendorItemCountChanged`/`OnCurrencyChanged`, assert `Current.PurchaseDetected`. |
| **`StepInferenceEngine` purchase rule (NEW, Slice E)** | `Infer(before, after)` → `StepType=="purchase-item"`. |
| **`RecordStepModal` purchase seeding (NEW, Slice E)** | editable item/quantity/currency fields seeded from `after.PurchaseDetected`. |
| **`UIObserver` + `IVendorProbe` (NEW, Slice E)** | `FakeVendorProbe` scripts shop-open + balances; assert observations + aggregator forwards. |
| **`SnapshotState` + `TraceToQuestExtractor` (NEW, Slice E offline)** | apply purchase observation events; assert offline `PurchaseItemStep`. |

**New fakes/adapters:** `IVendor` + `FakeVendor` + `DalamudVendor`; `GetGrandCompanySeals` on `IGameStateProvider` + all three impls (Dalamud, Fake, Recording proxy). Wire `IVendor` into the `QuestEngine` constructor and `EngineTestHarness`/`EngineHost`. **(Slice E adds)** `IVendorProbe` (authoring) + `FakeVendorProbe` + `DalamudVendorProbe`; `PurchaseDetection` snapshot fields; `StepInferenceEngine` purchase rule; `StepFactory` "purchase-item" case; `RecordStepModal` purchase fields; offline `SnapshotState` apply-cases + extractor branch + `CapabilityInferrer` tag.

---

## 8. Dalamud implementation sketch (clean-room — `DalamudVendor`)

Behavioral/public-API only. **No source copied from Questionable or any AGPL plugin.** Reference only public FFXIVClientStructs/Dalamud API shapes.

- **Gil shop:** the `GilShop`/`Shop` agent (`AgentInterface`-derived) opens via interacting with the merchant NPC (reuse the same target+interact mechanics as `DalamudInteractor.InteractWith`). Once the `Shop`/`InventoryBuy`-style addon is ready, the buy is driven by the shop agent's public "buy item, quantity" entry point and/or the addon's buy button + quantity-spinner callback. The follow-up `SelectYesno` is dismissed by the existing `SelectYesnoResponder` (D8) — `DalamudVendor` does **not** touch SelectYesno.
- **GC quartermaster:** the `GrandCompanyExchange` agent/addon is distinct. Interacting with the quartermaster opens it; the buy entry point differs from GilShop. `DalamudVendor` branches on `PurchaseCurrency`: `Gil → GilShop path`, `GcSeals → GrandCompanyExchange path`.
- **Idempotent per tick:** open-if-not-open, else set quantity + click buy; report `ShopOpening` when the addon is not yet ready (mirrors `HandOverItem`'s `NoDialog`). The engine re-reads `GetItemCount` to decide completion (D6), so a missed/dropped buy is simply retried next tick.
- **Affordability backstop:** if the shop rejects the buy for funds, report `InsufficientFunds`. If the item is not in the shop's list, `ItemNotSold`. Anything else → `Failed`.
- **`GetGrandCompanySeals` impl:** read the player's current Grand Company seal balance via the public client-state/GC API. Returns `Result.Fail` if the player has no Grand Company (engine fail-opens; the GilShop path is unaffected).

These are the only Dalamud-touching pieces; they are untested in CI (consistent with `DalamudInteractor`). **(Slice E adds `DalamudVendorProbe`** — see §13.3 — also untested in CI, consistent with `DalamudCombatProbe`.)

### 8.1 — Gated vendors (menu tree before the Shop opens) — DEFERRED to a follow-up slice

Slice C as shipped handles the **direct** case: interacting with the merchant opens the `Shop` addon, and `DalamudVendor` resolves the BuyList row (item id at `AtkValues[441 + i]`, count at `AtkValues[2]`) and fires the confirmed buy callback `FireCallback([Int 0 = buy, Int row, Int qty])`; the follow-up `SelectYesno` ("Purchase N X for Y gil?") is dismissed by the existing `SelectYesnoResponder`.

Many vendors are **gated**: interacting first opens a `SelectIconString` category picker (e.g. "Purchase Disciple of War Arms" / "Purchase Disciple of Magic Arms" / "Nothing"), and selecting a category MAY open a further `SelectString` range picker, before the `Shop` addon finally opens:

```
Interact → [SelectIconString category] → [maybe SelectString range] → Shop → buy
```

**Chosen design (author-declared index path).** `PurchaseItemStep` gains an OPTIONAL `OpenPath: int[]` — the zero-based option indices to select in sequence to reach the Shop (e.g. `[0, 2]` = SelectIconString option 0, then SelectString option 2). Rationale:
- **Locale-stable.** Menu option *order* is identical across client languages; only the displayed text differs. An index path is language-independent (matching the labels by string would not be — see [[locale-stable-quest-identifiers]]). The existing `IInteractor.SelectStringOption(int index)` already drives both `SelectIconString` and `SelectString` via FireCallback.
- **Deterministic / engine-pure.** No live string matching or game-data lookup in the decision path.
- **Recordable.** The authoring-detection slice (E) can capture the `SelectIconString`/`SelectString` selections the player makes between interacting with the vendor and the Shop opening, and pre-fill `OpenPath` automatically.

**Runtime behavior (follow-up):** `DalamudVendor.Purchase`, per tick — if `Shop` open → resolve row + buy (current); else if a `SelectIconString`/`SelectString` is open → select the next `OpenPath` index (advancing through the path as each menu instance opens) and report `ShopOpening`; else → `ShopOpening`. Absent an `OpenPath`, the direct case still works, and a gated vendor with no path → `AwaitUser` ("vendor requires menu navigation; author an openPath") rather than stalling.

**Rejected alternatives:** auto-search/brute-force (try each category, scan the Shop, back out) — stateful, slow, can briefly mis-navigate or mis-spend; game-data category derivation — fragile, complex. Both rejected for v1.

**Cross-slice impact when built:** schema (`OpenPath` field + round-trip test, mirrors Slice A), `DalamudVendor` menu-walk + per-purchase path state (Slice C-style, untested in CI), and authoring capture of the path (Slice E). Until then, gated vendors are documented as unsupported and degrade to `AwaitUser`.

---

## 9. Implementation order (slice plan, ordered for TDD)

Done-before-next is strict; each slice is independently green before the next begins.

**Slice A — Schema (TDD: red→green on round-trip) — ~0.5 day**
1. Add `PurchaseItemStep` + `PurchaseCurrency` to `Step.cs`; register discriminator + `QuestForgeJsonContext`.
2. Make GWT **Group E** (E1-E3) green. ← gate: schema round-trips pass.

**Slice B — Engine + fakes (TDD, the bulk) — ~1.5 days**
3. Add `EngineAction.Purchase` (D2).
4. Add `IVendor` + `PurchaseOutcome` (D3); `FakeVendor`; `GetGrandCompanySeals` on `IGameStateProvider` + `FakeGameStateProvider.SetGrandCompanySeals` + `RecordingGameStateProvider` passthrough.
5. Wire `IVendor` into `QuestEngine` ctor + `EngineTestHarness` + `RunToCompletion` Purchase case.
6. Implement the step-gated async purchase branch (D5), default-Expect synthesis (D6a), affordability gate (D7).
7. Make GWT **Groups A, B, C, D** green. ← gate: all engine + fake tests pass; engine has no Dalamud reference.

**Slice C — Dalamud shell (untested in CI) — ~1.5 days**
8. `DalamudVendor` (GilShop + GrandCompanyExchange, clean-room, D8); `DalamudGameStateProvider.GetGrandCompanySeals`.
9. `EngineHost`: construct `DalamudVendor`, pass to `QuestEngine`, add `EngineAction.Purchase` dispatch arm (D8). Confirm `SelectYesnoResponder` already wired (it is).
10. In-game smoke: a quest with one gil `purchase-item` and one GC-seal `purchase-item`.

**Slice D — Tools/validator (cross-repo `questforge-tools`) — ~0.5 day**
11. Mirror `PurchaseItemStep`/`PurchaseCurrency` if the validator vendors Schema; add the `CheckStepTypeRules` case (D9); `CapabilityInferrer` `step:purchase-item` tag.
12. Make GWT **Group F** (F1-F4) green in `QuestForge.Tools.Validator.Tests`.

**Slice E — Authoring detection (live + offline mirror) — ~2 days**
Depends on **Slice A** (the `PurchaseItemStep`/`PurchaseCurrency` types must exist so `StepFactory` and the extractor can build the step). It is independent of Slices B/C/D and can follow them. Models `COMBAT_AUTHORING_DETECTION_PLAN` slice-for-slice.

*E.1 — Authoring observation contract + probe + emission (CI-testable; `questforge`).*
13. Add `IVendorProbe` to `QuestForge.Plugin.Tracing` (§13.2). Add `PollVendor` to `UIObserver`'s heartbeat block emitting `ShopOpened`, `VendorItemCount`, `CurrencyBalance` observations; wire `ResetHeartbeatState`. Back-compat: no probe → no emission.
14. Tests **GWT-PU1..PU5** (§13.5) with `FakeVendorProbe`. ← gate.

*E.2 — Live correlation + inference + factory + modal (CI-testable; `questforge`).*
15. `PurchaseDetection` record + `GameStateSnapshot` fields (§13.2). `SnapshotAggregator` purchase-span correlation (`OnShopOpened`/`OnVendorItemCountChanged`/`OnCurrencyChanged`; window reset). `InferredFrom.Purchase`. `StepInferenceEngine` purchase rule (§13.1, ordering §13.1). `StepFactory` "purchase-item" case.
16. `RecordStepModal`: seed editable item-id / quantity / currency fields from `after.PurchaseDetected` (mirror combat's `_editKillEnemyDataIds` seeding) and build the `PurchaseItemStep` in `BuildRawStep`.
17. Tests **GWT-PL1..PL6, PI1..PI5, PF1..PF2** (§13.5). ← gate: engine has no Dalamud reference; no engine replay-fixture re-record (done-criterion §10.7).

*E.3 — Offline mirror (CI-testable; `questforge-tools`).*
18. `SnapshotState` purchase fields + `Apply` cases for `ShopOpened`/`VendorItemCount`/`CurrencyBalance` + correlation (mirror of E.2). `ToSnapshot` projection; window-reset clears the purchase span.
19. `TraceToQuestExtractor` purchase branch (emit `PurchaseItemStep` when `inference.StepType == "purchase-item"`, mirroring the combat branch at `TraceToQuestExtractor.cs:206`); ensure the `wait`/terminal-skip guard does not drop a window whose inference is purchase.
20. Tests **GWT-PO1..PO5, PE1..PE3** (§13.5). ← gate.

*E.4 — Dalamud probe + in-game validation (requires game).*
21. `DalamudVendorProbe` (`IsShopOpen` over GilShop/GrandCompanyExchange addon presence; `GetGil`/`GetGrandCompanySeals`/regular `GetItemCount` reads — clean-room). Inject into `UIObserver` construction in `AuthoringHost`.
22. In-game: author a gil purchase and a GC-seal purchase; confirm the draft `purchase-item` carries the right item/quantity and a pre-filled `currency`; replay the recorded trace through `qf-trace extract-quest`; confirm an identical `PurchaseItemStep`.

Slice A before B/C/D/E. Slices B/C/D are as before. Slice E depends only on Slice A and may run after D. CI-visible tests: A, B, D, and E.1/E.2/E.3. In-game only: C (smoke) and E.4.

---

## 10. Done criteria

1. A quest file containing a `purchase-item` step deserializes to `PurchaseItemStep` and round-trips with `"type": "purchase-item"` and `"currency": "gil"|"gcSeals"` (Group E passes in CI).
2. `QuestForge.Engine` resolves a `PurchaseItemStep` to `Navigate` when far, `Purchase` (carrying the right `Currency`/`Quantity`) when near and affordable, `Wait` when the absolute count target is already met, and `AwaitUser` when the balance is zero or the adapter reports `InsufficientFunds`/`ItemNotSold`/`UnsupportedCurrency` (Groups A/B/C pass in CI).
3. `QuestForge.Engine` references no concrete Dalamud type; the new vendor capability is behind `IVendor`, with a `FakeVendor` in `QuestForge.Adapters.Fakes` and the engine wired against the interface (CI builds without a game).
4. Buying is idempotent/resume-safe: a player already holding `>= Quantity` triggers no purchase (Group B B1/B2 pass).
5. `qf-validate` rejects `purchase-item` steps with `ItemId==0`, `Quantity<1`, or `Target.NpcId==0`, and accepts a well-formed one (Group F passes).
6. In-game (manual): a gil `purchase-item` and a GC-seal `purchase-item` each complete — vendor opened, item bought to `Quantity`, `SelectYesno` auto-confirmed by the existing responder, step advances.
7. **(Slice E)** `StepInferenceEngine` returns `StepType "purchase-item"` (before the talk/sequence/flag fallbacks) when a shop-open is correlated with a regular-inventory increase and a currency decrease in the same window, with the vendor as `Target`, the item-delta item id as `ItemId`, the delta magnitude as `Quantity`, and `Currency` pre-filled from the dropped balance (GWT-PL*, GWT-PI* pass in CI).
8. **(Slice E)** `RecordStepModal` surfaces the detected purchase with editable item/quantity/currency fields, and `StepFactory` + `TraceToQuestExtractor` both build a `PurchaseItemStep` from the same event stream (GWT-PF*, GWT-PO*, GWT-PE* pass in CI).
9. **(Slice E, fixture-cascade gate)** No engine replay fixture is re-recorded: purchase authoring observations are emitted only by authoring-mode pollers (`UIObserver.PollVendor` gated on an authoring `IVendorProbe`); existing engine replay fixtures are unchanged and still green (mirrors COMBAT done-criterion #6).

---

## 11. What this slice does NOT include

- Tomestone / MGP / scrip / voucher currencies — closed `PurchaseCurrency` enum; such vendors are out of scope and (if ever authored) route to `AwaitUser`/validation failure rather than mis-buying. Detection of such a purchase yields a draft whose `Currency` is left for the author (no `gil`/`gcSeals` balance dropped; §13.4) — never a silent wrong-currency emit.
- Per-item price reads / precise affordability math (`GetShopItemPrice`) — v1 affordability is a coarse `>0` engine pre-check + adapter `InsufficientFunds` backstop.
- Selling, buy-back, item exchange for non-currency tokens.
- `DraftValidator` NpcId-zero lint for `PurchaseItemStep` (mirror the TalkStep rule later).
- Recovery-ladder integration for repeated purchase failure (deferred across all step types).
- GC rank/seal-cap gating, GC affiliation requirements (see §12 risks — degrades to `AwaitUser`).
- **Auto-splitting one recording window that contains multiple distinct purchases** into multiple `purchase-item` steps — Slice E emits one best-effort draft and notes the ambiguity for the author to split (§13.4), mirroring combat's single-step-plus-split-note behavior.

> **Removed from this list:** "`qf-trace` / `RecordStepModal` auto-detection of purchases (hand-authored only)" — this deferral is **withdrawn**. Auto-detection is now in scope (Slice E, §13).

---

## 12. Open risks / unknowns (and safe degradation)

1. **GC-seal rank gating.** Some GC quartermaster items require a minimum GC rank. v1 does not read rank. If the shop refuses the buy, the adapter reports `Failed`/`ItemNotSold` → engine `AwaitUser`. Safe: pauses for the user rather than looping. (Future: a `GetGrandCompanyRank` read + a `requiresGcRank` schema hint.)
2. **Shop not unlocked / NPC not a vendor.** If interacting opens dialogue instead of a shop, the adapter reports `ShopNotOpen`/`Failed`; bounded retries → `AwaitUser`. Safe.
3. **Quantity-spinner addon specifics.** The exact addon node/callback for the GilShop quantity spinner vs GrandCompanyExchange is a Dalamud-shell detail to confirm in-game during Slice C. Degrades safely: if the adapter cannot set quantity, it buys 1 per tick; the engine re-reads count and keeps ticking until `Quantity` is reached (the absolute postcondition makes single-buy-per-tick correct, just slower). This is why D6 (absolute target) is load-bearing — it makes a dumb "buy one, recheck" loop correct.
4. **Player has no Grand Company.** `GetGrandCompanySeals` returns `Result.Fail` → engine fail-opens to `Purchase` → adapter `ShopNotOpen`/`Failed` → `AwaitUser`. Safe, though a clearer pre-check (fail → `AwaitUser("no grand company")`) is a possible refinement; v1 keeps fail-open uniform with other reads.
5. **SelectYesno text/variant differences** between GilShop and GrandCompanyExchange confirms. The responder is text-agnostic (answers any active-run SelectYesno with Yes); risk is low but should be smoke-verified for both paths in Slice C.
6. **Coarse affordability false-negative.** A player with 1 gil "passes" the pre-check but cannot afford a 5000-gil item; the adapter's `InsufficientFunds` catches it → `AwaitUser`. Accepted; precise pricing is §11 future work.
7. **(Slice E) Fixture-cascade from new authoring observations.** Detection needs signals the trace does NOT currently carry (regular-inventory counts, gil/seal balances, shop-open). Adding them as engine per-tick reads would starve engine replay fixtures (the GetNewGamePlusState lesson). MITIGATED by emitting them only from authoring-mode pollers behind an `IVendorProbe`, identical to how combat added `PollCombat`/`ICombatProbe` without touching engine fixtures (§13.2). First-class risk; see §13.2 for the full analysis and verification gate (§10.9).
8. **(Slice E) Currency drop without inventory rise (or vice-versa).** Partial signals (repair fees, teleport gil, a sale) must NOT produce a phantom purchase. The correlation rule requires ALL THREE of (shop-open AND item-count increase AND currency decrease) in the same window before emitting (§13.1). Partial signals fall through to existing rules and emit no purchase step.

---

## 13. Authoring detection design (Slice E) — signals, correlation, integration, fixture-cascade, ambiguity

This section is the detailed design for the in-scope authoring auto-detection (D10 revised). It mirrors `COMBAT_AUTHORING_DETECTION_PLAN` structurally: a dumb observer emits raw signals; both consumers (live `SnapshotAggregator`, offline `SnapshotState`) correlate identically; an inference rule fires before the generic fallbacks; the modal seeds editable fields; the offline extractor and `CapabilityInferrer` mirror the live path.

### 13.1 Inference signal + correlation rule

**The purchase signal is a three-way correlation within one recording window:**
1. **Shop-open observation** — a vendor shop addon (`GilShop` or `GrandCompanyExchange`) was observed open during the window. This is the bracket that says "the player was at a vendor." The interacted vendor NPC is already captured as `LastNpcInteracted` (the every-frame `PollTargetNpc`).
2. **Regular-inventory count INCREASE** for some item id `I` — `count(I)` rose by `Δ ≥ 1` during the window. This gives `ItemId = I` and `Quantity = Δ`.
3. **Currency DECREASE** — either gil dropped (`Δgil > 0`) or GC seals dropped (`Δseals > 0`) during the window. The currency that dropped disambiguates `Currency`: gil-drop → `Gil`; seal-drop → `GcSeals`.

**Correlation rule (exact).** When `RecordStepModal` clicks Record (live) or the extractor closes a decision window (offline), if the accumulated window state has **(shop was open at any point in the window) AND (at least one item rose) AND (at least one of gil/seals fell)**, emit a `purchase-item` draft:
- `Target` = the vendor NPC (`LastNpcInteracted` + its captured position), via `StepFactory` as for other NPC-target steps.
- `ItemId` = the item with the largest positive `Δcount` (tie → lowest item id, deterministic; record the others in `Notes`).
- `Quantity` = that item's `Δcount`.
- `Currency` = `Gil` if `Δgil > 0` and `Δseals == 0`; `GcSeals` if `Δseals > 0` and `Δgil == 0`; if BOTH fell → pick the larger-magnitude drop and note the ambiguity (§13.4); if NEITHER fell → currency undetermined, leave default + note (§13.4).
- `SuggestedExpect` = synthesized `playerHasItem(<ItemId>, <Quantity>)` (matches the engine's default postcondition, D6).

**Window model — span bracketed by shop-open, mirroring the combat span.** Combat uses a `_inCombat` false→true transition to open a span, clears the span sets, accumulates kills/bumps while in-span, and retains the span after true→false so the post-fight Record still sees it (`SnapshotAggregator.OnInCombatChanged`). Purchase uses the SAME model with `_shopOpen` as the span gate:
- On shop-open `false→true`: snapshot the **baseline** gil + seal balances and clear the per-span item-delta accumulator. (Baseline-on-open mirrors combat's `_prevQuestVariables` baseline + span clear.)
- While `_shopOpen`: accumulate per-item count deltas vs the open-time baseline; track the running gil/seal balances.
- The currency drop = `baselineGil - currentGil` (and likewise seals), computed at projection time.
- The span is RETAINED after shop-close so a Record taken just after closing the shop still sees the purchase (exactly as combat retains the span post-fight). `ResetDeltas` (window reset) clears the purchase span; baselines for the NEXT detection are re-snapshot on the next shop-open.

**Why the shop-open bracket (not a bare currency+item correlation).** Gil and regular-inventory both change for many non-purchase reasons (repair, teleport, gathering, sale, loot). The shop-open bracket is the disambiguator that says "this delta happened at a vendor," and the per-span baseline confines the deltas to the time the shop was open — eliminating the cross-window false positives that a bare "gil went down and an item went up sometime this session" heuristic would produce. This is the purchase analogue of combat's `_inCombat` gate (which prevents attributing a variable bump that happens outside combat to a kill).

**Rule ordering in `StepInferenceEngine`.** Insert the purchase rule as **Rule 2.1.5 — immediately after combat (Rule 2.2) is NOT correct; place it BEFORE Rule 2.3 (key-item) and Rule 2.6 (inventory-hash diff) and the sequence/flag/talk fallbacks**, because a purchase also moves regular inventory and (if the item is a key item) could be mis-caught by the key-item / inventory-hash rules, and the NPC interaction would otherwise be caught by Rule 7 as a talk step. Concretely: place the purchase rule directly after Rule 2.2 (combat) and before Rule 2.3 (key item). Guard: fires only when the purchase span has shop-open AND an item rise AND a currency drop. If the guard fails, fall through to the existing rules unchanged (a vendor visit that bought nothing is not a purchase).

```csharp
// StepInferenceEngine — after Rule 2.2 (combat), before Rule 2.3 (key item)
if (after.PurchaseDetected is { } pd
    && pd.ShopWasOpen
    && pd.ItemDeltas.Count > 0
    && (pd.GilDropped > 0 || pd.SealsDropped > 0))
{
    var primary = pd.ItemDeltas
        .OrderByDescending(kv => kv.Value)   // largest Δcount
        .ThenBy(kv => kv.Key)                // tie → lowest item id
        .First();

    var currency = (pd.GilDropped > 0, pd.SealsDropped > 0) switch
    {
        (true, false) => "gil",
        (false, true) => "gcSeals",
        (true, true)  => pd.GilDropped >= pd.SealsDropped ? "gil" : "gcSeals", // §13.4 note
        _             => "gil"   // neither dropped → undetermined; §13.4 note (default + note)
    };

    string? notes = null;
    if (pd.ItemDeltas.Count > 1)
        notes = $"Multiple items increased while the shop was open [{string.Join(",", pd.ItemDeltas.Keys.OrderBy(x => x))}]. " +
                $"Drafted purchase of item {primary.Key} (qty {primary.Value}); split if the others were separate purchases.";
    if (pd.GilDropped > 0 && pd.SealsDropped > 0)
        notes = (notes is null ? "" : notes + " ") +
                $"Both gil ({pd.GilDropped}) and seals ({pd.SealsDropped}) dropped; pre-filled '{currency}' — confirm.";
    if (pd.GilDropped == 0 && pd.SealsDropped == 0)
        notes = (notes is null ? "" : notes + " ") +
                "No gil/seal drop detected; currency defaulted to gil — set the correct currency.";

    return new InferenceResult(
        StepType: "purchase-item",
        SuggestedStepId: $"buy-item-{primary.Key}",
        SuggestedExpect: $"playerHasItem({primary.Key},{primary.Value})",
        Confidence: pd.ItemDeltas.Count > 1 || (pd.GilDropped > 0 && pd.SealsDropped > 0)
            ? Confidence.Low : Confidence.Medium,
        InferredFrom: InferredFrom.Purchase,   // NEW enum member
        Notes: notes);
}
```
`Currency` is carried into the modal/factory separately (the inference result has no currency field; the detected `pd` is read directly by the modal and by `StepFactory`, exactly as combat reads `KillCorrelatedTargets` from the snapshot rather than from the `InferenceResult`).

### 13.2 Trace observations required — and the FIXTURE-CASCADE risk (FIRST-CLASS)

**Audit of what the authoring trace captures today (verified):**
- `UIObserver.PollKeyItems` captures **KEY items only** (`IGameProbe.GetKeyItemSlots()`), emitted as `InventoryChanged {gained,lost,newHash}`. **Purchasable items are REGULAR inventory** (e.g. bronze cesti), which is **NOT** polled or emitted in authoring today.
- **No gil balance** observation exists anywhere in `UIObserver` (grep: `UIObserver` never reads gil/seals).
- **No GC-seal balance** observation exists.
- **No shop-open** observation in authoring. (`UiState.ShopOpen` exists on the *engine's* `IGameStateProvider`, but that is the engine per-tick path, not the authoring observer.)

**Verdict: NEW recorded observations ARE required.** Detection cannot be derived purely from observations already in the authoring trace. Three new authoring observation methods are needed: a shop-open signal, regular-item counts, and gil/seal balances.

**The fixture-cascade danger (called out explicitly).** The project has been bitten before: adding a new engine *per-tick* read (e.g. `GetNewGamePlusState`) STARVES existing engine replay fixtures, because the replay harness expects the engine's read sequence to match the recorded fixture — a new read forces a re-record cascade across all combat/engine fixtures (see MEMORY: "Trace-emission refactor"). We must NOT trigger that here.

**Mitigation (the combat precedent, which provably did NOT cascade).** Combat detection added `PollCombat` + `ICombatProbe` as **authoring-mode-only heartbeat pollers** on `UIObserver`. These write to the *authoring* trace and forward to the *authoring* `SnapshotAggregator` — they are NOT engine `IGameStateProvider` reads, so the engine's per-tick replay path is untouched and no engine replay fixture changed (COMBAT done-criterion #6: "No engine fixture re-record"). Purchase detection MUST follow this exactly:

- Add a new authoring probe `IVendorProbe` in `QuestForge.Plugin.Tracing` (alongside `ICombatProbe`):
  ```csharp
  namespace QuestForge.Plugin.Tracing;
  public interface IVendorProbe
  {
      /// <summary>True when a vendor shop addon (GilShop or GrandCompanyExchange) is open.</summary>
      bool IsShopOpen();
      /// <summary>Current gil balance.</summary>
      long GetGil();
      /// <summary>Current Grand Company seal balance; 0 if the player has no GC.</summary>
      int GetGrandCompanySeals();
      /// <summary>
      /// Current counts for regular-inventory items that changed since the last poll.
      /// Returns only deltas (id → new count) to keep the trace small; empty when nothing changed.
      /// The probe owns the prior-counts cache so the observer stays dumb.
      /// </summary>
      IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts();
  }
  ```
- Add `PollVendor()` to the `UIObserver` **heartbeat** block (gated on `_vendorProbe is not null`), emitting three observation methods via the existing `WriteObservation`/direct-write paths and forwarding to the aggregator:

  | Method | Value | Emitted when |
  |---|---|---|
  | `ShopOpened` | `{ "value": <bool> }` | `IsShopOpen()` differs from last poll (direct write, like `InCombat`) |
  | `CurrencyBalance` | `{ "gil": <long>, "seals": <int> }` | balances differ from last poll |
  | `VendorItemCount` | `{ "itemId": <uint>, "count": <int> }` | one per changed regular item (direct write, like `EnemyKilled`) |

- These observations live in the authoring trace ONLY. The engine's `IGameStateProvider` gains `GetGrandCompanySeals` (D4) for the *runtime* affordability read, but the engine does NOT gain any new *per-tick authoring-style* read — the engine reads `GetItemCount`/`GetGil`/`GetGrandCompanySeals` only when a `PurchaseItemStep` is the active step (step-gated, D5), so non-purchase engine fixtures see no new reads.
- **Affected fixtures:** NONE in the engine replay corpus, by construction, provided the rule above is followed (the new observations are authoring-only; the new engine read is step-gated). The verification gate is done-criterion §10.9: existing engine replay fixtures must remain byte-identical and green after Slice E. If a Builder is tempted to add an ungated per-tick `GetGrandCompanySeals`/shop read to the engine loop, that WOULD cascade — the step-gating (D5) is the load-bearing guard and the Tester must assert non-purchase steps emit no currency read.

**Why not derive from existing signals.** We considered reusing `InventoryChanged` (already emitted) for the item delta. Rejected: it carries **key items only**, and purchasables are regular inventory — the signal simply is not present. The shop-open and currency signals have no existing source at all. So new observations are unavoidable; the mitigation is *where* they are emitted (authoring pollers), not *whether*.

### 13.3 Integration points (cross-repo)

| Layer | Component | Change | Precedent |
|---|---|---|---|
| `questforge` plugin | `QuestForge.Plugin.Tracing.IVendorProbe` (NEW) + `UIObserver.PollVendor` | emit `ShopOpened`/`CurrencyBalance`/`VendorItemCount`; forward to aggregator | `ICombatProbe` + `PollCombat` |
| `questforge` engine | `GameStateSnapshot` + `PurchaseDetection` record (NEW fields) | carry the per-window purchase span to inference | `KillCorrelatedTargets` + `KillCorrelation` |
| `questforge` engine | `SnapshotAggregator` (`OnShopOpened`/`OnVendorItemCountChanged`/`OnCurrencyChanged`; baseline + span; `ResetDeltas`) | live correlation | combat span correlation |
| `questforge` engine | `StepInferenceEngine` purchase rule (§13.1) + `InferredFrom.Purchase` | live inference | Rule 2.2 combat |
| `questforge` engine | `StepFactory` `"purchase-item"` case | build `PurchaseItemStep` from `after.PurchaseDetected` (Target from NPC, ItemId/Quantity from delta, Currency from drop) | `"combat"` case |
| `questforge` plugin UI | `RecordStepModal` | when `StepType=="purchase-item"`, show editable item-id / quantity / currency fields seeded from `after.PurchaseDetected`; build the step in `BuildRawStep` | combat `_editKillEnemyDataIds` seeding + `BuildRawStep` combat arm |
| `questforge` adapters (Dalamud) | `DalamudVendorProbe` (NEW, untested in CI) | implement `IVendorProbe` over GilShop/GrandCompanyExchange addon presence + gil/seal/item reads | `DalamudCombatProbe` |
| **`questforge-tools` (OFFLINE MIRROR)** | `SnapshotState` apply-cases for `ShopOpened`/`CurrencyBalance`/`VendorItemCount` + correlation + `ToSnapshot` projection + window reset | offline correlation identical to live | combat `InCombat`/`EnemyKilled`/`GetQuestVariables` apply-cases |
| **`questforge-tools`** | `TraceToQuestExtractor` purchase branch (`if inference.StepType == "purchase-item"` → `StepFactory.Build("purchase-item", …)`), guarding the `wait`/terminal skip | offline emit | combat branch (`TraceToQuestExtractor.cs:206`) |
| **`questforge-tools`** | `CapabilityInferrer` `[typeof(PurchaseItemStep)] = "step:purchase-item"` | capability tag | existing step entries (D9) |

**Cross-repo note:** the offline mirror is the same code shape duplicated in `questforge-tools` (the project deliberately duplicates correlation in both consumers so live and offline replay the SAME raw observations — `COMBAT_AUTHORING_DETECTION_PLAN D1`). The single shared constant is none here (purchase has no timing window — the shop-open span is the bracket), which makes the mirror simpler than combat's ±500ms window.

### 13.4 Confidence / ambiguity / partial-signal handling

Combat seeding had bugs around empty correlated sets and multi-target windows; purchase detection front-loads the same care. **Never silently emit a wrong purchase** — emit a best-effort draft with whatever it could determine and leave the rest to the author via the modal (D1: `Currency` is author-authoritative; detection only pre-fills).

| Partial / ambiguous signal | Behavior |
|---|---|
| Shop opened, NO item-count increase | NOT a purchase (bought nothing / just browsed). Guard fails → fall through to existing rules; no purchase step. (GWT-PI3) |
| Item increased + currency dropped, but NO shop-open in window | NOT attributed as a purchase (could be a quest reward + a teleport fee). Guard requires shop-open → fall through. (GWT-PI4) |
| Currency dropped, shop open, but NO item increase | NOT a purchase (e.g. repair at a vendor, or a same-window sale-then-rebuy nets zero). Guard fails → fall through. |
| MULTIPLE items increased while shop open | Best-effort: draft the largest-Δ item (tie → lowest id), `Confidence.Low`, `Notes` lists all changed ids and asks the author to split. Mirrors combat's multi-target single-step-plus-note. (GWT-PL4, GWT-PI… via notes) |
| BOTH gil and seals dropped | Pre-fill the larger-magnitude currency, `Confidence.Low`, `Notes` flags the ambiguity. Author confirms/corrects in the modal. (GWT-PL5) |
| Item increased + shop open, but NEITHER gil nor seals dropped (e.g. tomestone/MGP/scrip vendor) | Guard FAILS (requires a gil/seal drop) → no purchase step is auto-emitted; an unsupported-currency vendor is left to the author (consistent with §11 closed-enum stance). If the author manually picks `purchase-item` in the modal's type override, the fields are still pre-fillable from item delta + NPC, currency defaults to gil with a note. |
| Empty `PurchaseDetected` (no shop seen at all) | `after.PurchaseDetected is null` → rule does not fire (the combat empty-set bug class: the guard checks `is { }` AND non-empty `ItemDeltas`). (GWT-PI3 negative) |

`Confidence` is `Medium` for a clean single-item single-currency purchase, `Low` when multi-item or dual-currency. The author always reviews the seeded fields before Confirm.

### 13.5 Given-When-Then specifications (Slice E)

Mirror `COMBAT_AUTHORING_DETECTION_PLAN §Task 4` test placement: live aggregator + inference + factory + observer tests in `QuestForge.Engine.Tests` (+ a plugin test project for the modal/observer); offline tests in `QuestForge.Tools.Trace.Tests`. Purchase test constants: `BuyQuest = 70001`, `Vendor = 1001234`, `Item = 1601`, `SealItem = 6141`.

**Live observer — `QuestForge.Plugin.Tracing` (FakeVendorProbe, FakeClock) — `QuestForge.*.Tests`**

- **GWT-PU1 shop-open transition emits `ShopOpened` once.** Given `IsShopOpen()` false→true→true→false across heartbeats. Then exactly two `ShopOpened` observations (`true`, then `false`) are written, not four; `OnShopOpened` forwarded on each transition.
- **GWT-PU2 changed item count emits `VendorItemCount`.** Given `GetChangedItemCounts()` returns `[(1601, 1)]` one poll, `[]` the next. Then one `VendorItemCount {itemId:1601,count:1}` is written and `OnVendorItemCountChanged(1601,1)` forwarded.
- **GWT-PU3 currency balance change emits `CurrencyBalance`.** Given gil 10000→9000 between polls. Then one `CurrencyBalance {gil:9000,seals:...}` is written and `OnCurrencyChanged(9000, seals)` forwarded.
- **GWT-PU4 no vendor probe → no purchase emission (back-compat).** Given `UIObserver` constructed without `IVendorProbe`. Then no `ShopOpened`/`CurrencyBalance`/`VendorItemCount` is ever written; existing pollers unaffected.
- **GWT-PU5 ResetHeartbeatState clears vendor tracking.** Given prior shop-open + balances cached; When `ResetHeartbeatState()`; Then the next poll re-emits the shop-open transition and re-baselines balances (no phantom drop from the stale baseline).

**Live aggregator correlation — `QuestForge.Engine.Tests`**

- **GWT-PL1 clean gil purchase → PurchaseDetected.** Given aggregator for `BuyQuest`; `OnShopOpened(true)` with baseline gil=10000, seals=0; `OnVendorItemCountChanged(1601, +1)`; `OnCurrencyChanged(gil=9000, seals=0)`. Then `Current.PurchaseDetected` has `ShopWasOpen==true`, `ItemDeltas[1601]==1`, `GilDropped==1000`, `SealsDropped==0`.
- **GWT-PL2 GC-seal purchase → seals dropped.** Given `OnShopOpened(true)` baseline gil=5000, seals=2000; `OnVendorItemCountChanged(6141, +3)`; `OnCurrencyChanged(gil=5000, seals=1400)`. Then `ItemDeltas[6141]==3`, `GilDropped==0`, `SealsDropped==600`.
- **GWT-PL3 span retained after shop close.** Given GWT-PL1 then `OnShopOpened(false)`. Then `Current.PurchaseDetected` still reports the same deltas (post-close Record still sees the purchase).
- **GWT-PL4 multiple items increased.** Given shop open, `OnVendorItemCountChanged(1601,+1)` and `OnVendorItemCountChanged(1602,+2)`, gil dropped. Then `ItemDeltas` has both keys (inference picks 1602 as primary by larger Δ; asserted in PI4-note).
- **GWT-PL5 both currencies dropped.** Given shop open, item rose, gil 10000→9500 and seals 2000→1900. Then `GilDropped==500`, `SealsDropped==100` (both > 0; inference flags ambiguity).
- **GWT-PL6 ResetDeltas clears the purchase span.** Given a detected purchase; When `ResetDeltas()`; Then `Current.PurchaseDetected` is null/empty and a subsequent `OnShopOpened(true)` re-baselines balances cleanly.

**Live inference — `StepInferenceEngine` — `QuestForge.Engine.Tests`**

- **GWT-PI1 clean purchase → purchase-item before fallbacks.** Given `after.PurchaseDetected` = (shopWasOpen, {1601:1}, gilDropped 1000, sealsDropped 0), `LastNpcInteracted=Vendor`, and seq advanced (which alone would trigger Rule 3 talk). Then `Infer` returns `StepType=="purchase-item"`, `SuggestedExpect=="playerHasItem(1601,1)"`, `SuggestedStepId=="buy-item-1601"`, `InferredFrom==Purchase`, `Confidence==Medium` (NOT a talk step).
- **GWT-PI2 seal purchase pre-fills gcSeals (read by factory/modal).** Given seals dropped, gil not. Then the rule fires; `StepFactory.Build("purchase-item", …, after)` produces a `PurchaseItemStep` with `Currency==GcSeals`.
- **GWT-PI3 shop open but no item rise → no purchase (fall through).** Given `PurchaseDetected` with `ShopWasOpen==true`, empty `ItemDeltas`, gil dropped. Then the purchase rule does NOT fire; inference falls through to the existing rules (talk/sequence) — no purchase step.
- **GWT-PI4 item rise + currency drop but no shop-open → no purchase.** Given `ShopWasOpen==false`, `ItemDeltas[1601]=1`, gil dropped. Then purchase rule does NOT fire (guard requires shop-open).
- **GWT-PI5 multi-item → primary by largest Δ, split note.** Given `ItemDeltas {1601:1, 1602:2}`, gil dropped. Then `SuggestedStepId=="buy-item-1602"`, `SuggestedExpect=="playerHasItem(1602,2)"`, `Confidence==Low`, `Notes` mentions 1601 and "split".

**Live factory — `StepFactory` — `QuestForge.Engine.Tests`**

- **GWT-PF1 builds PurchaseItemStep with vendor target + item/qty/currency.** Given `after.PurchaseDetected` ({1601:1}, gil dropped), `LastNpcInteracted=Vendor`, `LastNpcPosition=(10,0,20)`, zone 128. Then `StepFactory.Build("purchase-item", "buy-item-1601", "playerHasItem(1601,1)", after)` yields a `PurchaseItemStep` with `Target.NpcId==1001234`, `Target.Zone==128`, `Target.Position==(10,0,20)`, `ItemId==1601`, `Quantity==1`, `Currency==Gil`, `Expect` a `PredicateExpect`.
- **GWT-PF2 seal purchase → Currency GcSeals.** Given seals dropped, gil not. Then the built step has `Currency==GcSeals`.

**Offline mirror — `SnapshotState` — `QuestForge.Tools.Trace.Tests`**

- **GWT-PO1 ShopOpened + VendorItemCount + CurrencyBalance correlate.** Given `SnapshotState(70001)`; Apply `ShopOpened {value:true}` (baseline gil=10000); `VendorItemCount {itemId:1601,count:1}` (from a 0 baseline); `CurrencyBalance {gil:9000,seals:0}`. Then `ToSnapshot(t).PurchaseDetected` has `ItemDeltas[1601]==1`, `GilDropped==1000`. Each `Apply` returns true.
- **GWT-PO2 unrecognised-method count not inflated.** Given the three new methods applied. Then each returns `true` (recognised), mirroring combat's recognised-no-op discipline.
- **GWT-PO3 no shop-open → no PurchaseDetected.** Given `VendorItemCount` + `CurrencyBalance` but no `ShopOpened {true}`. Then `PurchaseDetected` does not report a purchase (guard at projection).
- **GWT-PO4 GC-seal offline purchase.** Mirror of PO1 with a seal drop → `SealsDropped>0`, `GilDropped==0`.
- **GWT-PO5 window reset clears the purchase span, re-baselines.** Given a detected purchase; When `ResetPendingKeyItemDeltas()` (the window reset); Then `PurchaseDetected` is cleared and a subsequent `ShopOpened {true}` re-baselines.

**Offline extractor — `TraceToQuestExtractor` — `QuestForge.Tools.Trace.Tests`**

- **GWT-PE1 end-to-end purchase extraction.** Given a synthetic observation-only trace: `run.start` quest 70001; `GetTarget` vendor / `GetPlayerPosition`; `ShopOpened true`, `VendorItemCount {1601,1}`, `CurrencyBalance {gil:9000}`, `ShopOpened false`; a `wait` decision; `run.end`. Then `Extract` yields a sequence whose step is a `PurchaseItemStep` with `Target.NpcId==1001234`, `ItemId==1601`, `Quantity==1`, `Currency==Gil`, `Expect` predicate `playerHasItem(1601,1)`, plus a TODO mentioning purchase review.
- **GWT-PE2 purchase window with a `wait` decision is not skipped.** Given the window's only decision is `wait`. Then the extractor still emits the `PurchaseItemStep` (the wait/terminal-skip guard must not drop a window whose inference is purchase — mirrors the combat guard).
- **GWT-PE3 vendor visit with no purchase → no PurchaseItemStep.** Given `ShopOpened true/false` but no item rise. Then no `PurchaseItemStep` is emitted; existing rules drive the window.

---

## 14. GC navigation (tab × tier matrix) — Slice G

> **Status:** ready for test creation (awaiting user go-ahead before any implementation). **Position:** follow-up slice to Slices A–F, depends only on Slice A (schema). **Why this section exists:** Slice C shipped a direct-buy path that works when the target item happens to be on the (category, rank-tier) the player last left the addon on. The `GrandCompanyExchange` addon is in reality a **category × rank-tier matrix** — its BuyList contents change with two independent radio-button axes — so a correctly-deployed quest must declare which axes to be on before `ResolveExchangeRow` can find the row. This section specifies that work end to end, in the style of §§5/8/9/13.

### 14.1 Motivation and captured facts (live-verified; corrected in-game 2026-05-28)

The `GrandCompanyExchange` addon hosts **two simultaneous radio-button axes** inside the same window:

1. **Rank tier** — radio group at node ids **37..39** (three positions, indexed bottom-to-top: `0=lowest, 1=middle, 2=highest`). Higher GC ranks may unlock further nodes (40..) but the player-visible set in our test was three.
2. **Category** — radio group at node ids **44..47** (four positions, indexed positionally: `0=Weapons, 1=Armor, 2=Materiel, 3=Materials`). AutoRetainer's `InRange(0, 4, false)` suggests a fifth slot (node 48) may exist at high GC ranks; treated as out-of-range by our validator until verified.

> **Earlier draft was wrong.** Slice G's initial design (drafted before in-game verification) documented `FireCallback(2, [Int 2, Int category])` with a captured "internal taxonomy" of `1=Materiel, 2=Weapons, 3=Armor, 4=Materials`. **Synthetic FireCallback on the addon does NOT trigger the radio buttons' state-change handlers** — the addon receives the event but no tab change occurs. The actual working technique replays each radio button's pre-registered `AtkEvent` via `AtkUnitBase.ReceiveEvent`. The "internal taxonomy" I captured was the `[1]` payload from FireCallback events *on a different code path* that does not in fact switch tabs; the correct indexing is the positional `44+i` / `37+i` node-id offset above.

**Switching technique (live-verified, adapted from AutoRetainer BSD-3 — see attribution in `DalamudVendor.cs`):**

```csharp
var node = addon->GetNodeById(nodeId);          // 44+gcCategory, or 37+gcRankTier
var evt  = node->AtkEventManager.Event;         // the radio button's pre-registered AtkEvent
addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, node->AtkEventManager.Event);
```

**Buy callback (unchanged, already shipped §8 / #90):** `FireCallback(6, [Int 0, Int row, Int qty, Int 0, Bool 1, Bool 0])`. The buy uses real FireCallback; only tab switching needs ReceiveEvent.

**AtkValues layout (unchanged, already shipped §8 / #90):** `count@[1]`, `name@[17+i]` (string), `price@[67+i]` (UInt), `icon@[167+i]` (UInt), `itemId@[317+i]` (UInt). **AtkValues contents change per (category, rank-tier).** `ResolveExchangeRow` therefore only finds the target item id when **both** axes are correctly set. The active axes cannot be read from AtkValues at `[11..16]` (they don't vary across category dumps); the runtime adapter does not need to read them (always-fire is cheap) but the authoring probe (G5/G6) does — via the radio components' `Selected` flag.

**Switch convergence (in-game measured):** the addon takes **2–3 ReceiveEvent dispatches** before it visibly commits a tab change. The first dispatch moves the addon into an intermediate state (different AtkValues count, neither origin nor target tab); the second or third lands on the target. The adapter handles this with a per-call counter (`MaxSwitchAttempts=5`) and a 500ms throttle — see D14.3.

This is a strictly additive feature: when `GcCategory`/`GcRankTier` are absent the behavior is exactly Slice C's (try resolve, `ItemNotSold` if not found, no switches). No existing quest breaks; the cost is paid only by quests that opt in.

### 14.2 Architectural decisions (read before coding)

#### D14.1 — Schema: dedicated optional fields `GcCategory: int?` and `GcRankTier: int?` (NOT extend `OpenPath`)

```csharp
// QuestForge.Schema/Step.cs — additive to the existing PurchaseItemStep
public class PurchaseItemStep : Step
{
    public NpcLocation Target { get; init; } = default!;
    public uint ItemId { get; init; }
    public int Quantity { get; init; } = 1;
    public PurchaseCurrency Currency { get; init; } = PurchaseCurrency.Gil;

    /// <summary>
    /// GC quartermaster category radio (0..3 positional: 0=Weapons, 1=Armor, 2=Materiel, 3=Materials).
    /// Only meaningful when Currency=GcSeals; validator warns if set with Currency=Gil.
    /// Null = leave whatever category the addon happens to be on (Slice C behavior).
    /// </summary>
    public int? GcCategory { get; init; }

    /// <summary>
    /// GC quartermaster rank-tier radio (0..2 bottom-to-top: 0=lowest visible tier, 2=highest).
    /// Only meaningful when Currency=GcSeals. Null = leave whatever rank-tier the addon happens to be on.
    /// </summary>
    public int? GcRankTier { get; init; }
}
```

**Rejected: extending `OpenPath: int[]` (reserved in §8.1) to cover GC navigation.** Different semantics:
- `OpenPath` (§8.1) is a **sequential menu drill-down** through `SelectIconString` / `SelectString` *before* the `Shop` addon opens — the addons literally come and go in sequence and each menu instance is consumed by a single index selection.
- GC navigation is **two simultaneous filters** inside the *same* already-open addon. Encoding it as a list `[category, tier]` would (a) conflate two unrelated mechanisms behind one ambiguous field, (b) force `DalamudVendor` to disambiguate the field's meaning by `Currency` (a smell), and (c) prevent future per-axis tooling (e.g. validator messages naming the offending axis, or partial overrides where only the rank-tier matters).

Two dedicated optional fields keep the schema self-documenting: a reader sees `gcCategory: 2, gcRankTier: 1` and immediately understands "the Weapons tab, rank tier 1." `OpenPath` remains reserved for its original gated-vendor purpose.

**What breaks if violated:** overloading `OpenPath` couples §8.1's gated-vendor work to GC navigation (two unrelated features land in one field), the validator cannot give axis-specific error messages, and the authoring detection slice (G5) cannot pre-fill per-axis without a special-case decoder for "which slot means category for GC vs. menu index for gated vendors."

#### D14.2 — Carrier: extend `EngineAction.Purchase` with two optional fields (NOT downcast `Origin`)

```csharp
// QuestForge.Engine/EngineAction.cs
public sealed record Purchase(
    NpcId Vendor,
    ItemId Item,
    int Quantity,
    PurchaseCurrency Currency,
    int? GcCategory = null,
    int? GcRankTier = null,
    Step? Origin = null) : EngineAction;
```

**Why explicit fields, not `Origin`-downcast in `DalamudVendor`.** `Quantity` and `Currency` already live on the action record despite being also derivable from `Origin.Step`; the same rationale applies here:
- **Avoids downcasting.** `DalamudVendor.Purchase(... Step? Origin)` would have to `Origin as PurchaseItemStep` and pattern-match — a smell, and one that silently treats a future caller that *omits* `Origin` (e.g. `/qf debug buy`) as "no GC navigation requested." Explicit nullable fields make the contract testable in isolation.
- **Mirrors existing precedent.** Every adapter-relevant scalar already on `EngineAction.Purchase` (`Vendor`, `Item`, `Quantity`, `Currency`) is duplicated from the step rather than read through `Origin`. The two new fields follow the established pattern.
- **Engine purity unchanged.** The engine simply copies `step.GcCategory`/`step.GcRankTier` into the action; no new branches in `ResolveAction`.

**Testability implication:** `Group A` tests assert four-tuple equality on `EngineAction.Purchase`; the two new fields are asserted alongside `Quantity`/`Currency` with zero ceremony.

**Constructor ordering note:** placed **before** `Origin` (the trailing `Step?`) to keep `Origin` last by convention. This is a minor source-compat change to existing callers that pass `Origin: step` by name (Slice B already uses named args — confirmed in `QuestEngine.cs` purchase branch); any positional callers must be updated in the same commit.

#### D14.3 — Adapter state machine: counter-based ReceiveEvent retries (revised in-game 2026-05-28)

Per-tick `DalamudVendor.PurchaseGcSeals` flow (additive to the Slice C body; only the *additions* are described — the AtkValues read, row resolve, and buy callback are unchanged):

```
1. addon not ready                                  → reset switchAttempts, return ShopOpening
2. signature changed since last call                → reset switchAttempts
3. throttle check (BuyThrottle 1s)                  → return ShopOpening
4. read AtkValuesCount / count gate (existing)      → return ShopOpening if invalid
5. ResolveExchangeRow(itemId)
   ├── row >= 0 (item is on the CURRENT tab) → reset switchAttempts, fire buy → return Purchased
   └── row <  0 (item NOT on current tab):
       ├── if BOTH GcCategory and GcRankTier are null → reset switchAttempts, return ItemNotSold (Slice C; back-compat)
       ├── if switchAttempts >= MaxSwitchAttempts    → reset switchAttempts, log warning, return ItemNotSold (give up)
       ├── if SwitchThrottle (500ms) not yet elapsed → return ShopOpening (no dispatch this tick)
       └── else:
           a. if GcRankTier is set: DispatchRadioButtonClick(addon, 37 + gcRankTier.Value, "rank")
           b. if GcCategory is set: DispatchRadioButtonClick(addon, 44 + gcCategory.Value, "cat")
           c. switchAttempts++; lastSwitchAt = now; return ShopOpening
```

`DispatchRadioButtonClick(addon, nodeId, axis)` resolves the radio button by `GetNodeById(nodeId)`, casts via `GetAsAtkComponentRadioButton()` to verify type, reads `AtkEventManager.Event`, and replays it via `addon->ReceiveEvent(evt->State.EventType, evt->Param, evt)`. Each of the three failure modes (missing node, wrong type, missing event) logs a `Warning` and returns without dispatching — making node-id regressions after a game patch visible without crashing.

**Decision: counter-based retries with a hard cap, NOT a binary latch.** The first draft of this slice assumed *one* switch dispatch would commit the tab change; the in-game test (PR #91 smoke 2026-05-28) found the addon needs **2–3 dispatches** before the visible tab actually changes (first dispatch yields an intermediate state where neither the original nor target tab is showing). A binary latch returns `ItemNotSold` after one wasted dispatch — terminating the engine step before the addon has settled. The counter approach re-fires the dispatch every `SwitchThrottle` interval (500ms) until either the row resolves or we hit `MaxSwitchAttempts = 5` (~2.5s total). Beyond that, we genuinely cannot find the item on the requested (cat, tier) and the `ItemNotSold` return is correct.

**Decision: ALWAYS fire the switch when fields are set, do NOT pre-read the radio button's `Selected` flag.** Rationale (carried over from the earlier draft, still valid):
- **Simplicity wins over one wasted dispatch.** A dispatch to the already-active tab is a no-op in the game. The throttle plus the post-buy reset of the counter prevent spam.
- **The Selected-flag read is not free.** Walking the radio button's component data to find `Selected` adds unverified ClientStructs traversal. The authoring probe (G5) does need it for pre-fill, but the runtime adapter has the row-resolve as its real success oracle, so a wasted dispatch is benign.

**The per-call counter** (`int _switchAttempts` on `DalamudVendor`) is **reset** on three conditions: (1) the addon is not ready (a fresh shop visit re-enters the switch path), (2) the `(itemId, qty, gcCategory, gcRankTier)` signature differs from the last call (a new purchase request), and (3) a successful resolve+buy happens. The counter is also reset when the give-up path fires, so subsequent purchases start fresh. Without this counter the adapter would either spam dispatches every tick (no throttle/cap → audio click spam) or give up after one attempt (current shipped bug pre-2026-05-28).

**Back-compat invariant (load-bearing):** when **both** `GcCategory` and `GcRankTier` are null, the entire switch branch (cap check, throttle, dispatch, counter increment) is skipped; the behavior is byte-identical to Slice C. The Tester MUST assert this with an existing-quest fixture (GWT-GD3) before any new switching code lands.

**What breaks if violated:** firing switches without the throttle produces audio-click spam (each radio dispatch is sound-event-emitting); using a binary latch terminates the step before the addon commits the tab change (verified 2026-05-28).

#### D14.4 — Authoring detection: extend `IVendorProbe` with `GetActiveGcCategory` / `GetActiveGcRankTier`

```csharp
// QuestForge.Plugin.Tracing/IVendorProbe.cs — additive
public interface IVendorProbe
{
    bool IsShopOpen();
    long GetGil();
    int GetGrandCompanySeals();
    IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts();

    /// <summary>
    /// Currently-selected GC category radio (0..3 positional: 0=Weapons, 1=Armor, 2=Materiel, 3=Materials),
    /// or null if the GrandCompanyExchange addon is not open OR the Selected flag could not be read.
    /// Authoring-only; never called by the engine.
    /// Node-id offsets are 44+i — read the radio component's Selected flag to identify the active one.
    /// </summary>
    int? GetActiveGcCategory();

    /// <summary>
    /// Currently-selected GC rank-tier radio (0..2 bottom-to-top: 0=lowest visible, 2=highest visible),
    /// or null on the same fail-quiet conditions. Node-id offsets are 37+i.
    /// </summary>
    int? GetActiveGcRankTier();
}
```

**Recommendation (and pinned choice): option (a) — `IVendorProbe` reads the radio-button `Selected` flag.** Rationale (vs. b: hook FireCallback to track most-recent switches; vs. c: leave the modal to require manual entry):
- **(b) — hooking FireCallback** is heavy (mirrors `/qf debug hookshop`), carries a hook lifecycle the probe must manage across shop sessions, and is fragile if a future patch reorders FireCallback indices. It also captures only switches the *player* fires; if the player navigates by clicking a different radio button (not a FireCallback path), the cache lies. Rejected.
- **(c) — manual modal entry** leaves the v1 modal showing two blank numeric inputs and a "VERIFY IN-GAME: pick the right tab" hint. Acceptable as a *fallback* if (a) is blocked, but it negates the §13 promise that "performing the action during recording pre-fills the draft."
- **(a) — read `Selected` from the radio component** is the cleanest "observe what is actually there" path. It costs one component-tree walk per `PollVendor` heartbeat **only while the `GrandCompanyExchange` addon is open** (a rare condition during a session), and degrades quietly to `null` on any traversal failure (no crashes, the modal falls back to blank fields with a hint — the same as (c)).

**Implementation tag — `// VERIFY IN-GAME:` on the radio-component path.** The exact `AtkComponentRadioButton` node ids for the category and rank-tier radio groups must be confirmed by Slice G6 (the only in-game-required slice). If the read is non-trivial — e.g. the radio buttons are inside a `AtkComponentList` whose layout drifts across patches — fall back to (c) for v1 and ship the modal hint; the slice plan accommodates this with the explicit G6 gate.

**No new authoring observations are emitted.** `GetActiveGcCategory`/`GetActiveGcRankTier` are read directly by `SnapshotAggregator` (live) and surfaced into `PurchaseDetection` as two new optional fields (D14.5). No new observation method joins the trace — this avoids re-cascading any fixture (mirrors §13.2's fixture-cascade discipline: the engine still emits zero new reads, and authoring trace events are unchanged; only the *forwarding* of the existing `ShopOpened` poll now also carries the active-axis snapshot).

**Cost of (a) if blocked at G6:** the modal opens with the new numeric inputs blank, plus a one-line hint ("Active GC category/rank-tier not detected; set manually if this is a GC quartermaster step"). The serialized step is still well-formed (author types 2 and 1), the engine path works, the validator passes — the only loss is the pre-fill convenience.

#### D14.5 — `PurchaseDetection` extension and snapshot projection

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs — additive
public sealed record PurchaseDetection(
    bool ShopWasOpen,
    IReadOnlyDictionary<uint, int> ItemDeltas,
    long GilDropped,
    int SealsDropped,
    int? ActiveGcCategory = null,    // NEW — last observed Selected of the category radio
    int? ActiveGcRankTier = null);   // NEW — last observed Selected of the rank-tier radio
```

`SnapshotAggregator` records the most recent non-null `(category, tier)` it sees while the shop-open span is active (i.e. observed during a `PollVendor` heartbeat between `OnShopOpened(true)` and the span retention period after `OnShopOpened(false)`). The fields are deliberately "last seen" rather than "at-purchase-instant" because the player may click around tabs before buying; the *final* tab they were on when the item count rose is the one we want, and "last value before window close" approximates this without an instant-correlation mechanism. (A wrong pre-fill is a one-click modal correction, never a silent mis-buy — D14.4 inherits this from D1.)

#### D14.6 — Validator rules (cross-repo: `questforge-tools`)

New rules added to `StructuralValidator.CheckStepTypeRules`'s `PurchaseItemStep` case:

| Error code | Suppression / condition | Severity |
|---|---|---|
| `structural/purchase-gc-category-out-of-range` | suppressed when `GcCategory is null OR 0..3` | Error |
| `structural/purchase-gc-rank-tier-out-of-range` | suppressed when `GcRankTier is null OR 0..2` | Error |
| `structural/purchase-gc-fields-on-gil` | suppressed when `Currency == GcSeals` OR both `GcCategory` and `GcRankTier` are null | **Warning** (message: "`gcCategory`/`gcRankTier` are ignored when `currency` is not `gcSeals`") |

The first two are hard errors (a `7` is illegal regardless of currency). The third is a **warning** because the engine simply ignores the fields when the currency is gil (no runtime damage), but the author almost certainly made a mistake — the warning surfaces that. The existing four error codes from §4 (`structural/purchase-item-id-zero`, `…-quantity-nonpositive`, `…-npc-id-zero`, `…-currency-invalid`) are unchanged.

**Conservative ranges:** the adapter dispatches whatever node-id offset the caller requests (`44+i`, `37+i`); out-of-range values fail safely (warning + no dispatch). The validator picks the narrower visible-on-tested-player ranges to surface obvious typos. A high-GC-rank character may expose more tabs (AutoRetainer's `InRange(0, 4, false)` permits a 5th category at node 48); when verified in-game the validator can widen the range without an adapter change.

#### D14.7 — `/qf debug buy` extension

Extend the existing command (`QfCommand.HandleDebugBuy`, currently `usage: /qf debug buy <itemId> [qty] [gil|gcSeals]`) with two optional trailing args:

```
/qf debug buy <itemId> [qty] [gil|gcSeals] [gcCategory] [gcRankTier]
```

Behavior:
- When `currency != gcSeals`, parsing `gcCategory`/`gcRankTier` succeeds but the values are passed as null into the `EngineAction.Purchase`-equivalent invocation; an info chat line warns "(gcCategory/gcRankTier ignored when currency=gil)" to match the validator's warning text.
- When parsing fails for either trailing arg (e.g. non-integer), print the usage line and return — same defensive pattern as the existing `itemId`/`qty` parses.
- The smoke-test workflow becomes: *"`/qf debug buy 6141 1 gcSeals 1 1`"* drives end-to-end navigation + buy in a single click, which is the entire point of this command.

The dispatch into `_host.DebugVendor.Purchase` is **already shaped** for two new fields once D14.2 lands (`DalamudVendor` only changes inside `PurchaseGcSeals`'s body — its `IVendor.Purchase` signature gets the two new nullable parameters too; back-compat is preserved by defaulting to null).

### 14.3 Schema contract — additive

```csharp
public class PurchaseItemStep : Step
{
    public NpcLocation Target { get; init; } = default!;
    public uint ItemId { get; init; }
    public int Quantity { get; init; } = 1;
    public PurchaseCurrency Currency { get; init; } = PurchaseCurrency.Gil;
    public int? GcCategory { get; init; }     // NEW — 0..3 (0=Weapons, 1=Armor, 2=Materiel, 3=Materials); null = leave addon as-is
    public int? GcRankTier { get; init; }     // NEW — 0..2 (0=lowest, 1=middle, 2=highest visible tier); null = leave addon as-is
}
```

Expected authoring usage:

```jsonc
// GC quartermaster — buy 3 of a Materiel-tab lowest-rank item; engine flips to that tab first
{
  "type": "purchase-item",
  "id": "buy-gc-materiel",
  "target": { "npcId": 1002000, "zone": 128, "position": { "x": 5.0, "y": 0, "z": 5.0 } },
  "itemId": 4564,
  "quantity": 3,
  "currency": "gcSeals",
  "gcCategory": 2,
  "gcRankTier": 0
}
```

Back-compat: existing GC quests without these fields keep Slice C's behavior — `ResolveExchangeRow` is tried against whatever tab the addon happens to be on; `ItemNotSold` if it's not there.

### 14.4 Validation rule table — additive

| Error code | Suppression / condition | Severity |
|---|---|---|
| `structural/purchase-gc-category-out-of-range` | suppressed when `GcCategory is null OR 0..3` | Error |
| `structural/purchase-gc-rank-tier-out-of-range` | suppressed when `GcRankTier is null OR 0..2` | Error |
| `structural/purchase-gc-fields-on-gil` | suppressed when `Currency == GcSeals` OR both `GcCategory` and `GcRankTier` are null | Warning |

(Existing §4 rules are unchanged and continue to apply.)

The narrower ranges (3 → 0..3, 5 → 0..2) reflect the visible-on-current-player addon state observed during the in-game test (2026-05-28). AutoRetainer's source allows `0..4` for category (suggesting a hidden 5th slot at high GC ranks) — out of conservatism the validator currently rejects 4 until a high-rank character can confirm it works in-game. A future PR can widen the range if needed.

### 14.5 Sub-slice plan (TDD ordering, done-before-next strict)

Slice G is one feature delivered as six independently-greenable sub-slices. Each is sized to ≤1 day except G3 (Dalamud shell, untested in CI, ~1.5 days) and G5 (authoring detection live+offline mirror, ~1.5 days).

| Sub-slice | Repo | CI-testable? | Depends on | What lands |
|---|---|---|---|---|
| **G1 — Schema** | `questforge` (+ `questforge-tools` mirror) | yes (round-trip) | nothing | `GcCategory: int?`, `GcRankTier: int?` on `PurchaseItemStep`; STJ source-gen unaffected; round-trip tests for both fields incl. omitted-defaults-to-null. |
| **G2 — Engine + factory + fakes** | `questforge` | yes (full engine arm) | G1 | `EngineAction.Purchase` gains the two nullable fields (D14.2); `StepFactory` `"purchase-item"` case copies them through; `QuestEngine` purchase branch copies `step.GcCategory`/`step.GcRankTier` into the emitted action; `FakeVendor.Purchase` accepts the two new params and records them on the captured call. |
| **G3 — Dalamud adapter** | `questforge` | NO (Dalamud-only) | G2 | `IVendor.Purchase` signature gains the two nullable params (`DalamudVendor` impl, `FakeVendor` impl); `DalamudVendor.PurchaseGcSeals` counter-based `ReceiveEvent` retry loop with `MaxSwitchAttempts=5` (D14.3); back-compat fully preserved when both are null. In-game smoke: a GC purchase that requires a category switch and a rank-tier switch from a "wrong-tab" starting state. |
| **G4 — Tools mirror + validator** | `questforge-tools` | yes | G1 | Mirror the two schema fields; add the three validator rules (D14.6); update `PurchaseItemValidationTests` with `Group G-F`. `CapabilityInferrer` requires no change (the existing `step:purchase-item` tag already covers it). |
| **G5 — Authoring detection (live + offline mirror)** | `questforge` (live) + `questforge-tools` (offline) | yes (CI-testable for both halves) | G1, G2 | `IVendorProbe` gains the two `GetActiveGc…` methods (D14.4); `FakeVendorProbe` returns scripted values. `PurchaseDetection` gains `ActiveGcCategory` / `ActiveGcRankTier` (D14.5); `SnapshotAggregator` records "last seen" non-null while shop-open span is active; `RecordStepModal` seeds the two new numeric inputs from `after.PurchaseDetected.ActiveGc…` and falls back to blank when null; `StepFactory` `"purchase-item"` case reads those values and writes them onto the built `PurchaseItemStep`. Offline mirror: `SnapshotState` and `TraceToQuestExtractor` mirror the same projection (the trace already carries no new event types — only the projection sees the new probe values via the existing `ShopOpened` heartbeat path). |
| **G6 — Dalamud probe + in-game** | `questforge` | NO (Dalamud-only) | G5 | `DalamudVendorProbe.GetActiveGcCategory`/`GetActiveGcRankTier` implementation that walks the category-radio and rank-tier-radio components and reads `Selected` (the `// VERIFY IN-GAME:` from D14.4). If the node mapping cannot be confirmed cleanly in one session, return `null` from both methods and ship the modal-fallback path (the rest of G5 still works — the pre-fill is just empty). In-game: author a GC purchase via `/qf author`, confirm the modal pre-fills (`gcCategory`, `gcRankTier`); replay through `qf-trace extract-quest`; confirm an identical step. Then `/qf debug buy 6141 1 gcSeals 1 1` from a wrong-tab starting state to verify the adapter's switching path. |

Strict ordering: G1 → {G2, G4} (can run in parallel) → G3 → G5 → G6. G4 may land before G3 since it only depends on G1. G5 depends on both G1 and G2 (it needs the schema fields and the engine action carrier to ship a working end-to-end draft).

CI gates:
- **After G1:** `QuestForge.Schema.Tests` round-trip passes for both fields with all three states (null, in-range value, missing).
- **After G2:** `QuestForge.Engine.Tests` `Group G-A` (engine arm) and `Group G-D` (fake adapter) pass; existing Slice B/C tests remain byte-identical (no test code edits except where the `IVendor.Purchase` signature change forces a default-null pass-through).
- **After G3:** `dotnet build` of the Dalamud projects succeeds; no CI tests added (adapter is untested in CI, consistent with Slice C).
- **After G4:** `QuestForge.Tools.Validator.Tests` `Group G-F` passes.
- **After G5:** `QuestForge.Engine.Tests` `Group G-S` (aggregator + modal seeding) and `QuestForge.Tools.Trace.Tests` `Group G-O` (offline mirror) pass.
- **After G6:** in-game smoke verifies the prose in the row above.

### 14.6 Given-When-Then specifications

Targets `QuestForge.Engine.Tests` (engine arm + aggregator + factory), `QuestForge.Schema.Tests` (round-trip), `QuestForge.Adapters.Tests` (FakeVendor), `QuestForge.Tools.Validator.Tests` (validator), and `QuestForge.Tools.Trace.Tests` (offline mirror). Mirror the harnesses already in `PurchaseItemStepTests` and §13.5's `PurchaseSnapshotAggregatorTests`. Test constants reuse §5's where applicable: `TestVendorNpc = 1001234`, `TestItem = 1601`, `SealItem = 6141`.

#### Group G-E — schema round-trip (`QuestForge.Schema.Tests`)

- **G-E1 — `GcCategory`/`GcRankTier` round-trip when set.** Given `PurchaseItemStep { Currency=GcSeals, GcCategory=2, GcRankTier=1, …}`, when serialized via `QuestForgeJsonContext.QuestFileOptions`, then JSON contains `"gcCategory": 2`, `"gcRankTier": 1`; deserialization yields the same values.
- **G-E2 — omitted fields default to null.** Given JSON without `gcCategory`/`gcRankTier`, when deserialized, then `GcCategory == null && GcRankTier == null`.
- **G-E3 — null on the C# side serializes as omitted (or `null`, whichever matches existing nullable-int handling in QuestForgeJsonContext).** Given `PurchaseItemStep { GcCategory=null, GcRankTier=null }`, when serialized, then either the keys are absent OR the values are `null` — match whichever the existing nullable-int convention does (assert by full round-trip rather than exact JSON shape if the convention varies across the codebase).

#### Group G-A — engine resolve arm (`QuestForge.Engine.Tests`)

- **G-A1 — in range, GC fields set → Purchase carries them.** Given a quest with `PurchaseItemStep { Target=TargetLoc, ItemId=SealItem, Quantity=1, Currency=GcSeals, GcCategory=2, GcRankTier=1 }`, player in range, `GetGrandCompanySeals=2000`, `GetItemCount(SealItem)=0`, when `Engine.Tick`, then `EngineAction.Purchase` with `Currency==GcSeals`, `GcCategory==2`, `GcRankTier==1`.
- **G-A2 — in range, GC fields null → Purchase carries nulls (back-compat).** Given the same step with `GcCategory=null, GcRankTier=null`, when `Engine.Tick`, then `EngineAction.Purchase` with both fields null. (Asserts the engine never substitutes a default like 0.)
- **G-A3 — gil step with GC fields set is still emitted (engine does not gate on them).** Given `Currency=Gil, GcCategory=2, GcRankTier=1`, when `Engine.Tick`, then `EngineAction.Purchase` with `Currency==Gil` carrying both fields. (The validator's warning is the right place to flag this — the engine remains a value-passer.)

#### Group G-D — `FakeVendor` records the new fields (`QuestForge.Adapters.Tests`)

- **G-D1 — `FakeVendor.Purchase` captures `gcCategory` and `gcRankTier`.** Given a `FakeVendor` scripted `Purchase → Purchased`, when `Purchase(NpcId(1001234), ItemId(SealItem), 1, GcSeals, gcCategory: 2, gcRankTier: 1, ct)`, then the recorded call has both new fields set to their argument values.
- **G-D2 — defaults to null when unspecified by the engine.** Given `Purchase(... currency: Gil, ct)` with the two new args defaulted, then the recorded call has `GcCategory==null && GcRankTier==null`.
- **G-D3 — back-compat: an existing FakeVendor test that does NOT pass the new args still compiles and asserts.** Validates the parameter default-null in `IVendor.Purchase`. (The Tester demonstrates this by leaving at least one existing `PurchaseItemStepTests` invocation unchanged after G2 lands and asserting it still passes.)

#### Group G-F — validator (`QuestForge.Tools.Validator.Tests`)

- **G-F1 — `GcCategory = 4` → `structural/purchase-gc-category-out-of-range` (Error).** (Valid range 0..3; 4 is the unverified 5th slot per §14.4.)
- **G-F2 — `GcCategory = -1` → `structural/purchase-gc-category-out-of-range` (Error).**
- **G-F3 — `GcRankTier = 3` → `structural/purchase-gc-rank-tier-out-of-range` (Error).** (Valid range 0..2.)
- **G-F4 — `GcRankTier = -1` → `structural/purchase-gc-rank-tier-out-of-range` (Error).**
- **G-F5 — `GcCategory = 0` (Weapons, valid) → no error, no warning.**
- **G-F6 — `Currency = Gil, GcCategory = 2` → `structural/purchase-gc-fields-on-gil` (Warning).**
- **G-F7 — `Currency = Gil, GcRankTier = 1` → `structural/purchase-gc-fields-on-gil` (Warning).**
- **G-F8 — `Currency = Gil, both null` → no warning.**
- **G-F9 — `Currency = GcSeals, GcCategory = 2, GcRankTier = 0` → no purchase-gc-* error/warning.**

#### Group G-S — authoring detection: aggregator + factory + modal (`QuestForge.Engine.Tests`)

- **G-S1 — `PurchaseDetection.ActiveGcCategory`/`ActiveGcRankTier` populate from probe reads.** Given `SnapshotAggregator` for a quest; given `OnShopOpened(true)` and a heartbeat that observes `IVendorProbe.GetActiveGcCategory()==2, GetActiveGcRankTier()==1`; given the rest of the §13.1 purchase signal (item rise, seal drop); then `Current.PurchaseDetected.ActiveGcCategory==2`, `ActiveGcRankTier==1` alongside the existing fields.
- **G-S2 — null probe reads keep the new fields null.** Given the same as G-S1 but the probe returns `null` for both, then `Current.PurchaseDetected.ActiveGcCategory==null && ActiveGcRankTier==null` (the rest of the detection is unchanged — no synthetic 0 default).
- **G-S3 — "last seen" semantics: the most recent non-null win.** Given two heartbeats during a span: first reads `(category=2, tier=1)`, second reads `(category=3, tier=1)`, then `Current.PurchaseDetected.ActiveGcCategory==3` (the latest).
- **G-S4 — `StepFactory.Build("purchase-item", …)` writes the new fields onto `PurchaseItemStep`.** Given `after.PurchaseDetected = (… ActiveGcCategory=2, ActiveGcRankTier=1)`, when the factory builds the step, then the result has `GcCategory==2, GcRankTier==1`.
- **G-S5 — factory falls back to null when detection has nulls.** Given `after.PurchaseDetected` with both active fields null, when the factory builds, then `GcCategory==null, GcRankTier==null` on the step (and the modal would show blank inputs — covered by G-S6 in plugin tests if a modal test project exists).
- **G-S6 — `RecordStepModal` seeds the two new inputs from snapshot.** (Same test project as the existing modal seeding tests.) Given `after.PurchaseDetected.ActiveGcCategory=2, ActiveGcRankTier=1`, when the modal opens for `StepType=="purchase-item"`, then the two numeric inputs are pre-filled with `"2"` and `"1"`; when both are null, the inputs are blank/`""`.

#### Group G-O — authoring detection offline mirror (`QuestForge.Tools.Trace.Tests`)

- **G-O1 — offline `SnapshotState` mirrors the active-axis projection.** Given the same synthetic trace shape as §13.5's PO1 (`ShopOpened` true, `VendorItemCount`, `CurrencyBalance`) **plus** the active-axis values carried alongside the `ShopOpened` event (the exact wire shape matches whatever the live probe forwards; see D14.5 — likely two extra fields on the `ShopOpened` payload or two new dedicated `ActiveGcCategory`/`ActiveGcRankTier` events, decided in G5 implementation), then `ToSnapshot(t).PurchaseDetected.ActiveGcCategory==2`, `ActiveGcRankTier==1`.
- **G-O2 — `TraceToQuestExtractor` writes the new fields onto the emitted `PurchaseItemStep`.** Given a trace with the values present, when `Extract` runs, then the resulting `PurchaseItemStep` has `GcCategory==2, GcRankTier==1`.
- **G-O3 — absent values produce null fields offline.** Given a trace without active-axis observations (or with null payloads), when `Extract` runs, then the emitted step has both fields null. (Covers traces produced by Slice C / E.4 that pre-date G5.)

### 14.7 Done criteria

1. `PurchaseItemStep` round-trips `gcCategory`/`gcRankTier` in both present and absent forms (G-E1..G-E3 pass).
2. `QuestForge.Engine` carries both fields through `EngineAction.Purchase` without alteration (G-A1..G-A3 pass), and the existing Slice B/C tests remain green with at most a mechanical default-null parameter addition.
3. `FakeVendor` records the two new fields on captured calls (G-D1..G-D3 pass); the `IVendor` interface remains the only adapter surface (no new interface added).
4. `DalamudVendor.PurchaseGcSeals` switches axes via `ReceiveEvent`-on-radio-button (AutoRetainer technique, attributed) when fields are set, retries up to `MaxSwitchAttempts=5` via `ShopOpening` for the addon's 2–3-dispatch convergence, and produces no behavior change when both fields are null. (In-game smoke under G3 — verified 2026-05-28 with `/qf debug buy 4564 1 gcSeals 2 0`.)
5. `qf-validate` flags out-of-range axes and warns on GC-fields-with-gil (G-F1..G-F9 pass).
6. Authoring detection populates `ActiveGcCategory`/`ActiveGcRankTier` on `PurchaseDetection` from the probe and propagates to the modal pre-fill and the built `PurchaseItemStep` (G-S1..G-S6 pass).
7. Offline `qf-trace extract-quest` produces the same `PurchaseItemStep` with the same two new fields populated (G-O1..G-O3 pass).
8. `/qf debug buy <itemId> [qty] [gil|gcSeals] [gcCategory] [gcRankTier]` parses the new trailing args, warns when GC fields are set with currency=gil, and drives end-to-end navigation+buy in a single click (G6 smoke).
9. NO engine replay fixture is re-recorded (mirrors §10.9): the only new engine surface is two passthrough fields on `EngineAction.Purchase`; the engine emits no new per-tick read; authoring observations are unchanged in shape (the probe values ride on the existing `ShopOpened` poll path or as additive events behind the existing `IVendorProbe` gate).

### 14.8 Open risks (and safe degradation)

1. **Potential 5th category (node 48) at high GC ranks.** AutoRetainer's source permits `0..4` but the validator currently rejects 4. Authors whose character can see a 5th tab must verify in-game, then we can widen the validator range. Safe — the runtime adapter dispatches whatever node-id offset is requested; an out-of-range value warns and returns without crashing. (D14.6, §14.1.)
2. **Wrong category index (e.g. author typed `1` thinking "Weapons" but Weapons is `2`).** No automated check possible (the taxonomy is internal). After the switch the addon shows the wrong items; `ResolveExchangeRow` returns -1 next tick (post-latch) → adapter returns `ItemNotSold` → engine emits `AwaitUser` ("vendor does not sell item …"). Safe — pauses for the user.
3. **`AtkComponentRadioButton.Selected` node path drifts across patches (G6 risk).** Mitigated by the D14.4 fail-quiet contract: probe returns null on any read failure, modal falls back to blank inputs with a hint, the rest of the pipeline (engine, validator, offline mirror) works unchanged. The `/qf debug buy` workaround lets the author still drive the path manually.
4. **Refresh latency between switch dispatch and AtkValues update.** The addon takes 2–3 `ReceiveEvent` dispatches to commit a tab change (verified 2026-05-28). Handled by `MaxSwitchAttempts=5` + 500ms `SwitchThrottle` in D14.3 — the counter-based loop converges within ~1s in practice while bounding the worst case at ~2.5s.
5. **No GC field set on a quest authored for a GC quartermaster (degenerate Slice C case).** Behavior is unchanged from today: try resolve, `ItemNotSold` if not on the current tab, `AwaitUser`. Safe; the validator does not require these fields (they are optional).
6. **Both currencies set with GC fields (e.g. `Currency=Gil, GcCategory=2`).** Warning surfaced by `structural/purchase-gc-fields-on-gil`; engine ignores the fields at runtime (gil path does not look at them). Safe + author-visible.

### 14.9 G5 implementation refinements (architect, 2026-05-28)

> **Why this exists.** §14.5 / §14.6 left two material questions for "G5 implementation": (a) the on-the-wire shape that carries the active GC axes into the trace and (b) the exact "last-seen" semantics for the aggregator span. This sub-section pins both, decides modal/factory propagation in light of the existing code, and pins the sub-slicing. It is a refinement of §14.5/§14.6 — not a rewrite. **Replaced GWT specs are listed in §14.9.6 with explicit `REPLACES §14.6.X` headers; §14.6 is left untouched so the chain of decisions is auditable.**
>
> **Authority over the rest of §14:** where this sub-section contradicts §14.5/§14.6/§14.7, this sub-section wins (it ran after in-game verification and after audit of the live Slice E code).

#### 14.9.1 Wire-shape decision — **Option A: extend the existing `ShopOpened` payload**

**Pinned: Option A** — the existing authoring observation `ShopOpened` (emitted by `UIObserver.PollVendor`, see `QuestForge.Plugin.Tracing/UIObserver.cs:472-485`) gains two new optional fields on its payload object:

```jsonc
// before (current Slice E wire shape, verified):
{ "type":"observation", "method":"ShopOpened", "value": { "value": true },  ... }

// after (G5):
{ "type":"observation", "method":"ShopOpened",
  "value": { "value": true, "activeGcCategory": 2, "activeGcRankTier": 1 }, ... }
```

The two new keys are **always present in writes from a G5-or-newer plugin**, with `null` payloads when the probe returns `null` (addon closed, fail-quiet, or not a GC quartermaster — see D14.4). Readers built before G5 silently ignore extra object keys; readers built at G5-or-later treat absent / `null` axes as `null`.

**File-level rationale (citing what was read):**

1. **Existing `ShopOpened` payload is already a JSON object, not a bare bool.** `UIObserver.cs:477` writes `JsonSerializer.SerializeToElement(new { value = open }, JsonOpts)` and the offline parser `SnapshotState.cs:175-183` accepts BOTH `true|false` (bare bool) AND an object with a `value` key. Adding sibling keys on that same object is a *strictly* additive shape change; no parser branch needs to widen, and no extra event type joins the trace.

2. **Zero new event types means zero new `case "X":` branches in `SnapshotState.Apply` AND zero new `method` discriminators in `TraceEventParser.DetectAndDeserialize` (`questforge-tools/.../TraceEventParser.cs:113`).** The existing `ObservationEvent` deserializer (`ObservationEvent.cs:6-16`) takes the value as a `JsonElement?`, so new sibling keys deserialize for free. Option B or C would each add at least one new method discriminator and at least one new apply-case (counted via the parallel apply-cases for `EnemyKilled`/`InCombat` at `SnapshotState.cs:124-153`). Option A adds **one** code edit per consumer (live emit + offline read), not one-per-new-event.

3. **Fixture-cascade analysis (the load-bearing concern, §14.7 done-criterion #9).** Walked the corpus:
   - `questforge-data/fixtures/engine/simple-linear-acceptance.json` — no `.trace.jsonl`; pure scripted state. **Unaffected.**
   - `questforge-data/fixtures/engine/with-attunement.{json,trace.jsonl}` — `grep -l "ShopOpened\|VendorItemCount\|CurrencyBalance" with-attunement.trace.jsonl` returned **zero matches**. No vendor observations exist in any engine replay fixture today. **Unaffected.**
   - `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs` and related scripted-state files do not consume vendor events. **Unaffected.**
   - The only consumers of `ShopOpened` outside of authoring are `SnapshotState.Apply` (offline mirror) and `UIObserver.PollVendor` (live emit). Option A's additive sibling keys are byte-compatible with both: the live emit appends two new keys; the offline parser deserializes them as `JsonElement?` via the existing `ObservationEvent` shape and only `SnapshotState.Apply("ShopOpened", …)` peeks for them — and only when they are present.

   **Verdict (and the gate for §14.7 done-criterion #9):** **NO engine replay fixture re-recording is required.** The new sibling keys land only on `ShopOpened` events emitted by a G5 plugin; existing fixtures emit none of these events at all, so the additive change is invisible to them. The Tester MUST assert this with a regression by running the existing engine fixture suite unchanged after G5 — the parametric `Quest66130ReplayTests` / `EngineFixtureTests` runs must remain green with byte-identical fixtures.

4. **Why Option B (two dedicated events) is rejected.** Two new method discriminators (`ActiveGcCategory`, `ActiveGcRankTier`) mean:
   - Two more apply-cases in `SnapshotState.cs` and two more entries in any test/`MakeTrace` helper that round-trips events.
   - Worse, they create a **temporal-ordering ambiguity** the aggregator must resolve: does the active-axis observation arrive **before**, **at**, or **after** the `ShopOpened {true}` heartbeat? Today the heartbeat polls `IsShopOpen()`, balances, and item-counts in *one* atomic block (`UIObserver.cs:465-516`), with `ShopOpened` written first and `_aggregator?.OnShopOpened(open)` forwarded immediately. Two new events would have to slot into that same heartbeat (otherwise the aggregator's "last seen" cannot guarantee "during the open span") — at which point they are *semantically* part of the `ShopOpened` heartbeat anyway, and Option A is the more honest shape.
   - Risk of fixture-cascade is non-zero if a non-vendor heartbeat accidentally drives an axis read in some future patch; bundling them into `ShopOpened` proves at the code level that they only fire when the shop-open path fires.

5. **Why Option C (one combined `ActiveGcAxesObserved`) is rejected.** Same "two paths into the same aggregator" temporal-ordering issue as B; loses the cohesion benefit of "the same JSON object that says the shop opened also says which tab"; still adds one new event type vs zero.

**Concrete production-side edit (the only wire-shape edit):**

```csharp
// QuestForge.Plugin.Tracing/UIObserver.cs PollVendor — current shape at line 477:
var valueEl = JsonSerializer.SerializeToElement(new { value = open }, JsonOpts);

// G5 shape:
var activeCat  = _vendorProbe.GetActiveGcCategory();
var activeTier = _vendorProbe.GetActiveGcRankTier();
var valueEl = JsonSerializer.SerializeToElement(
    new { value = open, activeGcCategory = activeCat, activeGcRankTier = activeTier },
    JsonOpts);
_traceSession.Write(new ObservationEvent(
    RunId:    runId,
    Method:   "ShopOpened",
    Argument: null,
    Value:    valueEl,
    At:       now));
_aggregator?.OnShopOpened(open, activeCat, activeTier);  // signature change, §14.9.2
```

Both probe reads are issued every transition (i.e. only when `IsShopOpen()` changes), keeping the per-heartbeat cost identical to today. **An additional periodic read** (so axis changes WHILE the shop stays open are captured) is specified in §14.9.2 as a non-`ShopOpened` heartbeat extension that **forwards to the aggregator only** — it does NOT emit a new trace event. (This is the small concession that keeps fixture-cascade at zero while still capturing mid-span axis changes.)

#### 14.9.2 Aggregator data structure — exact fields and "last seen" semantics

**Pinned fields on the span state in `SnapshotAggregator`** (added next to the existing `_shopOpen`/`_purchaseSpanStarted` block at `SnapshotAggregator.cs:35-42`):

```csharp
// ── purchase span state (additive: GC axes) ────────────────────────────
private int? _spanActiveGcCategory;   // last non-null value observed while span is open OR retained
private int? _spanActiveGcRankTier;   // same lifecycle as _spanActiveGcCategory
```

**One nullable field per axis (NOT a `(int?, int?)` tuple, NOT a single `GcAxes?` pair).** Rationale:

- The two axes update **independently** during a session — the player clicks Weapons (category changes; rank-tier unchanged) and then clicks rank tier 1 (rank-tier changes; category unchanged). A tuple/pair would force the aggregator to either (a) overwrite both fields on every update — losing the older axis — or (b) carry per-axis last-seen logic anyway and just re-pack into a tuple at projection time. The independent-field shape is simpler and matches the probe's two independent reads (`GetActiveGcCategory()` and `GetActiveGcRankTier()` from D14.4).
- "Last seen non-null wins" is the field's sole rule (see below); making it per-field keeps that rule trivially local to each setter.

**Aggregator API extension (one of two options — the Builder picks ONE; the Tester asserts behavior, not signature shape):**

- **Option (i) — extend `OnShopOpened`** to carry the two `int?` axes alongside `bool open`:
  ```csharp
  public void OnShopOpened(bool open, int? activeGcCategory = null, int? activeGcRankTier = null);
  ```
  Backward-compatible defaults preserve existing call sites in tests.

- **Option (ii) — add a dedicated forwarder** invoked by `PollVendor` every heartbeat:
  ```csharp
  public void OnActiveGcAxesObserved(int? activeGcCategory, int? activeGcRankTier);
  ```
  Called every `PollVendor` tick while a span is alive; the aggregator drops the call on the floor when no span is active (mirror of `OnVendorItemCountChanged`'s `if (!_purchaseSpanStarted) return;` at `SnapshotAggregator.cs:262`).

**Recommendation: BOTH.** Use (i) on the `false→true` transition (so the span baselines come with the axes seen at open) and (ii) every heartbeat thereafter (so mid-span axis changes update `_spanActiveGcCategory`/`_spanActiveGcRankTier`). `UIObserver.PollVendor` would call (i) only at the transition (line 484 today: `_aggregator?.OnShopOpened(open);`) and add an unconditional `_aggregator?.OnActiveGcAxesObserved(activeCat, activeTier);` outside the `if (open != _lastShopOpen)` guard, gated on `_vendorProbe is not null`. (ii) is **not** trace-emitting — it is in-process forwarding only — which is what keeps fixture-cascade at zero while capturing mid-span axis edits.

**"Last seen non-null wins" — exact rule (resolves G-S3):**

```csharp
// Inside the aggregator, in BOTH the OnShopOpened(open, cat, tier) and the
// OnActiveGcAxesObserved(cat, tier) setters:

if (cat is not null) _spanActiveGcCategory = cat;
if (tier is not null) _spanActiveGcRankTier = tier;
// NOTE the asymmetry: a NULL observation does NOT clear a previously-set value.
```

**Specifically:** between two non-null observations, a null observation **keeps** the prior value. This is intentional — `null` from the probe means "the addon was closed at this moment OR the read failed" (D14.4 fail-quiet contract), which is precisely the situation where we want to retain whatever we *did* see while the shop was open. A `(2 → null → 3)` sequence yields `3` (G-S3 already asserts this for the (2 → 3) case; G-S3-bis below covers the null in the middle).

**Span lifecycle interaction:**

- On `OnShopOpened(true, cat, tier)` `false→true`: the existing baseline-snapshot + `_spanItemDeltas.Clear()` runs (lines 246-252); ALSO `_spanActiveGcCategory = null; _spanActiveGcRankTier = null;` is set **before** the "last seen non-null wins" application of the `(cat, tier)` carried with the transition. (A new span MUST start with cleared axes — otherwise a previous span's tab choice would bleed into the new one's projection.)
- While the span is **open** (`_shopOpen == true`): `OnActiveGcAxesObserved` updates per the rule above.
- While the span is **retained** post-close (`_shopOpen == false` but `_purchaseSpanStarted == true` — mirror of combat retention, lines 252-253): `OnActiveGcAxesObserved` calls are dropped (`if (!_shopOpen) return;`). The retained span keeps whatever axes were observed during the open phase; we do not let post-close stale probe reads mutate them.
- On `ResetDeltas` (`SnapshotAggregator.cs:431-436`): both axis fields are cleared alongside `_spanItemDeltas`/`_shopOpen`/`_purchaseSpanStarted`/baselines.

**`BuildPurchaseDetected` projection update (`SnapshotAggregator.cs:441-451`):**

```csharp
private PurchaseDetection? BuildPurchaseDetected()
{
    if (!_purchaseSpanStarted) return null;
    var baselineGil   = _purchaseBaselineGil   ?? _currentGil;
    var baselineSeals = _purchaseBaselineSeals  ?? _currentSeals;
    return new PurchaseDetection(
        ShopWasOpen:        true,
        ItemDeltas:         new Dictionary<uint, int>(_spanItemDeltas),
        GilDropped:         Math.Max(0L, baselineGil  - _currentGil),
        SealsDropped:       Math.Max(0,  baselineSeals - _currentSeals),
        ActiveGcCategory:   _spanActiveGcCategory,    // NEW (named arg — D14.5 record uses defaults)
        ActiveGcRankTier:   _spanActiveGcRankTier);   // NEW
}
```

`PurchaseDetection`'s two new fields are already declared with `= null` defaults per D14.5; the constructor call simply passes them by name.

**Why "at-purchase-instant" was rejected** (rephrasing D14.5's prose decision with the concrete reason now visible from the code): there is no instant-correlation mechanism between an item count change and a tab state. Item-count changes arrive via `OnVendorItemCountChanged`; tab state arrives via the new probe read. Stitching them at instant granularity would require timestamping every probe read and every item-delta and picking the axes "just before" the rise — a complexity that buys nothing because (a) authors rarely flip tabs *between* the buy click and the count rise, and (b) wrong pre-fill is one click in the modal to fix. "Last seen non-null wins" is the smallest rule that correctly handles the common case (the player browses tabs, settles on the right one, clicks buy — the *last* tab they were on is the one we want).

#### 14.9.3 Modal UI — concrete control description and seeding

**Existing infrastructure (audited):** `QuestForge.Plugin/UI/Authoring/RecordStepModal.cs` already has three editable text fields for the purchase-item type override (`_editPurchaseItemId`/`_editPurchaseQuantity`/`_editPurchaseCurrency` at lines 40-42), seeded from `_after.PurchaseDetected` at lines 145-164, and rendered with `ImGui.InputText` at lines 217-228. The reset/clear paths exist at lines 69-71 and 424-426. **The pattern is established — G5 needs to mirror it for the two GC axes.**

**Pinned controls — two new text-input fields next to the currency input, NOT `ImGui.InputInt` numerics:**

```csharp
// Class fields (additive, near line 42):
private string _editPurchaseGcCategory = "";
private string _editPurchaseGcRankTier = "";

// DrawInferenceState — after line 228, only when StepType == "purchase-item":
ImGui.TextUnformatted("GC Category (0..3, blank for any):");
ImGui.SetNextItemWidth(80f);
ImGui.InputText("##purchasegccategory", ref _editPurchaseGcCategory, 8);
ImGui.TextUnformatted("GC Rank Tier (0..2, blank for any):");
ImGui.SetNextItemWidth(80f);
ImGui.InputText("##purchasegcranktier", ref _editPurchaseGcRankTier, 8);
```

**Why `ImGui.InputText`, not `ImGui.InputInt`** (matches existing precedent and supports the null/blank case G-S6 requires):

- All three existing purchase fields are `InputText` (lines 221, 224, 227). Mixing a numeric input alongside three text inputs is visually jarring and code-noise (two parse paths instead of one).
- `InputInt` cannot represent `null` — it always carries an `int`. G-S6 requires "when both axes are null, the inputs are blank/`""`" — that is trivially expressible with `InputText` and a `""` initial value, but with `InputInt` the user would see `0` (which is a legal axis value — Weapons / lowest tier!) and confuse "no opinion" with "axis = 0."
- Parsing on Confirm uses the same `int.TryParse` pattern as the existing quantity parse (line 363) — a blank string yields `false`/zero, which maps to `null` for the optional field.

**Seeding (extends the existing block at lines 145-164):**

```csharp
if (_inference.StepType == "purchase-item" && _after?.PurchaseDetected is { } pd && pd.ItemDeltas.Count > 0)
{
    // ... existing item/quantity/currency seeding ...

    _editPurchaseGcCategory = pd.ActiveGcCategory is { } cat
        ? cat.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    _editPurchaseGcRankTier = pd.ActiveGcRankTier is { } tier
        ? tier.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
}
else
{
    // ... existing else-branch ...
    _editPurchaseGcCategory = "";
    _editPurchaseGcRankTier = "";
}
```

**Confirm-time parse (extends `BuildRawStep` at lines 349-383):**

```csharp
int? gcCategory = null;
if (int.TryParse(_editPurchaseGcCategory.Trim(), out var catParsed)) gcCategory = catParsed;
int? gcRankTier = null;
if (int.TryParse(_editPurchaseGcRankTier.Trim(), out var tierParsed)) gcRankTier = tierParsed;

return new PurchaseItemStep
{
    // ... existing properties ...
    GcCategory = gcCategory,
    GcRankTier = gcRankTier
};
```

**Open + reset paths:** `Open()` (line 59), `ResetAndClose()` (line 417), and the unused-after-validator `Reset()` (lines 64-72) all set the two new fields to `""` — mirroring exactly how `_editPurchaseItemId` etc. are cleared.

**G-S6 specifics:** when both `ActiveGcCategory` and `ActiveGcRankTier` are non-null on `pd`, the two InputText controls show `"2"` and `"1"` respectively (after `.ToString(InvariantCulture)`). When both are null on `pd`, the controls show `""`. The Tester asserts the field state, not the rendered pixels — the existing modal tests already follow this convention.

**No new UI code beyond the additive controls is needed.** The modal already supports re-opening with new pre-fills (the Open/Reset chain is idempotent), and the validator's "axes ignored when currency=gil" warning (D14.6) is surfaced as the existing yellow notes block at line 200 — no additional inline UI work.

#### 14.9.4 StepFactory propagation — exact added lines

`StepFactory.BuildPurchaseItemStep` (in `QuestForge.Engine/Authoring/StepFactory.cs:288-332`) already builds a `PurchaseItemStep` from `after.PurchaseDetected` and `after.LastNpcInteracted`. Adding G5 is two property-init lines in the existing object initializer (lines 321-331):

```csharp
return new PurchaseItemStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    Target = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos),
    ItemId = primaryId,
    Quantity = primaryQty,
    Currency = currency,
    GcCategory = after?.PurchaseDetected?.ActiveGcCategory,   // NEW (G-S4)
    GcRankTier = after?.PurchaseDetected?.ActiveGcRankTier    // NEW (G-S5: null when null on pd)
};
```

That is the full factory change. No new branches; no new helpers. The `?.` chain copes with `after is null` and with `after.PurchaseDetected is null` — both produce `null` on the resulting step, which G-S5 asserts is the correct behavior. The modal's `BuildRawStep` (RecordStepModal.cs:372-382) does NOT delegate to `StepFactory` for `purchase-item` (it inlines the build — see line 372), so the modal's two property-inits in §14.9.3 are independent of the factory's two property-inits here.

**Equality of live and offline:** both `SnapshotAggregator.BuildPurchaseDetected` (live) and `SnapshotState.BuildPurchaseDetected` (offline, `questforge-tools/QuestForge.Tools.Trace/SnapshotState.cs:489-497`) project into the *same* `PurchaseDetection` record. The factory therefore reads from the same shape regardless of consumer — the line above is correct for both live (E.2) and offline (E.3) paths because `TraceToQuestExtractor.cs:219` calls `StepFactory.Build("purchase-item", …)`.

#### 14.9.5 Sub-slicing plan — **G5 as ONE branch per repo, with G5a (questforge) merged BEFORE G5b (questforge-tools)**

**Pinned: two branches, strictly ordered.**

- **G5a — `feat/purchase-item-gc-authoring`** on `questforge`. Lands all of: `IVendorProbe` extension (`GetActiveGcCategory`/`GetActiveGcRankTier`), `FakeVendorProbe` extension, `UIObserver.PollVendor` payload change (§14.9.1's Option A wire shape) and the `OnActiveGcAxesObserved` heartbeat forward, `SnapshotAggregator` two new fields + setters + lifecycle (§14.9.2), `PurchaseDetection` two new fields wired through `BuildPurchaseDetected`, `StepFactory` two property inits (§14.9.4), `RecordStepModal` two new InputText controls + seeding + Confirm parse + Open/Reset (§14.9.3). Tests in scope: GWT-PU* extensions for the `ShopOpened` payload, G-S1..G-S6 (six tests), `UIObserverVendorTests`/`UIObserverVendorForwardingTests` updates to assert the new payload + forward.
- **G5b — `feat/purchase-item-gc-offline`** on `questforge-tools`. Lands: `SnapshotState.Apply("ShopOpened", …)` extended to peek `activeGcCategory`/`activeGcRankTier` siblings on the payload object and store them on the same two new private fields with the same "last seen non-null wins" rule; `SnapshotState.BuildPurchaseDetected` projects them onto `PurchaseDetection`. (`TraceToQuestExtractor` requires NO change — it already calls `StepFactory.Build("purchase-item", …)`, which now propagates the fields per §14.9.4 since `questforge-tools` already references the engine project's `StepFactory`.) Tests: G-O1..G-O3.

**Why G5a strictly before G5b** (not parallel branches in one PR each):

- **Wire shape is the contract between the halves.** G5b's `Apply("ShopOpened", …)` parses the *exact* JSON the G5a `PollVendor` emits. If G5b lands first, the test fixtures it writes will have a guessed shape; if G5a then ships a different shape, the offline tests pass on stale fixtures and the live-vs-offline projections diverge silently. Landing G5a first pins the production-emitted shape (committed in `UIObserver.cs`), against which G5b's parser can be authored and asserted.
- **`StepFactory` is in `questforge` and is referenced by `questforge-tools`.** G5b's `TraceToQuestExtractor` invokes `StepFactory.Build("purchase-item", …)` from G5a's project. The two new property inits in §14.9.4 ship in G5a; G5b inherits them via the project reference. Trying to ship G5b first means either (a) duplicating the property inits in `questforge-tools` (forbidden by the "live and offline use the SAME StepFactory" precedent at `TraceToQuestExtractor.cs:200-205`) or (b) shipping G5b against an unmerged G5a (introduces inter-PR coupling for no benefit).
- **CI signal is cleaner.** G5a's tests live entirely in `questforge`; G5b's live entirely in `questforge-tools`. Each PR is a complete, mergeable unit. The strict order is enforced by the human reviewer (G5b's PR description should cite the G5a merge commit).

**Why NOT sub-sub-slicing within questforge (G5.1 = probe; G5.2 = aggregator + factory; G5.3 = modal):**

- The probe extension is two interface methods + a fake implementation. Splitting it into its own PR means a merged but-unconsumed probe surface — exactly the "interfaces shipped without consumers" anti-pattern that bit Slice E pre-PR-#80. Bundling probe + aggregator forward + factory ensures the new fields have a producer-to-consumer chain in one merge.
- The modal is the only piece that depends on the GameStateSnapshot projection (`PurchaseDetection` extension). It is conceptually one step downstream of the aggregator, but the aggregator's tests are pure and the modal's tests are pure — they share zero test infrastructure. Splitting them adds a PR for ~30 lines of UI code that has no behavioral test in isolation (it asserts pre-fill via `_editPurchaseGc*` field state in the existing modal test project).
- The CI runtime difference between "G5a as one PR" (~6-9 tests added) and "G5a as three sub-PRs" (~2-3 tests each) is not material; the review burden of three PRs is.

**Strict-ordering implications and protocol:**

1. Open G5a as `feat/purchase-item-gc-authoring` on `questforge`. Land all of §14.9.1's wire-shape edit, §14.9.2's aggregator extension, §14.9.3's modal extension, §14.9.4's factory extension, and all GWT-PU* updates + Group G-S tests. Merge to main when green.
2. Then open G5b as `feat/purchase-item-gc-offline` on `questforge-tools`. Land `SnapshotState.Apply("ShopOpened", …)`'s sibling-key peek + projection + Group G-O tests. PR description cites the G5a merge SHA. Merge when green.
3. **No sub-slicing within G5a or G5b.** Each is a single TDD red→green cycle.

The Slice G6 in-game (probe implementation: `DalamudVendorProbe.GetActiveGcCategory`/`GetActiveGcRankTier`) follows G5b and is unchanged from §14.5.

#### 14.9.6 Updated / refined GWT specs (REPLACES — §14.6 left untouched)

> Each entry below explicitly states which §14.6 spec it replaces. The Tester MUST use these refined versions; the originals in §14.6 are kept verbatim for audit but are superseded.

**REPLACES §14.6 G-O1** (the original deferred the wire shape to "G5 implementation"; now pinned):

- **G-O1 (refined) — offline `SnapshotState` mirrors the active-axis projection from the extended `ShopOpened` payload.**
  *Given* `SnapshotState(70001)`;
  *When* an `ObservationEvent` is applied with `Method == "ShopOpened"` and `Value == { "value": true, "activeGcCategory": 2, "activeGcRankTier": 1 }`, then a `CurrencyBalance { gil: 10000, seals: 0 }` event, then a `VendorItemCount { itemId: 1601, count: 1 }` event, then a `CurrencyBalance { gil: 9000, seals: 0 }` event;
  *Then* `ToSnapshot(t).PurchaseDetected` has `ItemDeltas[1601] == 1`, `GilDropped == 1000`, **`ActiveGcCategory == 2`, `ActiveGcRankTier == 1`**.
  *And* `Apply` returns `true` for the `ShopOpened` event (recognised-no-op discipline — the additional sibling keys do not break recognition).

**REPLACES §14.6 G-O3** (the original phrased "absent values" loosely; now pinned to two concrete sub-cases):

- **G-O3 (refined a) — pre-G5 trace: `ShopOpened` payload has no `activeGcCategory`/`activeGcRankTier` siblings.**
  *Given* `SnapshotState(70001)`;
  *When* a `ShopOpened` event with `Value == { "value": true }` (legacy shape — no GC keys at all) is applied, then the rest of a clean purchase trace;
  *Then* `ToSnapshot(t).PurchaseDetected` has `ActiveGcCategory == null && ActiveGcRankTier == null`. The downstream `PurchaseItemStep` emitted by `Extract` has both fields null.

- **G-O3 (refined b) — G5 trace with null-valued payload (probe fail-quiet).**
  *Given* `SnapshotState(70001)`;
  *When* a `ShopOpened` event with `Value == { "value": true, "activeGcCategory": null, "activeGcRankTier": null }` is applied;
  *Then* the projection yields `ActiveGcCategory == null && ActiveGcRankTier == null`. (Distinguishes G5-but-probe-failed from pre-G5 absent-keys; both must project to null and produce a step with null GC fields.)

**REPLACES §14.6 G-O2** (refining the precondition wording to match §14.9.1's concrete wire shape):

- **G-O2 (refined) — `TraceToQuestExtractor` writes the new fields onto the emitted `PurchaseItemStep`.**
  *Given* a synthetic trace whose only `ShopOpened` event carries `Value == { "value": true, "activeGcCategory": 2, "activeGcRankTier": 1 }`, plus the standard `CurrencyBalance`/`VendorItemCount`/`ShopOpened {false}` sequence that drives an extracted `PurchaseItemStep` (mirror PE1's shape);
  *When* `Extract` runs;
  *Then* the resulting `PurchaseItemStep` has `GcCategory == 2` and `GcRankTier == 1` (carried through `StepFactory.Build` per §14.9.4).

**REPLACES §14.6 G-S3** (clarifying the intermediate-null case the original under-specified):

- **G-S3 (refined) — "last seen non-null wins"; intermediate nulls preserve the prior value.**
  *Given* `SnapshotAggregator` for `BuyQuest`; `OnShopOpened(true, activeGcCategory: 2, activeGcRankTier: 1)`;
  *When* `OnActiveGcAxesObserved(null, null)` is called (probe momentarily returned null), then `OnActiveGcAxesObserved(3, 1)` is called;
  *Then* `Current.PurchaseDetected.ActiveGcCategory == 3 && ActiveGcRankTier == 1`. (The intermediate null did NOT clear the `2`; the subsequent `3` then replaced it. Combined with the original G-S3 (`(2 → 3)` → `3`), this pins the rule.)

**REPLACES §14.6 G-S1** (concretizing the API the probe-to-aggregator wiring uses, per §14.9.2):

- **G-S1 (refined) — `PurchaseDetection.ActiveGcCategory`/`ActiveGcRankTier` populate from the aggregator's two API entry points.**
  *Given* `SnapshotAggregator` for `BuyQuest`;
  *When* `OnShopOpened(true, activeGcCategory: 2, activeGcRankTier: 1)` is called (transition forwards the axes) and the rest of the §13.1 purchase signal arrives (`OnVendorItemCountChanged(1601, +1)`, `OnCurrencyChanged(seals dropped)`);
  *Then* `Current.PurchaseDetected.ActiveGcCategory == 2 && ActiveGcRankTier == 1` alongside the existing fields.
  *Also* (heartbeat path): with no axes on `OnShopOpened(true)` but a subsequent `OnActiveGcAxesObserved(2, 1)`, the same projection holds — confirms both APIs feed the same span fields.

**ADDS — GWT-PU6 (new; covers §14.9.1's wire-shape change in `UIObserver.PollVendor`):**

- **GWT-PU6 — `ShopOpened` payload carries `activeGcCategory`/`activeGcRankTier` siblings from the probe.**
  *Given* `UIObserver` with a `FakeVendorProbe` that reports `IsShopOpen()==true` and `GetActiveGcCategory()==2`, `GetActiveGcRankTier()==1`;
  *When* a heartbeat fires and `_lastShopOpen` was previously `false` (transition);
  *Then* exactly one `ObservationEvent` with `Method=="ShopOpened"` is written and its `Value` parses as an object with `value: true`, `activeGcCategory: 2`, `activeGcRankTier: 1`; `_aggregator?.OnShopOpened(true, 2, 1)` is forwarded.
  *Negative*: when the probe returns `null` for both axes, the `Value` contains `activeGcCategory: null`/`activeGcRankTier: null` (keys present, values null) and the forwarded `OnShopOpened(true, null, null)` carries nulls.

**ADDS — GWT-PU7 (covers the mid-span heartbeat forward path that does NOT emit a new trace event):**

- **GWT-PU7 — `OnActiveGcAxesObserved` is forwarded every heartbeat while a span is alive, without emitting a new trace event.**
  *Given* `UIObserver` with `FakeVendorProbe` after a `ShopOpened {true}` heartbeat has already fired (so `_lastShopOpen == true`);
  *When* a subsequent heartbeat fires with `IsShopOpen()` still true but `GetActiveGcCategory()` returning `3` (changed from `2`);
  *Then* NO new `ShopOpened` `ObservationEvent` is written (the open-state did not transition), but `_aggregator?.OnActiveGcAxesObserved(3, …)` IS forwarded.
  *Asserts* the "mid-span axis changes update the aggregator without inflating the trace" property — the load-bearing guard against fixture-cascade.

(All of §14.6 G-S2, G-S4, G-S5, G-S6, G-E1..G-E3, G-A1..G-A3, G-D1..G-D3, G-F1..G-F9, G-O remaining (the refined replacements above cover G-O1/G-O2/G-O3) stand as written.)

#### 14.9.7 Risks and explicit non-goals

**Risks (additive to §14.8):**

1. **A future reader parses `Value` as a bare bool when it is in fact an object.** Mitigated by `SnapshotState.cs:175-183` already accepting both forms; any new parser MUST follow that pattern. The Tester asserts both legacy `{ value: true }` AND `true` (bare) AND the G5 object-with-siblings shape all deserialize without error (G-O3a covers the legacy-keyless case; an additional bare-bool case is folded into the existing parsing tests for `ShopOpened`).
2. **Mid-span axis-flip storm.** If the player rapidly clicks tabs, every heartbeat (1Hz at most) updates `_spanActiveGcCategory`/`_spanActiveGcRankTier`. Cost: O(1) per update, no allocations. The trace is **unaffected** (no new event fires per the §14.9.2 design — `OnActiveGcAxesObserved` is in-process forwarding only). Accepted.
3. **Probe-fail oscillation while shop stays open.** The `null` observations during a transient probe failure no longer clear the prior non-null value (G-S3 refined). If the probe never recovers, the projection retains the last-known good axes; the Confirm-stage modal pre-fills correctly. Accepted; the alternative ("null clears") was rejected as making one transient probe glitch corrupt an otherwise clean detection.
4. **Sibling-key collision in `ShopOpened` `value` payload.** The two new keys (`activeGcCategory`, `activeGcRankTier`) are namespaced enough that no current or planned key collides; the validator does not lint observation payloads (they are post-mortem trace data). Accepted.

**Explicit non-goals (G5 does NOT do):**

1. **No probe for character GC rank.** §14.8 risk #1 (potential 5th category at high GC ranks) is unchanged; G5 reads only the *currently visible* radio button selection, not what tabs the character can see.
2. **No UI exposure of the high-rank 5th category.** The validator still rejects `GcCategory == 4` (D14.6); G5 inherits that limit. Widening is a separate slice once an in-game rank-3 character verifies.
3. **No validator change.** §14.4's three rules are unchanged; G5 is invisible to the validator (the schema fields already exist from G1; no new rules apply).
4. **No new TraceEvent type.** §14.9.1 specifically picks Option A to avoid this; G5 adds zero entries to `TraceEventParser.DetectAndDeserialize`, zero entries to `TraceEventJsonContext`.
5. **No new authoring-mode poller.** `PollVendor` is extended in-place; no `PollGcExchange` or similar new heartbeat method is introduced.
6. **No engine runtime change.** The engine's `EngineAction.Purchase` already carries `GcCategory`/`GcRankTier` from G2; the engine emits them whether or not authoring detection has populated them. G5 is authoring-time only.
7. **No live-switching of `IVendorProbe` implementation.** Plugins constructed without a probe (Inspect mode, tests) still get back-compat behavior — no `ShopOpened` events at all, no aggregator forwards. Live switching is out of scope (matches the `TraceMode` reload-required note in CLAUDE.md).
8. **No "split into multiple purchase steps" auto-emission.** §11's stance ("auto-splitting one recording window with multiple distinct purchases is out of scope") stands; if both axes flip mid-session because the player bought from two different tabs, the resulting single drafted step still pre-fills only the LAST-seen axes, and `Notes` on the inference result already flags multi-item windows for author split (existing §13.1 behavior).


---

## Report summary

- **Schema:** `PurchaseItemStep : Step` (`"purchase-item"`) with `NpcLocation Target`, `uint ItemId`, `int Quantity = 1`, `PurchaseCurrency Currency = Gil` (camelCase enum `gil`/`gcSeals`).
- **Adapter:** NEW `IVendor.Purchase(NpcId, ItemId, int qty, PurchaseCurrency, ct) → Result<PurchaseOutcome>` (cohesion; keeps `IInteractor` from bloating). Plus `IGameStateProvider.GetGrandCompanySeals` (new) for GC affordability.
- **EngineAction:** new `Purchase(NpcId Vendor, ItemId Item, int Quantity, PurchaseCurrency Currency, Step? Origin)`.
- **Postcondition:** ABSOLUTE — `GetItemCount(item) >= Quantity`, encoded as a synthesized default `playerHasItem(ItemId, Quantity)` Expect; idempotent/resume-safe, no captured baseline, reuses the existing predicate.
- **Currency:** author-declared (closed enum), so the engine picks the affordability read synchronously and the validator can check it statically; runtime inference rejected. Authoring detection PRE-FILLS the field (recording-time) from the currency that dropped, but the serialized step is always explicit.
- **Authoring detection (NOW IN SCOPE, Slice E):** signal = shop-open (`GilShop`/`GrandCompanyExchange`) correlated with a regular-inventory count increase AND a gil/seal decrease within a shop-open-bracketed span; the dropped currency disambiguates `Currency`, the item delta gives `ItemId`/`Quantity`, `LastNpcInteracted` gives `Target`. Mirrors combat's span model (no timing window needed — shop-open is the bracket). FIXTURE-CASCADE verdict: NEW recorded observations ARE required (regular-item counts, gil/seal balances, shop-open are NOT in the trace today — `PollKeyItems` is key-items-only, no gil/shop polling exists); mitigated by emitting them ONLY from authoring-mode pollers behind a new `IVendorProbe`/`PollVendor` (exactly like `ICombatProbe`/`PollCombat`, which did NOT cascade), with the new engine `GetGrandCompanySeals` read step-gated so NO engine replay fixture re-records.
- **Slices:** A schema → B engine+fakes → C Dalamud → D tools/validator → **E authoring detection (E.1 observer+probe, E.2 live correlation/inference/factory/modal, E.3 offline mirror in `questforge-tools`, E.4 Dalamud probe + in-game)**. Slice E depends only on A.
- **Slice G (§14): GC navigation (category x rank-tier matrix)** — additive optional `GcCategory: int?` (0..3 positional: 0=Weapons, 1=Armor, 2=Materiel, 3=Materials) and `GcRankTier: int?` (0..2 bottom-to-top) on `PurchaseItemStep`; `EngineAction.Purchase` carries them through; `DalamudVendor.PurchaseGcSeals` switches tabs via `AtkUnitBase.ReceiveEvent` on the radio buttons' pre-registered `AtkEvent` (technique adapted from AutoRetainer BSD-3 — synthetic `FireCallback` does NOT trigger the radio handlers) with a counter-based retry loop (`MaxSwitchAttempts=5`, 500ms throttle) because the addon takes 2–3 dispatches to commit; `IVendorProbe` extension `GetActiveGcCategory`/`GetActiveGcRankTier` reads the radio button `Selected` flag for modal pre-fill (fail-quiet → null → blank inputs + hint); validator adds `...-gc-category-out-of-range` / `...-gc-rank-tier-out-of-range` (Error) and `...-gc-fields-on-gil` (Warning); `/qf debug buy` gains trailing `[gcCategory] [gcRankTier]` args. Sub-slices G1 schema → {G2 engine, G4 tools/validator} → G3 Dalamud → G5 authoring (live + offline mirror) → G6 Dalamud probe + in-game. Depends only on G1 within Slice G; whole Slice G depends only on the existing Slice A schema.
- **Top risks:** GC rank gating / shop-not-unlocked (→ `AwaitUser`); quantity-spinner addon specifics (degrades to buy-one-per-tick, made correct by the absolute postcondition); Slice E fixture-cascade (mitigated by authoring-only pollers + step-gated engine read); partial purchase signals (guard requires all three of shop-open + item-rise + currency-drop → no phantom purchase).
- **Slice G top risks:** potential 5th category (node 48) at high GC ranks is currently validator-rejected — extend the range when verified in-game; wrong category index — no automated check, runtime degrades to `ItemNotSold` after `MaxSwitchAttempts=5` failed dispatches → `AwaitUser`; `AtkComponentRadioButton.Selected` node path drift (G6 only) — fail-quiet to null → modal blank inputs + hint, `/qf debug buy` workaround preserved.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §5 (core step), §13.5 (Slice E authoring detection), and §14.6 (Slice G GC navigation).
- Happy paths: 9 scenarios (A1, A2, A3, A4, B3, B4, C4, D1, E1) + Slice E: 9 (PU1, PU2, PU3, PL1, PL2, PI1, PI2, PF1, PO1, PE1)
- Edge cases: 9 scenarios (A5, B1, B2, B5, C3, D2, D3, E2, E3) + Slice E: PU5, PL3, PL4, PL5, PL6, PI5, PF2, PO4, PO5, PE2
- Error/await/negative cases: 6 scenarios (C1, C2, F1, F2, F3, F4) + Slice E: PU4, PI3, PI4, PO3, PE3
- Slice G adds: Happy paths — G-A1, G-D1, G-S1, G-S4, G-O1, G-F9 (6). Edge cases — G-E1..G-E3, G-A2, G-A3, G-D2, G-D3, G-S2, G-S3, G-S5, G-S6, G-O2, G-O3, G-F5, G-F8 (15). Error/await/negative — G-F1..G-F4, G-F6..G-F7 (6).
- Expected total: ~24 core tests (~15 QuestForge.Engine.Tests, ~3 Schema.Tests, ~3 Adapters.Tests, ~4 Validator.Tests) + ~31 Slice E tests (~22 QuestForge.Engine.Tests / plugin-tracing tests for observer+aggregator+inference+factory+modal, ~9 QuestForge.Tools.Trace.Tests for the offline mirror) + ~27 Slice G tests (~3 Schema.Tests, ~3 Engine.Tests engine arm, ~3 Adapters.Tests FakeVendor, ~9 Validator.Tests, ~6 Engine.Tests authoring aggregator+factory+modal, ~3 Tools.Trace.Tests offline mirror).
