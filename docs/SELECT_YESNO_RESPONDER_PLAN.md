# SelectYesno Responder Plan: event-driven Yes/No answering

**Status:** ready for test creation
**Branch:** `fix/cutscene-yesno` (off `main`)
**Input docs:** this plan; `docs/SPIKE_NOTES.md` (TextAdvance/AddonLifecycle behavior), `docs/ADAPTERS.md` §IInteractor
**Output (behavior change):** With TextAdvance off, the plugin answers the `SelectYesno` popup itself — *including inside a non-skippable cutscene* (`WatchingCutscene78`) — by listening for the addon's `PostSetup` event instead of waiting for the per-action poll. Quest 65847 ("Are you prepared for carnage?") advances without a manual click.

---

## Problem statement (confirmed in-game)

Quest 65847 raises a `SelectYesno` (`[1]=Yes / [2]=No`) *inside* a non-skippable cutscene. Today, yesno answering is bolted onto the engine's per-action poll:

- `DialogueChoiceDispatcher.TryDispatch` answers the authored choice, but **only** when the `Interact` action runs and only if `selectYesnoOpen` was sampled true that tick.
- `DalamudInteractor.AdvanceDialogue` (~L65-83) force-clicks **Yes** unconditionally, but only on `Engage`/`Navigate`/`Wait`/`HandOver` ticks.

Neither path fires reliably while `WatchingCutscene78` is active: the engine is transitioned away from `Interact`, and the tick-loop poll does not coincide with the popup. We proved via a debug command that firing our exact callback **works mid-cutscene** (the quest advanced) — so the game accepts the callback; the bug is that the engine never *fires* it.

Two root causes:
1. **No event source.** Answering is gated on the tick loop, not on the addon's construction.
2. **`close:false`.** `FireCallback(1, values)` defaults to `close:false` — it *registers* the answer but does not dismiss the addon. A real click uses `close:true`.

---

## Architectural decisions (read before coding)

### D1 — Split the responder into a PURE decision and a Dalamud SHELL

The untestable part (the `IAddonLifecycle` listener + `FireCallback`) is a thin shell. The *decision* — "should we answer this SelectYesno, and with what?" — is pure and unit-tested.

**Pure decision lives in the engine** (`QuestForge.Engine`, Dalamud-free):

```csharp
namespace QuestForge.Engine.Dialogue;

public enum YesNoAnswer { Yes, No }

// What the responder needs to know about the current run, supplied by the shell.
public readonly record struct YesNoContext(bool RunActive, YesNoAnswer? AuthoredAnswer);

public static class SelectYesnoDecider
{
    /// <summary>
    /// Decide whether to answer an open SelectYesno popup and with what.
    /// Returns null when we must NOT answer (no run active) — the user/other plugins own it.
    /// When a run is active: honor the authored answer; default to Yes when none is authored
    /// (subsumes the legacy always-Yes AdvanceDialogue behavior; all current quests are Yes).
    /// </summary>
    public static YesNoAnswer? Decide(YesNoContext ctx)
        => ctx.RunActive ? (ctx.AuthoredAnswer ?? YesNoAnswer.Yes) : null;
}
```

