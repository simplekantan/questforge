# DalamudEmoteExecutor Implementation Plan

**Status:** ready for test creation

**Input docs:**
- `docs/USE_EMOTE_STEP_PLAN.md` (engine slice this builds on — `IEmoteExecutor`, `EmoteCommandResolver`, `EngineAction.UseEmote`, `ResolveUseEmote` already shipped in PR #104)
- `docs/USE_ACTION_DALAMUD_PLAN.md` (closest analog — DAD-1 through DAD-11; mirror its decisions wherever applicable)
- `QuestForge.Adapters/Emotes/IEmoteExecutor.cs` (the interface to implement)
- `QuestForge.Adapters/Emotes/EmoteCommandResolver.cs` (the pure helper shipped in PR #104; covered by UE16–UE18)
- `QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs` (the exact pattern to mirror; the `am == null` guard, ObjectTable scan, `Result.Fail("targetNotFound", …)` posture all transfer 1:1)
- `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` (mapping-helper-in-Dalamud-bound-assembly precedent — see Decision DED-3 for why we do NOT add a sibling here)
- `QuestForge.Adapters.Dalamud/Movement/DalamudMount.cs` (thin-adapter precedent — fire-and-forget native call, no return-value check)
- `QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs:23-44` (ObjectTable scan + `TargetManager.Target` pattern)
- `QuestForge.Adapters.Dalamud/Scheduling/LuminaQuestDataProvider.cs:24-29` (Lumina sheet preload at adapter construction — the closer analog than `AetheryteZoneMap.Populate`)
- `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs:334` (existing `dataManager.GetExcelSheet<Lumina.Excel.Sheets.X>()` call site — confirms the API shape)
- `QuestForge.Adapters.Dalamud/PluginServices.cs` (the `IDataManager`, `IObjectTable`, `ITargetManager` fields used)
- `QuestForge.Plugin/EngineHost.cs:44` (existing `_actionExecutor` field declaration — the position-precedent for `_emoteExecutor`)
- `QuestForge.Plugin/EngineHost.cs:109` (ctor construction of `_actionExecutor` — position-precedent)
- `QuestForge.Plugin/EngineHost.cs:208-213` (engine ctor; `actionExecutor:` named-arg placement — `emoteExecutor:` slots right after)
- `QuestForge.Plugin/EngineHost.cs:447-456` (`UseAction` dispatch arm — the exact arm shape to mirror, including debounced log, `IsNavigating` guard, `TryCutsceneSkipConfirm`)
- `QuestForge.Plugin/EngineHost.cs:280-314` (lazy-dismount hook — the existing `not Navigate and not Teleport` condition already handles `UseEmote`; **no code change needed there**)
- `QuestForge.Engine/QuestEngine.cs:84-88,873-884` (already-shipped `_emoteExecutor` field, ctor param, `ResolveUseEmote` method — engine side is done)
- `QuestForge.Engine/EngineAction.cs:37-41` (already-shipped `EngineAction.UseEmote` record — engine side is done)
- `C:\Users\publi\RiderProjects\Lifestream\ECommons\ECommons\Automation\Chat.cs:68-81` — `Chat.SendMessage(string)` is the canonical entry point. Throws `ArgumentException` (empty / >500 bytes / invalid characters) and `InvalidOperationException` (signature not found).
- `C:\Users\publi\RiderProjects\Questionable\Questionable\Functions\ChatFunctions.cs:25-31` — **observed behavior only** (Questionable is AGPL; we read it to learn the contract, we do NOT crib code per the clean-room instruction). Actionable facts:
  - The Lumina preload pattern is `dataManager.GetExcelSheet<Emote>().Where(x => x.RowId > 0).Where(x => x.TextCommand.IsValid).Select(...).Where(...startsWith '/').ToDictionary(...)`.
  - Questionable extracts the command via `.TextCommand.Value.Command.ToString()`. **QuestForge convention is `.ExtractText()`** (per `QuestForge.Plugin/Commands/QfCommand.cs:360` and `QuestForge.Plugin/Tracing/DalamudAddonProbe.cs:78` — both use `.ExtractText()` to materialise `ReadOnlySeString`). We use `.ExtractText()` here for consistency.
  - The conventional invocation is `"<command> motion"` for chat-suppressed playback (matches Decision UE2 / UE16 in the engine plan).

**Output (CI behavior):** Adding `_emoteExecutor = new DalamudEmoteExecutor(services)` to `EngineHost` and a `case EngineAction.UseEmote:` arm to `DispatchAction` makes `UseEmoteStep` actually fire in-game. The engine's `ResolveUseEmote` no longer returns the `AwaitUser("UseEmoteStep dispatched but no IEmoteExecutor wired …")` fallback when `EngineHost` is the host. **No new automated tests are added by this slice** — the pure logic (`EmoteCommandResolver`) is already covered by UE16–UE18 in `QuestForge.Adapters.Tests`, and the Dalamud-bound shell is validated by in-game smoke. The smoke test on the corpus's first emote-bearing quest verifies the end-to-end path (engine → adapter → ECommons → game).

---

## Dependency graph

```
QuestForge.Adapters.Dalamud
   └── Emotes/DalamudEmoteExecutor.cs (NEW — uses ECommons.Automation.Chat + Lumina Emote sheet)
         └── consumed by ↓
QuestForge.Plugin
   └── EngineHost (field + ctor construct + BeginRun arg + DispatchAction arm)
```

**Build order:**
1. `DalamudEmoteExecutor` shell (Lumina preload in ctor, `UseEmote` body delegates to `EmoteCommandResolver` for command construction, scans ObjectTable for target, sends via `Chat.SendMessage` inside try/catch).
2. `EngineHost` field declaration after `_actionExecutor` (line 44).
3. `EngineHost` ctor construction after the `_actionExecutor = …` line (line 109).
4. `EngineHost.BeginRun` engine ctor gains `emoteExecutor: _emoteExecutor` (line 213-ish).
5. `EngineHost.DispatchAction` gains `case EngineAction.UseEmote ue:` between the UseAction (line 447) and Wait (line 458) cases.
6. Manual in-game smoke.

Total: 1 new file (~75 lines), 4 surgical edits to `EngineHost.cs`. No test-project changes; no engine-side changes.

---

## Architectural decisions (read before coding)

### Decision DED-1 — There is NO new pure-logic seam in this slice

The previous slice (`USE_ACTION_DALAMUD_PLAN.md`) had a non-trivial pure-logic surface: status-code interpretation, epsilon-bounded cooldown math, error-message formatting — together ~12 unit tests. This slice has nothing comparable.

**What's pure but already extracted (covered by UE16–UE18):**
- Command-string construction (`"/cheer" + " motion"`) — `EmoteCommandResolver.Resolve`.
- Leading-slash defensive check — same.
- `Result.Fail("emoteCommandNotFound", …)` shape on null lookup — same.
- `Result.Fail("emoteCommandMalformed", …)` shape on non-slash command — same.

**What's left in `DalamudEmoteExecutor` and is NOT pure-logic seam-worthy:**

| Bit | Why it's not a unit-testable seam |
|---|---|
| Lumina preload (`LoadEmoteCommands(IDataManager)`) | Operates on `Lumina.Excel.Sheets.Emote` rows. The "skip rows where `TextCommand.IsValid == false` or `Command` is null-or-empty or doesn't start with `/`" filter is conceptually pure, but the input type (`IEnumerable<Emote>` where `Emote` is a Lumina row struct) is Dalamud-bound. Extracting a `FilterEmoteRowCandidates(IEnumerable<(uint id, bool isValid, string? cmd)> rows)` helper would (a) require a test-only mock of the input shape that has no real correspondence to anything else in the codebase and (b) be one for-loop with three guards — the cost/benefit is wrong. |
| ObjectTable scan | Iterates `_svc.ObjectTable`. The filter (`BaseId == id && ObjectKind in {EventNpc, BattleNpc}`) is identical to `DalamudActionExecutor.UseAction`'s and `DalamudInteractor.InteractWith`'s. Already covered indirectly by their smoke tests. No new logic. |
| `Chat.SendMessage(command)` invocation | One static call. The `try/catch → Result.Fail("chatSendFailed", …)` shape is the only branchable code, but the only thing being tested is "exception thrown ⇒ Result.Fail" which is a tautology of the try/catch itself. A unit test for `MapChatException(Exception) → Result.Fail("chatSendFailed", …)` would be one assert against a hand-thrown `InvalidOperationException`; the test would be longer than the helper. |
| Target acquisition order | "set `TargetManager.Target` then send command" — same as `DalamudInteractor.InteractWith` (lines 33-39). No branching; not testable in isolation. |

**Recommendation:** no new test files. The pure-helper tests already shipped in `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` (UE16–UE18) are the unit-test surface for this entire slice; in-game smoke covers the rest.

**What breaks if violated:** if someone insists on adding a `FilterEmoteRowCandidates` helper to chase coverage, they will (a) need to invent a new tuple type as an indirection that no other code uses, (b) test logic that is one `if` statement, and (c) leave the actual integration risk (Lumina API shape changes between SDK versions) untested anyway because the mock doesn't exercise the real sheet shape. Better to spend the equivalent five minutes on careful smoke verification.

### Decision DED-2 — Mirror `DalamudActionExecutor` shell structure exactly

The `DalamudEmoteExecutor` shell is structurally identical to `DalamudActionExecutor.UseAction` minus the status-read pathway. Mirror the same code organisation:

1. `ct.ThrowIfCancellationRequested()` first.
2. Resolve the chat command via the pure helper before any Dalamud work (early-fail on `emoteCommandNotFound` / `emoteCommandMalformed` is cheap and saves an ObjectTable iteration on bad input).
3. If `targetNpcId is { } id`, scan ObjectTable filtered to `EventNpc | BattleNpc`, fail with `Result.Fail("targetNotFound", …)` on miss, otherwise assign `TargetManager.Target`.
4. Submit via `Chat.SendMessage(command)` inside `try/catch`.
5. Return `Result.Ok()`.

**Why command-resolve before ObjectTable scan:** a malformed quest with an unknown `emoteId` (e.g. `99999`) should fail loudly without touching the ObjectTable. Reversing the order would scan first, target an NPC, then bail on the command — leaving `TargetManager.Target` clobbered for no reason.

**Why no `am == null` analog:** there is no `Chat.Instance()` to null-check. `Chat` is a static class. The closest equivalent is `Chat.SendMessage` throwing `InvalidOperationException("signature not found")` — handled by the try/catch in step 4. No separate pre-check needed.

**What breaks if violated:** ObjectTable-before-resolve would set `TargetManager.Target` on bad input, which is a UX papercut (the user's manually acquired target gets clobbered for no reason).

### Decision DED-3 — Lumina Emote sheet preloaded **once at adapter construction**

Pinned by Decision UE9 in the engine plan; restating with the Dalamud-side concrete shape:

```csharp
private readonly IReadOnlyDictionary<uint, string> _emoteCommands;

public DalamudEmoteExecutor(PluginServices svc)
{
    _svc = svc;
    _emoteCommands = LoadEmoteCommands(svc.DataManager);
}

private static IReadOnlyDictionary<uint, string> LoadEmoteCommands(IDataManager dataManager)
{
    var dict = new Dictionary<uint, string>();
    var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
    if (sheet is null) return dict; // defensive — should never happen post-init
    foreach (var row in sheet)
    {
        if (row.RowId == 0) continue;
        if (!row.TextCommand.IsValid) continue;
        var cmd = row.TextCommand.Value.Command.ExtractText();
        if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('/')) continue;
        dict[row.RowId] = cmd;
    }
    return dict;
}
```

**Why prefer foreach over `.Where(...).ToDictionary(...)` LINQ:** matches the existing `DalamudGameStateProvider.GetAttunedAetherytes` (line 334-345) shape; one allocation, no IEnumerable wrappers. Questionable's LINQ form is fine too but the foreach form is one line shorter when the predicate fans out to four conditions.

**Why `sheet is null` defensive check:** other Lumina call sites in QF (`DalamudGameStateProvider.cs:336`, `LuminaQuestDataProvider.cs:27`) handle null differently — the latter throws `InvalidOperationException`, the former silently returns an empty list. For an emote sheet (small, cosmetic, no downstream invariants), the empty-dict fallback is the friendlier posture: the resolver will return `emoteCommandNotFound` for every emote, which surfaces cleanly as a dispatch failure rather than crashing plugin load.

**Why static, not injected, not lazy:** the Lumina sheet does not change at runtime within a single plugin session. One-time construction is correct; lazy would surface "Lumina unavailable" per-call rather than at load (worse error-surface posture per the rationale in engine-plan Decision UE9). Injection would be ceremony for a sheet that is functionally a constant.

**Capacity estimate:** the Emote sheet currently has ~270 rows; after filtering for `IsValid` + slash-prefixed `Command`, the dictionary holds roughly ~80 entries. Negligible allocation; not worth pre-sizing.

### Decision DED-4 — `Chat.SendMessage` is wrapped in `try/catch` that maps all exceptions to `Result.Fail("chatSendFailed", …)`

```csharp
try
{
    Chat.SendMessage(command);
}
catch (Exception ex)
{
    return Task.FromResult<Result<Unit>>(
        Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
}
```

**Exception inventory (per `Chat.cs:65-67`):**
- `ArgumentException` — empty message / > 500 bytes / invalid characters. Cannot happen for a well-formed emote command (always non-empty, always ASCII, well under 500 bytes), but the catch surfaces it if a Lumina row ever returns junk.
- `InvalidOperationException` — chat-box signature not found (game patch broke signature; ECommons hasn't updated yet). Plugin-wide failure mode; the engine sees per-call `Result.Fail` and surfaces it cleanly rather than crashing the dispatch loop.
- Any other `Exception` — defensive catch-all for the same reason `DalamudActionExecutor` doesn't try to enumerate every possible `am->UseAction` failure mode.

**Why `catch (Exception ex)` and not specific types:** the exception inventory is short and the recovery is identical for all of them (return `Result.Fail` so the engine can decide retry vs `AwaitUser`). Listing each type would be verbose without changing behavior, and a future ECommons version that adds a new exception type would silently propagate as an unhandled crash rather than a clean `Result.Fail`. The broad catch is the safer posture for adapter shells (matches QF policy: exceptions in adapter code surface as `Result.Fail`, not propagated).

**Stateless-retry interaction:** if `Chat.SendMessage` throws because the player is in a cutscene (chat input gated), the catch returns `Result.Fail("chatSendFailed", …)`. The engine's stateless retry re-emits `UseEmote` next tick. Once the cutscene ends, the next attempt succeeds. **This is the right posture** — no need for an explicit "cutscene → defer" guard because (a) `ResolveUseEmote` already gates on `Casting`, which is usually true during cutscene starts, and (b) the engine's tick cadence (~250 ms) makes the retry loop cheap. See Decision DED-7 for additional discussion.

**What breaks if violated:** narrowing the catch would let an unexpected exception type crash the dispatch loop. Removing the catch would let `ArgumentException("invalid characters")` (e.g. a future Lumina row with junk characters) crash the plugin on first use of that emote.

### Decision DED-5 — Order of operations: resolve, scan, target, send

```csharp
public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? targetNpcId, bool motion, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    // 1. Resolve the chat command (pure; bails on bad emoteId without touching game state).
    var commandResult = EmoteCommandResolver.Resolve(
        emoteId, motion, id => _emoteCommands.TryGetValue(id, out var c) ? c : null);
    if (commandResult is not Result<string>.Success { Value: var command })
    {
        var failure = (Result<string>.Failure)commandResult;
        return Task.FromResult<Result<Unit>>(Result.Fail(failure.Reason, failure.Detail));
    }

    // 2. Acquire target if requested.
    if (targetNpcId is { } id)
    {
        IGameObject? found = null;
        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null || obj.BaseId != id.Value) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
            found = obj;
            break;
        }
        if (found is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail("targetNotFound", $"no object in scene with BaseId {id.Value}"));
        _svc.TargetManager.Target = found;
    }

    // 3. Submit the chat command.
    try
    {
        Chat.SendMessage(command);
    }
    catch (Exception ex)
    {
        return Task.FromResult<Result<Unit>>(
            Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
    }

    return Task.FromResult<Result<Unit>>(Result.Ok());
}
```

**Why this order:** see Decision DED-2. Bailing on bad inputs (step 1) before touching game state (step 2) before the side-effecting submit (step 3) is the canonical fail-fast adapter shape.

**Note on the `Result<T>` failure-mapping line:** the codebase pattern (per `DalamudActionExecutor.UseAction` line 44) uses `ActionStatusInterpreter.FormatTargetNotFoundReason(id.Value)`. We could lift that helper into a shared place and reuse it here, but the call site is one line either way and the message phrasing is locked by the convention "no object in scene with BaseId {x}". Builder may inline (as shown) or call the helper directly — both compile, both produce identical output. Inlining keeps the dependency surface narrow (this shell doesn't otherwise reference `QuestForge.Adapters.Actions`).

**Note on the `Result<string>.Failure` cast:** the codebase's canonical Result-failure-copy idiom needs verification by Builder; if there is a `ToFailureOf<U>()` extension or similar, use it. Worst case: manually reconstruct `Result.Fail<Unit>(reason, detail)` from the two fields as shown above. This is genuinely thin and the exact API is whichever the codebase already exposes; both forms are correct.

### Decision DED-6 — No `RecordingEmoteExecutor` in v1

Mirrors `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-10. The engine already emits a `DecisionEvent` with `ActionType == "UseEmote"` (via the harness `EmitActionSubmitted("UseEmote", …)` arm and the real engine's equivalent trace path). The dispatch is visible in the trace via the engine-side decision event before the adapter is called. Adding a recording-proxy wrapper would be redundant.

If a future debugging case requires per-call adapter-act capture (e.g. "did we actually call into `Chat.SendMessage`?" mid-incident), add `RecordingEmoteExecutor` then. Not in scope.

### Decision DED-7 — No engine-side guard changes; rely on existing `Casting` gate + stateless retry for cutscene/transition cases

`ResolveUseEmote` (engine-side, already shipped per PR #104) gates on `PlayerStateSnapshot.Casting`. This covers:
- Mid-cast: defer until cast finishes.
- Mid-cutscene: the game's `Casting` flag is asserted during cutscene transitions, so the engine defers.

What it does NOT cover:
- Mid-cutscene where `Casting` is false but chat input is still gated by the cutscene UI. `Chat.SendMessage` will throw — handled by the `chatSendFailed` failure path in Decision DED-4.
- Zone transition load screens. Same path.
- Death window (player is dead; chat works but the emote doesn't play). The emote command goes out; the game silently ignores it; the engine's stateless retry re-emits on subsequent ticks until the player is no longer dead AND the predicate satisfies. Not perfect but not a problem — the existing engine death-recovery routing covers the broader case (`InstanceKind` routes determine whether to wait for resurrection or accept a return-to-aetheryte).

**Recommendation:** do NOT add new pre-flight guards in this slice. The combination of `Casting` gate + `chatSendFailed` map + stateless retry is sufficient. If smoke surfaces a real-world cutscene-related loop (engine spam-retries 50 times in 12 seconds while a cutscene rolls), add a `IsInCutscene` predicate to the engine's `ResolveUseEmote` as a follow-up — but that's an engine-side change, not a Dalamud-side one.

**What breaks if violated:** adding more guards in the Dalamud shell would violate the engine purity invariant in spirit (the adapter would be making decisions the engine should make) and would scatter the "when can an emote fire?" logic across two layers.

### Decision DED-8 — EngineHost dispatch arm: between UseAction and Wait

```csharp
case EngineAction.UseEmote ue:
    DebounceLog(
        $"useemote:{ue.EmoteId}:{ue.TargetNpcId?.Value}:{ue.Motion}",
        $"[UseEmote] id={ue.EmoteId}" +
        (ue.TargetNpcId is { } ueTargetId ? $" target={ueTargetId.Value}" : " (self)") +
        $" motion={ue.Motion}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _emoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
    break;
```

**Position:** between `case EngineAction.UseAction ua:` (ends at line 456) and `case EngineAction.Wait:` (starts at line 458). Mirrors the engine plan's `EngineAction` enum order (UseAction precedes UseEmote in `EngineAction.cs`).

**Why stop navigation first:** identical reasoning to UseAction (Decision DAD-9). vnavmesh may still be ticking; `IsNavigating` is a cheap gate; `Stop` prevents the player walking through the emote animation.

**Why `TryCutsceneSkipConfirm`:** matches every other dispatch arm. Quest-related emotes sometimes fire mid-cutscene; skipping the confirm gets the player back faster.

**Why debounced log:** the engine stateless-retries every tick while `Expect` is unmet. A 5-second NPC-reaction wait would dump ~20 `[UseEmote]` lines into the Dalamud log without debounce. The 10-second `DebounceInterval` constant (defined at `EngineHost.cs:96`) deduplicates per `useemote:{emoteId}:{target}:{motion}` key. Same shape as the UseAction arm at line 449.

**Why include `motion` in the dedup key:** if the engine flips `Motion` mid-step (it won't, but a future code path could), the new value should be visible immediately. Including it in the key costs nothing.

**Why no `_lastDispatchedActionWasUseEmote` tracking:** unlike Purchase (which needs deferred shop-close), UseEmote has no follow-up cleanup. Fire-and-forget. The lazy-dismount hook already at line 280 handles "if previous was Navigate, dismount before UseEmote" automatically because `UseEmote` is not in the `not Navigate and not Teleport` exemption (it falls into the dismount-eligible branch). No exemption-list change required (this is the explicit posture from engine plan Decision UE6).

### Decision DED-9 — EngineHost wiring: field declaration, ctor construction, BeginRun arg

**Field declaration** (after `_actionExecutor` at line 44):
```csharp
private readonly DalamudEmoteExecutor _emoteExecutor;
```

**Constructor body** (after `_actionExecutor = new DalamudActionExecutor(services);` at line 109):
```csharp
_emoteExecutor = new DalamudEmoteExecutor(services);
```

**`BeginRun` engine ctor** — modify lines 208-213 from:
```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor);
```

to:
```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor,
    emoteExecutor: _emoteExecutor);
```

After this wiring, `ResolveUseEmote` (engine-side, already shipped) stops returning the `AwaitUser("UseEmoteStep dispatched but no IEmoteExecutor wired …")` fallback and starts dispatching `EngineAction.UseEmote` for the host to consume.

**Why construction once-per-host, not once-per-run:** `DalamudEmoteExecutor` holds the preloaded Lumina dictionary as state. Per-run construction would re-preload on every quest start — wasted work. The dictionary doesn't change at runtime, so once-per-host is correct (matches every other adapter in `EngineHost`).

### Decision DED-10 — Optional `DebugEmoteExecutor` accessor

Mirroring `DebugCombat`, `DebugVendor`, `DebugMount` (lines 133-136), useful for a future `/qf debug emote <id> [<target>]` subcommand. Not required by this slice but a one-line add at low cost:

```csharp
public IEmoteExecutor DebugEmoteExecutor => _emoteExecutor;
```

Builder may include or defer at discretion. The cost is one line; the benefit is the future debug subcommand can avoid wiring through an internal API path.

### Decision DED-11 — Optional `EmoteName` for diagnostic logging is OUT OF SCOPE for v1

The Lumina `Emote` sheet has a `Name` column (`row.Name.ExtractText()` → e.g. `"cheer"`). Including it in the debounced log line would improve user-facing diagnostics:

```text
[UseEmote] cheer (id=17) target=1000789 motion=True
```

vs. the current:

```text
[UseEmote] id=17 target=1000789 motion=True
```

**Tradeoff:** the name lookup requires a second Lumina deref per log line (or, more efficiently, preloading a second `Dictionary<uint, string>` alongside `_emoteCommands`). Either approach costs little. The argument against: the log line is for the developer's eyes, not the user's, and the developer can map `id=17` → `cheer` from any wiki in two seconds. Not worth the indirection.

**Recommendation:** defer. If a future smoke session shows the bare id is hard to read, add a `private readonly Dictionary<uint, string> _emoteNames` field populated in `LoadEmoteCommands` (one extra line, same loop, capture `row.Name.ExtractText()`) and reference it in the debounce log. Trivial follow-up if/when needed.

**What breaks if added eagerly:** nothing breaks per se, but the helper grows from one tuple-keyed dict to two parallel dicts that must stay in lockstep — a minor cohesion smell for no proven benefit.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs` | NEW | The `IEmoteExecutor` shell — Lumina preload + ObjectTable scan + `Chat.SendMessage` |
| `QuestForge.Plugin/EngineHost.cs` | MODIFY | Field declaration (after `_actionExecutor`); ctor construct; `BeginRun` arg; `DispatchAction` case |

Total: 1 NEW file (~75 lines), 4 small edits to one existing file. No test-project changes.

---

## A. Pure-logic seams (per the brief's §A question)

**There is zero new pure-logic surface in this slice.**

Everything pure has been pulled out into `EmoteCommandResolver` (already shipped in PR #104 alongside the engine slice; tests UE16–UE18 in `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs`). What remains in `DalamudEmoteExecutor`:

- Lumina sheet enumeration — Dalamud-bound input type; the filter predicate is one expression, not a seam.
- ObjectTable scan — Dalamud-bound input; identical pattern to two existing shells.
- One static call (`Chat.SendMessage`) — no logic.
- One try/catch — defensive wrap, not a seam.

The brief flagged "`MapChatException(Exception) → Result.Fail(...)`" as a candidate. Considered and rejected: the helper would be one line of body and would have exactly one test (`Assert.Equal("chatSendFailed", MapChatException(new InvalidOperationException("x")).Reason)`) that tests nothing the eye can't verify by reading the catch arm. The cost (a static helper in a project that doesn't have one yet) outweighs the benefit.

The brief also flagged "filtering logic for the Lumina row→dict build" as a candidate. Considered and rejected: extracting `FilterEmoteRowCandidates(IEnumerable<(uint, bool, string?)>)` would require inventing a tuple shape that no other code uses, mock-feed it in tests, and verify that one for-loop with three guards behaves correctly. The real integration risk (Lumina API shape change between SDK versions) is not exercised by this hypothetical test because the mock doesn't use real Lumina types. Smoke is the only meaningful coverage for this filter.

**Decision:** no new unit tests in this slice. UE16–UE18 + in-game smoke is the coverage.

---

## B. `DalamudEmoteExecutor` shell — full source

```csharp
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Emotes;

/// <summary>
/// Issues a single emote text command via ECommons.Automation.Chat.SendMessage.
/// Lumina Emote sheet is preloaded at construction; lookup is O(1) per call.
///
/// Success means "Chat.SendMessage returned without throwing." It does NOT mean
/// "the emote animation played" or "the NPC reacted to it." The engine verifies
/// outcome via the authored Expect predicate (per engine plan Decision UE4).
///
/// Failure cases:
///   - emoteCommandNotFound  — emoteId has no Lumina row, or row has no text command
///   - emoteCommandMalformed — Lumina text command doesn't start with '/' (defensive)
///   - targetNotFound        — targetNpcId supplied but no matching object in ObjectTable
///   - chatSendFailed        — Chat.SendMessage threw (invalid characters, signature lost, etc.)
/// </summary>
public sealed class DalamudEmoteExecutor : IEmoteExecutor
{
    private readonly PluginServices _svc;
    private readonly IReadOnlyDictionary<uint, string> _emoteCommands;

    public DalamudEmoteExecutor(PluginServices svc)
    {
        _svc = svc;
        _emoteCommands = LoadEmoteCommands(svc.DataManager);
    }

    public Task<Result<Unit>> UseEmote(
        uint emoteId,
        NpcId? targetNpcId,
        bool motion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Resolve text command via the pure helper. Bails fast on bad emoteId
        //    BEFORE touching game state (don't clobber TargetManager.Target on bad input).
        var commandResult = EmoteCommandResolver.Resolve(
            emoteId, motion,
            id => _emoteCommands.TryGetValue(id, out var c) ? c : null);

        if (commandResult is not Result<string>.Success { Value: var command })
        {
            var failure = (Result<string>.Failure)commandResult;
            return Task.FromResult<Result<Unit>>(Result.Fail(failure.Reason, failure.Detail));
        }

        // 2. Acquire target if requested. Filter mirrors DalamudActionExecutor.UseAction.
        if (targetNpcId is { } id)
        {
            IGameObject? found = null;
            foreach (var obj in _svc.ObjectTable)
            {
                if (obj is null || obj.BaseId != id.Value) continue;
                if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
                found = obj;
                break;
            }
            if (found is null)
                return Task.FromResult<Result<Unit>>(
                    Result.Fail("targetNotFound", $"no object in scene with BaseId {id.Value}"));
            _svc.TargetManager.Target = found;
        }

        // 3. Submit. ECommons docs (Chat.cs:65-67) document ArgumentException
        //    (empty / too long / invalid chars) and InvalidOperationException
        //    (signature not found). Catch broadly so adapter-shell exceptions
        //    surface as Result.Fail rather than crashing the dispatch loop.
        try
        {
            Chat.SendMessage(command);
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<Unit>>(
                Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
        }

        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    private static IReadOnlyDictionary<uint, string> LoadEmoteCommands(IDataManager dataManager)
    {
        var dict = new Dictionary<uint, string>();
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet is null) return dict; // defensive — should never happen post-init
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (!row.TextCommand.IsValid) continue;
            var cmd = row.TextCommand.Value.Command.ExtractText();
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('/')) continue;
            dict[row.RowId] = cmd;
        }
        return dict;
    }
}
```

**Notes for Builder:**
- The `Result<string>.Failure` cast pattern at line "// 1." — if the codebase already exposes a `ToFailureOf<U>()` extension on `Result<T>`, use it; otherwise the manual reconstruction shown is correct. Builder verifies against `QuestForge.Adapters.Types.Result`'s actual API surface.
- The `using ECommons.Automation;` import is what brings `Chat` into scope. ECommons is already a referenced package in `QuestForge.Adapters.Dalamud.csproj` (per the engine plan's Decision UE16 verification).
- The `using Lumina.Excel.Sheets;` is not strictly needed if the fully-qualified `Lumina.Excel.Sheets.Emote` is used as shown; adding the using is fine if Builder prefers shorter call sites.

---

## C. EngineHost wiring — concrete edits

### C.1 Field declaration

In `EngineHost.cs:44`, after the existing `_actionExecutor` line, add:

```csharp
private readonly DalamudEmoteExecutor _emoteExecutor;
```

### C.2 Constructor body

In `EngineHost.cs:109`, after the existing `_actionExecutor = new DalamudActionExecutor(services);` line, add:

```csharp
_emoteExecutor   = new DalamudEmoteExecutor(services);
```

(Whitespace-aligned with the other ctor assignments in the same block.)

### C.3 `BeginRun` engine construction

Modify `EngineHost.cs:208-213` per Decision DED-9. The named-arg `emoteExecutor:` slots in after `actionExecutor:`.

### C.4 `DispatchAction` switch arm

Insert the arm from Decision DED-8 between the existing `case EngineAction.UseAction ua:` (ends at line 456) and `case EngineAction.Wait:` (starts at line 458):

```csharp
case EngineAction.UseEmote ue:
    DebounceLog(
        $"useemote:{ue.EmoteId}:{ue.TargetNpcId?.Value}:{ue.Motion}",
        $"[UseEmote] id={ue.EmoteId}" +
        (ue.TargetNpcId is { } ueTargetId ? $" target={ueTargetId.Value}" : " (self)") +
        $" motion={ue.Motion}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _emoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
    break;
```

### C.5 (Optional) Debug accessor

Per Decision DED-10, after the existing `DebugMount` accessor at line 136:

```csharp
public IEmoteExecutor DebugEmoteExecutor => _emoteExecutor;
```

Builder may include or defer.

---

## D. Test scenarios

**There are no new automated tests in this slice.**

The pure-helper test surface (UE16, UE17, UE18, optional UE18b) is already shipped in `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` from PR #104. Those tests cover:
- Happy path: lookup returns `"/cheer"`, `motion=true` → `"/cheer motion"` (UE16)
- Happy path: `motion=false` → bare command (UE17)
- Lookup returns null → `Result.Fail("emoteCommandNotFound", …)` (UE18)
- (Optional) lookup returns non-slash-prefixed string → `Result.Fail("emoteCommandMalformed", …)` (UE18b)

The Dalamud-bound surface (`DalamudEmoteExecutor`) is validated by smoke per §E. No engine-side test changes — engine-side dispatch (UE1–UE9) is already covered by `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` against `FakeEmoteExecutor`.

**Why no `DalamudEmoteExecutor` tests:** same rationale as `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-3. The shell logic is Dalamud-bound (ObjectTable, Lumina sheet, ECommons Chat); the existing `QuestForge.Adapters.Tests` project targets net10.0 and cannot reference Dalamud-bound assemblies. Setting up a separate `QuestForge.Adapters.Dalamud.Tests` project is the same out-of-scope item flagged in the action plan; if/when that project exists, the tests to add would be:

- `LoadEmoteCommands_FiltersInvalidRows` — mock Lumina returns a list with one row at id 0, one row with `IsValid=false`, one row with empty command, one row with no leading slash, one row valid. Resulting dict contains only the valid row.
- `UseEmote_BadEmoteId_ReturnsEmoteCommandNotFound_WithoutTouchingObjectTable` — verify the early-bail order. Requires injectable ObjectTable mock; not currently available.
- `UseEmote_TargetNotInObjectTable_ReturnsTargetNotFound` — same.
- `UseEmote_ChatSendThrows_ReturnsChatSendFailed` — requires injectable `Chat.SendMessage` indirection; not currently available.

These are recorded here so a future tooling investment can pick them up verbatim. They are NOT in this slice's done criteria.

---

## E. In-game smoke test plan

**Pre-requisites:**
- Plugin builds and loads on a character.
- The corpus contains at least one emote-bearing quest (data team's first quest with `{ "type": "use-emote", … }`). If no such quest exists yet, use the local-fixture approach in step 1 below.

**Smoke steps:**

1. **Author a local emote step.** Open `/qf` authoring on quest `99998` (or any existing test quest in the corpus). Edit the JSON to insert a `use-emote` step in an early sequence:

   ```json
   {
     "type": "use-emote",
     "id": "smoke-cheer-self",
     "emoteId": 17,
     "motion": true,
     "expect": "questSequence(99998) >= 1"
   }
   ```

   (Use an `Expect` predicate that is trivially true so the step short-circuits after one dispatch; this isolates the "did the emote fire?" verification from "did the NPC react?")

2. **Run with motion=true (chat suppressed).** Start the quest run. Observe:
   - The `/cheer` animation plays on the player character.
   - The chat window shows NO `"You cheer."` message.
   - Dalamud's `/xllog` shows one `[UseEmote] id=17 (self) motion=True` line per ~10s (debounce window).

3. **Run with NPC target.** Find a nearby NPC with a known BaseId (e.g. open `/qf debug game` to inspect the current target's BaseId, then write it into the step). Re-author:

   ```json
   {
     "type": "use-emote",
     "id": "smoke-cheer-npc",
     "emoteId": 17,
     "targetNpcId": <baseId>,
     "motion": true,
     "expect": "questSequence(99998) >= 2"
   }
   ```

   Run. Observe:
   - The NPC is acquired as the player's target (target slot updates).
   - The `/cheer` animation plays facing the NPC.
   - No chat broadcast.
   - Log line: `[UseEmote] id=17 target=<baseId> motion=True`.

4. **Run with motion=false (broadcast).** Re-author the step with `"motion": false`. Run. Observe:
   - Animation plays.
   - Chat window shows `"You cheer."` (or the localised equivalent) in the configured channel.
   - Log line: `[UseEmote] id=17 (self) motion=False`.

5. **Smoke-fail mode: unknown emote.** Re-author with `"emoteId": 99999`. Run. Observe:
   - No animation.
   - No chat broadcast.
   - Engine logs the `Result.Fail("emoteCommandNotFound", …)` from the adapter; the engine's stateless retry repeats every tick. The `[UseEmote]` debounced log still fires (the engine emits the dispatch, the adapter fails — both are normal trace events).
   - **Expected behavior:** the engine eventually trips `MaxConsecutiveStepFailures` and emits `AwaitUser` — confirms the failure path surfaces cleanly without crashing the plugin.

6. **Smoke-fail mode: NPC not in scene.** Re-author with `"targetNpcId": 9999999` (a BaseId no NPC in the current zone has). Run. Observe:
   - No target acquisition.
   - No animation.
   - Engine logs `Result.Fail("targetNotFound", "no object in scene with BaseId 9999999")`.
   - Same `MaxConsecutiveStepFailures` → `AwaitUser` outcome as step 5.

**Pass criteria:** steps 2-4 fire the emote correctly; steps 5-6 fail cleanly without plugin crash. If any step deviates (plugin crashes, chat suppress doesn't work, target acquisition fails on a valid NPC), file a bug before merging.

**Estimated time:** 20-30 minutes including authoring + 4 quest restarts.

---

## F. Implementation order

**Phase A — Adapter shell (15 min)**
1. Create `QuestForge.Adapters.Dalamud/Emotes/` directory.
2. Create `DalamudEmoteExecutor.cs` per §B.
3. Verify it compiles (`dotnet build QuestForge.Adapters.Dalamud`). ECommons, Lumina, FFXIVClientStructs all already referenced.

**Phase B — EngineHost wiring (10 min)**
1. Add field declaration per §C.1.
2. Add ctor construction per §C.2.
3. Modify `BeginRun` engine construction per §C.3.
4. Add `DispatchAction` arm per §C.4.
5. (Optional) Add debug accessor per §C.5.
6. Verify `dotnet build` succeeds with no warnings-as-errors regression.

**Phase C — In-game smoke (20-30 min, manual)**
1. Follow §E steps 1-6 on a character.
2. If pass: merge. If fail: bug report.

Total dev time: ~30 min code + ~30 min smoke ≈ 1 hour.

---

## G. Done criteria

1. `dotnet build` succeeds with no warnings-as-errors regression in `QuestForge.Adapters.Dalamud` or `QuestForge.Plugin`.
2. `QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs` exists and implements `IEmoteExecutor`.
3. `QuestForge.Plugin/EngineHost.cs:208-213` constructs `QuestEngine` with `emoteExecutor: _emoteExecutor` (no longer absent).
4. The `case EngineAction.UseEmote ue:` arm exists in `EngineHost.DispatchAction`, positioned between the `UseAction` and `Wait` cases.
5. Running an authored quest containing a `use-emote` step on a character causes the emote command to fire in-game (animation plays, NPC targeted if `targetNpcId` set, no chat broadcast when `motion=true`). Manual verification per §E.
6. The engine's `ResolveUseEmote` no longer returns the `AwaitUser("UseEmoteStep dispatched but no IEmoteExecutor wired …")` fallback (per `QuestEngine.cs:875-877`) when `EngineHost` is the host.
7. Smoke step §E.5 (unknown emoteId) surfaces `emoteCommandNotFound` cleanly without plugin crash.
8. Smoke step §E.6 (unknown targetNpcId) surfaces `targetNotFound` cleanly without plugin crash.
9. No regression in existing engine, schema, plugin, or adapter tests (`dotnet test`).

---

## H. Exclusions (what this plan does NOT include)

- **`RecordingEmoteExecutor`** (recording proxy). Deferred until a debugging case demands per-call adapter-act capture (see Decision DED-6). The engine emits decision events for `EngineAction.UseEmote` via the existing trace machinery; the dispatch is visible without an adapter-level wrap.
- **Direct tests for `DalamudEmoteExecutor`**. Dalamud-bound; validated by smoke. If/when a Dalamud-targeting test project is set up (tracked as the same future ticket from `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-3), the four tests sketched in §D are ready to copy in.
- **`EmoteName` lookup for diagnostic logs** (Decision DED-11). Trivial follow-up; not in v1.
- **`/qf debug emote <id> [<targetId>]` subcommand**. Optional `DebugEmoteExecutor` accessor in §C.5 makes it a one-liner to add later.
- **Authoring-mode inference** of UseEmoteStep from observed `/cheer` chat outputs. Phase 9 follow-up per engine plan Decision UE18.
- **`IsInCutscene` engine-side guard for UseEmote.** Deferred until smoke shows a real-world cutscene-loop problem (per Decision DED-7).
- **Quest data file for the first emote-bearing quest.** Authored by the data team; this plan only proves the engine + shell surface.
- **Emote-as-postcondition predicates** (e.g. `playerLastEmote(17)`). Not in this slice; not in the engine plan either.
- **Replacing `Chat.SendMessage` with a direct `ProcessChatBoxEntry` hook.** ECommons is the canonical pathway; cribbing the raw hook (as Questionable does) is unnecessary duplication for QF's needs and would tie us to ECommons' signature-tracking work without using ECommons.

---

## I. Open questions / decisions to call out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| Pure-logic seam beyond `EmoteCommandResolver`? | **No new seams** | Remaining surface is Dalamud-bound or single-call passthrough; helpers would have one-line bodies and tautological tests. | DED-1 |
| Lumina preload at construction or lazy? | **Preload at construction** | Sheet is small and stable; matches `LuminaQuestDataProvider`; surfaces "sheet unavailable" at plugin load not per call. | DED-3 |
| `Chat.SendMessage` exception handling? | **Try/catch all exceptions → `Result.Fail("chatSendFailed", …)`** | Inventory is short; recovery identical for all types; future-proofs against new ECommons exception types. | DED-4 |
| Order: resolve command, scan target, send? | **Resolve first, scan second, send third** | Fail-fast on bad emoteId without clobbering `TargetManager.Target` on garbage input. | DED-2, DED-5 |
| Add `am == null`-style nullability guard for `Chat`? | **No** | `Chat` is static; closest analog (`SendMessage` signature-lookup failure) throws and is caught. | DED-2 |
| Cutscene gate in the adapter? | **No — engine's `Casting` gate + chatSendFailed retry covers it** | Adapter shouldn't make engine-level decisions; smoke will surface if real-world loops occur. | DED-7 |
| Add `UseEmote` to dismount exemption list? | **No** | Engine plan Decision UE6 already pinned this; existing `not Navigate and not Teleport` lazy-dismount in `EngineHost` (line 280) already handles it correctly without change. | DED-8 |
| Debounced or always-on log in dispatch arm? | **Debounced** | Engine stateless-retries; always-on spams. Mirrors UseAction (Decision DAD-9). | DED-8 |
| Stop navigation first in dispatch arm? | **Yes** (cheap `IsNavigating` guard) | Mirrors Interact / Purchase / UseAction; vnavmesh may still be ticking. | DED-8 |
| Include emote `Name` in log line? | **Defer** | Trivial follow-up if smoke shows raw id is hard to read. | DED-11 |
| Wrap `IEmoteExecutor` in `RecordingEmoteExecutor`? | **No in v1** | Engine decision event already covers trace need. | DED-6 |
| Add a `/qf debug emote` subcommand? | **Optional one-line accessor (§C.5); subcommand itself deferred** | One-line cost, opens future debug path; subcommand can be its own slice. | DED-10 |

---

✅ READY FOR TEST CREATION

Tester: No new tests to write in this slice. The pure-helper coverage (UE16, UE17, UE18, optional UE18b) is already shipped in `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` from PR #104. Engine-side dispatch (UE1–UE9) is already shipped in `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs`. Validator rules (UE13–UE15) are already shipped.

- Happy paths: 0 new scenarios (UE16, UE17 already shipped)
- Edge cases: 0 new scenarios (UE18, optional UE18b already shipped)
- Error cases: 0 new scenarios (covered by engine-side UE4 already shipped)
- Expected total: 0 new tests in any project.

In-game smoke per §E is the sole verification surface for this slice. Builder proceeds to implementation; smoke verification happens on the author's character before merge.
