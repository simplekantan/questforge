# UseEmoteStep Implementation Plan

**Status:** ready for test creation

**Input docs:**
- `docs/USE_ACTION_STEP_PLAN.md` (closest analog — focused adapter, async pre-arm, author-required Expect, no synthesis)
- `docs/USE_ACTION_DALAMUD_PLAN.md` (pure-helper / Dalamud-shell split; the precedent for the `EmoteCommandResolver` pure helper)
- `CLAUDE.md` (engine purity invariant; ECommons available in `QuestForge.Adapters.Dalamud`)
- `QuestForge.Schema/Step.cs` (current placeholder `UseEmoteStep { EmoteId, Target: NpcLocation }` — **being replaced**, see Decision UE3)
- `QuestForge.Schema/SharedValueTypes.cs` (precedent for enum + value type placement in Schema)
- `QuestForge.Schema/QuestForgeJsonContext.cs` (STJ source-gen registration — UseEmoteStep is already registered)
- `QuestForge.Engine/EngineAction.cs` (discriminated union — `UseAction` record is the template)
- `QuestForge.Engine/QuestEngine.cs` `ResolveUseAction` lines 827-859 (template for the new `ResolveUseEmote`)
- `QuestForge.Adapters/Actions/IActionExecutor.cs` (focused-adapter precedent)
- `QuestForge.Adapters.Fakes/Actions/FakeActionExecutor.cs` (fake-with-recording-and-scripting precedent)
- `QuestForge.Engine.Tests/Engine/UseActionStepTests.cs` (test layout template)
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` (`ActionExecutor` field, `RunToCompletion` arm, ctor passthrough)
- `QuestForge.Plugin/EngineHost.cs` lines 109, 213, 447-456 (field, ctor pass, dispatch arm — the `UseAction` arm is the closest analog)
- `QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs` (Dalamud-shell template)
- `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` (pure-helper-in-Dalamud-bound-assembly precedent for `EmoteCommandResolver`'s sibling, though see UE16 — `EmoteCommandResolver` lives Dalamud-free)
- `QuestForge.Adapters.Dalamud/PluginServices.cs` (the `IDataManager` field used for Lumina lookup)
- `QuestForge.Engine/Authoring/DraftValidator.cs` (E1–E8 / W1–W7 rules; this plan adds E9 / E10 / W8)
- `C:\Users\publi\RiderProjects\Lifestream\ECommons\ECommons\Automation\Chat.cs` line 68 — `Chat.SendMessage(string)` is the public API used by the Dalamud shell
- `C:\Users\publi\RiderProjects\Questionable\Questionable\Functions\ChatFunctions.cs` lines 25–31, 128–141 — **observed behavior only** (Questionable is AGPL; we read it to learn the contract, we do NOT crib code per the clean-room instruction). The actionable facts learned:
  - The Lumina lookup path is `dataManager.GetExcelSheet<Emote>()[rowId].TextCommand.Value.Command.ToString()`.
  - Many `Emote` rows have `TextCommand.IsValid == false` (e.g. row 0, rows that are animations-without-commands) — skip those.
  - Returned commands begin with `'/'` (e.g. `"/cheer"`).
  - The conventional invocation is `"<command> motion"` when suppressing chat broadcast, `"<command>"` otherwise.

**Output (CI behavior):** Adding a `{ "type": "use-emote", "emoteId": 17, "targetNpcId": 1000789, "motion": true }` step to a quest dispatches a new `EngineAction.UseEmote` that the host translates to `IEmoteExecutor.UseEmote`. Engine unit tests (xUnit, `QuestForge.Engine.Tests`) cover all dispatch arms against `FakeEmoteExecutor`. A small set of pure-logic tests (`QuestForge.Adapters.Tests`) covers `EmoteCommandResolver`. The Dalamud-bound shell is validated by in-game smoke; the only emote-bearing quest in the corpus today (whichever quest the data team adds first) becomes authorable end-to-end. The existing placeholder `UseEmoteStep_RoundTrips` test in `QuestForge.Schema.Tests/RoundTripTests.cs` is updated to the new field shape in the same PR.

> **Note on the existing placeholder:** `QuestForge.Schema/Step.cs` lines 157-161 already defines a placeholder `UseEmoteStep { EmoteId: uint; Target: NpcLocation? }`. **This plan replaces that shape** — none of those fields is referenced from the engine today (no `ResolveUseEmote` exists; the engine throws `NotSupportedException` for it via the default arm). The replacement does **not** require a schema-version bump because no shipped quest authors the old shape. The validator and existing round-trip test (`QuestForge.Schema.Tests/RoundTripTests.cs::UseEmoteStep_RoundTrips` at line 251) **will be updated** as part of this plan — see Task UET-1 and Decision UE3. The `ExportDialog` mapping (`QuestForge.Plugin/UI/Authoring/ExportDialog.cs:207`) already emits the `"use-emote"` discriminator string and requires no change.

---

## Dependency graph

```
QuestForge.Schema
   └── UseEmoteStep (rewritten) + JSON registration
        └── consumed by ↓
QuestForge.Adapters
   └── new IEmoteExecutor (in QuestForge.Adapters/Emotes/IEmoteExecutor.cs)
   └── new EmoteCommandResolver (in QuestForge.Adapters/Emotes/EmoteCommandResolver.cs) — pure helper
        └── consumed by ↓
QuestForge.Adapters.Fakes
   └── FakeEmoteExecutor (Emotes/FakeEmoteExecutor.cs)
        └── consumed by ↓
QuestForge.Engine
   └── new EngineAction.UseEmote; ResolveUseEmote async pre-arm in QuestEngine.cs
        └── consumed by ↓
QuestForge.Engine.Tests
   └── UseEmoteStepTests against FakeEmoteExecutor + FakeGameStateProvider

QuestForge.Adapters.Tests (net10.0, Dalamud-free)
   └── Emotes/EmoteCommandResolverTests.cs (~3 tests for the pure helper)

QuestForge.Adapters.Dalamud
   └── Emotes/DalamudEmoteExecutor.cs (uses ECommons.Automation.Chat.SendMessage + IDataManager)

QuestForge.Plugin
   └── EngineHost.cs: field, ctor construct, BeginRun passthrough, DispatchAction arm
