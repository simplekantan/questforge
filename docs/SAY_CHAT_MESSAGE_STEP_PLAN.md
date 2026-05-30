# SayChatMessageStep Implementation Plan (Slice 2)

**Status:** ready for test creation

**Slice:** 2 (engine + schema + validator). Slice 1 = this spec. Slice 3 = Dalamud impl + EngineHost dispatch arm + tooling catch-up. Slice 4 = in-game smoke. Slice 5 = authoring inference.

**Input docs:**
- `CLAUDE.md` — "Adding a New Step Type — Fixed Slice Order" (this plan is Slice 1; the scope of work it describes is Slice 2)
- `docs/USE_EMOTE_STEP_PLAN.md` — closest analog (UE1–UE19). The shape of this plan mirrors it 1:1.
- `docs/USE_ACTION_STEP_PLAN.md` — the source decisions UE inherited (UA4 author-required Expect, UA12 optional executor, UA13 do-not-set-`_lastResolvedStep`)
- `QuestForge.Schema/Step.cs` lines 21 (polymorphic discriminator), 150–155 (current `SayChatMessageStep` placeholder — being replaced; see Decision SC1)
- `QuestForge.Schema/QuestForgeJsonContext.cs` line 20 — `[JsonSerializable(typeof(SayChatMessageStep))]` already present
- `QuestForge.Schema.Tests/RoundTripTests.cs` lines 235–248 — existing `SayChatMessageStep_RoundTrips` test (will be replaced — see Task SCT-1)
- `QuestForge.Engine/EngineAction.cs` lines 37–41 — `UseEmote` record (the template for the new `SayChatMessage` record)
- `QuestForge.Engine/QuestEngine.cs` lines 580–594 (step-dispatch async arms), 839–871 (`ResolveUseAction`), 873–885 (`ResolveUseEmote` — the closest template for `ResolveSayChatMessage`)
- `QuestForge.Adapters/Emotes/IEmoteExecutor.cs` — focused-adapter precedent
- `QuestForge.Adapters.Fakes/Emotes/FakeEmoteExecutor.cs` — fake-with-recording-and-scripting precedent
- `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` — test layout template (UE1–UE9 → SC1–SC9)
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` lines 57 (`EmoteExecutor` property), 129 (ctor passthrough), 221–228 (`RunToCompletion` arm)
- `QuestForge.Engine/Authoring/DraftValidator.cs` lines 100–157 (E9 / E10 / W8 + W1 suppression) — the template for SC11 / SC12 / SC13
- `QuestForge.Plugin/EngineHost.cs` lines 47 (field), 113 (ctor construct), 141 (Debug accessor), 464–474 (`UseEmote` dispatch arm) — Slice 3 placement reference; NOT implemented in this slice but documented for the next architect
- ECommons `Chat.SendMessage(string)` API at `C:\Users\publi\RiderProjects\Lifestream\ECommons\ECommons\Automation\Chat.cs` line 68 — the public API the Dalamud shell will use in Slice 3

**Output (CI behavior):** Adding a `{ "type": "say-chat-message", "message": "...", "targetNpcId": 1000789 }` step to a quest dispatches a new `EngineAction.SayChatMessage` from `QuestEngine`. Engine unit tests (xUnit, `QuestForge.Engine.Tests`) cover dispatch arms against `FakeChatSender`. Round-trip tests in `QuestForge.Schema.Tests` cover the rewritten field shape. Validator tests in `QuestForge.Engine.Tests/Authoring/` cover the three new draft-validator rules. **The Dalamud shell + `EngineHost` dispatch arm + tooling catch-up are deferred to Slice 3.** The placeholder `SayChatMessageStep` in `Step.cs` is replaced (no schema-version bump required — no shipped quest authors the old shape).

> **Note on the existing placeholder.** `QuestForge.Schema/Step.cs:150–155` currently defines `SayChatMessageStep { Channel, Message, Target: NpcLocation? }`. **This plan replaces that shape** — none of those fields is referenced from the engine today (no `ResolveSayChatMessage` exists; the engine throws `NotSupportedException` via the default arm). The replacement does **not** require a schema-version bump because no shipped quest authors the old shape. The existing round-trip test `RoundTripTests.SayChatMessageStep_RoundTrips` (line 236) **will be updated** as part of Task SCT-1. The `ExportDialog` mapping path (mirrors UE's already-emits-`"use-emote"` situation) will need a `"say-chat-message"` discriminator check verified during Slice 2 implementation; if present it requires no change.

---

## Dependency graph

```
QuestForge.Schema
   └── SayChatMessageStep (rewritten) + JSON registration (already present)
        └── consumed by ↓
QuestForge.Adapters
   └── new IChatSender (in QuestForge.Adapters/Chat/IChatSender.cs)
        └── consumed by ↓
QuestForge.Adapters.Fakes
   └── FakeChatSender (in QuestForge.Adapters.Fakes/Chat/FakeChatSender.cs)
        └── consumed by ↓
QuestForge.Engine
   └── new EngineAction.SayChatMessage; ResolveSayChatMessage async pre-arm in QuestEngine.cs
   └── DraftValidator.cs: E11 + E12 + W9 + W1 suppression extension
        └── consumed by ↓
QuestForge.Engine.Tests
   └── Engine/SayChatMessageStepTests.cs against FakeChatSender
   └── Authoring/DraftValidatorSayChatMessageTests.cs (or extend existing DraftValidatorTests.cs)
