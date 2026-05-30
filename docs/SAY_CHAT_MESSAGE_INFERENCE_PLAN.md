# SayChatMessageStep Authoring Inference Plan

**Status:** ready for test creation (with open questions §F O1–O4 — none blocking Phases A–D)

**Slice:** 5 of the SayChatMessageStep slice plan (Slices 1–4 shipped — schema, engine, validator, Dalamud shell, in-game smoke). This slice adds Author-mode inference: when the player types `/say <message>` during a recording session, the snapshot field `SayChatMessageSent` becomes non-null, `StepInferenceEngine.Infer` returns a `say-chat-message`-typed `InferenceResult`, the Record-Step modal previews a draft `SayChatMessageStep`, and confirming the modal appends it to the draft.

**Input docs:**
- `docs/USE_EMOTE_INFERENCE_PLAN.md` — the closest analog spec (UEI1–UEI16, UO_L1–UO_L11). The shape of THIS plan mirrors it 1:1; differences are flagged.
- `docs/USE_ACTION_INFERENCE_PLAN.md` — secondary analog (UAI1–UAI14, UO_K1–UO_K10). Establishes the monotonic-counter polling pattern this plan adopts (the chat log entry count is a monotonic counter — see Decision SCI9).
- `docs/SAY_CHAT_MESSAGE_STEP_PLAN.md` — engine + schema + validator (SC1–SC14 — already shipped). Defines `SayChatMessageStep { Message: string, TargetNpcId: uint? }`, `IChatSender.Send`, `EngineAction.SayChatMessage`.
- `docs/SAY_CHAT_MESSAGE_DALAMUD_PLAN.md` — `DalamudChatSender` + `EngineHost.DispatchAction` arm + tooling catch-up (SCD-1..SCD-10 — already shipped).
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs:264–287` — existing Rule 3.5e (`EmoteCompleted`) is the placement reference; the new Rule 3.5s sits immediately above it.
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs:17–34,160–166` — `ActionCompletedSignal` + `EmoteCompletedSignal` records + the `ActionCompleted` / `EmoteCompleted` snapshot properties are the structural analog for `SayChatMessageSentSignal` + `SayChatMessageSent`.
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs:36–37,113–114,523–534` — `_emoteCompleted` field, `EmoteCompleted = _emoteCompleted` initializer, `OnEmoteCompleted` / `OnEmoteConsumed` pair is the structural analog.
- `QuestForge.Engine/Authoring/InferredFrom.cs:20` — `EmoteCompleted` enum value; we add `SayChatMessageSent`.
- `QuestForge.Engine/Authoring/StepFactory.cs:145–152` — `"use-emote"` arm is the structural analog for the new `"say-chat-message"` arm.
- `QuestForge.Plugin.Tracing/UIObserver.cs:666–702` — `PollPlayerEmote` is the structural analog for the new `PollPlayerChatMessage`; `PollPlayerActionEffect` at 628–664 is the MONOTONIC-COUNTER analog (the SayChat poller's state shape mirrors it).
- `QuestForge.Plugin.Tracing/UIObserver.cs:187–194` — `_lastObservedActionSequence = null;` and `_aggregator?.OnActionConsumed();` reset/consume pattern.
- `QuestForge.Plugin/Authoring/AuthoringHost.cs:200,273–274` — `[QF-DIAG] PreviewInference:` line and `RecordStep` consume calls.
- `QuestForge.Plugin.Tracing/IGameProbe.cs:15–16` — `GetLastActionEffect` and `GetPlayerEmoteId` signatures are the surface analog.
- `QuestForge.Plugin/Tracing/DalamudGameProbe.cs:76–93` — `GetLastActionEffect` and `GetPlayerEmoteId` implementations are the cast-pointer pattern reference.
- `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs:96–169` — existing `FakeGameProbe` is the extension site; the `_nextActionEffect` / `_nextEmoteId` patterns transfer 1:1.
- `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs:2853–2940` — the `UO_K1..K10` test group is the layout reference for the new `UO_M*` group.
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\UI\Misc\RaptureLogModule.cs` — the signal source (see §Signal research below).

**Output (CI behavior):** When the player types `/say <message>` during an Author-mode recording session, a new entry lands in the `RaptureLogModule` chat log with `LogInfo.SourceKind == EntityRelationKind.LocalPlayer` and `LogMessageSource.ChatType == 10` (Say). `UIObserver.PollPlayerChatMessage` observes the count increment, fetches the entry, filters it via the new pure `ChatLogEntryFilter.IsPlayerSayMessage` helper, captures the message text + the currently-targeted NPC, and calls `_aggregator?.OnSayChatMessageSent(message, targetBaseId)`. The snapshot field `SayChatMessageSent` becomes non-null. `StepInferenceEngine.Infer` returns a `say-chat-message`-typed `InferenceResult` with `SuggestedExpect = null` (author MUST write the postcondition per `SAY_CHAT_MESSAGE_STEP_PLAN.md` Decision SC7 / W9 — there is no universal "did the NPC's `/say` recognition fire?" predicate), `Confidence.High`, and `InferredFrom.SayChatMessageSent`. Confirming the Record-Step modal records a `SayChatMessageStep { Message, TargetNpcId? }` into the draft. CI red → CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the pure log-entry filter, (d) the new inference rule, (e) the StepFactory `"say-chat-message"` arm, (f) the UIObserver poller, and (g) the `RecordStep` clearing are wired up.

This plan covers **engine-side (xUnit-testable)** wiring, **a pure-logic filter helper (Dalamud-free, xUnit-testable in `QuestForge.Adapters.Tests`)**, and **UIObserver-side polling**. The `RaptureLogModule` read in `DalamudGameProbe` is a thin Dalamud-bound shell (no test); the polling state machine, the filter, the inference rule, and the StepFactory arm are all testable without Dalamud.

---

## Signal research (CONFIRMED — see Decision SCI10 for caveats)

**Signal source: `RaptureLogModule.MsgSourceArrayLength` (int monotonic-ish counter) + `MsgSourceArray[i]` + `GetLogMessageDetail(LogMessageIndex, ...)`.**