**Why a `null` return rather than a bool + answer:** a single nullable encodes the full decision (don't-answer vs answer-Yes vs answer-No) and is trivially testable. The shell maps `null → no-op`, `Yes → FireCallbackInt(0)`, `No → FireCallbackInt(1)`.

**Why this lives in the engine, not the plugin:** the engine project is the testability boundary and already references `QuestForge.Schema`. Putting `SelectYesnoDecider` + `YesNoContext` here means the GWT tests sit in `QuestForge.Engine.Tests` alongside `DialogueChoiceDispatcherTests`, with no Dalamud reference. `YesNoAnswer`/`YesNoContext` carry no Dalamud types.

**What breaks if violated:** if the decision logic is written inside the plugin-layer shell (which references Dalamud), it becomes untestable in CI and the run-active/default-Yes/authored-No matrix goes unverified — exactly the regression surface we are trying to lock down.

### D2 — The answer-source seam: `EngineHost.CurrentYesNoAnswer`

The responder needs the active step's authored Yes/No *the instant the popup opens*, synchronously, on the framework thread. The seam is a synchronous property on `EngineHost` (plugin layer), backed by a synchronous query into the engine.

```csharp
// QuestForge.Engine — QuestEngine exposes the active step's authored yesno, Dalamud-free.
// Returns null when there is no active step or the step authors no yesno choice.
public YesNoAnswer? CurrentYesNoAnswer { get; }   // computed from the last-resolved step

// QuestForge.Plugin — EngineHost surfaces run state + the engine's answer to the shell.
public bool IsRunActive => _engine is not null;          // already exists
public YesNoAnswer? CurrentYesNoAnswer => _engine?.CurrentYesNoAnswer;
```

**How the engine computes it (Dalamud-free):** `QuestEngine` already tags every `Interact`/`Wait` action with `Origin: step` (`EngineAction.Interact(.., Origin: step)`). The engine caches the last resolved step (`_lastResolvedStep`, set in `ResolveAction` right before returning) and derives the authored answer from it:

```csharp
private Step? _lastResolvedStep;   // set at the end of ResolveAction each tick

public YesNoAnswer? CurrentYesNoAnswer => ExtractYesNo(_lastResolvedStep);

private static YesNoAnswer? ExtractYesNo(Step? step)
{
    DialogueChoice[] choices = step switch
    {
        TalkStep t      => t.DialogueChoices,
        TurnInStep ti   => ti.DialogueChoices,
        TravelStep tr   => tr.RouteHint?.NpcDialogue?.DialogueChoices ?? [],
        _               => []
    };
    foreach (var c in choices)
        if (string.Equals(c.Type, "yesno", StringComparison.OrdinalIgnoreCase))
            return string.Equals(c.Answer, "no", StringComparison.OrdinalIgnoreCase)
                ? YesNoAnswer.No : YesNoAnswer.Yes;
    return null;  // no authored yesno → responder defaults to Yes via SelectYesnoDecider
}
```

**Why a property and not "consume the next pending choice":** the popup can open mid-cutscene with no preceding `Interact` tick, so we cannot rely on `_dialogueChoiceProgress`. The authored answer is a *property of the active step*, not a position in a per-tick cursor. Reading the step's first `yesno` choice is idempotent and order-independent — exactly what an event handler that may fire at any moment needs.

**Why synchronous:** `IAddonLifecycle` `PostSetup` callbacks run on the framework thread; all Phase 6 adapters are synchronous (`Task.FromResult`). The seam is a pure property read — no `await`, no parking across frames.

**Multiple authored yesno choices in one step:** out of scope. No current quest authors more than one yesno per step. `ExtractYesNo` returns the *first* yesno choice; a follow-up (tracked separately) can add positional tracking if a quest ever needs it. The validator already constrains `answer ∈ {yes,no}` (`structural/dialogue-choice-answer-invalid`).

### D3 — The shell: `SelectYesnoResponder` in `QuestForge.Plugin`

A new plugin-layer class owns the Dalamud wiring. It is constructed in `Plugin.cs` and lives for plugin lifetime; the listener is registered once at construction and disposed once at plugin teardown. Gating is done *inside the handler* via `EngineHost.IsRunActive` — not by register/unregister churn on run start/end.

```csharp
// QuestForge.Plugin/Interaction/SelectYesnoResponder.cs
public sealed class SelectYesnoResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;

    public SelectYesnoResponder(EngineHost host, IAddonLifecycle addonLifecycle,
        IGameGui gameGui, IPluginLog log)
    {
        _host = host; _addonLifecycle = addonLifecycle; _gameGui = gameGui; _log = log;
        // PostSetup fires the instant the addon is constructed — independent of any tick loop.
        // This is why it answers mid-cutscene where the per-action poll never coincides.
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSetup);
    }

    // Poll-once entry point: call on run start to catch a popup already open before we registered.
    public void TryAnswerOpenPopup()
    {
        var p = _gameGui.GetAddonByName("SelectYesno");
        if (!p.IsNull && p.IsReady) Answer(p.Address);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => Answer(args.Addon);

    private unsafe void Answer(nint addonAddr)
    {
        var decision = SelectYesnoDecider.Decide(
            new YesNoContext(_host.IsRunActive, _host.CurrentYesNoAnswer));
        if (decision is not { } answer) return;        // null → not our popup; leave it for the user

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return; // re-open / teardown guard
        // close:true dismisses the addon (a real click does this); idempotency comes from the
        // addon closing on the first fire — a second PostRefresh sees IsVisible flip and no-ops.
        addon->FireCallbackInt(answer == YesNoAnswer.Yes ? 0 : 1);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSetup);
    }
}
```

**Lifetime — plugin-wide, gated in the handler (not register/unregister per run).** Rationale:
- Registration/disposal is the error-prone part (must run on the framework thread, must be paired). Doing it once at plugin construct/dispose minimizes the leak/double-register surface.
- The handler is cheap and self-gating: `Decide` returns `null` the instant no run is active, so the listener is inert outside a run. We never answer a popup the user raised manually.

**`PostRefresh` in addition to `PostSetup`:** some addons are constructed once and refreshed for a second prompt without a fresh `PostSetup`. Registering both closes that gap. Idempotency holds because `FireCallbackInt(.., close:true)` closes the addon and the `IsVisible` guard rejects a stale second fire.

**Wiring in `Plugin.cs`:** `IAddonLifecycle` is a standard Dalamud service — add it to the `Plugin` constructor parameter list and to `PluginServices` (alongside `IGameGui`). Construct `SelectYesnoResponder` after `_host`, dispose it in `Plugin.Dispose()` before `_host.Dispose()`. Call `_responder.TryAnswerOpenPopup()` from `EngineHost.BeginRun` (or have `BeginRun` invoke a host-held responder) to catch an already-open popup at run start.

**`IGameGui` availability:** already present in `PluginServices` and the `Plugin` constructor — no new service beyond `IAddonLifecycle`.

### D4 — Retire the old yesno poll paths (responder is authoritative)

To prevent double-handling, the responder becomes the **only** thing that answers `SelectYesno`:

1. **`DalamudInteractor.AdvanceDialogue`** — remove the leading `SelectYesno` branch (~L67-83). `AdvanceDialogue` returns to advancing only the `Talk` addon. (The responder, not the poll, now confirms yesno.)
2. **`DialogueChoiceDispatcher.TryDispatch`** — remove the `yesno` branch (L28-35) and the `selectYesnoOpen` parameter. `TryDispatch` keeps only `list` handling. Update `EngineHost.DispatchAction` `Interact` case to drop the `syn`/`selectYesnoOpen` plumbing (L306, L313).
3. **`DalamudInteractor.ConfirmYesNoPrompt`** — no longer called by any live path. Keep the method (still on `IInteractor`) but it is dead for now; mark it for removal in the follow-up. *If* a fallback is desired, leave it but it is never invoked by EngineHost.

**Why remove rather than keep a fallback:** the responder fires on `PostSetup` *before* any tick poll could, and dismisses with `close:true`. A surviving poll path would, at best, see an already-closed addon (`IsReady` false → no-op) — harmless but noise — and at worst, race a re-opened addon and fire the wrong answer. The single-owner rule eliminates the double-fire question entirely. No double-fire is possible because only one component fires and it closes the addon.

**Idempotency guarantee:** one owner (`SelectYesnoResponder`); `close:true` dismisses on first fire; `IsVisible` guard rejects a stale `PostRefresh`. `Decide` returning `null` outside a run means nothing fires when no automation is active.

### D5 — `close:true` everywhere we fire SelectYesno

The responder uses `FireCallbackInt(0|1)`. NOTE: `AtkUnitBase.FireCallbackInt` is a *distinct native function*, NOT a managed wrapper over `FireCallback(.., close:true)` (the IL resolves a separate native pointer and passes only the value). Whether it closes the addon is native behavior not determinable from managed code — but in-game validation on quest 65847 CONFIRMED it dismisses the SelectYesno (the popup is answered and gone before it visibly renders). If a future case shows it does NOT dismiss, switch to `FireCallback(1, values, close:true)`. The legacy `close:false` paths are removed under D4, so this is the only fire site.

### D6 — Do NOT touch the cutscene-skip path

`TryCutsceneSkipConfirm` (EngineHost ~L482) clicks the `SelectString` *skip-confirmation* dialog and is gated on `OccupiedInCutSceneEvent` (skippable cutscenes only). It targets a *different addon* (`SelectString`) than the responder (`SelectYesno`) and must remain unchanged. The non-skippable case (`WatchingCutscene78`) is exactly where the responder adds value — and it never raises a SelectString skip dialog, so there is no overlap.

---

## Scope / slicing recommendation

**One slice: SelectYesno only.** This is the reported bug, the in-game-proven fix, and the highest-frequency popup. Build:
- `SelectYesnoDecider` + `YesNoContext` + `YesNoAnswer` (engine, pure, tested)
- `QuestEngine.CurrentYesNoAnswer` + `EngineHost.CurrentYesNoAnswer` (seam)
- `SelectYesnoResponder` (plugin shell, untested)
- Retire old yesno poll paths (D4)

**Follow-up (do NOT build now):** `SelectString` / `SelectIconString` list choices share the same tick-poll fragility — `DialogueChoiceDispatcher`'s `list` branch only runs on the `Interact` action and would miss a list popup raised mid-cutscene. The same event-driven pattern (a `SelectStringResponder` listening on `PostSetup` for `SelectString`/`SelectIconString`, deciding via a pure `SelectStringDecider` that reads the active step's `list` answer index) applies. Defer because: (a) no confirmed in-game failure for lists-in-cutscenes; (b) list answers are positional (need the `_dialogueChoiceProgress` cursor reasoning), a larger pure-decision design than the boolean yesno; (c) keeping this slice minimal de-risks the cutscene fix. File the follow-up referencing this plan's D-decisions.