```

**Build order:**
1. Schema (`SayChatMessageStep` rewrite — JSON-context entry already exists). Round-trip tests gate it.
2. `IChatSender` interface in `QuestForge.Adapters/Chat/`.
3. `FakeChatSender` in `QuestForge.Adapters.Fakes/Chat/`.
4. `EngineAction.SayChatMessage` record.
5. `QuestEngine` field, optional ctor param, `ResolveSayChatMessage` async pre-arm + dispatch wiring.
6. `EngineTestHarness` wires `FakeChatSender`; `RunToCompletion` gains a `SayChatMessage` arm.
7. Engine tests SC1–SC9.
8. Round-trip tests SC10–SC11.
9. Validator extension (E11 / E12 / W9 + W1 exclusion) and tests SC12–SC14.

(No pure-helper analog of `EmoteCommandResolver` is needed — see Decision SC8.)

---

## Architectural decisions (read before coding)

### Decision SC1 — `SayChatMessageStep` schema is rewritten; `Channel` and `Target: NpcLocation?` fields removed

Existing placeholder (`Step.cs:150–155`):
```csharp
public class SayChatMessageStep : Step
{
    public string Channel { get; init; } = default!;  // "say" | "yell" | "shout"
    public string Message { get; init; } = default!;
    public NpcLocation? Target { get; init; }
}
```

**Replace with:**
```csharp
// QuestForge.Schema/Step.cs (replaces existing SayChatMessageStep)
public sealed class SayChatMessageStep : Step
{
    /// <summary>
    /// The literal text to type after the "/say " prefix. The engine sends this verbatim
    /// to the game's chat box. Quest data is authored in English; the in-game chat parser
    /// matches case-insensitively but is otherwise locale-literal. See Decision SC3 for
    /// the localization-limitation note.
    /// </summary>
    public string Message { get; init; } = default!;

    /// <summary>
    /// Optional NPC target. Null means "no target write — the chat command is sent without
    /// adjusting TargetManager.Target." When set, the Dalamud adapter resolves this BaseId
    /// via ObjectTable and writes TargetManager.Target before submitting the command so
    /// the player faces the NPC while speaking.
    /// NpcId here is the BNpcBase / ENpcBase data-id, matching InteractStep / TalkStep /
    /// UseActionStep / UseEmoteStep target conventions.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? TargetNpcId { get; init; }
}
```

**Why drop `Channel`:** the user has confirmed the engine always sends `/say`. Quests requiring `/yell` or `/shout` are vanishingly rare in the corpus; if one ever needs `/yell` we add a second step type (`YellChatMessageStep`) rather than carrying a per-instance channel discriminator that 99.9% of authors will set to `"say"`. Removing the field also removes a class of validator drift ("channel must be one of …") and a class of in-game routing bugs ("authored 'say' but adapter sent 'yell'").

**Why drop `Target: NpcLocation?` in favor of `TargetNpcId: uint?`:** identical reasoning to Decision UE3 in `USE_EMOTE_STEP_PLAN.md`. `NpcLocation` bundles `(NpcId, Zone, Position)` — Zone and Position are travel information that does NOT belong on a say step (the preceding TravelStep / TalkStep handles positioning). Mirrors `UseActionStep.TargetNpcId` and `UseEmoteStep.TargetNpcId` so authors learn one targeting idiom across all "do-something-at-an-NPC" steps.

**Why `sealed class`:** matches the post-rewrite `UseEmoteStep` (`Step.cs:157` `public sealed class UseEmoteStep`). New step types added to the schema should be sealed (no use case for inheritance).

**JSON sample (target set):**
```json
{
  "type": "say-chat-message",
  "id": "say-the-magic-word",
  "message": "Open Sesame",
  "targetNpcId": 1000789,
  "expect": "questFlag(65657, 4)"
}
```

Self-cast variant (no target):
```json
{
  "type": "say-chat-message",
  "id": "shout-into-the-void",
  "message": "Hello",
  "expect": "questSequence(65657) >= 3"
}
```

**What breaks if violated:** if `Channel` is kept, the engine must validate it on every dispatch (yet another switch arm), the validator must enforce its enumerated values, and authors will get the channel-mismatch bug class for free. If `Target: NpcLocation?` is kept, every say-step that targets an NPC carries Zone/Position the engine ignores, and authors will mistakenly believe authoring `Target.Position` enables auto-walk.

### Decision SC2 — `/say` is hard-wired; no `Channel` field, no engine override

The engine's `ResolveSayChatMessage` constructs the payload string deterministically: `"/say " + step.Message`. The Dalamud shell submits it via `ECommons.Automation.Chat.SendMessage`. There is no engine config for the channel.

**Why a hard-wired prefix is acceptable as a schema-level decision:** the prefix is part of the step type's *meaning*. Adding a new chat-channel step is a schema additive change (new discriminator string), not a runtime config. This is the same posture as `TeleportStep` (the engine never asks "should this teleport go to an aetheryte or aethernet shard?" — that's the step type's identity).

**What breaks if violated:** authoring an explicit `channel` field means every consumer (validator, exporter, inference, debug UI) must learn about it; the surface area scales with the number of channels even though the corpus uses one.

### Decision SC3 — Message is a literal string; English-only is an explicit limitation

Quest data carries the exact text the engine will send (e.g. `"Open Sesame"`). The Dalamud adapter does not translate, lookup, or fingerprint the message — `Chat.SendMessage("/say " + message)` runs verbatim.

**Known limitation (documented for authors):** quests authored against an English client store English text. On non-English clients the message will be sent literally and the NPC's recognition trigger may not fire (FFXIV chat-recognition for quest scripts is text-match against the localized script string). This is the same trade-off Questionable accepted; a Lumina-text-ID-based system would be a future improvement (see Decision SC9 deferral). Authors targeting non-English clients today must clone the step and substitute the localized text per locale — but no quest in the planned corpus does this, so it is YAGNI for v1.

**Why not store a Lumina text-id (e.g. `messageTextId: 12345`):** the FFXIV `CustomTalk` / `EventItemHelp` sheets that drive these prompts are *internal* IDs — the player's chat input is matched against the localized display string, not a row id. The naive `dataManager.GetExcelSheet<...>()[textId].Text.ExtractText()` returns the *English* string on an English client and the *French* string on a French client — but that requires the data team to know which sheet+row the quest script is matching against. Today no validated source documents this mapping. Literal strings are a pragmatic v1; deferring the Lumina-id approach to a future slice avoids designing a system on undocumented data.

**Validator implication:** the empty-string check (Decision SC7, rule E11) is the only message-content validation. There is no allow-list, no length cap, no character allow-list (Chat.SendMessage itself rejects pathological strings — see Decision SC9).

### Decision SC4 — `IChatSender` is a new focused adapter (not extended into `IEmoteExecutor` or `IInteractor`)

Mirrors Decision UE1's split-rather-than-extend posture.

`IInteractor` is for "click NPC / advance dialogue / accept quest" — purely interaction, not chat. `IEmoteExecutor` is for slash-command-via-chat-box but with Lumina-resolved commands and a Motion suffix. The say-message pathway is "submit a literal command verbatim, with optional pre-call target write" — different enough on both axes (no Lumina lookup, no Motion concept) that bundling would force every consumer to learn about both contracts.

**Concrete shape:**

```csharp
// QuestForge.Adapters/Chat/IChatSender.cs (new file, new namespace)
namespace QuestForge.Adapters.Chat;