Located at `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\UI\Misc\RaptureLogModule.cs`. The actual struct shape (corrects the brief's assumption that `LogMessageCount` + `LocalPlayerContentId` are direct fields):

| Field on `RaptureLogModule` | Offset | Type | Purpose |
|---|---|---|---|
| `MsgSourceArray` | `0x3478` | `LogMessageSource*` | Circular array of log message metadata |
| `MsgSourceArrayLength` | `0x3480` | `int` | Number of valid entries in `MsgSourceArray` (the counter we poll) |
| `GetLogMessageDetail(int index, LogInfo*, Utf8String* sender, Utf8String* message, int* timestamp)` | member fn | `bool` | Fetches the formatted message text + sender + LogInfo for a given log index |

`LogMessageSource` entry shape (offset 0x18 stride):

| Field | Offset | Type | Purpose |
|---|---|---|---|
| `ContentId` | `0x00` | `ulong` | Sender's character ContentId. Compare against `InfoModule.LocalContentId` (see below) to identify the local player. |
| `AccountId` | `0x08` | `ulong` | Sender's account |
| `LogMessageIndex` | `0x10` | `int` | Index into the log; pass to `GetLogMessageDetail` |
| `World` | `0x14` | `short` | Sender's home world |
| `ChatType` | `0x16` | `short` | **The Dalamud `XivChatType` value.** `10` = `Say`. |

`LogInfo` shape (returned by `GetLogMessageDetail`):

| Field | Type | Purpose |
|---|---|---|
| `LogKind` | `ushort` (bits 0..7) | Log channel id |
| `TargetKind` | `EntityRelationKind` (bits 7..11) | byte enum |
| `SourceKind` | `EntityRelationKind` (bits 11..15) | byte enum; `LocalPlayer == 1` |

**Local player ContentId source:** `InfoModule.LocalContentId` at offset `0x1A90` (verified — `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\UI\Info\InfoModule.cs:13`) OR `InfoModule.GetLocalContentId()` member function (same file, line 27). The latter is more idiomatic and is what `DalamudGameProbe` will call (Decision SCI11).

**Why `MsgSourceArrayLength` is the counter (NOT a separate `LogMessageCount` field as the brief assumed):** the brief was wrong about the field name. The closest equivalent is `MsgSourceArrayLength` — the count of valid entries in `MsgSourceArray`. New chat lines push entries; the array IS bounded (likely a circular buffer; FFXIVClientStructs doesn't document the cap), so the counter is NOT strictly monotonic across a long session. See Decision SCI10 / Open Question §F O2.

**Pollable: yes.** First-observation-establishes-baseline pattern (matches `PollPlayerActionEffect`'s UO_K1 — Decision UAI8). On each tick: compare `MsgSourceArrayLength` to baseline; if greater, iterate the new entries `[baseline..length)`, filter via `IsPlayerSayMessage`, fire on match, advance baseline.

**Locale-stable:** the message text travels as the literal player input (a `Utf8String`). No localized-sheet lookup is involved at the read site. The text matches what the player typed verbatim.

---

## Dependency graph

```
QuestForge.Adapters.Chat
  └── ChatLogEntryFilter (NEW: pure helper — IsPlayerSayMessage)
        ↓ used by ↓
QuestForge.Engine.Authoring
  ├── GameStateSnapshot           (add SayChatMessageSent property + SayChatMessageSentSignal record)
  ├── InferredFrom (enum)         (add SayChatMessageSent)
  ├── SnapshotAggregator          (add OnSayChatMessageSent / OnSayChatMessageConsumed)
  └── StepInferenceEngine         (add Rule 3.5s — SayChatMessageSent, fires immediately ABOVE existing Rule 3.5e)
        ↓
QuestForge.Engine.Authoring.StepFactory  (route stepType="say-chat-message" → SayChatMessageStep)
        ↓
QuestForge.Plugin.Authoring.AuthoringHost.RecordStep  (clear SayChatMessageSent after consume)
        ↓
QuestForge.Plugin.Tracing.IGameProbe                   (add GetChatLogMessageCount + GetChatLogEntry + GetLocalContentId)
        ↓
QuestForge.Plugin.Tracing.UIObserver.PollPlayerChatMessage   (new poller; tracks last-observed log count)
        ↓
QuestForge.Plugin.Tracing.DalamudGameProbe.GetChatLogMessageCount / GetChatLogEntry / GetLocalContentId   (Dalamud-bound)
```

**Build order:**
1. `ChatLogEntryFilter` pure helper in `QuestForge.Adapters/Chat/ChatLogEntryFilter.cs` (alongside the existing `IChatSender.cs`). Testable in `QuestForge.Adapters.Tests`.
2. `SayChatMessageSentSignal` record + `GameStateSnapshot.SayChatMessageSent` property + `InferredFrom.SayChatMessageSent` enum value — engine surface, no Dalamud.
3. `SnapshotAggregator.OnSayChatMessageSent` / `OnSayChatMessageConsumed` — engine surface.
4. `StepInferenceEngine` Rule 3.5s — engine surface (placement: immediately above the existing Rule 3.5e EmoteCompleted).
5. `StepFactory.Build` `"say-chat-message"` arm — engine surface.
6. `AuthoringHost.RecordStep` consume call — one-line edit.
7. `IGameProbe.GetChatLogMessageCount()` + `GetChatLogEntry(int)` + `GetLocalContentId()` + `FakeGameProbe` extension — interface + fake.
8. `UIObserver.PollPlayerChatMessage` — testable in `QuestForge.Plugin.Tests` against `FakeGameProbe` + `FakeTargetProbe`.
9. `DalamudGameProbe` impl — concrete Dalamud impl, smoke-tested in-game.

Steps 1–7 are pure xUnit. Step 8 is `QuestForge.Plugin.Tests` against fakes. Step 9 is manual smoke.

---

## Architectural decisions (read before coding)

### Decision SCI1 — Snapshot field is `SayChatMessageSentSignal?`, a record carrying both `Message` AND `TargetBaseId`

The brief's §A asks whether to include `TargetBaseId`. **Decision: include it**, mirroring `ActionCompletedSignal` (Decision UAI1) and `EmoteCompletedSignal` (Decision UEI1) for consistency.

`/say` is broadcast — the game does not carry "intended target NPC" with the chat message itself. **But** the *author's intent* when typing `/say <message>` AT an NPC is captured by `TargetManager.Target` at the time of the read, the same way emote and action target capture works. Authors who want a target on the step get it for free; authors who don't can clear the field in the modal.

**Concrete shape:**

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs — appended near EmoteCompleted (line 166)
//
// Records that the player typed /say <message> during this recording window. Set by
// SnapshotAggregator.OnSayChatMessageSent, which is driven by UIObserver.PollPlayerChatMessage
// reading RaptureLogModule's MsgSourceArrayLength + GetLogMessageDetail for matching entries.
// Cleared by OnSayChatMessageConsumed (called from AuthoringHost.RecordStep) so it does not
// bleed into the next recording window.
//
// Message is the literal text the player typed (no "/say " prefix — the chat log stores the
// message body, not the command).
// TargetBaseId is the BNpcBase / ENpcBase row id of the currently-targeted NPC at the moment
// the chat-log entry was observed (null = no hard target / self-cast).
public sealed record SayChatMessageSentSignal(
    string Message,
    uint? TargetBaseId);

public SayChatMessageSentSignal? SayChatMessageSent { get; init; }
```

**Why `Message: string` not `Message: Utf8String`:** the engine boundary uses `string`. The Dalamud probe converts `Utf8String → string` at the cast boundary (the FFXIVClientStructs `Utf8String.ToString()` extension handles UTF-8 → UTF-16). Snapshot fields are CLR-friendly.

**Why `TargetBaseId: uint?` not `NpcId?`:** matches `EmoteCompletedSignal.TargetBaseId` (Decision UEI1). The schema-side `SayChatMessageStep.TargetNpcId` is also `uint?` (Step.cs:154), so the StepFactory passes through without conversion.

**Rejected alternatives:**
- **`Message` only, no `TargetBaseId`.** Inconsistent with the other two text-input signals (action, emote). Authors who type `/say` while targeting an NPC would lose target context for no benefit.
- **`Message` + `TargetGameObjectId` (read from `TargetManager.Target` and store the per-instance id).** Schema authors target by BaseId; resolving GameObjectId → BaseId at modal-confirm time would race the ObjectTable (target may have moved). Same reasoning as Decision UEI12.
- **A list of messages (player sent multiple `/say` lines).** YAGNI for v1. The rule fires on the most recent (last-write-wins inside `OnSayChatMessageSent`). If the author types two lines, the modal previews the second; the author can record the first manually if needed. See Open Question §F O4.

**What breaks if violated:** if the field is split into two separate slots (`SayChatMessageText: string?`, `SayChatMessageTarget: uint?`), the priority check in Rule 3.5s must read both and disambiguate "are these from the same event?" via null-checking each. Atomic record gives an atomic presence check.

### Decision SCI2 — Pure-logic seam: `ChatLogEntryFilter.IsPlayerSayMessage` (Dalamud-free)

The chat-line filter is genuinely a testable seam. Given a `LogInfo.SourceKind`, a `LogMessageSource.ChatType`, the entry's `ContentId`, and the local player's `ContentId`, decide whether this entry is a "player /say" worth firing on.

**Concrete shape:**

```csharp
// QuestForge.Adapters/Chat/ChatLogEntryFilter.cs (NEW)
namespace QuestForge.Adapters.Chat;

/// <summary>
/// Pure decision helper for filtering RaptureLogModule chat log entries down to
/// "player /say" messages worth firing the SayChatMessageSent signal on.
///
/// The integer parameters are the raw wire values (kept as primitives to keep this
/// assembly Dalamud-free):
///   sourceKind   = LogInfo.SourceKind (from RaptureLogModule.GetLogMessageDetail)
///                  CANONICAL: 1 = LocalPlayer (per FFXIVClientStructs EntityRelationKind)
///   chatType     = LogMessageSource.ChatType (Dalamud's XivChatType)
///                  CANONICAL: 10 = Say
///   entryContentId    = LogMessageSource.ContentId (the entry's sender)
///   localPlayerContentId = InfoModule.LocalContentId (the player's character)
///
/// Returns true ONLY when ALL of:
///   - sourceKind == 1 (LocalPlayer)
///   - chatType == 10 (Say)
///   - entryContentId == localPlayerContentId (filters out other players' /say in the zone)
///   - localPlayerContentId != 0 (defensive: in early-init or character-select, the local
///                                ContentId is unknown; we must not match every entry)
/// </summary>
public static class ChatLogEntryFilter
{
    public const int SourceKindLocalPlayer = 1;   // EntityRelationKind.LocalPlayer
    public const int ChatTypeSay = 10;             // XivChatType.Say

    public static bool IsPlayerSayMessage(
        int sourceKind,
        int chatType,
        ulong entryContentId,
        ulong localPlayerContentId)
    {
        if (localPlayerContentId == 0UL) return false;     // unknown local player → never match
        if (sourceKind != SourceKindLocalPlayer) return false;
        if (chatType != ChatTypeSay) return false;
        if (entryContentId != localPlayerContentId) return false;
        return true;
    }
}
```

**Why a pure helper (unlike Decision UEI2 which rejected a filter):** for emotes the equivalent filter would be a hard-coded skip-id list ("don't infer /sit") — that's a configuration question, not logic. For chat, the filter answers a real semantic question with FOUR inputs, each of which has a distinct failure mode (NPC `/say` from a quest script, the player's own `/yell`, another player's `/say` in the zone, pre-login state). The four-way conjunction warrants a single named entry point + property-style tests.

**Lives in `QuestForge.Adapters/Chat/`:** alongside the existing `IChatSender.cs` (already there at `QuestForge.Adapters/Chat/IChatSender.cs:1`). Dalamud-free — only uses primitives. Tests live in `QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs`.

**Rejected alternatives:**
- **Encapsulate in `UIObserver.PollPlayerChatMessage` as inline branches.** Inline branches would not be unit-testable; the four-way conjunction is the highest-value test surface in this plan.
- **Take a struct/record instead of four primitives.** A new struct adds a type to coordinate with the probe-tuple shape. Four primitives are simpler and match the `(uint sequence, uint ffxivActionType, uint actionId)` precedent of `GetLastActionEffect`.

**What breaks if violated:** if filtering is inline, an off-by-one in the four-way conjunction (e.g. forgetting the `localPlayerContentId != 0` defensive check) silently misfires inference in the early-init window (every chat line attributed to the player when no LocalPlayer is loaded).

### Decision SCI3 — Aggregator setter/consumer mirror the EmoteCompleted pair exactly

```csharp
// QuestForge.Engine/Authoring/SnapshotAggregator.cs — appended near the EmoteCompleted methods
private SayChatMessageSentSignal? _sayChatMessageSent;

// In the Current property's object-initializer block (alongside EmoteCompleted = _emoteCompleted at line 114):
SayChatMessageSent = _sayChatMessageSent,

/// <summary>
/// Called by UIObserver.PollPlayerChatMessage when a new chat-log entry matching
/// ChatLogEntryFilter.IsPlayerSayMessage is observed. Records the message text and optional
/// target (from TargetManager.Target at the time of the read). Survives ResetDeltas;
/// cleared by OnSayChatMessageConsumed (called from AuthoringHost.RecordStep).
///
/// Multiple calls within the same window: LAST-WRITE-WINS (matches the action/emote
/// precedent — the most recent signal is the most relevant authoring intent). The author
/// will see the latest /say in the modal preview; multi-line records are §F O4.
///
/// Does NOT update LastNpcInteracted, LastAttuned, or any other unrelated field — the target
/// of a /say is the currently-hovered target, NOT a dialogue interaction. Setting
/// LastNpcInteracted from here would cause spurious Rule 7 (NpcInteracted-changed) talk-step
/// inference on subsequent windows. Mirrors the OnEmoteCompleted no-side-effects discipline
/// (Decision UEI3).
/// </summary>
public void OnSayChatMessageSent(string message, uint? targetBaseId)
    => _sayChatMessageSent = new SayChatMessageSentSignal(message, targetBaseId);

/// <summary>
/// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to
/// consume the say-chat-message signal so it does not bleed into the next recording window.
/// Mirrors OnEmoteConsumed exactly.
/// </summary>
public void OnSayChatMessageConsumed() => _sayChatMessageSent = null;
```

**Why no side effects on other state:** see the docstring. Same reasoning as Decision UEI3.

**Why not clear in `ResetDeltas`:** the field follows the per-window event lifecycle (set during recording window, consumed at modal-confirm). Clearing in `ResetDeltas` would make the modal-preview unable to see the message when the heartbeat tick reset deltas between Record-click and modal-display. Pinned by test SCI8 sub-B.

**What breaks if violated:** if `OnSayChatMessageSent` sets `_lastNpcInteracted = new NpcId(targetBaseId.Value)`, the next recording window's Rule 7 sees the /say target as a "newly interacted NPC" and emits a wrong `talk` step.

### Decision SCI4 — New inference rule "Rule 3.5s — SayChatMessageSent" fires IMMEDIATELY ABOVE Rule 3.5e (EmoteCompleted)

**Priority placement is load-bearing.** The cascade in `StepInferenceEngine.Infer` (post-Slice-4) runs:
- 1: QuestCompleted → 2: QuestAccepted → 2.1: ForeignQuestAccepted → 2.2: Combat → 2.2b: Purchase → 2.3: KeyItemsAdded → 2.4: KeyItemsRemoved → 2.5: Attune → 2.6: InventoryHash → **3.5e: EmoteCompleted** → **3.5: ActionCompleted** → 3: QuestSequence advanced → 4.0: TeleportCompleted → 4: Zone changed → 2.7: AethernetTeleportCompleted (same zone) → 5: QuestFlags → 6: DialogueAnswer → 7: NpcInteracted → 8: Movement → 9: Empty.

Insertion site: **between Rule 2.6 (InventoryHash) and Rule 3.5e (EmoteCompleted)**, labelled "Rule 3.5s" (the "s" suffix denotes "say").

**Per the brief: priority above Rule 3 (sequence advance) and Rule 4 (zone change), below Rules 1 and 2.x. Tie-break with action/emote is the open call.**

**Decision: Rule 3.5s fires ABOVE Rule 3.5e (EmoteCompleted) and ABOVE Rule 3.5 (ActionCompleted).** Reasoning:

1. **The three signals are independent in the snapshot.** A user can type `/say` while their cast bar fires AND while pressing an emote. The fields are not mutually exclusive in code (only in unrelated game-code paths). A defensive ordering matters.
2. **Chat is the rarest of the three.** Auto-attacks and emote queueing happen incidentally; deliberate `/say <text>` is always intentional. If multiple fire, the chat is the most likely authoring intent.
3. **The brief says "chat is its own signal, no collision risk with emote/action — they fire on different conditions."** Concurring on the practical mutual-exclusivity, but the defensive ordering matters when the snapshot fields all happen to be set by a quirk of timing.
4. **Symmetric to the action/emote tie-break (Decision UEI13 — emote wins above action).** The triple "chat → emote → action" cascades from most-deliberate to most-incidental.

**Why ABOVE Rule 3 (QuestSequence advance):** identical reasoning to Decisions UEI4 / UAI4. A `/say` that advances the sequence (the NPC's `/say`-recognition script flips a flag) is the natural authoring case for SayChatMessageStep. Firing Rule 3 first would draft a `talk` step the author would have to delete.

**Why BELOW Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6:** identical reasoning to UEI4 / UAI4. Those rules represent distinct authoring intents (turn-in, accept, combat, purchase, key-item exchange, attunement, inventory-diff exchange) where a `/say` would be incidental and not the primary step.

**Final placement: between Rule 2.6 (InventoryHash) and Rule 3.5e (EmoteCompleted).** Concrete location: insert immediately above the `// Rule 3.5e — EmoteCompleted` comment at line 264 of `StepInferenceEngine.cs`.

```csharp
// Rule 3.5s — SayChatMessageSent
// Fires when UIObserver.PollPlayerChatMessage detected that the player typed /say <message>
// during this recording window (RaptureLogModule observed a new log entry matching
// ChatLogEntryFilter.IsPlayerSayMessage).
//
// PRIORITY: above Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted). The three signals
// are mutually exclusive in practice (different game code paths), but if multiple fire in the
// same window the /say is the most-deliberate authoring intent — chat input is always
// intentional, while emotes and actions can be incidental (queued, auto-attack tick, pet
// command). Defensive ordering: chat > emote > action.
// PRIORITY: above Rule 3 (QuestSequence advanced) — say-chat-message is more specific than the
// catch-all sequence-advance which defaults to "talk". Say-step that advances the sequence
// (NPC /say-recognition script flips a flag) is the MOST common case.
// PRIORITY: below Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6 — those represent distinct
// authoring intents where a /say would be incidental.
//
// CONFIDENCE: High — the player demonstrably typed the message (the chat log records it).
// EXPECT: null — author MUST write the postcondition (Decision SC7 of SAY_CHAT_MESSAGE_STEP_PLAN.md:
// the validator emits W9 with "spin-loop" if Expect is missing on a say-step).
if (after.SayChatMessageSent is { } saySignal)
{
    var stepIdSuffix = saySignal.TargetBaseId is { } tid
        ? $"on-{tid}"
        : "broadcast";
    return new InferenceResult(
        StepType:        "say-chat-message",
        SuggestedStepId: $"say-chat-message-{stepIdSuffix}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.SayChatMessageSent,
        Notes:           $"Author MUST write the Expect predicate (no universal say-chat-message postcondition). Message=\"{Truncate(saySignal.Message, 40)}\"");
}
```

Add a small private helper inside the same file (mirroring patterns in the file):

```csharp
private static string Truncate(string s, int n)
    => s.Length <= n ? s : s.Substring(0, n) + "...";
```

**Why the SuggestedStepId pattern omits the message text from the id:** message text may contain spaces, quotes, or non-ASCII — anything that would corrupt the step id. The id is `say-chat-message-on-{tid}` or `say-chat-message-broadcast`; the author edits the id in the modal if needed. Step ids are tokens, not human-readable phrases.

**Why `SuggestedExpect = null` (not a placeholder):** identical reasoning to Decisions UEI4 / UAI4 / SC7. The engine treats `Expect == null` as "step never satisfies, re-emit forever" — the loud failure mode. Synthesising a placeholder predicate would be a lie. Null is honest, and validator W9 surfaces the requirement to the author.

**Why the `Notes` string includes (truncated) message text:** the Record-Step modal surfaces `InferenceResult.Notes` in the UI. Telling the author "the say-step we drafted contains 'Open Sesame...' — write your Expect" lets them verify the right message was captured without opening the JSON. 40-char truncate keeps the modal line readable.

### Decision SCI5 — `InferredFrom.SayChatMessageSent` is a new enum value

Existing values (per `InferredFrom.cs` post-Slice-4): `ZoneChange, QuestFlagChange, QuestSequenceChange, DialogueInteraction, QuestAccepted, QuestCompleted, AttunementChange, MovementChange, Manual, None, InventoryChange, Combat, Purchase, TeleportCompleted, ActionCompleted, EmoteCompleted`.

Adding `SayChatMessageSent` keeps the existing taxonomy intact and lets downstream consumers (trace events, UI badge colours, future analytics) differentiate chat-derived steps from emote/action-derived ones.

```csharp
public enum InferredFrom
{
    ZoneChange,
    QuestFlagChange,
    QuestSequenceChange,
    DialogueInteraction,
    QuestAccepted,
    QuestCompleted,
    AttunementChange,
    MovementChange,
    Manual,
    None,
    InventoryChange,
    Combat,
    Purchase,
    TeleportCompleted,
    ActionCompleted,
    EmoteCompleted,
    SayChatMessageSent,    // NEW
}
```

**Rejected alternative:** reusing `DialogueInteraction` — would collapse the chat distinction in trace data and prevent downstream filtering.

### Decision SCI6 — `StepFactory.Build` gains a `"say-chat-message"` arm

Mirrors the `"use-emote"` arm at `StepFactory.cs:145–152`. Reads `after.SayChatMessageSent` to populate the `SayChatMessageStep` fields. If `after.SayChatMessageSent` is null at the moment `StepFactory.Build` is called for `stepType == "say-chat-message"` (defensive — the inference engine only returns "say-chat-message" when the field is set), fall back to `SayChatMessageStep { Message = "", TargetNpcId = null }` so the Builder's validator catches it later (E11 fires on empty message); do not throw.

```csharp
// QuestForge.Engine/Authoring/StepFactory.cs — append to the switch block, near the "use-emote" arm at line 145
"say-chat-message" => new SayChatMessageStep
{
    Id = stepId,
    Expect = expectValue,           // null in v1 inference (Decision SCI4); author edits in modal
    Message = after?.SayChatMessageSent?.Message ?? string.Empty,
    TargetNpcId = after?.SayChatMessageSent?.TargetBaseId
},
```

**Why no `Zone` / `RequiredZone` / `Target` location:** `SayChatMessageStep` (per `Step.cs:150–155`) inherits only `Id` and `Expect` from `Step` — it has NO `Zone` field, NO `RequiredZone`, NO `Target` location. Same posture as `UseEmoteStep` (Decision UEI7).

**Why `Message = string.Empty` (not throw) on null SayChatMessageSent:** validator E11 fires on empty `Message` and renders a friendly error. Throwing here would crash the Tester invoking `Build("say-chat-message", …, after: emptySnapshot)`. Defensive fallback + validator catch is the established pattern.

**What breaks if violated:** if the StepFactory throws on null `SayChatMessageSent`, a defensive caller (Tester invoking `Build("say-chat-message", …, after: emptySnapshot)`) crashes the test harness instead of producing a validator-rejectable draft.

### Decision SCI7 — `AuthoringHost.RecordStep` clears `SayChatMessageSent` after consuming

After the existing five consume calls in `RecordStep` (lines 270–274):

```csharp
_aggregator.OnAethernetTeleportConsumed();
_aggregator.OnDialogueOptionConsumed();
_aggregator.OnTeleportConsumed();
_aggregator.OnActionConsumed();
_aggregator.OnEmoteConsumed();
_aggregator.OnSayChatMessageConsumed();   // NEW
```

Same lifecycle as the other five: survives `ResetDeltas` so it remains visible to `PreviewInference`, cleared only after the author confirms the modal.

`UIObserver.ResetWindowState` should ALSO call `_aggregator?.OnSayChatMessageConsumed()` AND reset the baseline counter for symmetry with the existing five (lines 187–194):

```csharp
// UIObserver.ResetWindowState — alongside existing consume calls
_lastObservedActionSequence = null;
_lastObservedEmoteId = 0u;
_lastObservedChatLogCount = null;        // NEW — re-baseline (next read silently establishes new baseline)

_aggregator?.OnAethernetTeleportConsumed();
_aggregator?.OnDialogueOptionConsumed();
_aggregator?.OnTeleportConsumed();
_aggregator?.OnActionConsumed();
_aggregator?.OnEmoteConsumed();
_aggregator?.OnSayChatMessageConsumed();   // NEW
```

**Why baseline is nullable (matches action-effect pattern, NOT emote pattern):** chat log count is a monotonic-ish counter (Decision SCI10); first-observation-silent matches `_lastObservedActionSequence: uint?` (Decision UAI8) and differs from the emote pattern (`_lastObservedEmoteId: uint` initialized to 0). See Decision SCI9.

### Decision SCI8 — Diagnostic log line extension

The existing `[QF-DIAG] PreviewInference:` line in `AuthoringHost.PreviewInference` (line 200) already includes `ActionCompleted={...}` and `EmoteCompleted={...}`. Append the chat signal analogously:

```csharp
_services.Log.Debug($"[QF-DIAG] PreviewInference: zone {before.Zone.Value}→{after.Zone.Value} " +
    $"AethernetTeleportCompleted={after.AethernetTeleportCompleted?.To.Value} " +
    $"TeleportCompleted={after.TeleportCompleted?.Value} " +
    $"ActionCompleted={after.ActionCompleted?.ActionId} " +
    $"EmoteCompleted={after.EmoteCompleted?.EmoteId} " +
    $"SayChatMessageSent={TruncateForLog(after.SayChatMessageSent?.Message, 30)} " +    // NEW
    $"DialogueOptionSelected={after.DialogueOptionSelected} " +
    $"DialogueNpcSource={after.DialogueNpcSource?.NpcId} " +
    $"isAethernet_before_shard={before.LastAethernetShardInteracted?.Value} " +
    $"isAethernet_before_npc={before.LastNpcInteracted?.Value}");
```

`TruncateForLog` is a static helper added to `AuthoringHost` (or inlined as ternary):

```csharp
private static string TruncateForLog(string? s, int n)
    => s is null ? "null"
     : s.Length <= n ? s
     : s.Substring(0, n) + "...";
```

**Why truncate:** chat messages can be up to ECommons' limit (~500 chars). The diagnostic line is single-line; an untruncated message would push it past readable width and bury other diagnostic fields. 30 chars matches the inferred-from-emote analog reasoning.

### Decision SCI9 — UIObserver poller `PollPlayerChatMessage` is a MONOTONIC-COUNTER watcher (matches PollPlayerActionEffect, NOT PollPlayerEmote)

The signal source mirrors the action-effect counter, NOT the emote momentary-state:
- **Emote poller** watches `EmoteController.EmoteId`, **momentary state** (set while emoting, 0 when idle). Transition-watcher state machine.
- **Action-effect poller** watches `CastInfo.ResponseGlobalSequence`, **monotonic counter** (rising edge fires).
- **Chat-message poller** watches `RaptureLogModule.MsgSourceArrayLength`, **monotonic-ish counter** (new entries push the count up — though see Decision SCI10 for circular-buffer caveat). The poller iterates `[baseline..count)` new entries on each tick and fires per matching entry.

**Concrete state:**
- `private int? _lastObservedChatLogCount;` — nullable. `null` = not yet baselined (first observation silent — matches `_lastObservedActionSequence` precedent).
- First observation: silent baseline (mirrors UO_K1 — pre-session chat lines from before Author mode started are NOT events worth surfacing).
- Subsequent polls: if `count > _lastObservedChatLogCount`, iterate the new entries at indices `[_lastObservedChatLogCount..count)`, fire per filter match, advance baseline to `count`.

```csharp
// QuestForge.Plugin.Tracing/UIObserver.cs — new every-frame poller, added next to PollPlayerEmote
//
// REQUIRES on IGameProbe:
//   int? GetChatLogMessageCount();        // returns MsgSourceArrayLength; null when no LocalPlayer / unavailable
//   ChatLogEntry? GetChatLogEntry(int index);   // returns null if index out of range or read fails
//   ulong GetLocalContentId();            // returns InfoModule.LocalContentId; 0 when unavailable
//
// State: _lastObservedChatLogCount tracks the last-consumed MsgSourceArrayLength.
// First observation silently baselines (matches PollPlayerActionEffect UO_K1).
// Subsequent counts > baseline iterate the new entries [baseline..count); each matching
// entry fires OnSayChatMessageSent exactly once. Non-matching entries silently advance
// the baseline (other players' /say in the zone, NPC chat, etc.) — they consume the
// counter slot but do not fire.

// Field declarations — place alongside _lastObservedEmoteId at line 92
private int? _lastObservedChatLogCount;

private void PollPlayerChatMessage()
{
    if (_gameProbe is null) return;

    var current = _gameProbe.GetChatLogMessageCount();
    if (current is null) return;   // probe unavailable (no LocalPlayer, RaptureLogModule null)
    var currentCount = current.Value;

    // First observation: establish baseline silently.
    // WHY: at session start, the chat log already contains pre-session lines (the player may
    // have /say'd before clicking "Author Mode"). Treating those as events would draft steps
    // for messages the author has no intention of including. Matches PollPlayerActionEffect
    // baseline-silent precedent (Decision UAI8 / UO_K1).
    if (_lastObservedChatLogCount is null)
    {
        _lastObservedChatLogCount = currentCount;
        return;
    }

    if (currentCount == _lastObservedChatLogCount) return;   // no new lines

    // Defensive: a SHRINKING counter means circular-buffer wrap or session-internal log-clear.
    // Re-baseline silently rather than reading "negative new entries." See Decision SCI10.
    if (currentCount < _lastObservedChatLogCount)
    {
        _lastObservedChatLogCount = currentCount;
        return;
    }

    var localContentId = _gameProbe.GetLocalContentId();   // 0 when unavailable

    // Iterate the new entries [baseline..currentCount).
    var firstNewIndex = _lastObservedChatLogCount.Value;
    _lastObservedChatLogCount = currentCount;
    for (var idx = firstNewIndex; idx < currentCount; idx++)
    {
        var entry = _gameProbe.GetChatLogEntry(idx);
        if (entry is null) continue;   // out-of-range / read failed

        if (!QuestForge.Adapters.Chat.ChatLogEntryFilter.IsPlayerSayMessage(
                entry.SourceKind, entry.ChatType, entry.ContentId, localContentId))
            continue;

        // Capture target. Same priority order as PollPlayerEmote (Decision UEI9):
        // hostile (BattleNpc) wins over interactable (EventNpc) when both are set.
        uint? targetBaseId = null;
        var hostile      = _targetProbe?.GetBattleNpcTarget();
        var interactable = _targetProbe?.GetInteractableNpcTarget();
        if (hostile is { } h)
            targetBaseId = h.BaseId;
        else if (interactable is { } i)
            targetBaseId = i.BaseId;
        // If neither — no target, leave targetBaseId = null.

        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("SayChatMessageSent",
            (uint)idx,                                                     // argument: the log index (for dedup key)
            new { message = entry.Message, targetBaseId = targetBaseId ?? 0u },
            runId, now);
        _aggregator?.OnSayChatMessageSent(entry.Message, targetBaseId);
    }
}
```

Add the call into `OnFrameworkUpdate` alongside the other every-frame pollers, **after** `PollPlayerEmote` (line 231):

```csharp
PollAethernetDestination();
PollTeleportAddonOpen();
PollDialogueOption();
PollSelectYesno();
PollTargetNpc();
PollPlayerActionEffect();
PollPlayerEmote();
PollPlayerChatMessage();   // NEW
```

`ResetWindowState` adds (per Decision SCI7):
```csharp
_lastObservedChatLogCount = null;        // re-baseline; next observation silently establishes new baseline
_aggregator?.OnSayChatMessageConsumed(); // mirrors the five existing consume calls
```

**Why first observation is silent (matches UAI8, differs from UEI9):** see the docstring. Unlike emotes (where a non-zero EmoteId at session start IS a live in-progress emote), a non-zero chat-log count at session start is purely historical — pre-session lines that the author did not intend to record. Treating those as events would spam steps on session entry.

**Why per-index iteration (not "fire the latest"):** if the player types `/say A` and `/say B` between two heartbeats (250 ms window — possible if pasting), iterating fires both into the aggregator. The aggregator's `OnSayChatMessageSent` is last-write-wins, so the snapshot ends up with B's signal — but BOTH writes are observed in the trace (one `WriteObservation` per matching entry). The author sees B in the modal (which is correct — most recent intent); the trace still records A for replay-test purposes. See Open Question §F O4.

**Why iterate to currentCount, then advance baseline AFTER the loop (NOT during):** if the per-index `GetChatLogEntry` call throws or returns null on the first index, we still want subsequent indices attempted. Advancing the baseline upfront ensures we don't re-read those entries next tick even if a few in the middle returned null. The "advance after for-loop" alternative would force a retry-storm if any single entry consistently failed to read.

### Decision SCI10 — `MsgSourceArrayLength` is a "monotonic-ish" counter; defensive shrink-detection

The brief assumed a strictly monotonic `LogMessageCount`. Reality: `MsgSourceArrayLength` is the count of valid entries in `MsgSourceArray`, which is a fixed-size circular buffer (FFXIVClientStructs does not document the cap, but the struct layout at offset 0x3478 with `_3488` immediately after suggests a fixed size). In a normal short authoring session (under ~1000 chat lines) the count is effectively monotonic. In a long session OR after a `/clearlog` (if such a command exists; verify in smoke) the count can SHRINK.

**Decision: defensive shrink-detection.** If `current < baseline`, silently re-baseline to `current`. Do NOT iterate negative range. Do NOT fire any event.

**Why this is correct behavior:** a shrinking counter means the slots the previous baseline pointed to have been overwritten or removed. There is no valid way to recover "what happened in between" — those entries are gone. The safest behavior is to treat the shrink as if the user had just clicked Record (re-establish baseline at the current count, fire on future increments only).

**What breaks if violated:** if we don't shrink-detect, the `for (var idx = firstNewIndex; idx < currentCount; idx++)` loop runs zero iterations when `current < firstNewIndex` (since `firstNewIndex > current`), so no events fire. But `_lastObservedChatLogCount` stays at the old high value, so we silently lose all NEW events until the count catches back up — a worse silent-failure mode than the explicit re-baseline. The defensive re-baseline preserves the contract: "after a glitch, future genuine events fire correctly."

Pinned by test UO_M5 (shrinking counter).

### Decision SCI11 — `IGameProbe` gains three methods (count, entry-by-index, local content id)

Following the established `IGameProbe` shape:

```csharp
// QuestForge.Plugin.Tracing/IGameProbe.cs — append three methods
public interface IGameProbe
{
    // ... existing methods ...

    /// <summary>
    /// Returns RaptureLogModule.MsgSourceArrayLength, the count of valid entries in the
    /// chat log circular buffer. null when the module is unavailable (early init / character
    /// select). The UIObserver poller treats null as "skip this tick."
    /// </summary>
    int? GetChatLogMessageCount();

    /// <summary>
    /// Returns the chat log entry at the given index (the LogMessageSource at MsgSourceArray[idx]
    /// joined with GetLogMessageDetail's LogInfo + Message), or null if the index is out of
    /// range, the read failed, or the entry is invalid (empty message body).
    /// </summary>
    ChatLogEntry? GetChatLogEntry(int index);

    /// <summary>
    /// Returns InfoModule.LocalContentId (via InfoModule.GetLocalContentId()), the local
    /// player's character ContentId used to filter chat log entries to the player's own /say.
    /// Returns 0 when unavailable (early init / character select); the filter treats 0 as
    /// "no match" (Decision SCI2 defensive guard).
    /// </summary>
    ulong GetLocalContentId();
}

/// <summary>
/// Flattened chat log entry returned by IGameProbe.GetChatLogEntry. The integer fields are
/// the raw wire values (LogInfo.SourceKind as int, LogMessageSource.ChatType as int) so the
/// pure ChatLogEntryFilter helper (Dalamud-free) can consume them directly.
/// </summary>
public sealed record ChatLogEntry(
    int SourceKind,
    int ChatType,
    ulong ContentId,
    string Message);
```

**Why three methods + a record (not one fat method returning a list):** the poller needs the count *first* to detect the rising edge cheaply (one read per tick when no chat is happening). Iterating new entries only happens when the count moved. Splitting count vs. entry keeps the no-activity tick at one cheap pointer read.

**Why `int?` (not `uint?`):** `MsgSourceArrayLength` is `int` in the FFXIVClientStructs layout (line 57: `public int MsgSourceArrayLength;`). Mirror the source type at the boundary.

**Why `GetLocalContentId() → ulong` (not nullable):** `InfoModule.GetLocalContentId()` returns `ulong`; 0 means "unavailable." A separate accessor avoids returning a tuple from `GetChatLogMessageCount` (which would be cluttered: every tick reads the count, only some ticks need the content id, and the content id is rarely re-fetched once set).

**Concrete Dalamud implementation:**

```csharp
// QuestForge.Plugin/Tracing/DalamudGameProbe.cs — append
using FFXIVClientStructs.FFXIV.Client.UI.Misc;     // RaptureLogModule, LogInfo, EntityRelationKind
using FFXIVClientStructs.FFXIV.Client.UI.Info;     // InfoModule
using FFXIVClientStructs.FFXIV.System.String;       // Utf8String

public int? GetChatLogMessageCount()
{
    var mod = RaptureLogModule.Instance();
    if (mod == null) return null;
    return mod->MsgSourceArrayLength;
}

public ulong GetLocalContentId()
{
    var info = InfoModule.Instance();
    if (info == null) return 0UL;
    return info->LocalContentId;   // or info->GetLocalContentId() if the member fn is preferred
}

public unsafe ChatLogEntry? GetChatLogEntry(int index)
{
    var mod = RaptureLogModule.Instance();
    if (mod == null) return null;
    if (index < 0 || index >= mod->MsgSourceArrayLength) return null;

    ref var src = ref mod->MsgSourceArray[index];
    var logMessageIndex = src.LogMessageIndex;
    var entryContentId  = src.ContentId;
    var chatType        = (int)src.ChatType;

    // GetLogMessageDetail wants out-buffer Utf8String*; we allocate temporaries and copy.
    var pSender  = new Utf8String();
    var pMessage = new Utf8String();
    LogInfo info;
    int timestamp;
    var ok = mod->GetLogMessageDetail(logMessageIndex, &info, &pSender, &pMessage, &timestamp);
    if (!ok) { pSender.Dtor(); pMessage.Dtor(); return null; }

    var message = pMessage.ToString();
    pSender.Dtor();
    pMessage.Dtor();

    return new ChatLogEntry(
        SourceKind: (int)info.SourceKind,
        ChatType:   chatType,
        ContentId:  entryContentId,
        Message:    message);
}
```

**Smoke verification points (§F O1):**
1. Does `RaptureLogModule.Instance()` return non-null after login? Should be yes (always present once UIModule is initialized).
2. Does `GetLogMessageDetail` populate `info.SourceKind == EntityRelationKind.LocalPlayer` (= 1) for the local player's `/say`? Should be yes (the game uses the same struct for all chat sources).
3. Does `LogMessageSource.ChatType == 10` for `/say`? Verify against `XivChatType.Say == 10` in Dalamud (this is the canonical mapping).
4. Does `MsgSourceArray` ever shrink during a normal session? If yes, Decision SCI10's defensive re-baseline catches it. If no, the defensive branch is silent overhead.
5. Lifetime: does `pMessage.Dtor()` need explicit call or does the struct's RAII handle it? FFXIVClientStructs Utf8String requires manual `Dtor()` for stack-allocated instances; verify against the actual struct.

### Decision SCI12 — Trace observation event method is `"SayChatMessageSent"`

The trace stream is consumed by `qf-trace extract-quest` (Phase 10) to reconstruct quest definitions. Using a distinct method name lets the extractor route the event to a `SayChatMessageStep` arm directly. Mirroring the existing `WriteObservation("ActionCompleted", actionId, …)` and `WriteObservation("EmoteCompleted", emoteId, …)` patterns:

```csharp
WriteObservation("SayChatMessageSent",
    (uint)idx,                                                     // argument: the log index (per-tick dedup key)
    new { message = entry.Message, targetBaseId = targetBaseId ?? 0u },
    runId, now);
```

- `argument` carries the log index (uint cast). This gives `TraceSession.WriteObservation`'s dedup logic a stable key — re-firing on the same index (if the count somehow stalls) dedups correctly.
- `value` is a structured object with `message` (the literal string) and `targetBaseId` (0 = no target).

**Why `argument` is the index, not a hash of the message:** the message field contains the text; using its content as the dedup key would conflate two distinct utterances of the same phrase ("/say Hello" twice in a row). Index is unique per fire.

**Why `targetBaseId ?? 0u`:** same dedup-stability reasoning as Decisions UEI11 / UAI10.

### Decision SCI13 — Target read is "current target at the time of the read" (TargetManager.Target via ITargetProbe)

Same posture as Decisions UEI12 / UAI11. The chat log entry itself does NOT carry a target — `/say` is broadcast, and the `TargetKind` field on `LogInfo` does not encode "the NPC the player intended to speak to" (it's about message-target plumbing for tells/party-chat, not /say).

**Authors target NPCs by BaseId, captured from `TargetManager.Target` at the moment the chat-log entry is observed.** If the player tab-targets between typing `/say` and the log entry being read, the target may be stale — but the same race exists for emotes and actions. The acceptable-in-v1 verdict applies (§F O3).

### Decision SCI14 — Tie-breaker rule when multiple deliberate-input signals are set

Per Decision SCI4: **chat wins > emote > action** when multiple fire. Implementation: the `return` statement inside Rule 3.5s's if-block ensures Rule 3.5e and Rule 3.5 never run when the say signal is set. Standard short-circuit-on-first-match cascade. Test scenario SCI6 (defensive — both set) pins this.

### Decision SCI15 — `FakeGameProbe` extension (Plugin.Tests)

Extend the existing `FakeGameProbe` (currently in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` lines 96–169) with scriptable chat-log accessors:

```csharp
public sealed class FakeGameProbe : IGameProbe
{
    // ... existing fields ...

    // ── UO_M*: chat log scripting ────────────────────────────────────────────
    // Two control axes:
    //   1. The reported COUNT (sticky; advances on AppendChatEntry; can be set explicitly).
    //   2. The PER-INDEX entries (a dictionary; AppendChatEntry adds at the next index).
    //   3. The local-player ContentId (sticky; defaults to 1234 — a non-zero placeholder so
    //      tests don't need to set it explicitly).
    //
    // Tests script:
    //   gp.SetLocalContentId(999);                      // optional override
    //   gp.AppendChatEntry(new ChatLogEntry(1, 10, 999, "Open Sesame"));   // adds at idx N, count becomes N+1
    //   fw.Tick();                                       // poller observes, filters, fires
    //
    // To simulate a count shrink (Decision SCI10):
    //   gp.SetChatLogMessageCount(2);                    // force count to 2 (entries at higher
    //                                                    // indices remain in the dict but won't
    //                                                    // be iterated until count climbs again)

    private readonly Dictionary<int, ChatLogEntry> _chatEntries = new();
    private int _chatLogCount = 0;
    private ulong _localContentId = 1234UL;
    private bool _chatLogProbeAvailable = true;    // toggle to test "no LocalPlayer" path

    public void SetLocalContentId(ulong contentId) => _localContentId = contentId;
    public void SetChatLogProbeAvailable(bool available) => _chatLogProbeAvailable = available;
    public void SetChatLogMessageCount(int count) => _chatLogCount = count;

    public void AppendChatEntry(ChatLogEntry entry)
    {
        _chatEntries[_chatLogCount] = entry;
        _chatLogCount++;
    }

    public void ClearChatLog()
    {
        _chatEntries.Clear();
        _chatLogCount = 0;
    }

    public int? GetChatLogMessageCount() => _chatLogProbeAvailable ? _chatLogCount : null;

    public ChatLogEntry? GetChatLogEntry(int index)
        => _chatEntries.TryGetValue(index, out var e) ? e : null;

    public ulong GetLocalContentId() => _localContentId;
}
```

The existing `FakeTargetProbe` already supports `SetBattleNpcTarget` / `SetInteractableNpcTarget` (per `UIObserverCombatForwardingTests.cs`); no extension needed.

**Why `SetChatLogProbeAvailable(false)` accessor:** lets us test the "early init / no RaptureLogModule" path (UO_M7) without making the probe permanently null (which would block all other reads in the same fixture).

### Decision SCI16 — No re-record cascade impact (additive observation)

Per the user's MEMORY note about trace-emission refactor cascades:
- A **new probe method group** (`GetChatLogMessageCount`, `GetChatLogEntry`, `GetLocalContentId`) called every frame by UIObserver.
- A **new observation event** (`"SayChatMessageSent"`).

**Why this does NOT cascade existing fixtures:**
- The engine's `QuestEngine.Tick` is unchanged — `IChatSender` is consulted only when a `SayChatMessageStep` is at the cursor. The new probe calls are in the **authoring poller**, not in the engine.
- Engine replay fixtures (which capture engine reads) are unaffected.
- UIObserver-fixture replays would see new `"SayChatMessageSent"` observation events, but the UIObserver test suite uses `FakeGameProbe` (the new methods default to count=0/empty when `AppendChatEntry` is not called), so existing tests stay green. The defaults are silent.

If a future trace-replay test exists that records UIObserver output against a real game session, the new `"SayChatMessageSent"` events would appear in new fixtures only — no existing fixture is re-recorded.

---

## Snapshot field summary

| Field (new or existing) | Type | Set by | Cleared by | Survives ResetDeltas? |
|---|---|---|---|---|
| `SayChatMessageSent` (NEW) | `SayChatMessageSentSignal?` | `OnSayChatMessageSent` | `OnSayChatMessageConsumed` | yes |
| `EmoteCompleted` (existing) | `EmoteCompletedSignal?` | `OnEmoteCompleted` | `OnEmoteConsumed` | yes |
| `ActionCompleted` (existing) | `ActionCompletedSignal?` | `OnActionCompleted` | `OnActionConsumed` | yes |
| `TeleportCompleted` (existing) | `AetheryteId?` | `OnTeleportCompleted` | `OnTeleportConsumed` | yes |
| `AethernetTeleportCompleted` (existing) | `AethernetHop?` | `OnAethernetTeleportCompleted` | `OnAethernetTeleportConsumed` | yes |
| `DialogueOptionSelected` (existing) | `int?` | `OnDialogueOptionSelected` | `OnDialogueOptionConsumed` | yes |

The new `SayChatMessageSent` shares the **per-window event lifecycle** of the other five: cleared in `RecordStep` (and defensively in `ResetWindowState`), survives `ResetDeltas` between the "before" capture and the "after" Preview.

---

## Inference rule table — updated

| Rule | Trigger condition | Step type | InferredFrom | Confidence |
|---|---|---|---|---|
| 1 | QuestCompleted false→true | turn-in | QuestCompleted | High |
| 2 | QuestAccepted false→true | accept | QuestAccepted | High |
| 2.1 | ForeignQuestAccepted set | accept | QuestAccepted | High |
| 2.2 | KillCorrelatedTargets non-empty | combat | Combat | Med/Low |
| 2.2b | PurchaseDetected with item delta + currency drop | purchase-item | Purchase | Med/Low |
| 2.3 | KeyItemsAdded non-empty | pickup-item | DialogueInteraction | Medium |
| 2.4 | KeyItemsRemoved non-empty | hand-over-item | DialogueInteraction | Medium |
| 2.5 | LastAethernetShardInteracted changed, same zone, NPC == shard | attune | AttunementChange | High |
| 2.6 | InventoryHash changed, KeyItems diff non-empty | pickup/handover/talk | InventoryChange | Medium |
| **3.5s (NEW)** | **after.SayChatMessageSent set** | **say-chat-message** | **SayChatMessageSent** | **High** |
| 3.5e | after.EmoteCompleted set | use-emote | EmoteCompleted | High |
| 3.5 | after.ActionCompleted set | use-action | ActionCompleted | High |
| 3   | QuestSequence advanced | talk | QuestSequenceChange | High |
| 4.0 | after.TeleportCompleted set AND zone changed | teleport | TeleportCompleted | High |
| 4   | Zone changed (aethernet / NPC dialogue / shard / catch-all) | travel | ZoneChange | High |
| 2.7 | AethernetTeleportCompleted set, same zone | travel | ZoneChange | High |
| 5   | QuestFlags changed, sequence unchanged | talk | QuestFlagChange | Medium |
| 6   | LastDialogueAnswer changed | talk | DialogueInteraction | Medium |
| 7   | LastNpcInteracted changed | talk | DialogueInteraction | Low |
| 8   | Player moved >5u, same zone | travel | MovementChange | Low |
| 9   | nothing matched | Empty | None | Low |

Rule 3.5s occupies the position **immediately before Rule 3.5e** in the source file. It does NOT require any zone-change or quest-state condition — the presence of `after.SayChatMessageSent` alone is sufficient.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Adapters/Chat/ChatLogEntryFilter.cs` | NEW | Pure filter helper (Decision SCI2) |
| `QuestForge.Engine/Authoring/GameStateSnapshot.cs` | MODIFY | `SayChatMessageSentSignal` record + property (Decision SCI1) |
| `QuestForge.Engine/Authoring/InferredFrom.cs` | MODIFY | Append `SayChatMessageSent` (Decision SCI5) |
| `QuestForge.Engine/Authoring/SnapshotAggregator.cs` | MODIFY | Field + initializer + `OnSayChatMessageSent` / `OnSayChatMessageConsumed` (Decision SCI3) |
| `QuestForge.Engine/Authoring/StepInferenceEngine.cs` | MODIFY | Rule 3.5s + `Truncate` private helper (Decision SCI4) |
| `QuestForge.Engine/Authoring/StepFactory.cs` | MODIFY | `"say-chat-message"` arm (Decision SCI6) |
| `QuestForge.Plugin/Authoring/AuthoringHost.cs` | MODIFY | Consume call + `[QF-DIAG]` extension + `TruncateForLog` helper (Decision SCI7 / SCI8) |
| `QuestForge.Plugin.Tracing/IGameProbe.cs` | MODIFY | Append three methods + `ChatLogEntry` record (Decision SCI11) |
| `QuestForge.Plugin.Tracing/UIObserver.cs` | MODIFY | `_lastObservedChatLogCount` field + `PollPlayerChatMessage` + ResetWindowState wiring (Decision SCI9) |
| `QuestForge.Plugin/Tracing/DalamudGameProbe.cs` | MODIFY | Implement three new methods (Decision SCI11) |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | MODIFY | Extend `FakeGameProbe` (Decision SCI15) + add `UO_M*` tests |
| `QuestForge.Engine.Tests/Authoring/SayChatMessageInferenceTests.cs` | NEW | SCI1–SCI11 |
| `QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs` | NEW | SCI-F1–SCI-F6 |

---

## Validation rules (this plan adds none)

Validator rules for `structural/say-chat-message-*` are already shipped (E11, E12, W9 per `SAY_CHAT_MESSAGE_STEP_PLAN.md` Decision SC7). The authoring path is downstream of validation: a draft containing a `SayChatMessageStep { Message = "" }` (defensive fallback in Decision SCI6) will be caught when the draft is exported.

---

## Given-When-Then test scenarios

Tests are split into three files:

| File | Scenarios | Test type |
|---|---|---|
| `QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs` | SCI-F1..F6 | Pure filter unit tests |
| `QuestForge.Engine.Tests/Authoring/SayChatMessageInferenceTests.cs` | SCI1..SCI11 | Inference engine + aggregator + StepFactory |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | UO_M1..UO_M9 | UIObserver polling state-machine |

### Filter unit tests (`QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs`)

#### SCI-F1 — Happy path: player's own /say matches

```csharp
[Fact]
public void IsPlayerSayMessage_PlayerOwnSay_ReturnsTrue()
{
    Assert.True(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 1,          // EntityRelationKind.LocalPlayer
        chatType: 10,            // XivChatType.Say
        entryContentId: 12345UL,
        localPlayerContentId: 12345UL));
}
```

#### SCI-F2 — NPC chat is NOT a player /say (SourceKind = 7 FriendlyNpc)

```csharp
[Fact]
public void IsPlayerSayMessage_FriendlyNpc_ReturnsFalse()
{
    Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 7,           // EntityRelationKind.FriendlyNpc
        chatType: 10,            // XivChatType.Say (NPCs do speak via /say-like channels)
        entryContentId: 0UL,
        localPlayerContentId: 12345UL));
}
```

Pins the SourceKind guard.

#### SCI-F3 — Player /yell (ChatType != 10) does NOT fire

```csharp
[Fact]
public void IsPlayerSayMessage_PlayerYell_ReturnsFalse()
{
    Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 1,
        chatType: 11,            // XivChatType.Yell — NOT Say
        entryContentId: 12345UL,
        localPlayerContentId: 12345UL));
}
```

Pins the ChatType guard. Decision SC2 (hard-wired /say only).

#### SCI-F4 — Another player's /say in the zone does NOT fire (different ContentId)

```csharp
[Fact]
public void IsPlayerSayMessage_OtherPlayerSay_ReturnsFalse()
{
    Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 1,           // their game records them as LocalPlayer too — defensive
        chatType: 10,
        entryContentId: 99999UL,  // someone else
        localPlayerContentId: 12345UL));
}
```

Pins the ContentId guard. The most important filter: hangs/spam from other players in the zone must not fire inference.

#### SCI-F5 — Unknown local ContentId (== 0) never matches

```csharp
[Fact]
public void IsPlayerSayMessage_ZeroLocalContentId_ReturnsFalse()
{
    Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 1,
        chatType: 10,
        entryContentId: 12345UL,
        localPlayerContentId: 0UL));    // early-init: unknown local player
}
```

Pins the defensive guard from Decision SCI2.

#### SCI-F6 — Theory: all unsupported chat types return false

```csharp
[Theory]
[InlineData(0)]    // None
[InlineData(11)]   // Yell
[InlineData(12)]   // Shout
[InlineData(13)]   // Party
[InlineData(14)]   // Alliance
[InlineData(33)]   // FreeCompany
[InlineData(56)]   // NoviceNetwork
public void IsPlayerSayMessage_NonSayChatTypes_ReturnFalse(int chatType)
{
    Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
        sourceKind: 1,
        chatType: chatType,
        entryContentId: 12345UL,
        localPlayerContentId: 12345UL));
}
```

### Inference-engine tests (`QuestForge.Engine.Tests/Authoring/SayChatMessageInferenceTests.cs`)

For all tests below, helpers `MakeSnapshot(...)` and `MakeAggregator(...)` follow the same patterns as `UseEmoteInferenceTests` / `UseActionInferenceTests` — the Tester picks the exact factory signature.

#### SCI1 — Happy path, no target: SayChatMessageSent set → infers `say-chat-message` step

**Given:**
- `before = MakeSnapshot()` (no quest changes, no zone change)
- `after  = before with { SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"`
- `result.SuggestedStepId == "say-chat-message-broadcast"`
- `result.SuggestedExpect == null`
- `result.Confidence == Confidence.High`
- `result.InferredFrom == InferredFrom.SayChatMessageSent`
- `result.Notes` is non-null and contains the substring `"Expect"` AND the substring `"Open Sesame"`.

#### SCI2 — Happy path, NPC target: SayChatMessageSent with TargetBaseId → step id includes "-on-{tid}"

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { SayChatMessageSent = new SayChatMessageSentSignal(Message: "Hello friend", TargetBaseId: 1000789u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"`
- `result.SuggestedStepId == "say-chat-message-on-1000789"`
- `result.SuggestedExpect == null`
- `result.InferredFrom == InferredFrom.SayChatMessageSent`

#### SCI3 — Self-cast: TargetBaseId = null → step id is `"say-chat-message-broadcast"` (NO `-on-` suffix)

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { SayChatMessageSent = new SayChatMessageSentSignal(Message: "Hello", TargetBaseId: null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"`
- `result.SuggestedStepId == "say-chat-message-broadcast"` (NO `-on-` suffix)
- `result.InferredFrom == InferredFrom.SayChatMessageSent`

Pins Decision SCI4's step-id pattern for no-target.

#### SCI4 — Priority over Rule 3 (QuestSequence advanced): SayChatMessageSent wins

**Given:**
- `before = MakeSnapshot(questSequence: 1)`
- `after  = before with { QuestSequence = 2, SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: 1000789u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"` (NOT `"talk"`)
- `result.InferredFrom == InferredFrom.SayChatMessageSent` (NOT `QuestSequenceChange`)

**Rationale:** say-chat-message that advances the sequence is the most common authoring case; firing Rule 3 first would draft a `talk` step.

#### SCI5 — Priority over Rule 4 (Zone changed): SayChatMessageSent wins (defensive)

**Given:**
- `before = MakeSnapshot(zone: ZoneId(132))`
- `after  = before with { Zone = ZoneId(129), SayChatMessageSent = new SayChatMessageSentSignal("Open Sesame", null) }`
- (defensive — production should never zone-change mid-say, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"` (NOT `"travel"`)
- `result.SuggestedStepId == "say-chat-message-broadcast"`
- `result.InferredFrom == InferredFrom.SayChatMessageSent`

Pins Decision SCI4's "Rule 3.5s above Rule 4" ordering (transitive via Rule 3.5s above 3 above 4).

#### SCI6 — Priority over Rule 3.5e (EmoteCompleted) AND Rule 3.5 (ActionCompleted): say wins when all three set (defensive)

**Given:**
- `before = MakeSnapshot()`
- `after  = before with {
    ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, null),
    EmoteCompleted  = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null),
    SayChatMessageSent = new SayChatMessageSentSignal("Open Sesame", null) }`
- (defensive — production should never set all three, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"` (NOT `"use-emote"`, NOT `"use-action"`)
- `result.SuggestedStepId == "say-chat-message-broadcast"`
- `result.InferredFrom == InferredFrom.SayChatMessageSent`

Pins Decisions SCI4 / SCI14 (chat > emote > action tie-break).

#### SCI7 — Priority below Rule 1 (QuestCompleted): turn-in wins over SayChatMessageSent

**Given:**
- `before = MakeSnapshot(questCompleted: false)`
- `after  = before with { QuestCompleted = true, SayChatMessageSent = new SayChatMessageSentSignal("Open Sesame", null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "turn-in"` (Rule 1)
- `result.InferredFrom == InferredFrom.QuestCompleted`

Pins "earlier rules take precedence over Rule 3.5s."

#### SCI8 — Priority below Rule 2.3 (KeyItemsAdded): pickup-item wins over SayChatMessageSent

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { KeyItemsAdded = new[] { 2001u }, SayChatMessageSent = new SayChatMessageSentSignal("Open Sesame", null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "pickup-item"` (Rule 2.3)
- `result.InferredFrom == InferredFrom.DialogueInteraction`

Pins placement above Rule 3.5e but below the specific Rule-2.x family.

#### SCI9 — Aggregator: `OnSayChatMessageSent` sets the field; `Current.SayChatMessageSent` returns the same signal; `OnSayChatMessageConsumed` clears; `ResetDeltas` does NOT clear

**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When (sub-A — sets the field):**
- `agg.OnSayChatMessageSent(message: "Open Sesame", targetBaseId: 1000789u);`
- `var snap = agg.Current;`

**Then (sub-A):**
- `snap.SayChatMessageSent is not null`
- `snap.SayChatMessageSent.Message == "Open Sesame"`
- `snap.SayChatMessageSent.TargetBaseId == 1000789u`

**When (sub-B — ResetDeltas does NOT clear):**
- `agg.ResetDeltas();`
- `var snap2 = agg.Current;`

**Then (sub-B):**
- `snap2.SayChatMessageSent is not null` (survives ResetDeltas; only OnSayChatMessageConsumed clears).
- `snap2.SayChatMessageSent.Message == "Open Sesame"`.

**When (sub-C — OnSayChatMessageConsumed clears and does NOT side-effect):**
- `agg.OnSayChatMessageConsumed();`
- `var snap3 = agg.Current;`

**Then (sub-C):**
- `snap3.SayChatMessageSent is null`
- `snap3.LastNpcInteracted is null` (chat did NOT bleed into NPC-interaction state — pins Decision SCI3)
- `snap3.LastAttuned is null`
- `snap3.LastAethernetShardInteracted is null`
- `snap3.EmoteCompleted is null` (defensive — unrelated field untouched)
- `snap3.ActionCompleted is null` (defensive — unrelated field untouched)

**When (sub-D — last-write-wins on repeated calls):**
- `agg.OnSayChatMessageSent("first message", null);`
- `agg.OnSayChatMessageSent("second message", 555u);`
- `var snap4 = agg.Current;`

**Then (sub-D):**
- `snap4.SayChatMessageSent.Message == "second message"`
- `snap4.SayChatMessageSent.TargetBaseId == 555u`

Pins Decision SCI3's last-write-wins discipline.

#### SCI10 — `StepFactory.Build("say-chat-message", …)` produces a `SayChatMessageStep` with snapshot fields populated

**Given:**
- `after = MakeSnapshot() with { SayChatMessageSent = new SayChatMessageSentSignal("Open Sesame", 1000789u) }`

**When:** `var step = StepFactory.Build("say-chat-message", "say-chat-message-on-1000789", null, after);`

**Then:**
- `step is SayChatMessageStep sc`
- `sc.Id == "say-chat-message-on-1000789"`
- `sc.Message == "Open Sesame"`
- `sc.TargetNpcId == 1000789u`
- `sc.Expect is null`

#### SCI11 — `StepFactory.Build("say-chat-message", …)` defensive: SayChatMessageSent null → Message = "", no throw

**Given:** `after = MakeSnapshot()` (SayChatMessageSent is null)

**When:** `var step = StepFactory.Build("say-chat-message", "say-chat-message-X", null, after);`

**Then:**
- `step is SayChatMessageStep sc`
- `sc.Message == ""` (defensive fallback; validator E11 will catch)
- `sc.TargetNpcId is null`
- No exception thrown.

Pins Decision SCI6's defensive behaviour.

### UIObserver tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

These tests use the existing `BuildFixtureWithAggregatorAndTarget` helper (lines 2867–2901) and the `FakeGameProbe` extension from Decision SCI15. Test naming follows `UO_M*` convention (`M` for "Message"; `UO_K*` is action, `UO_L*` is emote).

#### UO_M1 — First observation establishes baseline; no event fires

**Given:**
- `var (obs, fw, _, gp, _, writer, _, agg, _) = BuildFixtureWithAggregatorAndTarget();`
- `gp.SetLocalContentId(12345UL);`
- `gp.AppendChatEntry(new ChatLogEntry(SourceKind: 1, ChatType: 10, ContentId: 12345UL, Message: "pre-session"));`
- (chat log already contains a pre-session entry; count is 1)

**When:** `fw.Tick();` (first tick)

**Then:**
- NO `ObservationEvent` with `Method == "SayChatMessageSent"` written (silent baseline).
- `agg.Current.SayChatMessageSent is null`.

**Critical contract:** matches `PollPlayerActionEffect` UO_K1 baseline-silent precedent (Decision SCI9). Pre-session chat lines are NOT inference events.

#### UO_M2 — Count unchanged on second tick: no event

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "pre-session"));`
- `fw.Tick();` (baseline established at count=1)

**When:** `fw.Tick();` (no new chat entries; count still 1)

**Then:**
- NO `ObservationEvent` with `Method == "SayChatMessageSent"` written (no new line).
- `agg.Current.SayChatMessageSent is null`.

#### UO_M3 — Count increases by 1 with matching entry: fires once

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `fw.Tick();` (baseline established at count=0)

**When:**
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "Open Sesame"));` (count=1)
- `fw.Tick();`

**Then:**
- Exactly one `ObservationEvent` with `Method == "SayChatMessageSent"`.
  - `Argument == 0u` (the log index).
  - The serialized `value` contains `"message": "Open Sesame"` and `"targetBaseId": 0`.
- `agg.Current.SayChatMessageSent is not null`.
- `agg.Current.SayChatMessageSent.Message == "Open Sesame"`.
- `agg.Current.SayChatMessageSent.TargetBaseId is null`.

#### UO_M4 — Count increases with non-matching entry: does NOT fire but advances baseline

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `fw.Tick();` (baseline established at count=0)

**When:**
- `gp.AppendChatEntry(new ChatLogEntry(SourceKind: 1, ChatType: 10, ContentId: 99999UL, Message: "another player"));` (count=1, but different ContentId)
- `fw.Tick();`

**Then:**
- NO `ObservationEvent` with `Method == "SayChatMessageSent"` written.
- `agg.Current.SayChatMessageSent is null`.
- AND THEN a follow-up tick with a matching entry SHOULD fire:
  - `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "Open Sesame"));` (count=2)
  - `fw.Tick();`
  - One `ObservationEvent` with `Method == "SayChatMessageSent"`, `Argument == 1u`, `value.message == "Open Sesame"`.

Pins "baseline advances past non-matching entries silently."

#### UO_M5 — Shrinking counter (circular buffer wrap / log clear): silent re-baseline

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `gp.SetChatLogMessageCount(100);`     // pretend many lines already present
- `fw.Tick();` (baseline = 100)

**When:**
- `gp.SetChatLogMessageCount(2);`        // simulated wrap / log clear
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "After Wrap"));`   // count becomes 3 (post-AppendChatEntry behavior in the fake)
- (Tester adjustment if `AppendChatEntry` resets `_chatLogCount` differently — use `SetChatLogMessageCount(3)` then add the entry at idx 2 manually)
- `fw.Tick();`

**Then:**
- Either:
  - NO `ObservationEvent` (if the test scenario only does the SetChatLogMessageCount(2) shrink without a subsequent matching entry — proves the defensive re-baseline catches shrink without throwing), OR
  - Exactly one `ObservationEvent` from the matching entry added AFTER the re-baseline (proves the next genuine event fires correctly).
- No exception thrown.

Pins Decision SCI10's defensive shrink-detection. Tester picks the simplest scenario shape that proves the re-baseline behavior.

#### UO_M6 — Count increases by N with mixed matching/non-matching: fires per match

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `fw.Tick();` (baseline=0)

**When:** (between heartbeats, three chat lines land — two from the player, one from another player)
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "A"));`     // match, idx=0
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 99999UL, "B"));`     // OTHER player, no match, idx=1
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "C"));`     // match, idx=2
- `fw.Tick();`

**Then:**
- Exactly TWO `ObservationEvent`s with `Method == "SayChatMessageSent"`.
- First: `Argument == 0u`, `value.message == "A"`.
- Second: `Argument == 2u`, `value.message == "C"`.
- `agg.Current.SayChatMessageSent.Message == "C"` (last-write-wins per Decision SCI3).

Pins per-index iteration + last-write-wins aggregator semantics.

#### UO_M7 — Probe unavailable (no LocalPlayer / RaptureLogModule null): silent no-op

**Given:**
- `gp.SetChatLogProbeAvailable(false);`     // probe returns null for GetChatLogMessageCount

**When:** multiple `fw.Tick()` calls.

**Then:**
- NO `ObservationEvent` with `Method == "SayChatMessageSent"` written.
- No exception thrown.
- `agg.Current.SayChatMessageSent is null`.

Pins the `if (current is null) return;` early-out in `PollPlayerChatMessage`.

#### UO_M8 — Target capture via TargetManager.Target (interactable NPC)

**Given:**
- `tp.SetInteractableNpcTarget((BaseId: 1000789u, X: 0f, Y: 0f, Z: 0f, Zone: 132));`
- `gp.SetLocalContentId(12345UL);`
- `fw.Tick();` (baseline=0)

**When:**
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "Open Sesame"));`
- `fw.Tick();`

**Then:**
- One `ObservationEvent` with `Method == "SayChatMessageSent"`, `Argument == 0u`.
- The serialized `value.targetBaseId == 1000789u`.
- `agg.Current.SayChatMessageSent.TargetBaseId == 1000789u`.

#### UO_M9 — ResetWindowState re-baselines + calls OnSayChatMessageConsumed

**Given:**
- `gp.SetLocalContentId(12345UL);`
- `fw.Tick();` (baseline=0)
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "First"));`
- `fw.Tick();` (event fires; baseline=1; aggregator has signal)

**When:**
- `obs.ResetWindowState();`   (clears baseline + calls OnSayChatMessageConsumed)

**Then (immediately after ResetWindowState, BEFORE next tick):**
- `agg.Current.SayChatMessageSent is null` (consumed).

**When (continuation — verify re-baseline is silent on next tick):**
- `fw.Tick();`   (count is still 1, but baseline reset → silently re-baselines)

**Then:**
- Total `ObservationEvent` count with `Method == "SayChatMessageSent"` is still 1 (no new event from the re-baseline tick).

**When (continuation 2 — verify post-reset genuine event fires):**
- `gp.AppendChatEntry(new ChatLogEntry(1, 10, 12345UL, "Second"));`   (count=2)
- `fw.Tick();`

**Then:**
- Total `ObservationEvent` count with `Method == "SayChatMessageSent"` is 2.
- The new event's `Argument == 1u`, `value.message == "Second"`.
- `agg.Current.SayChatMessageSent.Message == "Second"`.

Pins Decision SCI7 (ResetWindowState calls OnSayChatMessageConsumed + re-baselines).

### Plan-level scenario classification

| Scenario | File | Type |
|---|---|---|
| SCI-F1..F6 | `ChatLogEntryFilterTests` | pure filter |
| SCI1..SCI8 | `SayChatMessageInferenceTests` | inference engine (incl. priority pinning) |
| SCI9 | `SayChatMessageInferenceTests` | aggregator |
| SCI10..SCI11 | `SayChatMessageInferenceTests` | StepFactory |
| UO_M1..UO_M9 | `UIObserverTests` | UIObserver + FakeGameProbe + FakeTargetProbe |

**Total: 6 filter + 11 inference/aggregator/factory + 9 UIObserver = 26 new tests.**

---

## F. Open questions / discovery items

### O1 — Does `RaptureLogModule.Instance()` return non-null and populate entries for the local player's /say?

**Status:** unknown until in-game validation.

The signal-research above confirms the struct layout (line offsets are documented) but assumes:
- `RaptureLogModule.Instance()` works post-login (verified by analogous use of `UIModule` in the codebase).
- `GetLogMessageDetail` returns `info.SourceKind == EntityRelationKind.LocalPlayer` (1) when the entry was from the local player's `/say`.
- `LogMessageSource.ChatType` is the same wire value as Dalamud's `XivChatType.Say` (10).

**Discovery recommendation:**
1. Implement the engine + UIObserver + filter surface (Phases A–D). Tests pass against `FakeGameProbe`.
2. Implement `DalamudGameProbe.GetChatLogMessageCount` / `GetChatLogEntry` / `GetLocalContentId` (Phase E).
3. Add a temporary `[QF-DIAG] ChatLog poll: count={N}, lastEntry={SourceKind=X, ChatType=Y, Content=Z}` debug log in `PollPlayerChatMessage` for the smoke session.
4. Manual in-game: enter Inspect mode, type `/say test`. Observe whether the QF-DIAG line shows `count` increment, `SourceKind=1`, `ChatType=10`, and `Content == LocalContentId`.
5. **If `SourceKind` is something other than 1 for the local player's /say** (e.g. the game records the local player as `EntityRelationKind.OtherPlayer` for log purposes — unlikely but possible): update `ChatLogEntryFilter.SourceKindLocalPlayer` to match.

**Resolution unblocks Phase E** (in-game smoke). Phases A–D (engine surface + tests) are unblocked.

### O2 — Is `MsgSourceArrayLength` actually monotonic, or does it shrink as the circular buffer wraps?

**Status:** Decision SCI10 assumes it can shrink and includes a defensive re-baseline. Smoke should verify.

If the buffer truly wraps (overwrites old slots without changing the count) the count is fully monotonic and Decision SCI10's defensive branch is dead code. If the game compacts (memmove the array down on wrap, decrement count) the count CAN shrink and Decision SCI10's branch protects us.

**Discovery recommendation:** in Phase E smoke, log `count` every 10 seconds for ~30 minutes of normal play in a busy zone (Limsa Aetheryte at peak). If count climbs monotonically to thousands without shrinking, the buffer is large enough that wrap is not a practical concern; document the empirical observation and Decision SCI10 stays as defensive guard. If count shrinks at any point, the defensive branch is load-bearing.

**Resolution unblocks NOTHING — purely correctness verification.**

### O3 — Race window between count read and per-entry read

The poller does:
1. Read `_gameProbe.GetChatLogMessageCount()` → say it returns N.
2. Loop `idx in [baseline..N)`, calling `_gameProbe.GetChatLogEntry(idx)`.

Between step 1 and step 2's first iteration, the game may have added entries (count now N+1), which we miss this tick — but they'll be picked up next tick via the new baseline. Acceptable.

Between steps within the loop, the buffer could in theory be modified — but RaptureLogModule writes happen on the game's main thread (the same thread the framework callback runs on), so re-entrancy within a single poll is impossible. Safe.

**Defer.** Not a release blocker.

### O4 — Multi-line paste

If the player pastes a multi-line message (or rapid-fires `/say A` + `/say B` between heartbeats), each line becomes a separate log entry. The poller fires `OnSayChatMessageSent` once per matching entry (UO_M6 pins this). The aggregator's last-write-wins means the snapshot shows the LAST message; the trace stream records ALL messages.

**Behavior verdict for v1: acceptable.** The author probably intended the multi-line as one logical step; recording each as a separate `say-chat-message-*` would force them to delete N-1 drafts. Recording only the last in the snapshot and showing it in the modal is the right UX. The trace records all lines so future tooling can split them if needed.

**If smoke reveals authors complaining about lost lines:** add a `SayChatMessages: IReadOnlyList<SayChatMessageSentSignal>` cumulative snapshot field and a UI-level "show all N lines" affordance. Phase 9+ follow-up.

**Resolution unblocks NOTHING — purely UX polish.**

### O5 — Should outbound /say be blocked while in GM jail / chat-banned?

If the game silently drops the player's `/say` (chat ban, GM jail, system silence), no log entry is written → no event fires → inference correctly does nothing. Aligns with the engine's stateless retry posture — the engine's own dispatch retries until either Expect satisfies or the user intervenes.

**No action required.** Document as expected behavior.

### O6 — Should `IGameProbe.GetChatLogMessageCount` be called by Inspect mode (passive trace)?

Currently `UIObserver` calls all every-frame pollers regardless of mode; the `WriteObservation` calls write to the trace per the TraceSession gate. If TraceMode is `Always` or `Authoring`, the `"SayChatMessageSent"` events appear in the trace even outside Author mode. Same posture as `"EmoteCompleted"`, `"ActionCompleted"`.

**Decision: yes, fire in Inspect mode too** (matches existing pattern, mirrors Decision UEI O5). The aggregator forwarding is gated by `_aggregator is not null` — in Inspect mode there IS an aggregator (`EnterInspectModeCore` calls `SetAggregator(_aggregator, "inspect")`) so the snapshot field updates, but `RecordStep` is not invoked in Inspect mode so the field is never consumed. The Record modal in Inspect mode is read-only anyway.

---

## Implementation order

**Phase A — Pure filter helper (5 min, fully xUnit-testable)**
1. Create `QuestForge.Adapters/Chat/ChatLogEntryFilter.cs` per Decision SCI2.
2. Tester: write SCI-F1..F6 in `QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs`. Red.
3. Implement the filter. Green.

**Phase B — Engine surface (15 min, all xUnit-testable)**
1. Add `SayChatMessageSentSignal` record + `GameStateSnapshot.SayChatMessageSent` property (Decision SCI1).
2. Add `InferredFrom.SayChatMessageSent` (Decision SCI5).
3. Add `SnapshotAggregator.OnSayChatMessageSent` / `OnSayChatMessageConsumed` methods + `Current` initializer (Decision SCI3).
4. Tester: write SCI9 (aggregator test). Red, implement, green.

**Phase C — Inference rule + StepFactory (10 min)**
1. Insert Rule 3.5s in `StepInferenceEngine` (between Rule 2.6 and Rule 3.5e) (Decision SCI4).
2. Add `"say-chat-message"` arm to `StepFactory.Build` (Decision SCI6).
3. Tester: write SCI1..SCI8 (inference tests with priority pinning) + SCI10..SCI11 (StepFactory tests). Red, implement, green.

**Phase D — AuthoringHost clearing + diagnostic (3 min)**
1. Add `_aggregator.OnSayChatMessageConsumed();` to `RecordStep` (Decision SCI7).
2. Extend `[QF-DIAG]` line with `SayChatMessageSent=...` + add `TruncateForLog` helper (Decision SCI8).
3. No new test in this plan (covered structurally by SCI9's aggregator test; the host wiring is a one-line edit verified in-game).

**Phase E — IGameProbe contract + FakeGameProbe extension + UIObserver tests + impl (45 min)**
1. Extend `IGameProbe` with three methods + `ChatLogEntry` record (Decision SCI11).
2. Extend `FakeGameProbe` (Decision SCI15).
3. Tester: write UO_M1..UO_M9. Red.
4. Implement `PollPlayerChatMessage` + `_lastObservedChatLogCount` field + `ResetWindowState` updates (Decision SCI9). Green.

**Phase F — Dalamud probe + in-game smoke (BLOCKED on §F O1 verification)**
1. Implement `DalamudGameProbe.GetChatLogMessageCount` / `GetChatLogEntry` / `GetLocalContentId` (Decision SCI11).
2. Manual in-game test: enter Inspect mode with TraceMode=Always, type `/say test message`. Confirm the QF-DIAG output shows the chat log count incrementing and the new log entry with `SourceKind=1, ChatType=10, Content==LocalContentId, Message="test message"`.
3. If §F O1 fails (SourceKind / ChatType / ContentId don't match expectations): adjust constants in `ChatLogEntryFilter` and re-run smoke. No engine/inference test changes needed (the contract is unchanged).
4. Enter Author mode for any quest, type a `/say` while targeting an NPC, open Record modal — confirm the modal shows `say-chat-message` inference with the correct Message and TargetNpcId.
5. Confirm the recorded draft's JSON shape: `{ "type": "say-chat-message", "id": "say-chat-message-on-{tid}", "message": "...", "targetNpcId": {tid} }` (matching Decision SC1 of `SAY_CHAT_MESSAGE_STEP_PLAN.md`).

---

## Done criteria

1. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~ChatLogEntryFilterTests` reports all 6 filter tests green.
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~SayChatMessageInferenceTests` reports all 11 inference/aggregator/factory tests green.
3. `dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests&FullyQualifiedName~UO_M"` reports all 9 UIObserver tests green.
4. No regression in existing `StepInferenceEngineTests`, `UseEmoteInferenceTests`, `UseActionInferenceTests`, `TeleportInferenceTests`, `AethernetInferenceTests`, `AethernetStepFactoryTests`, or `UIObserverTests` (UO_A..UO_L).
5. The trace stream emitted during a player /say contains an `ObservationEvent` with `Method == "SayChatMessageSent"`, `Argument == <log index>`, and value object containing `message` and `targetBaseId`.
6. **In-game smoke (after Phase F):** With Author mode enabled for any quest, typing `/say <message>` produces a draft `SayChatMessageStep { Message, TargetNpcId? }` in the recorded steps. The author edits the `Expect` field; the draft validates (W9 disappears once Expect is set).
7. The `[QF-DIAG] PreviewInference:` line includes `SayChatMessageSent=<truncated message>` and shows the correct message when /say was the inferred trigger.

---

## Exclusions (what this plan does NOT include)

- **Validator rules** for `structural/say-chat-message-*` — already shipped (E11, E12, W9 per `SAY_CHAT_MESSAGE_STEP_PLAN.md` Decision SC7).
- **Chat-channel inference beyond /say** (`/yell`, `/shout`, `/party`, `/fc`, `/tell`). Decision SC2 already restricts the step type to /say. Inference correspondingly filters via `ChatLogEntryFilter.ChatTypeSay == 10`. Future channels = new step types + new filter constants + new rule.
- **Outbound /say HOOK** — explicitly rejected per signal-research preference for polling. The brief flagged hooks would require user OK; not needed since RaptureLogModule polling suffices.
- **Multi-line aggregation** (Open Question §F O4). v1 uses last-write-wins; trace stream preserves all lines.
- **Filtering bot / spam messages by content** (e.g. URL detection, obscenity filter). Out of scope; not authorable.
- **Synthesizing Expect from message content** (e.g. inferring `questFlag(...)` from common phrases). No reliable heuristic; W9 surfaces the requirement.
- **Trace-side extractor** (`qf-trace extract-quest` route for `"SayChatMessageSent"` events) — Phase 10 follow-up. The tooling catch-up notes in `SAY_CHAT_MESSAGE_DALAMUD_PLAN.md` SCD-9 already covered the engine-action side; the observation-event extractor is separately deferred.
- **Re-firing on the same message** — per-index iteration ensures each log entry fires once. If the player types `/say A` twice in a row, two distinct entries are recorded; both fire (per UO_M6).
- **`LogInfo.TargetKind` interpretation** — the field exists but doesn't reliably encode the player's intended /say target. We use `TargetManager.Target` instead (Decision SCI13).
- **Message-content sanitization at the snapshot boundary** — we store the literal Utf8String→string conversion. Embedded control codes (the game's auto-translate brackets, item-link tags) pass through to the draft step's `Message`. The validator and Dalamud shell handle character-level concerns.
- **Custom-emote / emote-aliased /say** — out of scope.
- **Hooking into ECommons' Chat.SendMessage outbound side** — not needed; the round-trip via RaptureLogModule observes our own outgoing /say AND any /say the player typed directly. Single observation path is simpler.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 4 scenarios (SCI1, SCI2, SCI3, UO_M3)
- Edge cases: 11 scenarios (SCI4, SCI5, SCI9, SCI10, UO_M1, UO_M2, UO_M4, UO_M5, UO_M6, UO_M8, UO_M9)
- Error / no-op cases: 4 scenarios (SCI11, UO_M7, SCI-F2, SCI-F3, SCI-F4, SCI-F5)
- Priority pinning: 4 scenarios (SCI4, SCI5, SCI6, SCI7, SCI8) — covers above/below for Rules 1, 2.3, 3, 3.5 (ActionCompleted), 3.5e (EmoteCompleted), 4
- Filter happy/error: 6 scenarios (SCI-F1, SCI-F2, SCI-F3, SCI-F4, SCI-F5, SCI-F6)
- Expected total: ~26 tests across three files:
  - 6 in `QuestForge.Adapters.Tests/Chat/ChatLogEntryFilterTests.cs`
  - 11 in `QuestForge.Engine.Tests/Authoring/SayChatMessageInferenceTests.cs`
  - 9 in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (as new `UO_M*` group)

Builder is **fully unblocked for Phases A–E**. Phase F (in-game smoke) requires the `DalamudGameProbe` implementation plus §F O1 verification (do `SourceKind=1`, `ChatType=10`, `ContentId==LocalContentId` actually identify the local player's /say?).