---

## Given-When-Then specifications (pure decision — `QuestForge.Engine.Tests`)

All tests target `SelectYesnoDecider.Decide(YesNoContext)` and `QuestEngine.CurrentYesNoAnswer` / `ExtractYesNo`. No Dalamud types. Place in `QuestForge.Engine.Tests/Dialogue/SelectYesnoDeciderTests.cs`.

### Group A — `SelectYesnoDecider.Decide` (the core matrix)

1. **Run inactive → don't answer.**
   Given `new YesNoContext(RunActive: false, AuthoredAnswer: YesNoAnswer.Yes)`
   When `Decide(ctx)`
   Then result is `null` (we never answer when no run is active, even with an authored answer).

2. **Run active + authored Yes → Yes.**
   Given `new YesNoContext(true, YesNoAnswer.Yes)` When `Decide` Then `YesNoAnswer.Yes`.

3. **Run active + authored No → No.**
   Given `new YesNoContext(true, YesNoAnswer.No)` When `Decide` Then `YesNoAnswer.No`.

4. **Run active + no authored answer → default Yes.**
   Given `new YesNoContext(true, AuthoredAnswer: null)` When `Decide` Then `YesNoAnswer.Yes` (subsumes legacy always-Yes).

5. **Run inactive + no authored answer → don't answer.**
   Given `new YesNoContext(false, null)` When `Decide` Then `null`.