using QuestForge.Adapters.Types;

/// <summary>
/// Sends a literal chat message (always with the "/say " prefix per Decision SC2)
/// optionally targeting an NPC. The Dalamud implementation owns:
///   - target acquisition (set TargetManager.Target before sending the command), and
///   - chat submission (ECommons.Automation.Chat.SendMessage).
///
/// Success means "the chat command was submitted." It does NOT mean "the NPC reacted."
/// The engine verifies outcome via the authored Expect predicate.
/// </summary>
public interface IChatSender
{
    /// <summary>
    /// Submits a /say chat command containing <paramref name="message"/>, optionally
    /// after writing TargetManager.Target to the NPC matching <paramref name="targetNpcId"/>.
    /// </summary>
    /// <param name="message">The literal message text. The engine sends "/say " + message verbatim.</param>
    /// <param name="targetNpcId">Optional NPC target; null means "no target write".</param>
    /// <param name="ct">Cancellation token. Throws OperationCanceledException if cancelled before submission.</param>
    /// <returns>
    ///   Result.Ok() on submission success.
    ///   Result.Fail("targetNotFound", ...) if targetNpcId is supplied but no matching ObjectTable entry exists.
    ///   Result.Fail("chatSendFailed", ex.Message) if ECommons.Chat.SendMessage threw (well-formed
    ///   "/say x" messages should never trip this in practice — see Decision SC9).
    /// </returns>
    Task<Result<Unit>> Send(
        string message,
        NpcId? targetNpcId,
        CancellationToken ct);
}
```

**Naming rationale:**
- `IChatSender` (not `ISayExecutor`): the interface is focused on the "submit a chat message" pathway. Tying the *interface* name to `say` would mislead — if we ever add a `/yell` step (Decision SC2 explicitly allows a new step type) it would reuse the same Dalamud-chat submission pathway with a different prefix; a `IChatSender.Send(message, target, ct)` shape can grow a second method or a discriminator without renaming.
- `Send` (not `SaySayChat` / `SayChat`): the verb is the operation; the channel is the step type's identity (Decision SC2). One method, one operation, clean shape.

**Why no `GetStatus` analog (matches Decision UE1):** chat has no cooldown, no resource gate, no game-side "unavailable" verdict. If the game silently rejects the command (mid-cast, system message blocked input, cutscene grab-bag), the engine's stateless retry recovers next tick. Adding a status read would invent semantics the game does not expose.

**What breaks if violated:** if chat-submission is folded into `IEmoteExecutor`, every emote test learns about the say pathway and vice versa, and the `EmoteCommandResolver` pure helper's signature has to handle both Lumina-resolved and literal-command flows — a complexity tax for no benefit.

### Decision SC5 — Engine pre-arm: single guard (player casting), no other gates

Identical posture to Decision UE5: the only meaningful guard is the casting check. The game silently rejects chat commands submitted while the player is casting; deferring via `Wait` until the cast finishes is correct.

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `EngineAction.Wait("player casting; deferring say-chat-message", Origin: step)` |
| 2. (none — proceed to emit) | — | `EngineAction.SayChatMessage(message, target, Origin: step)` |

**No InCombat guard:** chat works in combat (the player can `/say` mid-fight). Combat is the player's problem.

**No "target validity" engine-side guard:** if the supplied `TargetNpcId` is not in ObjectTable, the adapter returns `Result.Fail("targetNotFound", …)` and the engine retries next tick. Pre-validating in the engine would require a `GetObjectInScene` adapter read on every emission and would still race the ObjectTable.

**Concrete shape:**

```csharp
// QuestForge.Engine/QuestEngine.cs — new method, placed immediately after ResolveUseEmote (~line 886)
private async Task<EngineAction> ResolveSayChatMessage(SayChatMessageStep step, CancellationToken ct)
{
    if (_chatSender is null)
        return new EngineAction.AwaitUser(
            "SayChatMessageStep dispatched but no IChatSender wired — host must supply one");

    // Guard 1: casting → Wait (defer until cast finishes)
    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring say-chat-message", Origin: step);

    var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
    return new EngineAction.SayChatMessage(step.Message, target, Origin: step);
}
```

**Optional executor mirrors Decision UE11 / UA12.** `IChatSender? chatSender = null` is the last QuestEngine constructor parameter. Absence yields a loud `AwaitUser` instead of a crash, preserving the old-tests-don't-break invariant.

**Do NOT set `_lastResolvedStep`:** mirrors Decision UE12 / UA13. `SayChatMessageStep` does not carry `DialogueChoices`; `ExtractYesNo` returns null for it; setting `_lastResolvedStep` would be wrong-pattern noise.

### Decision SC6 — Lazy-dismount applies; SayChatMessage is NOT in the dismount-exemption list

Mirrors Decision UE6. Quest-triggering scripts often check the player's pose (standing vs mounted) when matching `/say` input — and even when they don't, dismounting before facing the NPC is the "cleanest" pose for the user to observe. The lazy-dismount hook in `EngineHost` (`_lastDispatchedActionWasNavigate` → dismount before non-Navigate / non-Teleport actions) already covers this. The `EngineHost.DispatchAction` arm (Slice 3) must NOT add `SayChatMessage` to the exemption list.

**Action required in this plan:** add regression tests pinning the behavior (SC6 mounted+prior-Navigate, SC7 standalone-mounted). No engine code change required — the dismount hook is in `EngineHost`, which Slice 2 does not touch.

**Note on harness coupling:** the engine-side mount-state-aware behavior tested in SC6 / SC7 piggybacks on the existing `EngineTestHarness.Mount` fake and the `_lastDispatchedWasNavigate` tracking already wired in `RunToCompletion`. No harness changes needed beyond the `FakeChatSender` plumbing (Decision SC10).

### Decision SC7 — Validator rules added in this slice (mirror Decision UE7)

Three new rules consolidated alongside the existing UseAction (E7/E8/W7) and UseEmote (E9/E10/W8) rules.

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| `message` non-empty | `E11` | Error | `SayChatMessageStep.Message` is null or empty (`string.IsNullOrEmpty`) | — |
| `targetNpcId` non-zero when present | `E12` | Error | `SayChatMessageStep.TargetNpcId == 0` (null is allowed) | — |
| `expect` authored | `W9` | Warning | `SayChatMessageStep.Expect is null` | — |
| W1 ("step has no Expect") | `W1` | Warning | (existing rule) | extended exclusion: `not UseActionStep and not UseEmoteStep and not SayChatMessageStep` |

**Why E11 / E12 / W9:** the next free codes after E10 / W8 (the UseEmote validator rules). Numbering continues the flat scheme.

**W1 suppression:** the existing W1 rule excludes UseActionStep and UseEmoteStep so W7 / W8 can fire instead with their stronger "engine will spin-loop" messaging. Extend the exclusion to include `SayChatMessageStep` so W9 fires for missing-Expect on a say-step without W1 duplicating. The W9 message must contain `"spin-loop"` (per `CLAUDE.md` slice 2 spec: "The W# message must contain 'spin-loop' so authors understand the runtime cost.").

**Concrete shape:**

```csharp
// QuestForge.Engine/Authoring/DraftValidator.cs — insert after the existing E10 block (~line 120)