```

**Build order:**
1. Schema (`UseEmoteStep` rewrite + JSON-context — already registered) — round-trip test gates it.
2. `IEmoteExecutor` interface in `QuestForge.Adapters/Emotes/`.
3. `EmoteCommandResolver` pure helper in `QuestForge.Adapters/Emotes/`.
4. `FakeEmoteExecutor` in `QuestForge.Adapters.Fakes/Emotes/`.
5. `EngineAction.UseEmote` record.
6. `QuestEngine.ResolveUseEmote` async pre-arm + dispatch.
7. `EngineTestHarness` wires `FakeEmoteExecutor`; `RunToCompletion` arm.
8. Engine tests UE1–UE15.
9. Pure-helper tests UE16–UE18 in `QuestForge.Adapters.Tests`.
10. `DalamudEmoteExecutor` shell.
11. `EngineHost` wiring (field, ctor, dispatch arm).
12. In-game smoke.

---

## Architectural decisions (read before coding)

### Decision UE1 — `IEmoteExecutor` is a new focused adapter, separate from `IActionExecutor`

`IActionExecutor` exists for `ActionManager.UseAction` (combat abilities, general actions, key items). Emotes are not used via `ActionManager.UseAction`; they are issued as text commands through the chat box (the in-game equivalent of the player typing `/cheer motion`). The mechanism is entirely different.

**Concrete shape:**

```csharp
// QuestForge.Adapters/Emotes/IEmoteExecutor.cs (new file, new namespace)
namespace QuestForge.Adapters.Emotes;

using QuestForge.Adapters.Types;

/// <summary>
/// Issues a single emote (text command via the chat-box pathway) optionally on an NPC target.
/// The Dalamud implementation owns target acquisition (set TargetManager.Target before sending
/// the command) and the Lumina lookup that turns the Emote sheet RowId into a text command
/// (e.g. row 17 → "/cheer").
///
/// Success means "the chat command was submitted." It does NOT mean "the emote animation
/// played" or "the NPC reacted." The engine verifies outcome via the authored Expect predicate.
/// </summary>
public interface IEmoteExecutor
{
    /// <summary>
    /// Issues the emote. Returns Result.Failure if:
    ///   - targetNpcId is supplied and no matching object is in ObjectTable (kind: EventNpc / BattleNpc), or
    ///   - the Lumina Emote row has no associated text command (TextCommand.IsValid == false or
    ///     the command string is empty / does not start with '/').
    ///
    /// Success implies the text command was submitted (e.g. Chat.SendMessage("/cheer motion") returned).
    /// </summary>
    /// <param name="emoteId">Lumina Emote sheet RowId (e.g. 17 = /cheer).</param>
    /// <param name="targetNpcId">Optional NPC target; null means "self / no target write".</param>
    /// <param name="motion">When true, append " motion" to the command (suppresses chat broadcast,
    /// plays animation only). When false, fire the bare command (broadcasts to the configured channel).</param>
    Task<Result<Unit>> UseEmote(
        uint emoteId,
        NpcId? targetNpcId,
        bool motion,
        CancellationToken ct);
}
```

**Why not extend `IActionExecutor`:** mechanism mismatch. `IActionExecutor.UseAction` reaches `ActionManager` natively; emotes go through `Chat.SendMessage`. Combining them would force every `IActionExecutor` consumer to learn about the chat pathway and every emote consumer to learn about `ActionStatus` semantics that are meaningless for emotes (no cooldown, no Unusable / OnCooldown branches).

**Why no `GetEmoteStatus`:** emotes are stateless. There is no cooldown, no resource gate, no "Unusable" verdict the game exposes for an emote. If the game silently rejects the command (e.g. mid-cast), the engine's stateless retry next tick recovers. Adding a status read would invent semantics that don't exist.

**What breaks if violated:** if emote execution is folded into `IActionExecutor`, the `ActionStatus` union has to grow a fourth case (`NotApplicable`?) for emotes, or every emote call has to script `ScriptNextStatus(Ready)` in tests, which is noise.

### Decision UE2 — `Motion: bool` defaults to `true` (suppress chat broadcast)

When the player types `/cheer` in-game, the emote animation plays AND a chat broadcast goes out (e.g. "You cheer!" in the configured channel — Say by default). When the player types `/cheer motion`, only the animation plays. For a quest automation tool the bare-command broadcast is virtually always unwanted noise.

**Default: `Motion = true`** — quest data omits the field 99% of the time and gets the right behavior automatically.

**JSON serialization:** the field MUST round-trip when explicit AND default correctly when absent. The serializer's `WriteIndented = true` + camelCase + the default-value `true` together mean a UseEmoteStep authored without `motion` deserializes to `Motion = true`. Round-trip test UE12 pins this.

**Note on JSON-default behavior:** unlike `TargetNpcId` (which uses `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`), `Motion` is a non-nullable `bool` with default `true`. STJ writes `bool` fields unconditionally by default — i.e. a step with `Motion = true` will serialize as `"motion": true`. This is acceptable: it makes the field's value explicit in every serialized form, which is preferable for a setting that materially changes user-visible behavior (chat broadcast on/off). Authors who want minimal JSON can omit the field on read; the writer's verbosity is a feature, not a bug.

### Decision UE3 — `UseEmoteStep` schema is rewritten; old `Target: NpcLocation?` field removed

Existing placeholder (`Step.cs:157-161`):
```csharp
public class UseEmoteStep : Step
{
    public uint EmoteId { get; init; }
    public NpcLocation? Target { get; init; }
}
```

**Replace with:**
```csharp
// QuestForge.Schema/Step.cs (replaces existing UseEmoteStep)
public class UseEmoteStep : Step
{
    /// <summary>Lumina Emote sheet RowId (e.g. 17 = /cheer, 7 = /salute).</summary>
    public uint EmoteId { get; init; }

    /// <summary>
    /// Optional NPC target. Null means self / no target write.
    /// The Dalamud adapter resolves this BaseId via ObjectTable and writes
    /// TargetManager.Target before submitting the text command.
    /// NpcId here is the BNpcBase / ENpcBase data-id, matching the convention
    /// used by InteractStep / TalkStep / UseActionStep targets.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }

    /// <summary>
    /// When true (default), append " motion" to the emote command to suppress the chat
    /// broadcast and play only the animation. When false, fire the bare command (broadcasts
    /// "You cheer!" / "You salute!" etc. to the configured chat channel).
    /// </summary>
    public bool Motion { get; init; } = true;
}
```

**Why drop `Target: NpcLocation?` in favor of `TargetNpcId: uint?`:** mirrors `UseActionStep.TargetNpcId` (Decision UA3 in `USE_ACTION_STEP_PLAN.md`). `NpcLocation` bundles `(NpcId, Zone, Position)` — Zone and Position are travel information that does NOT belong on an emote step (the player must already be at the NPC; the preceding TravelStep / TalkStep handles positioning). Carrying Zone/Position here would lure authors into believing the emote step does navigation, which it does not.

**JSON sample (cheer on an NPC, motion-only):**
```json
{
  "type": "use-emote",
  "id": "cheer-the-recruit",
  "emoteId": 17,
  "targetNpcId": 1000789,
  "expect": "questFlag(65657, 5)"
}
```

Self-cast variant (no target, default motion=true):
```json
{
  "type": "use-emote",
  "id": "celebrate",
  "emoteId": 17,
  "expect": "questSequence(65657) >= 3"
}
```

Explicit broadcast variant (motion=false):
```json
{
  "type": "use-emote",
  "id": "yell-at-the-crowd",
  "emoteId": 17,
  "motion": false,
  "expect": "questFlag(65657, 5)"
}
```

**What breaks if violated:** if `Target: NpcLocation?` is kept, every emote step that touches an NPC carries a Zone field that the engine ignores, and validator authors are tempted to write predicates that gate on the Zone — predicates that diverge from the actual game state.

### Decision UE4 — Author-required `Expect`; engine performs NO synthesis

Identical posture to Decision UA4 in `USE_ACTION_STEP_PLAN.md`:

1. **No universal postcondition exists.** Different emote-triggered NPCs respond with different state changes (some flip a quest flag, some advance a sequence, some spawn a cutscene and then advance). No `emoteUsed(id)` predicate would mean "the emote had its intended effect."
2. **Loss mode is recoverable.** Without Expect, the engine spam-emits `UseEmote` each tick. The trace is loud enough to notice; silent synthesis would be worse.
3. **Validator surface:** missing `Expect` on a UseEmoteStep is a **warning** (Decision UE7 — `W8`), not an error.

### Decision UE5 — Pre-flight guards live in `ResolveUseEmote`, single guard

Emotes have far fewer pre-flight gates than actions. The only meaningful guard is the casting check (a mid-cast emote command is silently rejected by the game). No cooldown read, no status read.

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `EngineAction.Wait("player casting; deferring use-emote")` |
| 2. (none — proceed to emit) | — | `EngineAction.UseEmote(emoteId, target, motion, Origin: step)` |

**No InCombat guard:** an in-combat `/cheer` works in-game (animation plays even with weapon drawn). Combat is the player's problem; the engine should not refuse to dispatch an emote because the player happens to be fighting.

**No EmoteStatus probe:** as noted in UE1, the game does not expose one. The engine assumes the emote is always issuable; if rejection happens (mid-cast slipped through, system message blocked input), the stateless retry handles it.

**Concrete shape:**
```csharp
private async Task<EngineAction> ResolveUseEmote(UseEmoteStep step, CancellationToken ct)
{
    if (_emoteExecutor is null)
        return new EngineAction.AwaitUser(
            "UseEmoteStep dispatched but no IEmoteExecutor wired — host must supply one");

    // Guard 1: casting → Wait (defer until cast finishes)
    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring use-emote", Origin: step);

    var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
    return new EngineAction.UseEmote(step.EmoteId, target, step.Motion, Origin: step);
}
```

**Optional executor mirrors Decision UA12.** `IEmoteExecutor? emoteExecutor = null` is the last QuestEngine constructor parameter. Absence yields a loud `AwaitUser` instead of a crash.

### Decision UE6 — Mount handling: lazy-dismount applies; UseEmote is NOT in the dismount-exemption list

Most emotes will play while mounted (e.g. `/cheer` on a chocobo), but quest-triggering emotes typically require the player to be dismounted (the NPC's reaction script checks for the player's standing pose). Following the same conservative posture as `UseAction` (Decision UA6), the lazy-dismount hook fires before `EngineAction.UseEmote` is returned to the dispatch loop, whenever the previous tick dispatched `Navigate`.

**Action required in this plan:** the `HarnessEngine.Tick` arm and the `EngineHost.DispatchAction` arm **do not need code changes** — the existing pattern dismounts before any non-`Navigate` action that is not `Teleport`. We only need regression tests pinning the behavior:
- UE6 (mounted + prior Navigate) — dismount fires before UseEmote.
- UE7 (standalone, no prior Navigate) — dismount does NOT fire.

**Why not exempt:** unlike Teleport (the game auto-dismounts on aetheryte arrival), there is no game-side auto-dismount for emotes. Adding `UseEmote` to the exemption list would mean a quest that does `Navigate → UseEmote` while mounted leaves the player mounted, the emote runs in the mounted pose, and the NPC's script fails to detect the right pose. Better to dismount and accept the (rare) extra animation cost.

### Decision UE7 — Validator rules (added by this plan in `DraftValidator.cs`)

Unlike Decision UA7 (which deferred validator rules to a separate PR), the use-emote validator rules are small and consolidated. **Add them in this plan** — pattern-match on the existing E7 / E8 / W7 rules for UseActionStep.

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| `emoteId` non-zero | `E9` | Error | `UseEmoteStep.EmoteId == 0` | — |
| `targetNpcId` non-zero when present | `E10` | Error | `UseEmoteStep.TargetNpcId == 0` (null is allowed) | — |
| `expect` authored | `W8` | Warning | `UseEmoteStep.Expect is null` | also suppress `W1` for UseEmoteStep (mirrors the existing `is not UseActionStep` exclusion in W1) |

**Why E9 / E10 / W8 not E11 / E12 / W9:** the validator currently runs E1–E8 + W1–W7. The next free codes are E9, E10, W8. Numbering continues the established sequence rather than introducing a `UE`-namespaced sub-scheme — predicate codes are flat and globally unique.

**W1 suppression:** the existing W1 rule (`Step.Raw.Expect is null && Step.Raw is not UseActionStep`) excludes UseActionStep so W7 can fire instead. Extend the exclusion: `Step.Raw.Expect is null && Step.Raw is not (UseActionStep or UseEmoteStep)` so W8 fires for UseEmoteStep without W1 duplicating. (Pattern syntax verified compatible with C# 11+.)

**Pinned by tests:** UE13, UE14, UE15.

### Decision UE8 — `EmoteCommandResolver` lives in `QuestForge.Adapters` (Dalamud-free), NOT in `QuestForge.Adapters.Dalamud`

This is the key difference from Decision DAD-2 (where `ActionExecutorLogic` had to live in the Dalamud-bound assembly because it returned a `FFXIVClientStructs` type).

`EmoteCommandResolver` operates on primitives only:
- **Inputs:** `uint emoteId`, `bool motion`, `Func<uint, string?> commandLookup` (callback the Dalamud side wires up).
- **Outputs:** `Result<string>` — the formatted command (`"/cheer motion"`) or a structured failure (`"emoteCommandNotFound"`).

No FFXIVClientStructs types, no Lumina types. The Dalamud side supplies the lookup callback at construction time; the resolver is pure logic and lives in the Dalamud-free assembly so tests can target it directly (mirrors Decision DAD-3 for `ActionStatusInterpreter`).

**Concrete shape:**

```csharp
// QuestForge.Adapters/Emotes/EmoteCommandResolver.cs
namespace QuestForge.Adapters.Emotes;