### Group B — `ExtractYesNo` / `QuestEngine.CurrentYesNoAnswer` (answer source)

6. **TalkStep with `yesno`/`yes` → Yes.**
   Given a `TalkStep` whose `DialogueChoices` contains `new DialogueChoice("yesno", Answer: "yes")`
   When `ExtractYesNo(step)` Then `YesNoAnswer.Yes`.

7. **TalkStep with `yesno`/`no` → No.**
   Given `DialogueChoice("yesno", Answer: "no")` When `ExtractYesNo` Then `YesNoAnswer.No`.

8. **Answer casing is ignored.**
   Given `DialogueChoice("yesno", Answer: "No")` When `ExtractYesNo` Then `YesNoAnswer.No`
   (and `Type: "YESNO"` → still recognized).

9. **TalkStep with only a `list` choice → null.**
   Given a `TalkStep` whose only choice is `new DialogueChoice("list", Answer: "0")`
   When `ExtractYesNo` Then `null` (so the responder defaults to Yes via `Decide`).

10. **TurnInStep with `yesno`/`yes` → Yes.**
    Given a `TurnInStep` with `DialogueChoice("yesno", Answer: "yes")` When `ExtractYesNo` Then `Yes`.

11. **TravelStep with NpcDialogue `yesno`/`no` → No.**
    Given a `TravelStep` whose `RouteHint.NpcDialogue.DialogueChoices` has `DialogueChoice("yesno", Answer:"no")`
    When `ExtractYesNo` Then `No`.

12. **Step with no choices → null.**
    Given a `TalkStep` with empty `DialogueChoices` When `ExtractYesNo` Then `null`.

13. **Non-dialogue step (e.g. `CombatStep`) → null.**
    Given a `CombatStep` When `ExtractYesNo` Then `null`.

14. **Null step → null.**
    Given `(Step?)null` When `ExtractYesNo` Then `null`.

15. **First yesno wins when a step mixes list then yesno.**
    Given a `TalkStep` with choices `[ list/"0", yesno/"no" ]`
    When `ExtractYesNo` Then `YesNoAnswer.No` (the `list` is ignored; the `yesno` resolves).