// E11: SayChatMessageStep with empty Message
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is SayChatMessageStep sc && string.IsNullOrEmpty(sc.Message))
    {
        errors.Add(new DraftValidationError("E11",
            $"Step '{steps[i].StepId}' is a SayChatMessageStep with an empty Message.",
            [i]));
    }
}

// E12: SayChatMessageStep with TargetNpcId == 0 (null is allowed; explicit zero is invalid)
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is SayChatMessageStep sc && sc.TargetNpcId == 0)
    {
        errors.Add(new DraftValidationError("E12",
            $"Step '{steps[i].StepId}' is a SayChatMessageStep with TargetNpcId == 0.",
            [i]));
    }
}

// W1 extension (existing block at ~line 127): change the type guard
if (step.Raw.Expect is null
    && step.Raw is not UseActionStep
    && step.Raw is not UseEmoteStep
    && step.Raw is not SayChatMessageStep)
{
    // ...existing W1 emit...
}

// W9: SayChatMessageStep with no Expect — engine spin-loops without one (SC7 stronger than W1)
for (var i = 0; i < steps.Count; i++)
{
    var step = steps[i];
    if (step.Raw is SayChatMessageStep sc && sc.Expect is null)
    {
        warnings.Add(new DraftValidationWarning("W9",
            $"Step '{step.StepId}' is a SayChatMessageStep with no 'expect' predicate — without it the engine will spin-loop re-emitting the message. Add an expect predicate.",
            [i]));
    }
}
```

**Pinned by tests:** SC12, SC13, SC14.

### Decision SC8 — No pure helper (no `ChatMessageResolver` / `SayCommandFormatter`)

Unlike Decision UE8 (`EmoteCommandResolver` extracts `id → "/cheer motion"` formatting because the Lumina lookup, the `motion` suffix, and the leading-`/` defensive check are non-trivial), say-chat-message formatting is a single string concat: `"/say " + message`.

A pure helper for this would be one line — no branching, no Lumina, no resolver. Extracting it would be ceremony.

**Consequence for testing:** there is no `QuestForge.Adapters.Tests/Chat/ChatMessageResolverTests.cs` analog. The `"/say "` prefix lives in the Dalamud shell (Slice 3) and is verified by Slice 4 smoke. The engine never constructs the wire string — it passes `step.Message` and `target` through `EngineAction.SayChatMessage` to the adapter.

**What breaks if violated:** a `ChatMessageResolver` helper just to wrap one concat creates a function-call boundary with no testable failure modes (no inputs produce non-trivial outputs). It would be unused architecture.

### Decision SC9 — Dalamud shell uses `Chat.SendMessage("/say " + message)` (Slice 3 deferral)

The Slice 3 `DalamudChatSender` is essentially:

```csharp
// QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs (Slice 3 — NOT IN THIS PLAN)
public Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    // 1. Set target if requested (same ObjectTable scan pattern as DalamudEmoteExecutor).
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

    // 2. Submit "/say " + message.
    try
    {
        Chat.SendMessage("/say " + message);
    }
    catch (Exception ex)
    {
        return Task.FromResult<Result<Unit>>(
            Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
    }

    return Task.FromResult<Result<Unit>>(Result.Ok());
}
```

Mirrors `DalamudEmoteExecutor` exactly (the same ObjectTable scan, the same try/catch around `Chat.SendMessage`, the same fire-and-forget Result.Ok). The try/catch surfaces the ECommons documented exceptions (`ArgumentException` for empty/too-long/invalid-character messages, `InvalidOperationException` if the signature was not found).

**Slice 3 work that this plan calls out but does NOT implement:**
- `QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs` (new file).
- `QuestForge.Plugin/EngineHost.cs`:
  - Field `private readonly DalamudChatSender _chatSender;` after the `_emoteExecutor` field (line 47).
  - Construct in ctor: `_chatSender = new DalamudChatSender(services);` after line 113.
  - Pass to `QuestEngine` in `BeginRun` as `chatSender: _chatSender`.
  - `case EngineAction.SayChatMessage sc:` arm in `DispatchAction`, between `UseEmote` (line 464) and `Wait` (line 476), debounce-keyed on `$"saychat:{sc.Message.Length}:{sc.TargetNpcId?.Value}"` to avoid logging the full message text on every retry.
  - Optional: `public IChatSender DebugChatSender => _chatSender;` if other Debug accessors warrant it.
- `EngineHost.DispatchAction` arm: NOT in the exemption list (Decision SC6) — the existing lazy-dismount logic will dismount before the say.
- Tooling catch-up (`questforge-tools`):
  - `CapabilityInferrer.StepCapabilities`: add `[typeof(SayChatMessageStep)] = "step:say-chat-message"` (already present per FIXTURES.md line 116).
  - `TraceToFixtureExtractor.FilenameLookup`: add `(["step:talk", "step:travel", "step:say-chat-message"], "with-say-chat-message.json")`.
  - `TraceToFixtureExtractor.DistinguishingCapPriority`: add `"step:say-chat-message"` (priority lower than action/emote/teleport, higher than purchase-item — the say step is more shape-defining than purchase but less than action because it's text-driven).
  - `TraceConstants`: add `ActionSayChatMessage = "saychatmessage"` (lowercased `EngineAction.SayChatMessage`).
- `docs/FIXTURES.md`: add `step:say-chat-message` to the capabilities table (already present at line 116) and `"saychatmessage"` to the `actionType` canonical strings table.

These items are listed here so the Slice 3 architect / builder does not have to rediscover them.

### Decision SC10 — `FakeChatSender` exposes `RecordedCalls` + `ScriptNextFailure` (no status scripting)

Identical to `FakeEmoteExecutor`. No Motion analog, no Lumina-lookup analog.

**Concrete shape:**

```csharp
// QuestForge.Adapters.Fakes/Chat/FakeChatSender.cs
namespace QuestForge.Adapters.Fakes.Chat;