public static class EmoteCommandResolver
{
    /// <summary>
    /// Builds the chat command for an emote.
    /// </summary>
    /// <param name="emoteId">Lumina Emote sheet RowId.</param>
    /// <param name="motion">When true, append " motion" to suppress chat broadcast.</param>
    /// <param name="commandLookup">
    ///   Caller-supplied lookup that, given an EmoteId, returns the raw text command
    ///   (e.g. "/cheer") or null if no command is defined for that row.
    ///   In production, the Dalamud shell wires this to a memoised Lumina lookup.
    ///   In tests, supply a stub dictionary.
    /// </param>
    /// <returns>
    ///   Result.Ok("/cheer motion") on success.
    ///   Result.Fail("emoteCommandNotFound", "no text command for emote {id}") if lookup returned null.
    ///   Result.Fail("emoteCommandMalformed", "emote {id} text command does not start with '/': '{cmd}'")
    ///       if lookup returned a non-empty string that does not begin with '/' (defensive — Lumina
    ///       rows where Command is not a slash command should already be filtered out by the caller's
    ///       lookup, but we double-check so a malformed sheet entry never reaches Chat.SendMessage).
    /// </returns>
    public static Result<string> Resolve(uint emoteId, bool motion, Func<uint, string?> commandLookup)
    {
        var raw = commandLookup(emoteId);
        if (string.IsNullOrEmpty(raw))
            return Result.Fail<string>(
                "emoteCommandNotFound",
                $"no text command for emote {emoteId}");

        if (!raw.StartsWith('/'))
            return Result.Fail<string>(
                "emoteCommandMalformed",
                $"emote {emoteId} text command does not start with '/': '{raw}'");

        return Result.Ok(motion ? $"{raw} motion" : raw);
    }
}
```

**Why pass a `Func` rather than an `IEmoteCommandTable` interface:** the lookup callback has exactly one input and one output; an interface would be ceremony for no benefit. A delegate composes cleanly with the Dalamud-side `Dictionary<uint, string>` and with test dictionaries.

**What breaks if violated:** if `EmoteCommandResolver` is colocated with the Dalamud shell, the resolver tests cannot run in `QuestForge.Adapters.Tests` (net10.0, Dalamud-free) and the format-construction bugs (the `" motion"` suffix, the leading-slash check) become smoke-test-only — the exact failure mode `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-1 warned against.

### Decision UE9 — Lumina Emote lookup is preloaded once at `DalamudEmoteExecutor` construction

Following the precedent in `ChatFunctions.cs` lines 25-31 (Questionable, observed-not-cribbed): preload the `Emote` sheet into a `ReadOnlyDictionary<uint, string>` at adapter construction, then index it per call. The sheet is small (a few hundred rows) and stable for the lifetime of the plugin.

```csharp
// QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs (sketch — see §C for full)
private readonly IReadOnlyDictionary<uint, string> _emoteCommands;

public DalamudEmoteExecutor(PluginServices svc)
{
    _svc = svc;
    _emoteCommands = LoadEmoteCommands(svc.DataManager);
}

private static IReadOnlyDictionary<uint, string> LoadEmoteCommands(IDataManager dataManager)
{
    var dict = new Dictionary<uint, string>();
    foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
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

**Why eager preload over on-demand:** the per-call cost is dominated by the Lumina row deref; preloading and indexing once at construction is one allocation amortised forever. `AetheryteZoneMap.Populate` is the established pattern (`QuestForge.Engine/Travel/AetheryteZoneMap.cs`).

**Why static, not injected:** the Lumina sheet does not change at runtime within a single plugin session. A static-at-construction snapshot is correct; an injection layer would be ceremony.

**What breaks if violated:** if the lookup is per-call without caching, every UseEmote dispatch pays Lumina deref + LINQ filter cost. Cheap, but pointless. More importantly, if the per-call lookup is in `DalamudEmoteExecutor.UseEmote` rather than at construction, the failure mode "Lumina sheet missing or unreadable" surfaces per-call rather than at plugin load — a worse error-surface posture.

### Decision UE10 — `EngineAction.UseEmote` record shape

```csharp
// QuestForge.Engine/EngineAction.cs (append)
public sealed record UseEmote(
    uint EmoteId,
    QuestForge.Adapters.Types.NpcId? TargetNpcId,
    bool Motion,
    Step? Origin = null) : EngineAction;
```

Carries the schema-side primitives directly. No FFXIVClientStructs translation needed (emotes don't go through `ActionManager`).

### Decision UE11 — `QuestEngine` constructor gains `IEmoteExecutor? emoteExecutor = null` (last parameter)

Mirrors Decision UA12. Appended after `IActionExecutor?` to preserve ordering for trace-replay and to avoid disturbing existing test sites:

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
    IEmoteExecutor? emoteExecutor = null)   // NEW
```

`emoteExecutor` is optional. Absence yields `AwaitUser("UseEmoteStep dispatched but no IEmoteExecutor wired — host must supply one")` (per Decision UE5). `EngineHost.BeginRun` MUST pass a non-null `DalamudEmoteExecutor`. `EngineTestHarness` MUST pass `EmoteExecutor`.

### Decision UE12 — `_lastResolvedStep` is NOT updated by `ResolveUseEmote`

Mirrors Decision UA13. `UseEmoteStep` does not carry `DialogueChoices`, so `ExtractYesNo` returns null for it. Do NOT set `_lastResolvedStep` in the async pre-arm — the `Origin: step` field on `EngineAction.UseEmote` carries the necessary context for downstream trace consumers.

### Decision UE13 — Dispatch arm goes between `UseAction` (line 447) and `Wait` (line 458) in `EngineHost.DispatchAction`

Insertion point in `EngineHost.cs`:

```csharp
case EngineAction.UseAction ua:
    // ... existing UseAction arm (lines 447-456)
    break;

// NEW arm — UseEmote
case EngineAction.UseEmote ue:
    DebounceLog(
        $"useemote:{ue.EmoteId}:{ue.TargetNpcId?.Value}:{ue.Motion}",
        $"[UseEmote] id={ue.EmoteId}" +
        (ue.TargetNpcId is { } ueId ? $" target={ueId.Value}" : " (self)") +
        $" motion={ue.Motion}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _emoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
    break;

case EngineAction.Wait:
    // ... (line 458)
```

**Why stop navigation first:** identical reasoning to Decision DAD-9 for UseAction — vnavmesh may still be ticking; cheap `IsNavigating` guard plus `Stop` prevents the player walking through the emote animation.

**Why debounced log:** the engine re-emits `UseEmote` every tick until `Expect` satisfies (stateless retry). Without debounce a 5-second NPC-reaction wait dumps ~20 `[UseEmote]` lines into the Dalamud log. Same `DebounceInterval` constant (10s) as all other arms.

**Why no `_lastDispatchedActionWasUseEmote` tracking:** unlike `Purchase` (which needs deferred shop-close), `UseEmote` has no follow-up cleanup. Fire-and-forget.

### Decision UE14 — `EngineTestHarness` constructs `FakeEmoteExecutor` and `RunToCompletion` gains an arm