### Group C — end-to-end pure composition (decider over extracted answer)

16. **Active run + step authors No → Decide returns No.**
    Given `RunActive: true` and a `TurnInStep` authoring `yesno`/`no`
    When `Decide(new YesNoContext(true, ExtractYesNo(step)))` Then `YesNoAnswer.No`.

17. **Active run + step authors nothing → Decide returns Yes (the 65847 default case).**
    Given `RunActive: true` and a `TalkStep` with no yesno choice
    When `Decide(new YesNoContext(true, ExtractYesNo(step)))` Then `YesNoAnswer.Yes`.

---

## Implementation order

**Phase A — pure decision (engine, TDD) — ~0.5 day**
1. Add `YesNoAnswer`, `YesNoContext`, `SelectYesnoDecider` to `QuestForge.Engine/Dialogue/`.
2. Add `QuestEngine._lastResolvedStep` (set at end of `ResolveAction`), `CurrentYesNoAnswer`, `ExtractYesNo`.
3. Make GWT Groups A/B/C green. ← **gate: all 17 pure tests pass before touching plugin code.**

**Phase B — seam + shell (plugin, untested) — ~0.5 day**
4. `EngineHost.CurrentYesNoAnswer => _engine?.CurrentYesNoAnswer`.
5. Add `IAddonLifecycle` to `Plugin` ctor + `PluginServices`.
6. `SelectYesnoResponder` (D3); construct/dispose in `Plugin.cs`; call `TryAnswerOpenPopup` from `BeginRun`.

**Phase C — retire old paths (D4) — ~0.5 day**
7. Remove `SelectYesno` branch from `DalamudInteractor.AdvanceDialogue`.
8. Remove `yesno` branch + `selectYesnoOpen` param from `DialogueChoiceDispatcher`; update `DialogueChoiceDispatcherTests` (drop the yesno-dispatch assertions) and `EngineHost.DispatchAction` `Interact` case.
9. Confirm `ConfirmYesNoPrompt` has no live callers.

**Phase D — in-game verification (manual)**
10. TextAdvance off; run 65847; confirm the carnage prompt auto-answers Yes mid-cutscene and the quest advances.

Done-before-next: A before B before C. Phase A is the only one with CI-visible tests.

---

## Done criteria

1. `SelectYesnoDecider.Decide` and `ExtractYesNo` are pure (`QuestForge.Engine`, no Dalamud reference) and the 17 GWT tests pass in CI.
2. `DialogueChoiceDispatcher` no longer has a `yesno` branch or `selectYesnoOpen` parameter; `DialogueChoiceDispatcherTests` compile and pass against the reduced surface.
3. `DalamudInteractor.AdvanceDialogue` no longer answers `SelectYesno`.
4. Exactly one component (`SelectYesnoResponder`) fires the SelectYesno callback, with `close:true`.
5. In-game (manual): with TextAdvance off, quest 65847's mid-cutscene carnage prompt is answered Yes automatically and the quest advances; no manual click required.
6. No regression: skippable-cutscene skip (`TryCutsceneSkipConfirm` / `OccupiedInCutSceneEvent`) and `Talk`/list dialogue still behave as before.

---

## What this slice does NOT include

- `SelectString` / `SelectIconString` event-driven responder (follow-up; see Scope/slicing).
- Multiple distinct yesno prompts within a single step (positional tracking) — first yesno wins.
- Live `TraceMode` switching or trace events for responder fires (the engine still emits its per-tick decision events; the responder is a shell with no trace surface).
- Removing `IInteractor.ConfirmYesNoPrompt` from the interface (kept dead for now; remove in follow-up).

---

## Open question for the user's gate

The plan **removes** the legacy yesno poll paths (D4) rather than keeping one as a fallback, on the grounds that a single owner is the only way to guarantee no double-fire. If you would prefer a belt-and-suspenders fallback (keep `ConfirmYesNoPrompt` wired on the `Interact` tick as a backstop if the lifecycle listener ever fails to register), say so — but note it reintroduces the double-fire race the single-owner rule eliminates. Default recommendation: remove.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then.
- Happy paths: 4 scenarios (A2, A3, A4, C17)
- Edge cases: 11 scenarios (B6–B15, C16)
- Error/guard cases: 2 scenarios (A1, A5 — run-inactive don't-answer)
- Expected total: ~17 tests in QuestForge.Engine.Tests (Dialogue/SelectYesnoDeciderTests.cs)