using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

public sealed class FakeChatSender : IChatSender
{
    public record SendCall(
        string Message,
        NpcId? TargetNpcId,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<SendCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    /// <summary>Forces Send to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> Send(
        string message, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new SendCall(message, targetNpcId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
```

### Decision SC11 — `EngineAction.SayChatMessage` record shape

```csharp
// QuestForge.Engine/EngineAction.cs (append after UseEmote at line 41)
public sealed record SayChatMessage(
    string Message,
    Adapters.Types.NpcId? TargetNpcId,
    Step? Origin = null) : EngineAction;
```

Carries the schema primitives directly. No prefix logic (Decision SC8 — the `"/say "` prefix is the Dalamud shell's responsibility).

### Decision SC12 — `EngineTestHarness` constructs `FakeChatSender` and `RunToCompletion` gains an arm

`EngineTestHarness` gains:
```csharp
public FakeChatSender ChatSender { get; } = new FakeChatSender();
```
…and passes `ChatSender` to the `QuestEngine` constructor (last positional kwarg).

`RunToCompletion` gains an arm mirroring the `UseEmote` arm at line 221:
```csharp
case EngineAction.SayChatMessage sc:
    actions.Add(action);
    EmitActionSubmitted("SayChatMessage", JsonSerializer.SerializeToElement(
        new { message = sc.Message, targetNpcId = sc.TargetNpcId?.Value },
        _jsonOpts));
    var scResult = await ChatSender.Send(sc.Message, sc.TargetNpcId, ct);
    EmitActionCompleted("SayChatMessage", scResult.IsSuccess ? "Done" : "Failed");
    break;
```

### Decision SC13 — Authoring inference is OUT OF SCOPE for this slice (Slice 5)

A future Slice 5 will detect "the player just typed `/say <text>`" during recording and synthesize a `SayChatMessageStep`. The signal source is genuinely harder than for emotes:

- For emotes, `Character.EmoteController.EmoteId` (FFXIVClientStructs) is a single field readable per frame from `IObjectTable.LocalPlayer`. The polling pattern is established (see `UseEmoteStep` Slice 5 inference plan).
- For outbound chat, there is no analogous "last-said-message" field on the local player object. Candidate signal sources require investigation:
  - **`RaptureLogModule`** (FFXIVClientStructs) — exposes the chat log buffer. Polling the latest entries for player-said messages (filter by source = local player, channel = Say) would yield the text. Open question: does the entry land in the log synchronously with `Chat.SendMessage`, or one frame later?
  - **`AgentChatLog`** or a related Agent — may expose a "current input buffer" or "last submitted command" field; needs research.
  - **Hooking outbound chat** (last resort) — intercepting `ProcessChatBoxEntry` or `Chat.SendMessage` itself. The CLAUDE memory entry on hooks (`feedback_no_hooks_without_user_ok`) means we must surface this to the user before adopting.

**The Slice 5 architect's first task will be signal research.** This Slice 2 plan does not constrain that choice; it merely calls out that the research is the hard part and that polling-based detection (matching the established `UIObserver.Poll*` pattern) is preferred over hooks.

The schema shape this plan adopts (literal `Message`, optional `TargetNpcId`) is inference-friendly: the recorded chat-log line contains both fields directly, no Lumina translation, no synthesis from multiple signals.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Schema/Step.cs` | MODIFY | Replace `SayChatMessageStep` per Decision SC1 |
| `QuestForge.Schema/QuestForgeJsonContext.cs` | (no change) | `[JsonSerializable(typeof(SayChatMessageStep))]` already present at line 20 |
| `QuestForge.Schema.Tests/RoundTripTests.cs` | MODIFY | Replace existing `SayChatMessageStep_RoundTrips` (line 236) with SC10 + add SC11 sibling |
| `QuestForge.Adapters/Chat/IChatSender.cs` | NEW | Adapter interface (Decision SC4) |
| `QuestForge.Adapters.Fakes/Chat/FakeChatSender.cs` | NEW | Fake with recording + scripting (Decision SC10) |
| `QuestForge.Engine/EngineAction.cs` | MODIFY | Append `SayChatMessage` record (Decision SC11) |
| `QuestForge.Engine/QuestEngine.cs` | MODIFY | Field, ctor param, `ResolveSayChatMessage` method, dispatch-switch wiring |
| `QuestForge.Engine/Authoring/DraftValidator.cs` | MODIFY | E11, E12, W9 + extend W1 exclusion (Decision SC7) |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | MODIFY | `ChatSender` property + ctor passthrough + `RunToCompletion` arm |
| `QuestForge.Engine.Tests/Engine/SayChatMessageStepTests.cs` | NEW | SC1–SC9 |
| `QuestForge.Engine.Tests/Authoring/DraftValidatorSayChatMessageTests.cs` (or extend existing) | NEW or MODIFY | SC12, SC13, SC14 |
| `QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs` | **Slice 3** | NOT IN THIS PLAN |
| `QuestForge.Plugin/EngineHost.cs` | **Slice 3** | NOT IN THIS PLAN |

---

## Validation rule table

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| `message` non-empty | `E11` | Error | `SayChatMessageStep.Message` is null or empty | — |
| `targetNpcId` non-zero when present | `E12` | Error | `SayChatMessageStep.TargetNpcId == 0` (null is allowed) | — |
| `expect` authored | `W9` | Warning | `SayChatMessageStep.Expect is null` | — |
| W1 ("step has no Expect") | `W1` | Warning | (existing rule) | extended exclusion: `not UseActionStep and not UseEmoteStep and not SayChatMessageStep` |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/SayChatMessageStepTests.cs`)

All tests follow the `UseEmoteStepTests` pattern: one `[Fact]` per scenario, `BuildSingleStepQuest` / `BuildTwoStepQuest` factories at the bottom. For each scenario:
- `harness.QuestState.SetQuestSequence(new QuestId(<questId>), 0)`.
- The quest contains exactly one `SayChatMessageStep` in sequence 0 (unless noted).
- The step has an authored `Expect` (PredicateExpect using a predicate that does NOT auto-satisfy from default fake state, unless the test specifically tests Expect satisfaction).
- Quest IDs reserved for this test file: **83001–83009** (UseEmote uses 82001–82009; offsetting by +1000 avoids collisions).

#### SC1 — Happy path, no target → emits SayChatMessage(message, null, Origin: step)

**Given:**
- Player not casting.
- `SayChatMessageStep { Message = "Open Sesame", TargetNpcId = null, Expect = PredicateExpect("questFlag(83001, 3)") }` (predicate false).

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.SayChatMessage(Message: "Open Sesame", TargetNpcId: null, Origin: <step>)`.
- `Origin` is not null.
- `harness.ChatSender.RecordedCalls.Count == 0` (engine returns the action; harness `RunToCompletion` would call the adapter, but this test ticks once).

#### SC2 — Happy path, NPC target → emits SayChatMessage with TargetNpcId set

**Given:**
- Player not casting.
- `SayChatMessageStep { Message = "Hello friend", TargetNpcId = 1000789u, Expect = PredicateExpect("questFlag(83002, 3)") }`.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.SayChatMessage(Message: "Hello friend", TargetNpcId: new NpcId(1000789), Origin: <step>)`.
- `sayChat.TargetNpcId!.Value == new NpcId(1000789)`.

#### SC3 — Player casting → Wait; no SayChatMessage emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `SayChatMessageStep` as SC1.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"` (case-insensitive).
- `harness.ChatSender.RecordedCalls.Count == 0`.

#### SC4 — Adapter Send returns Result.Failure → stateless retry on next tick

**Given:**
- `SayChatMessageStep` as SC1 (no target, Expect false).
- `harness.ChatSender.ScriptNextFailure("chat-failed", "Chat.SendMessage threw")`.

**When:**
1. Tick 1 → returns `EngineAction.SayChatMessage(...)`.
2. Manually call `await harness.ChatSender.Send("Open Sesame", null, ct)` (consumes the scripted failure; returns `Result.Failure`; recorded call appended).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.SayChatMessage(...)` again — stateless retry.
- `harness.ChatSender.RecordedCalls.Count == 1` (only the manual call; engine emits the action but does not invoke the adapter directly).

#### SC5 — Cancellation propagates from Tick

**Given:**
- `SayChatMessageStep` as SC1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### SC6 — Mounted + prior Navigate: lazy-dismount fires before SayChatMessage

**Given:**
- Two-step quest in sequence 0:
  1. `TravelStep` navigating to `(200, 0, 0)` in zone `130` with `Expect = "playerZone() == 130"`.
  2. `SayChatMessageStep { Message = "Open Sesame", TargetNpcId = null, Expect = PredicateExpect("questFlag(83006, 3)") }` (predicate false).
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 → `EngineAction.Navigate`. After this tick `_lastDispatchedWasNavigate = true` in the harness.
2. Advance state: `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect now satisfies).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.SayChatMessage(...)`.
- `harness.Mount.DismountCallCount >= 1` (lazy-dismount fired — SayChatMessage is NOT in the exemption list per Decision SC6).

Pins Decision SC6.

#### SC7 — Standalone SayChatMessage + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: `SayChatMessageStep` as SC1.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.SayChatMessage(...)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount is bound to *prior Navigate*).

#### SC8 — Authored Expect already satisfied → step skipped → no SayChatMessage emitted

**Given:**
- `SayChatMessageStep { Message = "Open Sesame", TargetNpcId = null, Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true)` so the predicate is true *before* the step runs.

**When:** `harness.Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits dispatch; step confirmed; no more steps).
- `harness.ChatSender.RecordedCalls.Count == 0`.

#### SC9 — Integration two-tick: SayChatMessage fires, Expect satisfies, step completes

**Given:**
- `SayChatMessageStep { Message = "Open Sesame", TargetNpcId = 1000789u, Expect = PredicateExpect("questFlag(83009, 3)") }`.

**When:**
1. Tick 1 → `EngineAction.SayChatMessage(...)`.
2. Mimic the harness dispatch:
   - `await harness.ChatSender.Send("Open Sesame", new NpcId(1000789), ct)`.
   - `harness.QuestState.SetQuestFlagBit(new QuestId(83009), 3, true)` (matches the setter used in UE9).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Wait` (Expect now satisfies; step confirmed; no more steps).
- `harness.ChatSender.RecordedCalls.Count == 1`.
- The recorded call's fields: `Message == "Open Sesame"`, `TargetNpcId == new NpcId(1000789)`.

### Round-trip tests (`QuestForge.Schema.Tests/RoundTripTests.cs`)

#### SC10 — JSON round-trip with TargetNpcId set

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs` (replaces the existing `SayChatMessageStep_RoundTrips` at line 236).

**Given:** A `SayChatMessageStep { Id = "say-the-magic-word", Message = "Open Sesame", TargetNpcId = 1000789u, Expect = PredicateExpect("questFlag(65657, 4)") }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, deserialize as `Step`.

**Then:**
- Deserialized value is a `SayChatMessageStep`.
- `result.Message == "Open Sesame"`.
- `result.TargetNpcId == 1000789u`.

#### SC11 — JSON round-trip with no target (TargetNpcId omitted from JSON)

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs` (sibling test).

**Given:** A `SayChatMessageStep { Id = "shout-into-void", Message = "Hello", TargetNpcId = null, Expect = PredicateExpect("questSequence(65657) >= 3") }`.

**When:** Serialize, capture the JSON, deserialize.

**Then:**
- `result.Message == "Hello"`.
- `result.TargetNpcId == null`.
- The serialized JSON does NOT contain the substring `"targetNpcId"` (verifies the `[JsonIgnore(WhenWritingNull)]` attribute is honored). Use `compactJson` pattern from existing round-trip tests (line 230: `json.Replace(" ", "").Replace("\r", "").Replace("\n", "")`).

### Validator tests (`QuestForge.Engine.Tests/Authoring/`)

Place these in a new `DraftValidatorSayChatMessageTests.cs` file or extend an existing draft-validator test file — the Tester chooses based on which is already present and following the precedent set by UE13/UE14/UE15.

#### SC12 — Validator E11 (empty Message)

**Given:** A `QuestDraft` containing a `SayChatMessageStep` with `Message = ""`, `TargetNpcId = 1000789u`, `Expect = PredicateExpect("questFlag(83012, 3)")`. AcceptStep present (so E4 does not fire).

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E11"`.
- The error message mentions the step id and the string `"Message"` (or `"empty Message"` per the literal message in Decision SC7).

Defensive sub-case (optional): a `SayChatMessageStep` with `Message = null` (constructed via `with { Message = null! }` or similar) must also trigger E11. Tester can include or omit; the rule's `string.IsNullOrEmpty` check covers both.

#### SC13 — Validator E12 (TargetNpcId == 0)

**Given:** A `QuestDraft` containing a `SayChatMessageStep` with `Message = "Open Sesame"`, `TargetNpcId = 0u`, `Expect = PredicateExpect("questFlag(83013, 3)")`. AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `errors` contains exactly one entry with `Code == "E12"`.
- The error message mentions the step id and `"TargetNpcId == 0"`.

Defensive sub-case: a `SayChatMessageStep` with `TargetNpcId = null` MUST NOT trigger E12 (the null branch is allowed). Optionally include a second assertion in the same test that `validator.Validate(draftWithNullTarget).Errors` does not contain E12.

#### SC14 — Validator W9 (missing Expect) + W1 suppression

**Given:** A `QuestDraft` containing a `SayChatMessageStep` with `Message = "Open Sesame"`, `TargetNpcId = null`, `Expect = null` (missing). AcceptStep present.

**When:** `validator.Validate(draft)`.

**Then:**
- `warnings` contains exactly one entry with `Code == "W9"`.
- The W9 message contains the substring `"spin-loop"` (per CLAUDE.md slice 2 requirement).
- `warnings` does NOT contain an entry with `Code == "W1"` referencing the same step (W1 is suppressed for `SayChatMessageStep` per Decision SC7).

---

## Implementation order

**Phase A — Schema (10 min)**
1. Replace `SayChatMessageStep` in `QuestForge.Schema/Step.cs` per Decision SC1.
2. (No JSON context change — `[JsonSerializable(typeof(SayChatMessageStep))]` already registered at line 20.)
3. Update `RoundTripTests.SayChatMessageStep_RoundTrips` to the new shape (SC10) and add SC11 sibling test.
4. Run round-trip tests — must be green before proceeding.

**Phase B — Adapter surface (5 min)**
1. Create `QuestForge.Adapters/Chat/IChatSender.cs` per Decision SC4.
2. (No pure-helper file — Decision SC8.)

**Phase C — Fake (5 min)**
1. Create `QuestForge.Adapters.Fakes/Chat/FakeChatSender.cs` per Decision SC10.

**Phase D — Engine (20 min, TDD)**
1. Append `EngineAction.SayChatMessage` record per Decision SC11.
2. Add `_chatSender` field + constructor param to `QuestEngine` per Decision SC5 (last positional kwarg after `emoteExecutor`).
3. **Tester writes SC1, SC3, SC5** (single-tick dispatch shape; cheapest). Red.
4. Insert async pre-arm in `ResolveAction` (mirror placement: between `UseEmoteStep` arm at line 590 and `TeleportStep` arm at line 597) + implement `ResolveSayChatMessage` per Decision SC5. Green.
5. Tester writes SC2 (NPC-target variant). Green (no engine change).
6. Tester writes SC8 (Expect short-circuit). Green (no engine change).

**Phase E — Harness wiring (10 min)**
1. `EngineTestHarness` gains `ChatSender` property + constructor passthrough.
2. `RunToCompletion` gains the `SayChatMessage` arm per Decision SC12.
3. Tester writes SC4 (stateless retry via manual two-tick).
4. Tester writes SC9 (integration two-tick).
5. Tester writes SC6 (lazy-dismount with prior Navigate) and SC7 (standalone, no dismount).
6. Make them green.

**Phase F — Validator (10 min, TDD)**
1. **Tester writes SC12, SC13, SC14** in `DraftValidatorSayChatMessageTests.cs` (or extends the existing draft-validator test file). Red.
2. Add E11 + E12 + W9 + W1 exclusion extension to `QuestForge.Engine/Authoring/DraftValidator.cs` per Decision SC7.
3. Green.

Total dev time (Slice 2): ~1 hour code + tests.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~SayChatMessageStepTests` reports all 9 engine tests (SC1–SC9) green.
2. `dotnet test QuestForge.Schema.Tests --filter FullyQualifiedName~SayChatMessage` reports SC10 + SC11 green; the legacy `SayChatMessageStep_RoundTrips` test (at the old `{ Channel, Message, Target }` shape) no longer exists.
3. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidatorSayChatMessage` (or wherever SC12/SC13/SC14 land) reports all 3 validator tests green.
4. A quest JSON file with `{ "type": "say-chat-message", "message": "Open Sesame", "targetNpcId": 1000789 }` round-trips through `QuestForgeJsonContext.QuestFileOptions` losslessly.
5. `dotnet build` succeeds (no `TreatWarningsAsErrors` regressions in any project).
6. `QuestEngine` constructor signature is `(..., IActionExecutor? actionExecutor = null, IEmoteExecutor? emoteExecutor = null, IChatSender? chatSender = null)` — `chatSender` is the last optional param.
7. `EngineTestHarness` constructs `FakeChatSender` and passes it to the engine; `RunToCompletion` has a `case EngineAction.SayChatMessage:` arm.
8. `ResolveSayChatMessage` returns the `AwaitUser("...no IChatSender wired...")` fallback only when constructed without a chat sender (verified incidentally by older tests still passing without code changes).
9. The W9 message contains the substring `"spin-loop"` (per CLAUDE.md slice 2 requirement).
10. The W1 rule excludes `SayChatMessageStep` (no duplicate "step has no Expect" emit for say-steps).
11. No regression in `UseActionStepTests`, `UseEmoteStepTests`, `TeleportStepTests`, `PurchaseItemStepTests`, `AttunementStepTests`, or any existing test.

---

## Exclusions (what this plan does NOT include)

- **Dalamud shell** (`DalamudChatSender`). Slice 3. Sketched in Decision SC9 for reference.
- **`EngineHost` field / ctor / `DispatchAction` arm wiring.** Slice 3.
- **Tooling catch-up** (CapabilityInferrer, TraceToFixtureExtractor, TraceConstants, FIXTURES.md row updates beyond the already-present `step:say-chat-message` capability row). Slice 3 — Decision SC9 lists the exact items.
- **Authoring inference.** Slice 5. Signal research is REQUIRED first — see Decision SC13. Candidate signal sources:
  - `RaptureLogModule` chat-log buffer polling (preferred — matches the established polling pattern).
  - `AgentChatLog` (needs research).
  - Outbound chat hook (last resort; requires explicit user OK per CLAUDE memory).
- **Multi-channel support** (`/yell`, `/shout`, `/party`, `/fc`). Decision SC2 — `/say` only. If a future quest needs `/yell`, add a new step type `YellChatMessageStep` rather than reviving the `channel` field.
- **Lumina-text-id message storage.** Decision SC3 — literal English strings only for v1. Non-English clients are a documented limitation, not a regression.
- **Message validation beyond empty-string check.** No allow-list, no length cap, no character allow-list. `Chat.SendMessage` itself rejects pathological strings; the adapter's try/catch surfaces the failure.
- **Post-message advance specialization.** Decision (user-confirmed): the existing `EngineHost.Wait` case calls `TryCutsceneSkipConfirm` + `AdvanceDialogue` + `AcceptQuest` + `CompleteQuest` (line 476–483), which is exactly what a cutscene-or-dialogue-after-say needs. No special handling in the `SayChatMessage` dispatch arm.
- **InCombat behavior gate.** Decision SC5 — combat is the player's problem. If quests prove this wrong, add an `InCombat` predicate to the step's `SkipIf` rather than baking it into the engine.
- **`ChatMessageResolver` pure helper.** Decision SC8 — one-line concat doesn't warrant extraction.
- **`Recording*` proxy wrapper for `IChatSender`.** Per CLAUDE.md slice 3 guidance: write-only adapters do not need a `RecordingXxx` wrapper — `action.submitted`/`action.completed` from `EngineHost.DispatchAction` already capture writes. Add this in Slice 3 only if the Dalamud shell grows reads worth recording (none planned).

---

## Open questions / decisions to call out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| Should `IChatSender` extend `IEmoteExecutor` (both are slash-command shippers)? | **No — separate focused adapter** | Different mechanism (literal vs Lumina-resolved); Motion concept does not apply to /say. | SC4 |
| Drop `Channel` field; hard-wire `/say`? | **Yes — hard-wire** | `/yell` / `/shout` not in corpus; new channel = new step type. Removes a class of validator drift. | SC2 |
| Replace `Target: NpcLocation?` with `TargetNpcId: uint?`? | **Yes — replace** | Zone/Position on a say-step is misleading; mirrors UseEmoteStep / UseActionStep precedent. | SC1 |
| Engine synthesise default `Expect`? | **No — author-required** | No universal postcondition; loss mode (spin-loop) is recoverable. | UA4 / UE4 inherited |
| InCombat guard? | **No** | Combat is the player's problem; /say works while in combat. | SC5 |
| Add `SayChatMessage` to dismount-exemption list (Slice 3)? | **No** | NPC scripts may check pose; no game-side auto-dismount before /say. | SC6 |
| Validator: empty-message / zero-target / missing-Expect? | **Add E11 / E12 / W9 in this PR** | Small surface; consolidates with existing E9/E10/W8 for UseEmoteStep. | SC7 |
| Pure helper for `"/say " + message`? | **No — one-line concat** | Extraction would be ceremony with no testable failure modes. | SC8 |
| `Chat.SendMessage` discard return / check return? | **Discard** (try/catch surfaces exceptions) | Same posture as DalamudEmoteExecutor (UE16); engine uses `Expect` to verify outcome. | SC9 |
| Bundle Dalamud impl in this slice? | **No — Slice 3** | Per CLAUDE.md slice order; Slice 2 = engine + schema + validator only. | (slice order) |
| Authoring inference in this slice? | **No — Slice 5** | Signal research required first. RaptureLogModule is the lead candidate. | SC13 |
| Multi-language message storage? | **Literal English strings (documented limitation)** | Lumina-text-id mapping is undocumented; YAGNI for v1. | SC3 |

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 3 scenarios (SC1, SC2, SC9)
- Edge cases: 3 scenarios (SC6, SC7, SC8)
- Error / wait cases: 3 scenarios (SC3, SC4, SC5)
- Validator: 3 scenarios (SC12, SC13, SC14)
- Serialization: 2 scenarios (SC10, SC11)
- Expected total:
  - `QuestForge.Engine.Tests/Engine/SayChatMessageStepTests.cs`: 9 tests (SC1–SC9)
  - `QuestForge.Schema.Tests/RoundTripTests.cs`: 2 tests (SC10 replaces existing; SC11 new)
  - Draft validator tests (new file or sibling): 3 tests (SC12, SC13, SC14)
  - Grand total: ~14 tests across three projects.