`EngineTestHarness` gains:
```csharp
public FakeEmoteExecutor EmoteExecutor { get; } = new FakeEmoteExecutor();
```
…and passes `EmoteExecutor` to the `QuestEngine` constructor (last positional kwarg).

`RunToCompletion` gains an arm mirroring the UseAction arm at line 209:

```csharp
case EngineAction.UseEmote ue:
    actions.Add(action);
    EmitActionSubmitted("UseEmote", JsonSerializer.SerializeToElement(
        new { emoteId = ue.EmoteId, targetNpcId = ue.TargetNpcId?.Value, motion = ue.Motion },
        _jsonOpts));
    var ueResult = await EmoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
    EmitActionCompleted("UseEmote", ueResult.IsSuccess ? "Done" : "Failed");
    break;
```

### Decision UE15 — `FakeEmoteExecutor` exposes `RecordedCalls` + `ScriptNextFailure` (no status scripting needed)

Simpler than `FakeActionExecutor` because there is no status read. Concrete shape:

```csharp
// QuestForge.Adapters.Fakes/Emotes/FakeEmoteExecutor.cs
namespace QuestForge.Adapters.Fakes.Emotes;

using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

public sealed class FakeEmoteExecutor : IEmoteExecutor
{
    public record UseEmoteCall(
        uint EmoteId,
        NpcId? TargetNpcId,
        bool Motion,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseEmoteCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    /// <summary>Forces UseEmote to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> UseEmote(
        uint emoteId, NpcId? targetNpcId, bool motion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseEmoteCall(emoteId, targetNpcId, motion, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
```

### Decision UE16 — `DalamudEmoteExecutor` shell uses `ECommons.Automation.Chat.SendMessage`

ECommons is already a referenced package in `QuestForge.Adapters.Dalamud.csproj` (line 13). The `Chat.SendMessage(string)` API is the canonical way to submit a slash command from a Dalamud plugin without re-implementing the `ProcessChatBoxEntry` plumbing. Concrete shape:

```csharp
// QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Emotes;

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

        // 1. Resolve the text command (pure helper; returns Result).
        var commandResult = EmoteCommandResolver.Resolve(
            emoteId, motion, id => _emoteCommands.TryGetValue(id, out var c) ? c : null);

        if (commandResult is not Result<string>.Success { Value: var command })
            return Task.FromResult<Result<Unit>>(
                ((Result<string>)commandResult).ToFailureOf<Unit>());
        // (or whatever the codebase's canonical Result<T> → Result<U> failure copy is —
        //  Builder verifies; fallback: re-create Result.Fail with the same code/detail.)

        // 2. Set target if requested.
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
                    Result.Fail("targetNotFound",
                        $"no object in scene with BaseId {id.Value}"));
            _svc.TargetManager.Target = found;
        }

        // 3. Submit the command.
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
            // Note: row.TextCommand.Value.Command may be a Lumina ReadOnlySeString.
            // Use the conventional .ExtractText() (or .ToString() if ExtractText not present).
            // Builder picks the right one based on the Lumina version in use.
            var cmd = row.TextCommand.Value.Command.ExtractText();
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('/')) continue;
            dict[row.RowId] = cmd;
        }
        return dict;
    }
}
```

**Why try/catch around `Chat.SendMessage`:** the ECommons doc (`Chat.cs:65-67`) says it throws `ArgumentException` for empty / too-long / invalid-character messages, plus `InvalidOperationException` if the signature was not found. For a well-formed emote command (always short, always ASCII, signature stable), none of these should fire in practice — but the catch surfaces the failure to the engine cleanly rather than crashing the dispatch loop.

**Why no return-bool check:** `Chat.SendMessage` returns void. Success means it didn't throw. Same posture as `am->UseAction` discard (Decision DAD-8) — the engine uses `Expect` to verify the emote had its intended effect.

### Decision UE17 — `EngineHost` constructs `DalamudEmoteExecutor` once at host construction time

Mirrors Decision DAD-10. Adapter is stateless; one construction per host lifetime is correct.

```csharp
// EngineHost field, after _actionExecutor (line 44)
private readonly DalamudEmoteExecutor _emoteExecutor;

// EngineHost constructor, after line 109
_emoteExecutor = new DalamudEmoteExecutor(services);

// EngineHost.BeginRun, line 208-213, append emoteExecutor:
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor,
    emoteExecutor: _emoteExecutor);
```

After this wiring, `ResolveUseEmote` stops returning `AwaitUser("...no IEmoteExecutor wired...")` and starts dispatching.

### Decision UE18 — Authoring inference is OUT OF SCOPE for this slice

A future authoring-mode feature would detect "the player just used /cheer on this NPC" during recording and synthesize a UseEmoteStep. Hooks for this likely include:
- **Outbound chat capture** — intercept `Chat.SendMessage` or hook `ProcessChatBoxEntry` to detect slash-command emotes.
- **EmoteController polling** — observe `EmoteController` (FFXIVClientStructs) for "this NPC just received this emote from the local player" events.

Neither path is in this plan. The spec for UseEmoteStep makes inference possible (the EmoteId is locale-stable and target-explicit), but the inference plumbing belongs in a Phase 9 follow-up alongside the broader authoring inference work for UseActionStep.

**Recommendation:** when the inference work begins, the cleanest hook is likely outbound-chat capture (matches every slash-command-based step type uniformly — UseEmote, SayChatMessage, future macro-based steps). Do NOT plumb that here.

### Decision UE19 — Do NOT bundle Dalamud impl tests with engine tests; pure helper gets its own test file

Mirrors Decision DAD-3:
- `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` — pure-helper tests (UE16, UE17, UE18). 3 tests; targets `QuestForge.Adapters` (Dalamud-free).
- `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` — engine dispatch tests (UE1–UE15).
- No tests for `DalamudEmoteExecutor` — Dalamud-bound; validated by in-game smoke.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Schema/Step.cs` | MODIFY | Replace `UseEmoteStep` per Decision UE3 |
| `QuestForge.Schema/QuestForgeJsonContext.cs` | (no change) | `[JsonSerializable(typeof(UseEmoteStep))]` already present |
| `QuestForge.Schema.Tests/RoundTripTests.cs` | MODIFY | Update existing `UseEmoteStep_RoundTrips` to new field shape + add 2 sibling tests |
| `QuestForge.Adapters/Emotes/IEmoteExecutor.cs` | NEW | Adapter interface |
| `QuestForge.Adapters/Emotes/EmoteCommandResolver.cs` | NEW | Pure helper |
| `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` | NEW | 3 tests for the pure helper |
| `QuestForge.Adapters.Fakes/Emotes/FakeEmoteExecutor.cs` | NEW | Fake with recording + scripting |
| `QuestForge.Engine/EngineAction.cs` | MODIFY | Append `UseEmote` record |
| `QuestForge.Engine/QuestEngine.cs` | MODIFY | Field, ctor param, async pre-arm in `ResolveAction`, `ResolveUseEmote` method |
| `QuestForge.Engine/Authoring/DraftValidator.cs` | MODIFY | E9, E10, W8 + extend W1 exclusion |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | MODIFY | `EmoteExecutor` property + ctor passthrough + RunToCompletion arm |
| `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` | NEW | UE1–UE15 |
| `QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs` | NEW | Dalamud-bound shell |
| `QuestForge.Plugin/EngineHost.cs` | MODIFY | Field, ctor construct, BeginRun arg, DispatchAction arm |

---

## Validation rule table

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| `emoteId` non-zero | `E9` | Error | `UseEmoteStep.EmoteId == 0` | — |
| `targetNpcId` non-zero when present | `E10` | Error | `UseEmoteStep.TargetNpcId == 0` (null is allowed) | — |
| `expect` authored | `W8` | Warning | `UseEmoteStep.Expect is null` | — |
| W1 ("step has no Expect") | `W1` | Warning | (existing rule) | extended exclusion: `not UseActionStep and not UseEmoteStep` |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs`)

All tests follow the `UseActionStepTests` pattern: one `[Fact]` per scenario, `BuildSingleStepQuest` / `BuildTwoStepQuest` factories at the bottom. For each scenario:
- `harness.QuestState.SetQuestSequence(new QuestId(<questId>), 0)`.
- The quest contains exactly one UseEmoteStep in sequence 0 (unless noted).
- The step has an authored `Expect` (PredicateExpect using a predicate that does NOT auto-satisfy from default fake state, unless the test specifically tests Expect satisfaction).

#### UE1 — Happy path, no target, motion=true (default) → emits UseEmote(emoteId, null, true)

**Given:**
- Player not casting.
- UseEmoteStep `{ EmoteId = 17, TargetNpcId = null, Motion = true, Expect = PredicateExpect("questFlag(82001, 3)") }` (predicate false).

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseEmote(EmoteId: 17u, TargetNpcId: null, Motion: true, Origin: <step>)`.
- `harness.EmoteExecutor.RecordedCalls.Count == 0` (engine returns the action; harness `RunToCompletion` would call the adapter, but this test ticks once).

#### UE2 — Happy path, NPC target → emits UseEmote with TargetNpcId set

**Given:**
- Player not casting.
- UseEmoteStep `{ EmoteId = 17, TargetNpcId = 1000789u, Motion = true, Expect = PredicateExpect("questFlag(82002, 3)") }`.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseEmote(EmoteId: 17u, TargetNpcId: new NpcId(1000789), Motion: true, Origin: <step>)`.
- `useEmote.TargetNpcId!.Value == new NpcId(1000789)`.

#### UE3 — Player casting → Wait, no UseEmote emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- UseEmoteStep as UE1.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains the substring `"player casting"`.
- `harness.EmoteExecutor.RecordedCalls.Count == 0`.

#### UE4 — Adapter UseEmote returns Result.Failure → stateless retry on next tick

**Given:**
- UseEmoteStep as UE1 (no target, Motion=true, Expect false).
- `harness.EmoteExecutor.ScriptNextFailure("adapter-error", "Chat.SendMessage threw")`.

**When:**
1. Tick 1 → returns `EngineAction.UseEmote(...)`.
2. Manually call `await harness.EmoteExecutor.UseEmote(17u, null, true, ct)` (consumes the scripted failure; returns `Result.Failure`; recorded call appended).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseEmote(...)` again — stateless retry.
- `harness.EmoteExecutor.RecordedCalls.Count == 1` (only the manual call; engine emits the action but does not invoke the adapter).

#### UE5 — Cancellation propagates from dispatch arm

**Given:**
- UseEmoteStep as UE1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### UE6 — Mounted + prior Navigate: lazy-dismount fires before UseEmote

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep navigating to position `(200, 0, 0)` in zone `130` with `Expect = "playerZone() == 130"`.
  2. UseEmoteStep `{ EmoteId = 17, TargetNpcId = null, Motion = true, Expect = PredicateExpect("questFlag(82006, 3)") }` (predicate false).
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 → `EngineAction.Navigate`. After this tick `_lastDispatchedWasNavigate = true`.
2. Advance state: `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect now satisfies).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseEmote(...)`.
- `harness.Mount.DismountCallCount >= 1` (lazy-dismount fired — UseEmote is NOT in the exemption list).

Pins Decision UE6.

#### UE7 — Standalone UseEmote + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: UseEmoteStep as UE1.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.UseEmote(...)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount is bound to *prior Navigate*).

#### UE8 — Authored Expect already satisfied → step skipped → no UseEmote emitted

**Given:**
- UseEmoteStep `{ EmoteId = 17, TargetNpcId = null, Motion = true, Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(8), true)` so the predicate is true *before* the step runs.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits dispatch; step confirmed; no more steps).
- `harness.EmoteExecutor.RecordedCalls.Count == 0`.

#### UE9 — Integration two-tick: UseEmote fires, Expect satisfies, step completes

**Given:**
- UseEmoteStep `{ EmoteId = 17, TargetNpcId = 1000789u, Motion = true, Expect = PredicateExpect("questFlag(82009, 3)") }`.

**When:**
1. Tick 1 → `EngineAction.UseEmote(...)`.
2. Mimic the harness dispatch:
   - `await harness.EmoteExecutor.UseEmote(17u, new NpcId(1000789), true, ct)`.
   - `harness.QuestState.SetQuestFlagBit(new QuestId(82009), 3, true)` (or the established setter the existing UseActionStep tests use — Tester picks the matching setter).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Wait` (Expect now satisfies; step confirmed; no more steps).
- `harness.EmoteExecutor.RecordedCalls.Count == 1`.
- The recorded call's fields: `EmoteId == 17u`, `TargetNpcId == new NpcId(1000789)`, `Motion == true`.

#### UE10 — JSON round-trip with motion=true (default), TargetNpcId set

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs` (replaces the existing `UseEmoteStep_RoundTrips` at line 251).

**Given:** A `UseEmoteStep { Id = "salute", EmoteId = 7, TargetNpcId = 1000789u, Motion = true, Expect = PredicateExpect("questFlag(65657, 5)") }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, deserialize as `Step`.

**Then:**
- Deserialized value is a `UseEmoteStep`.
- `result.EmoteId == 7u`.
- `result.TargetNpcId == 1000789u`.
- `result.Motion == true`.

#### UE11 — JSON round-trip with motion=false explicit

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs` (sibling test).

**Given:** A `UseEmoteStep { Id = "yell", EmoteId = 17, TargetNpcId = null, Motion = false, Expect = PredicateExpect("questSequence(65657) >= 3") }`.

**When:** Serialize, deserialize.

**Then:**
- `result.EmoteId == 17u`.
- `result.TargetNpcId == null`.
- `result.Motion == false`.

#### UE12 — Motion field defaults to true when absent from JSON

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs` (sibling test).

**Given:** A JSON literal string:
```json
{
  "type": "use-emote",
  "id": "celebrate",
  "emoteId": 17,
  "expect": "questSequence(65657) >= 3"
}
```
(no `motion` field, no `targetNpcId` field).

**When:** Deserialize as `Step` via `QuestForgeJsonContext.QuestFileOptions`.

**Then:**
- Result is a `UseEmoteStep`.
- `result.EmoteId == 17u`.
- `result.TargetNpcId == null`.
- `result.Motion == true` (default).

Pins Decision UE2.

#### UE13 — Validator E9 (emoteId == 0)

**Located in** `QuestForge.Engine.Tests` validator test file (existing `DraftValidatorTests.cs` or sibling).

**Given:** A `QuestDraft` containing a UseEmoteStep with `EmoteId = 0, TargetNpcId = 1000789u, Expect = PredicateExpect("questFlag(82013, 3)")`. AcceptStep present (so E4 does not fire).

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E9"`.
- The error message mentions the step id and "EmoteId == 0".

#### UE14 — Validator E10 (targetNpcId == 0)

**Given:** A `QuestDraft` containing a UseEmoteStep with `EmoteId = 17u, TargetNpcId = 0u, Expect = PredicateExpect("questFlag(82014, 3)")`. AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E10"`.
- The error message mentions the step id and "TargetNpcId == 0".

Defensive sub-case: a UseEmoteStep with `TargetNpcId = null` MUST NOT trigger E10 (the null branch is allowed). Optionally include a second assertion in the same test that `validator.Validate(draftWithNullTarget).Errors` does not contain E10.

#### UE15 — Validator W8 (missing Expect) + W1 suppression

**Given:** A `QuestDraft` containing a UseEmoteStep with `EmoteId = 17u, TargetNpcId = null, Expect = null` (missing). AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `warnings` contains exactly one entry with `Code == "W8"`.
- `warnings` does NOT contain an entry with `Code == "W1"` referencing the same step (W1 is suppressed for UseEmoteStep per Decision UE7).

### Pure-helper tests (`QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs`)

#### UE16 — Resolve happy path: lookup returns "/cheer", motion=true → "/cheer motion"

**Given:** A lookup callback `id => id == 17u ? "/cheer" : null`.

**When:** `EmoteCommandResolver.Resolve(emoteId: 17u, motion: true, lookup)`.

**Then:** `result.IsSuccess == true && result.ValueOrThrow == "/cheer motion"`.

#### UE17 — Resolve happy path: motion=false → bare command

**Given:** A lookup callback `id => id == 17u ? "/cheer" : null`.

**When:** `EmoteCommandResolver.Resolve(emoteId: 17u, motion: false, lookup)`.

**Then:** `result.IsSuccess == true && result.ValueOrThrow == "/cheer"`.

#### UE18 — Resolve fails when lookup returns null

**Given:** A lookup callback that returns null for every input.

**When:** `EmoteCommandResolver.Resolve(emoteId: 9999u, motion: true, lookup)`.

**Then:**
- `result.IsSuccess == false`.
- The failure's reason code is `"emoteCommandNotFound"`.
- The failure's detail string contains `"9999"`.

Optional defensive sibling: lookup returns `"cheer"` (no leading slash) → `Result.Fail("emoteCommandMalformed", ...)`. Builder may add this as `UE18b` for full coverage of the resolver's branches.

---

## Implementation order

**Phase A — Schema (10 min)**
1. Replace `UseEmoteStep` in `QuestForge.Schema/Step.cs` per Decision UE3.
2. (No JSON context change — `[JsonSerializable(typeof(UseEmoteStep))]` already registered at line 21.)
3. Update `RoundTripTests.UseEmoteStep_RoundTrips` to the new shape (UE10) and add two sibling tests (UE11, UE12).
4. Run round-trip tests — must be green before proceeding.

**Phase B — Adapter surface (10 min)**
1. Create `QuestForge.Adapters/Emotes/IEmoteExecutor.cs`.
2. Create `QuestForge.Adapters/Emotes/EmoteCommandResolver.cs` per Decision UE8.

**Phase C — Pure-helper tests (10 min, TDD)**
1. Create `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs` with UE16, UE17, UE18 (+ optional UE18b).
2. Make them green (resolver body is ~6 lines).

**Phase D — Fake (5 min)**
1. Create `QuestForge.Adapters.Fakes/Emotes/FakeEmoteExecutor.cs` per Decision UE15.

**Phase E — Engine (25-30 min, TDD)**
1. Append `EngineAction.UseEmote` record per Decision UE10.
2. Add `_emoteExecutor` field + constructor param to `QuestEngine` per Decision UE11.
3. **Tester writes UE1, UE3, UE5** (single-tick dispatch shape; cheapest). Red.
4. Insert async pre-arm in `ResolveAction` (mirror Decision UA14 — placement between UseAction async arm and TeleportStep async arm) + implement `ResolveUseEmote` per Decision UE5. Green.
5. Tester writes UE2 (NPC-target variant). Green (no engine change).
6. Tester writes UE8 (Expect short-circuit). Green (no engine change).

**Phase F — Harness wiring (10 min)**
1. `EngineTestHarness` gains `EmoteExecutor` property + constructor passthrough.
2. `RunToCompletion` gains the `UseEmote` arm per Decision UE14.
3. Tester writes UE4 (stateless retry via manual two-tick).
4. Tester writes UE9 (integration two-tick).
5. Tester writes UE6 (lazy-dismount with prior Navigate) and UE7 (standalone, no dismount).
6. Make them green.

**Phase G — Validator (10 min, TDD)**
1. **Tester writes UE13, UE14, UE15** in the existing draft-validator test file (red).
2. Add E9 + E10 + W8 + W1 exclusion to `QuestForge.Engine/Authoring/DraftValidator.cs` per Decision UE7.
3. Green.

**Phase H — Dalamud impl (15 min)**
1. Create `QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs` per Decision UE16.
2. Verify it compiles (ECommons + Lumina + FFXIVClientStructs all already referenced in the .csproj).

**Phase I — EngineHost wiring (10 min)**
1. Add `_emoteExecutor` field + ctor construct per Decision UE17.
2. Modify `BeginRun` to pass `emoteExecutor:` per Decision UE17.
3. Add `case EngineAction.UseEmote ue:` arm to `DispatchAction` per Decision UE13.

**Phase J — In-game smoke (20-30 min, manual)**
1. Author a small quest (or local fixture) containing `{ "type": "use-emote", "emoteId": 17, "targetNpcId": <some-NPC-near-you>, "motion": true, "expect": { ... } }`.
2. Run on character.
3. Verify the emote animation plays AND the NPC is targeted AND no chat broadcast occurs.
4. Repeat with `motion: false` and confirm the broadcast appears.
5. Smoke-fail mode: pick an unknown EmoteId (e.g. 99999) and confirm the engine surfaces the `emoteCommandNotFound` failure cleanly.

Total dev time: ~1.5-2 hours code + 30 min smoke.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseEmoteStepTests` reports all ~12 engine tests green.
2. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~EmoteCommandResolverTests` reports all 3 (or 4 with optional UE18b) tests green.
3. `dotnet test QuestForge.Schema.Tests --filter FullyQualifiedName~UseEmoteStep` reports UE10, UE11, UE12 green; the legacy `UseEmoteStep_RoundTrips` test (at the old shape) no longer exists.
4. Draft validator tests for UE13, UE14, UE15 are green.
5. A quest JSON file with `{ "type": "use-emote", "emoteId": 17, "targetNpcId": 1000789, "motion": true }` round-trips through `QuestForgeJsonContext.QuestFileOptions` losslessly.
6. `dotnet build` succeeds (no `TreatWarningsAsErrors` regressions in any project).
7. `EngineHost.BeginRun` constructs `QuestEngine` with `emoteExecutor: _emoteExecutor` (no longer null).
8. The `case EngineAction.UseEmote:` arm exists in `EngineHost.DispatchAction` between the UseAction and Wait cases.
9. Running an authored quest containing a `use-emote` step on a character causes the emote command to fire in-game (animation plays, NPC targeted). Manual verification.
10. The engine's `ResolveUseEmote` no longer returns the `AwaitUser("...no IEmoteExecutor wired...")` fallback when `EngineHost` is the host.
11. No regression in `UseActionStepTests`, `TeleportStepTests`, `PurchaseItemStepTests`, `AttunementStepTests`, or any existing test.

---

## Exclusions (what this plan does NOT include)

- **Authoring-mode inference** of UseEmoteStep from observed `/cheer` / `/salute` chat outputs (Decision UE18). Phase 9 follow-up; the cleanest hook is outbound chat capture, shared with SayChatMessageStep inference.
- **Repeated emotes** (use the same emote N times). The engine's stateless retry handles "fire until Expect satisfies"; explicit repeat-count semantics not needed.
- **Emote-as-postcondition predicates** (e.g. `playerLastEmote(17)`). No predicate-language extension here; authors rely on the NPC-side state change (quest flag, sequence) that the emote triggers.
- **EmoteController polling** (using FFXIVClientStructs `EmoteController` to detect the emote landed). Out of scope; `Result.Ok` on `Chat.SendMessage` return is the success contract per Decision UE16.
- **Macro-based emote sequences** (run `/macro emote-combo`). The schema-side step type would be `MacroStep`, not `UseEmoteStep`; a separate slice.
- **Per-emote validator allow-list** (reject `emoteId` values that don't correspond to a real Lumina Emote row). The validator does not have Lumina access; the runtime's `EmoteCommandResolver` returns `emoteCommandNotFound` for invalid rows, which surfaces as a clean dispatch failure rather than a silent no-op.
- **Direct test for `DalamudEmoteExecutor`** (the Dalamud shell). Validated by smoke; the pure helper `EmoteCommandResolver` carries the testable logic per Decision UE19. If/when a Dalamud-bound test project is set up (the same future-ticket item from `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-3), one can add tests for the ObjectTable scan + Lumina preload there.
- **Quest data file for the first use-emote-bearing quest.** Authored by the data team; this plan only proves the engine + shell surface.
- **In-combat behavior of emotes.** Some animations are interrupted by combat; the engine does not gate on this (Decision UE5). If interrupted-emote-causes-loop becomes a real problem, add an `InCombat` predicate to the step's `SkipIf` rather than baking it into the engine.

---

## Open questions / decisions to call out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| Should `IEmoteExecutor` extend `IActionExecutor`? | **No — separate focused adapter** | Different mechanism (chat command vs ActionManager); status semantics don't apply. | UE1 |
| `Motion` default true or false? | **True** (suppress broadcast) | Quest automation almost never wants chat broadcasts. | UE2 |
| Replace `Target: NpcLocation?` with `TargetNpcId: uint?`? | **Yes — replace** | Zone/Position on emote steps is misleading; mirrors UseActionStep precedent. | UE3 |
| Engine synthesise default `Expect`? | **No — author-required** | No universal postcondition; loss mode (spin-loop) is recoverable. | UE4 |
| InCombat guard? | **No** | Combat is the player's problem; emotes generally still animate. | UE5 |
| Add `UseEmote` to dismount-exemption list? | **No** | No game-side auto-dismount; NPC scripts may check pose. | UE6 |
| Validator: emoteId / targetNpcId / missing-Expect? | **Add E9 / E10 / W8 in this PR** | Small surface; consolidates with the existing E7 / E8 / W7 for UseActionStep. | UE7 |
| Where does `EmoteCommandResolver` live? | **`QuestForge.Adapters`** (Dalamud-free) | Pure helper; no FFXIVClientStructs types; testable directly. | UE8 |
| Preload Lumina Emote sheet at adapter construction? | **Yes — eager preload** | Small sheet, stable for plugin lifetime, follows `AetheryteZoneMap.Populate` precedent. | UE9 |
| `Chat.SendMessage` discard return / check return? | **Discard** (try/catch surfaces exceptions) | Same posture as `am->UseAction`; engine uses `Expect` to verify outcome. | UE16 |
| Bundle Dalamud impl in this slice? | **Yes** — small (~80 lines incl preload) | Avoids cross-PR coordination; pure helper still gets its own test file. | UE19 |
| Authoring inference in this slice? | **No** | Phase 9 follow-up; outbound chat capture is the right hook then. | UE18 |

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 4 scenarios (UE1, UE2, UE9, UE16+UE17 in helper)
- Edge cases: 4 scenarios (UE6, UE7, UE8, UE12)
- Error / wait cases: 3 scenarios (UE3, UE4, UE5)
- Validator: 3 scenarios (UE13, UE14, UE15)
- Serialization: 2 scenarios (UE10, UE11) + UE12 (counted above)
- Pure helper: 3 scenarios (UE16, UE17, UE18) + optional UE18b
- Expected total:
  - `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs`: ~9 tests (UE1–UE9)
  - `QuestForge.Schema.Tests/RoundTripTests.cs`: 3 tests (UE10 replaces existing; UE11, UE12 new)
  - Draft validator tests (existing test file or sibling): 3 tests (UE13, UE14, UE15)
  - `QuestForge.Adapters.Tests/Emotes/EmoteCommandResolverTests.cs`: 3-4 tests (UE16, UE17, UE18, optional UE18b)
  - Grand total: ~18-19 tests across four projects.
