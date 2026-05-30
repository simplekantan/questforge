# DalamudChatSender Implementation Plan (Slice 3)

**Status:** ready for test creation

**Slice:** 3 (Dalamud impl + EngineHost dispatch arm + paired tooling catch-up). Slice 1 = `docs/SAY_CHAT_MESSAGE_STEP_PLAN.md`. Slice 2 (engine + schema + validator + `IChatSender` + `FakeChatSender`) shipped — verified by greps over `QuestForge.Engine/QuestEngine.cs:42-92,600-604,899-910`, `QuestForge.Adapters/Chat/IChatSender.cs:1-9`, `QuestForge.Engine/EngineAction.cs:43-46`. Slice 4 = in-game smoke (this plan §G). Slice 5 = authoring inference.

**Input docs:**
- `CLAUDE.md` — "Adding a New Step Type — Fixed Slice Order" §"Slice 3" — the cross-repo paired-PR rule and the explicit lazy-dismount-exemption checkpoint
- `docs/SAY_CHAT_MESSAGE_STEP_PLAN.md` — Slice 2 spec; **Decision SC9** sketches the Dalamud shell (mirrored exactly here), Decision SC6 pins the dismount posture
- `docs/USE_EMOTE_DALAMUD_PLAN.md` — closest analog (DED-1 through DED-11). The shape of this plan mirrors it; SCD-N decisions map ~1:1 to DED-N where the analog applies
- `QuestForge.Adapters/Chat/IChatSender.cs` — `Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct)`; this is the interface being implemented
- `QuestForge.Adapters.Dalamud/Emotes/DalamudEmoteExecutor.cs` — the structural template. The ECommons namespace shadow is already handled there via `using ECChat = ECommons.Automation.Chat;` (line 7) — same hazard applies here (Decision SCD-4)
- `QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs:23-44` — ObjectTable scan + `_svc.TargetManager.Target` write pattern
- `QuestForge.Plugin/EngineHost.cs:47` (`_emoteExecutor` field), `:113` (ctor construct), `:141` (Debug accessor), `:213-219` (`BeginRun` engine ctor call with `emoteExecutor:`), `:464-474` (`UseEmote` dispatch arm) — the four placement-precedents for SCD-5/SCD-6/SCD-7/SCD-8
- `C:\Users\publi\RiderProjects\Lifestream\ECommons\ECommons\Automation\Chat.cs:65-81` — `Chat.SendMessage(string)`: throws `ArgumentException` (empty / >500 bytes / invalid chars) and `InvalidOperationException` (signature lookup failure)
- `QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs:26` — `[typeof(SayChatMessageStep)] = "step:say-chat-message"` already present; no inferrer edit needed
- `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs:18-62` — the two catch-up tables (`FilenameLookup` and `DistinguishingCapPriority`)
- `QuestForge.Tools.Trace/TraceConstants.cs:10-22` — the action-type lowercase catalog
- `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs:614-628` — `SuggestFilename_WithUseEmote_ReturnsWithUseEmote` (the test-pattern template for SCD-T1)

**Output (CI behavior):** Adding `_chatSender = new DalamudChatSender(services)` to `EngineHost` and a `case EngineAction.SayChatMessage:` arm to `DispatchAction` makes `SayChatMessageStep` actually fire in-game. The engine's `ResolveSayChatMessage` (already shipped) no longer returns the `AwaitUser("SayChatMessageStep dispatched but no IChatSender wired — host must supply one")` fallback when `EngineHost` is the host (`QuestEngine.cs:899-903`). **No new tests on the questforge side** (the Dalamud-bound shell is validated by smoke; engine purity precludes a test-project reference). **One new test on the questforge-tools side**: `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage`. `CapabilityInferrer.StepCapabilities` entry is already present so no inferrer test is added.

---

## Dependency graph

```
QuestForge.Adapters.Dalamud
   └── Chat/DalamudChatSender.cs (NEW — uses ECommons.Automation.Chat, no Lumina lookup)
         └── consumed by ↓
QuestForge.Plugin
   └── EngineHost (field + ctor construct + BeginRun arg + DispatchAction arm + optional Debug accessor)

questforge-tools (paired PR — push both before either merges)
   └── QuestForge.Tools.Trace
        ├── Fixture/TraceToFixtureExtractor.cs (FilenameLookup + DistinguishingCapPriority)
        └── TraceConstants.cs (ActionSayChatMessage)
   └── QuestForge.Tools.Trace.Tests
        └── TraceToFixtureExtractorTests.cs (SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage)
```

**Build order (questforge):**
1. `DalamudChatSender` shell (no Lumina, target-then-send, try/catch around `ECChat.SendMessage`).
2. `EngineHost` field declaration after `_emoteExecutor` (line 47).
3. `EngineHost` ctor construction after `_emoteExecutor = …` (line 113).
4. `EngineHost.BeginRun` engine ctor gains `chatSender: _chatSender` (line 219).
5. `EngineHost.DispatchAction` gains `case EngineAction.SayChatMessage sc:` between `UseEmote` (line 474) and `Wait` (line 476).
6. (Optional) `IChatSender DebugChatSender => _chatSender;` after line 141.
7. Manual in-game smoke (§G).

**Build order (questforge-tools):**
1. Verify `[typeof(SayChatMessageStep)] = "step:say-chat-message"` is in `CapabilityInferrer.StepCapabilities` (already present at line 26 — no edit).
2. Append the new exact-shape row to `FilenameLookup`.
3. Append the new fallback row to `DistinguishingCapPriority`.
4. Append `ActionSayChatMessage = "saychatmessage"` to `TraceConstants`.
5. Add `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage` test in `TraceToFixtureExtractorTests.cs`.

Total: 1 NEW file (~75 lines) + 4 surgical edits in `EngineHost.cs` (questforge); 3 surgical edits + 1 test (questforge-tools). No engine-side changes; no schema-side changes.

---

## Architectural decisions (read before coding)

### Decision SCD-1 — There is NO new pure-logic seam in this slice

Mirrors `USE_EMOTE_DALAMUD_PLAN.md` Decision DED-1.

Slice 2 already locked in Decision SC8 (no `ChatMessageResolver` / `SayCommandFormatter` — the `"/say " + message` concat is a one-liner with no testable failure modes). This slice does not re-litigate. Honestly evaluating the candidates raised in the brief:

| Candidate | Why not extracted |
|---|---|
| "Format the command string from (message)" | `"/say " + message` — one expression, no branching, no Lumina lookup. Extracting would create a function whose body is shorter than its declaration. Tautological test. |
| "Map ECommons exceptions to `Result.Fail`" | Same as DED-1's analysis for emote-side: the try/catch body is `Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}")`. A helper `MapChatException(Exception) → Result.Fail(...)` would be one line, tested by one tautological assert against a hand-thrown exception. `DalamudEmoteExecutor` does not extract it; consistency wins. |
| "Filter ObjectTable predicate" (`obj.BaseId == id && ObjectKind in {EventNpc, BattleNpc}`) | Identical to `DalamudActionExecutor.UseAction`, `DalamudEmoteExecutor.UseEmote`, `DalamudInteractor.InteractWith`. No new abstraction warranted by a fourth instance. |

**Recommendation:** no new test files in questforge. The Slice 2 engine tests (SC1–SC9, already shipped) plus this slice's in-game smoke (§G) is the coverage. On the questforge-tools side: one new test (SCD-T1) for `SuggestFilename`.

**What breaks if violated:** extracting a `SayCommandFormatter` helper to chase coverage creates a function-call boundary in a code path with no branching; tests exist only to test the test (the helper's body and the inline expression are identical strings of code).

### Decision SCD-2 — Mirror `DalamudEmoteExecutor` shell structure (minus Lumina preload + minus pure resolver call)

The `DalamudChatSender` shell is structurally `DalamudEmoteExecutor` minus the Lumina preload step minus the pure-resolver step (because the command is built inline). The result is shorter and simpler than the emote shell.

Order of operations:

1. `ct.ThrowIfCancellationRequested()` first.
2. If `targetNpcId` is set, scan ObjectTable filtered to `EventNpc | BattleNpc`, fail with `Result.Fail("targetNotFound", …)` on miss, otherwise assign `TargetManager.Target`.
3. Build the command string inline: `var command = "/say " + message;`
4. Submit via `ECChat.SendMessage(command)` inside `try/catch`.
5. Return `Result.Ok(Unit.Value)`.

**Why scan-target THEN build-command (different from DED-2's resolve-then-scan order):** there is no command-resolution-can-fail stage here. The command is unconditionally `"/say " + message` regardless of which NPC (if any) is targeted. The "fail fast on bad inputs without clobbering `TargetManager.Target`" rationale that drove DED-2 does not apply because there is no input that can fail before the target scan — `message` validity is the adapter's problem only via `Chat.SendMessage` throwing, which is the last step.

**What if `message` is null?** Slice 2 Decision SC7 / rule E11 rejects null/empty `Message` at validator time. The adapter is defensive only insofar as `Chat.SendMessage` itself rejects pathological strings (`ArgumentException` → caught → `chatSendFailed`). No explicit null guard at the adapter layer.

**Why no `am == null`-style nullability guard:** there is no `Chat.Instance()` — `ECChat.SendMessage` is a static method. The closest analog (signature-lookup failure) throws `InvalidOperationException`, caught by step 4. Same posture as `DalamudEmoteExecutor` (DED-2).

**What breaks if violated:** if the command-string build moves before the target scan, nothing breaks because the build cannot fail — but it makes the shell visually diverge from `DalamudEmoteExecutor` for no reason. Keep parallel structure.

### Decision SCD-3 — No Lumina lookup; command is `"/say " + message` literal

Pinned by Slice 2 Decisions SC2 (channel hard-wired) and SC3 (literal message). The adapter does not translate, does not fingerprint, does not look up a CustomTalk row, does not pre-validate against an allow-list.

The non-English-client limitation is the user's known-issue per SC3; the adapter does not "fix" it.

**What breaks if violated:** any Lumina-text-id pathway adds a sheet preload step (extra ctor work, extra failure mode), a per-call lookup branch (extra error code), and a localization concern (which client's translation wins?). None of that is needed for v1 which ships English literals.

### Decision SCD-4 — Namespace shadow: use `using ECChat = ECommons.Automation.Chat;` alias

**Hazard:** Slice 2 added `QuestForge.Adapters.Chat` namespace (for `IChatSender.cs`). C#'s namespace-vs-type lookup precedence means `Chat` bare-resolves to the **namespace** within any code that has `QuestForge.Adapters.Chat` in scope, including code inside `QuestForge.Adapters.Dalamud.Chat` (the namespace of the new shell). Without an alias, `Chat.SendMessage(...)` is a compile error — and confusingly `Chat` is "in scope" (just as a namespace, not as a type).

This is the same hazard `DalamudEmoteExecutor.cs:4-7` documents. Mirror the fix exactly:

```csharp
// QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs (header)
using ECChat = ECommons.Automation.Chat;
```

Use `ECChat.SendMessage(command)` at the call site.

**Why not `using static ECommons.Automation.Chat;`:** also works but bare `SendMessage` reads less clearly than `ECChat.SendMessage` at the call site (the prefix advertises the ECommons origin). The codebase chose the alias form for `DalamudEmoteExecutor`; consistency wins.

**Why not move `IChatSender` to a different namespace to dodge the shadow:** the namespace shadow is a one-line alias workaround; refactoring `IChatSender` would touch Slice 2's already-shipped code for no behavioral benefit. The alias is the right granular fix.

**What breaks if violated:** build error inside `QuestForge.Adapters.Dalamud.Chat` ("namespace `Chat` does not contain definition for `SendMessage`"). Discoverable at compile time; cheap to recover from but humiliating to ship a PR that fails compile.

### Decision SCD-5 — `Chat.SendMessage` is wrapped in `try/catch` that maps all exceptions to `Result.Fail("chatSendFailed", …)`

Identical to DED-4 (which see for the full exception inventory). For the say-step specifically:

- `ArgumentException("Message is empty")` — should never fire because `"/say " + message` is at least 5 chars. Defensive coverage in case validator E11 is bypassed (drafts, partial JSON, etc.).
- `ArgumentException("Message exceeds 500 bytes")` — a 495-character `message` would trip this. Highly unlikely from quest data; possible if a future bug populates message from user input. Surfaces cleanly as `chatSendFailed`.
- `ArgumentException("invalid characters")` — non-ASCII or chat-control characters. Should not occur in well-authored quest data; the catch is defensive.
- `InvalidOperationException("signature not found")` — game-patch / ECommons-out-of-sync failure mode. Plugin-wide; surfaces as `Result.Fail` so the engine can `AwaitUser` cleanly rather than crash the dispatch loop.

**Why `catch (Exception ex)` and not specific types:** see DED-4 rationale. Adapter-shell policy is "exceptions surface as `Result.Fail`, never propagated."

**What breaks if violated:** narrowing the catch lets future ECommons exceptions crash the plugin. Removing the catch lets `ArgumentException("invalid characters")` crash the plugin on first malformed message.

### Decision SCD-6 — EngineHost dispatch arm: between `UseEmote` and `Wait`

```csharp
case EngineAction.SayChatMessage sc:
    DebounceLog(
        $"saychat:{sc.Message.Length}:{sc.TargetNpcId?.Value}",
        $"[SayChatMessage] message=\"{sc.Message}\" target={sc.TargetNpcId?.Value.ToString() ?? "broadcast"}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    await _chatSender.Send(sc.Message, sc.TargetNpcId, ct);
    break;
```

**Position:** between `case EngineAction.UseEmote ue:` (ends at `EngineHost.cs:474`) and `case EngineAction.Wait:` (starts at `:476`). Mirrors the engine plan's `EngineAction` enum order (`SayChatMessage` follows `UseEmote` in `EngineAction.cs:43`).

**Why debounced log:** the engine stateless-retries every tick while `Expect` is unmet. A several-second NPC-reaction wait would dump 10+ `[SayChatMessage]` lines per second into `dalamud.log` without debounce. Same posture as every other dispatch arm.

**Debounce key:** `$"saychat:{sc.Message.Length}:{sc.TargetNpcId?.Value}"` — keyed on **message length** rather than the full message text. Rationale:
- Full-message-text keys (`$"saychat:{sc.Message}:{...}"`) work but if the message contains characters that look weird in a debounce-key context (newlines, quotes, colons), the key becomes unreadable in debug output. The length proxy avoids that risk.
- Quests almost never have two `SayChatMessageStep`s with the same `(length, target)` pair distinct from `(text, target)`, so collisions are vanishingly rare and harmless (the worst case is one extra suppressed log line on a collision tick — the next dispatch logs as soon as either field changes).

**Why the log line includes the full message text** (but the key does not): the log is for developer eyes — seeing the actual text the engine submitted is the primary diagnostic value of the line. Keying on text would make the key noisy for no benefit; keying on length keeps the dedup tight while preserving readable output.

**Why stop navigation first:** identical reasoning to UseEmote (DED-8) and UseAction. vnavmesh may still be ticking; `IsNavigating` is a cheap gate; `Stop` prevents the player walking through the speech (or, given Decision SCD-7's no-`TryCutsceneSkipConfirm`, more relevantly: prevents the player walking away from the NPC they're supposed to face).

**Why no `TryCutsceneSkipConfirm()`:** unlike UseAction/UseEmote which sometimes fire mid-cutscene-fade (combat actions during transitions), say-chat-message is essentially never appropriate mid-cutscene — the engine's `Casting` guard (Slice 2 Decision SC5) defers during cutscene starts, and there is no quest-script pattern where the player must speak *during* a cutscene. Omitting the cutscene-skip-confirm keeps the arm thin and avoids a "magic" call that does not match a real use case. Builder may add it if a future smoke surfaces a real need; not required for v1.

**Why no `_lastDispatchedActionWasSayChat` tracking:** unlike Purchase (deferred shop-close) or Navigate (lazy dismount), SayChatMessage has no follow-up cleanup. Fire-and-forget. The lazy-dismount hook already at `EngineHost.cs:286` handles "if previous was Navigate, dismount before SayChatMessage" automatically because `SayChatMessage` is not in the `not Navigate and not Teleport` exemption (it falls into the dismount-eligible branch).

**Why SayChatMessage is NOT added to the lazy-dismount exemption list:** pinned by Slice 2 Decision SC6. NPC scripts may check the player's pose; standing while speaking is the cleanest pose to observe; there is no game-side auto-dismount before `/say`. The existing `EngineHost.cs:286` condition (`is not EngineAction.Navigate and not EngineAction.Teleport`) already routes `SayChatMessage` through the dismount path without modification — **explicit non-action required**, but the architect calls this out so the builder does not "helpfully" add an exemption.

**What breaks if violated:**
- Adding `SayChatMessage` to the exemption list: mounted player speaks while still on chocobo; some NPC scripts ignore the input; quest stalls.
- Removing the navigator `Stop()`: player walks past the NPC mid-speech.
- Adding `TryCutsceneSkipConfirm()`: probably no-op in practice (cutscene + say is not a real scenario) but adds a call site that future readers wonder about.

### Decision SCD-7 — Lazy-dismount: NO exemption (mirrors SC6)

Restating SC6 with the Slice 3 concrete touchpoint:

The hook at `EngineHost.cs:286`:
```csharp
if (_lastDispatchedActionWasNavigate && action is not EngineAction.Navigate and not EngineAction.Teleport)
```

routes any non-Navigate, non-Teleport action through the dismount-before-dispatch path. `EngineAction.SayChatMessage` falls into the dismount-eligible branch automatically because it is neither `Navigate` nor `Teleport`. **The builder must not add `and not EngineAction.SayChatMessage` to this condition.**

**Verification via tests (already shipped in Slice 2):** SC6 (`SayChatMessageStepTests.SayChat_AfterNavigate_Mounted_Dismounts`) and SC7 (`SayChatMessageStepTests.SayChat_Standalone_Mounted_DoesNotDismount`) pin the engine-side mount-state behavior. The harness uses `_lastDispatchedWasNavigate` tracking that mirrors `EngineHost`'s; passing SC6/SC7 in Slice 2 is the strong indicator that the equivalent host-side wiring will behave correctly.

**What breaks if violated:** mounted-after-navigate speaks-from-chocobo; the NPC's chat-recognition trigger may not fire (script-dependent); quest stalls; user files an issue that looks like "say doesn't work."

### Decision SCD-8 — EngineHost wiring: field, ctor, BeginRun, optional debug accessor

**Field declaration** (after `_emoteExecutor` at `EngineHost.cs:47`):
```csharp
private readonly DalamudChatSender _chatSender;
```

**Constructor body** (after `_emoteExecutor = new DalamudEmoteExecutor(services);` at `:113`):
```csharp
_chatSender = new DalamudChatSender(services);
```

(Whitespace-aligned with the surrounding ctor assignments.)

**`BeginRun` engine ctor** — modify lines 213-219 from:
```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor,
    emoteExecutor: _emoteExecutor);
```

to:
```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor,
    emoteExecutor: _emoteExecutor,
    chatSender: _chatSender);
```

`chatSender:` is the last positional kwarg, matching the `QuestEngine` ctor signature (verified via grep at `QuestEngine.cs:87` — `IChatSender? chatSender = null` is the final optional parameter).

After this wiring, `ResolveSayChatMessage` (`QuestEngine.cs:899-910`) no longer returns the `AwaitUser("…no IChatSender wired…")` fallback when `EngineHost` is the host.

**Optional debug accessor** (after `IEmoteExecutor DebugEmoteExecutor => _emoteExecutor;` at `:141`):
```csharp
public IChatSender DebugChatSender => _chatSender;
```

Mirrors the existing `DebugCombat`, `DebugVendor`, `DebugMount`, `DebugEmoteExecutor` accessors. Useful for a future `/qf debug say <message> [<targetNpcId>]` subcommand. Builder may include or defer at discretion — the one-line cost is low and the future debug-subcommand surface is otherwise harder to reach.

**Why construction once-per-host, not once-per-run:** `DalamudChatSender` is stateless (no Lumina preload, no caches). Per-run construction is technically cheap but inconsistent with every other adapter in `EngineHost`. Once-per-host wins on consistency.

**Why no `RecordingChatSender` proxy in v1:** mirrors DED-6 and CLAUDE.md's slice 3 guidance ("write-only adapters do not need a `RecordingXxxExecutor` wrapper"). The engine already emits a `DecisionEvent` with `ActionType == "SayChatMessage"` via the harness `EmitActionSubmitted("SayChatMessage", ...)` arm and the real engine's equivalent trace path. The dispatch is visible in the trace via the engine-side decision event before the adapter is called. Adding a recording-proxy wrapper would be redundant.

### Decision SCD-9 — Tooling catch-up (paired questforge-tools branch)

Per CLAUDE.md Slice 3, tooling catch-up MUST land paired with the Dalamud impl. Push both PRs before either merges. The four touchpoints:

#### SCD-9a — `CapabilityInferrer.StepCapabilities`: already present (no edit)

`QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs:26` already has:
```csharp
[typeof(SayChatMessageStep)] = "step:say-chat-message",
```

Verified by direct read. No edit needed; no `CapabilityInferrerTests` addition needed (the entry is exercised transitively by any test that constructs a fixture containing a `SayChatMessageStep`, and the dictionary-membership behavior is uniform across all entries).

#### SCD-9b — `TraceToFixtureExtractor.FilenameLookup`: append new exact-shape row

Insert into the array literal at `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs:18-38`. Place after the existing UseEmote entry (line 35) to match the order of related steps:

```csharp
(["step:say-chat-message", "step:talk", "step:travel"], "with-say-chat-message.json"),
```

**Sort-key invariant:** the existing `SuggestFilename` flow at line 224 uses `OrderBy(c => c)` on capabilities — alphabetical. The 3-element tuple above must be in alphabetical order for the `SequenceEqual` match (line 232) to succeed. Verified: `step:say-chat-message` < `step:talk` < `step:travel` ordinal-comparing. The tuple order matches.

#### SCD-9c — `TraceToFixtureExtractor.DistinguishingCapPriority`: append new fallback row

Insert into the array literal at `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs:43-62`. Place after the existing `("step:use-emote", "with-use-emote.json")` entry (line 59) and before `("step:teleport", ...)` (line 60):

```csharp
("step:say-chat-message", "with-say-chat-message.json"),
```

**Priority rationale:** the say-step is more "shape-defining" within a multi-shape quest than teleport or purchase-item (both of which are infrastructure-y), but less than use-action (combat actions dominate shape identity per the existing comment at line 56) and less than use-emote (the comment-pinned ordering of "action precedes emote"). Placing say between emote and teleport matches the brief's hint ("speech-like, below use-emote; use-action ranks higher per existing ordering") and matches my reading of the existing comments.

**Why below emote:** emotes are physically expressive game animations that strongly distinguish a fixture shape ("this quest exercises the emote pathway"). Say-chat-message is text-input; its trace shape is a single submission event with no animation correlate. Slightly less distinguishing.

**Why above teleport:** teleport is an infrastructure step (player got somewhere); say-chat-message is a content-interaction step (player did a thing at an NPC). Content interactions distinguish quest shape more than transport steps.

#### SCD-9d — `TraceConstants.ActionSayChatMessage`

Append to `QuestForge.Tools.Trace/TraceConstants.cs:10-22`, placed alphabetically-ish after `ActionUseEmote` (line 21):
```csharp
internal const string ActionSayChatMessage = "saychatmessage";  // lowercased from "SayChatMessage"
```

**Verification of the lowercased value:** `EngineAction.SayChatMessage.GetType().Name` is `"SayChatMessage"` (record-class name verbatim, no nested-type-prefix because `EngineAction` is the declaring type and `record.GetType().Name` returns just the class portion). `.ToLowerInvariant()` is `"saychatmessage"`. Matches the convention documented in the file header (lines 6-9).

**Comment-style match:** the surrounding constants are mostly bare strings; only `ActionAwaitUser` (line 14) and `ActionAttune` (line 15) have inline-comment annotations. Adding the `// lowercased from "SayChatMessage"` annotation keeps the future reader from wondering why the case mapping looks weird. Builder may omit if the surrounding style is more austere; not load-bearing.

**Why no behavior change:** `IsTerminalAction` (line 24) only checks `done`/`awaituser`. The new constant documents what the trace emits without changing dispatch.

#### SCD-9e — Test: `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage`

Append to `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs` immediately after the existing `SuggestFilename_WithUseEmote_ReturnsWithUseEmote` (line 615). Mirror the structure exactly:

```csharp
[Fact]
public void SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage()
{
    var extractor = new TraceToFixtureExtractor();
    var fixture = new FixtureModel(
        SchemaVersion: "1.0.0",
        Description: "TODO",
        InitialState: "fresh",
        Capabilities: ["step:say-chat-message", "step:talk", "step:travel"],
        QuestFile: "quests/arr/sid/12346-say-the-magic-word.json",
        ExpectedTransitions: [],
        TerminalOutcome: "done");

    Assert.Equal("with-say-chat-message.json", extractor.SuggestFilename(fixture));
}
```

**Capabilities order:** alphabetical (`step:say-chat-message` < `step:talk` < `step:travel`), matching `SuggestFilename`'s `OrderBy(c => c)` sort.

**No `CapabilityInferrerTests` addition:** SCD-9a — the entry is already present and tested transitively. The `CapabilityInferrer` test surface tests the dictionary as a whole (every test that constructs a quest with mixed steps exercises the lookup), and there is no per-entry assertion pattern that would warrant a per-step test. If `CapabilityInferrerTests` follows a per-entry pattern (some test classes do), a sibling test pinning `step:say-chat-message` may be added; default is to skip.

### Decision SCD-10 — Test scenarios on the questforge side: zero new tests

Per Decision SCD-1, the Dalamud shell has no new pure-logic seams and the engine-side dispatch (SC1–SC9) is already covered. The shell is validated by in-game smoke (§G).

**What's covered without new tests:**
- Engine emits `EngineAction.SayChatMessage(Message, TargetNpcId, Origin)` — SC1, SC2, SC8, SC9 already shipped.
- Engine guards on casting — SC3 already shipped.
- Stateless retry on adapter failure — SC4 already shipped.
- Lazy-dismount routing — SC6, SC7 already shipped.
- Validator E11/E12/W9/W1-suppression — SC12/SC13/SC14 already shipped.
- Round-trip serialization — SC10/SC11 already shipped.

**What's NOT covered by automated tests** (and would require a `QuestForge.Adapters.Dalamud.Tests` project that does not exist):
- `DalamudChatSender.Send` returns `Result.Fail("targetNotFound", …)` when targetNpcId is supplied but no matching ObjectTable entry exists.
- `DalamudChatSender.Send` returns `Result.Fail("chatSendFailed", …)` when `Chat.SendMessage` throws.
- `DalamudChatSender.Send` writes `_svc.TargetManager.Target` before the chat submission.
- `DalamudChatSender.Send` skips the target scan when `targetNpcId is null`.

Recorded here so a future tooling investment can pick them up. NOT in this slice's done criteria.

---

## File layout (summary)

### questforge repo

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs` | NEW | The `IChatSender` shell — ObjectTable scan + `Chat.SendMessage` |
| `QuestForge.Plugin/EngineHost.cs` | MODIFY | Field declaration; ctor construct; `BeginRun` arg; `DispatchAction` arm; (optional) Debug accessor |

### questforge-tools repo (paired PR)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs` | MODIFY | Append `FilenameLookup` row + `DistinguishingCapPriority` row |
| `QuestForge.Tools.Trace/TraceConstants.cs` | MODIFY | Append `ActionSayChatMessage` constant |
| `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs` | MODIFY | Add `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage` |
| `QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs` | (no change) | Entry already present at line 26 |

### questforge docs

| File | Status | Purpose |
|---|---|---|
| `docs/FIXTURES.md` | (no change if already present) | Verify `step:say-chat-message` capability row exists (Slice 2 plan claims it does at line 116); add `"saychatmessage"` to the `actionType` canonical strings table |

---

## A. Pure-logic seams (per the brief's §A question)

**There is zero new pure-logic surface in this slice.**

Decision SCD-1 walks through each candidate the brief flagged:
- **"Format the command string from (message)"** — `"/say " + message`, one expression, no testable failure mode. Slice 2 Decision SC8 already settled this.
- **"Map ECommons exceptions to `Result.Fail`"** — `DalamudEmoteExecutor` does not extract; consistency wins. The helper would be one line of body and have exactly one tautological test.

The slice's verification surface is:
- The Slice 2 engine tests (SC1–SC9, already green).
- The Slice 2 validator tests (SC12–SC14, already green).
- The Slice 2 round-trip tests (SC10–SC11, already green).
- The new questforge-tools test (SCD-T1 below).
- In-game smoke per §G.

---

## B. `DalamudChatSender` shell — full source

```csharp
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
// Aliased: Slice 2 introduced QuestForge.Adapters.Chat (for IChatSender), which shadows
// the bare `Chat` type via namespace-vs-type lookup precedence. Without the alias, the
// `Chat.SendMessage(...)` call below resolves to the new sibling namespace and breaks
// the build. Mirrors the same fix in DalamudEmoteExecutor.cs.
using ECChat = ECommons.Automation.Chat;
using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Chat;

/// <summary>
/// Submits a literal /say chat message via ECommons.Automation.Chat.SendMessage.
/// Optionally writes TargetManager.Target before submitting so the player faces the NPC
/// while speaking.
///
/// Success means "Chat.SendMessage returned without throwing." It does NOT mean
/// "the NPC reacted to it" or "the quest script advanced." The engine verifies outcome
/// via the authored Expect predicate (per Slice 2 Decision SC5).
///
/// Failure cases:
///   - targetNotFound  — targetNpcId supplied but no matching object in ObjectTable
///   - chatSendFailed  — Chat.SendMessage threw (empty / too long / invalid chars / signature lost)
/// </summary>
public sealed class DalamudChatSender : IChatSender
{
    private readonly PluginServices _svc;

    public DalamudChatSender(PluginServices svc) => _svc = svc;

    public Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Acquire target if requested. Filter mirrors DalamudEmoteExecutor / DalamudActionExecutor /
        //    DalamudInteractor. EventNpc and BattleNpc cover the spoken-to NPC types; Aetheryte and
        //    EventObj are excluded (you don't /say at an aetheryte).
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
                    Result.Fail<Unit>("targetNotFound", $"no object in scene with BaseId {id.Value}"));
            _svc.TargetManager.Target = found;
        }

        // 2. Build the command string. Slice 2 Decision SC2 / SC8: hard-wired /say, no resolver.
        var command = "/say " + message;

        // 3. Submit. ECommons docs (Chat.cs:65-67) document ArgumentException (empty /
        //    too long / invalid chars) and InvalidOperationException (signature not found).
        //    Catch broadly so adapter-shell exceptions surface as Result.Fail rather than
        //    crashing the dispatch loop.
        try
        {
            ECChat.SendMessage(command);
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<Unit>>(
                Result.Fail<Unit>("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
        }

        return Task.FromResult<Result<Unit>>(Result.Ok(Unit.Value));
    }
}
```

**Notes for Builder:**
- The `Result.Fail<Unit>(...)` vs `Result.Fail(...)` form — Builder should match whichever generic-vs-inference idiom `DalamudEmoteExecutor.cs:55,71,86` uses (currently the non-generic `Result.Fail(...)` form inside `Task.FromResult<Result<Unit>>(...)`, which works because the outer Task type fixes the inference). Either form compiles; consistency with the sibling shell is the tiebreaker. (Source-of-truth check: re-read `DalamudEmoteExecutor.cs` at build time and mirror exactly.)
- The `using QuestForge.Adapters.Chat;` import brings `IChatSender` into scope. Combined with the `ECChat` alias, the bare `Chat.X` namespace prefix is never referenced inside the shell body — clean.
- `Result.Ok(Unit.Value)` returns the Slice-2 documented success shape; if the codebase exposes a bare `Result.Ok()` (per `DalamudEmoteExecutor.cs:89`), prefer that form for consistency.
- No `using` for `Lumina.Excel.Sheets` — unlike `DalamudEmoteExecutor`, this shell has no Lumina dependency.

---

## C. EngineHost wiring — concrete edits

### C.1 Field declaration

In `EngineHost.cs:47` (after the existing `_emoteExecutor` line), add:

```csharp
private readonly DalamudChatSender _chatSender;
```

### C.2 Constructor body

In `EngineHost.cs:113` (after `_emoteExecutor = new DalamudEmoteExecutor(services);`), add:

```csharp
_chatSender      = new DalamudChatSender(services);
```

(Whitespace-aligned with the other ctor assignments in the same block.)

### C.3 `BeginRun` engine construction

Modify `EngineHost.cs:213-219` per Decision SCD-8. The named-arg `chatSender:` slots in as the final positional kwarg after `emoteExecutor:`.

### C.4 `DispatchAction` switch arm

Insert the arm from Decision SCD-6 between the existing `case EngineAction.UseEmote ue:` (ends at line 474) and `case EngineAction.Wait:` (starts at line 476):

```csharp
case EngineAction.SayChatMessage sc:
    DebounceLog(
        $"saychat:{sc.Message.Length}:{sc.TargetNpcId?.Value}",
        $"[SayChatMessage] message=\"{sc.Message}\" target={sc.TargetNpcId?.Value.ToString() ?? "broadcast"}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    await _chatSender.Send(sc.Message, sc.TargetNpcId, ct);
    break;
```

**Verification checklist** (builder must self-verify before pushing):
- [ ] The arm is BEFORE `case EngineAction.Wait:` so `Wait` is not preempted.
- [ ] The arm is AFTER `case EngineAction.UseEmote ue:` to match `EngineAction` enum declaration order.
- [ ] No `TryCutsceneSkipConfirm()` call (per Decision SCD-6).
- [ ] No `and not EngineAction.SayChatMessage` added to the dismount-exemption condition at line 286 (per Decision SCD-7).

### C.5 (Optional) Debug accessor

Per Decision SCD-8, after the existing `IEmoteExecutor DebugEmoteExecutor => _emoteExecutor;` at `:141`:

```csharp
public IChatSender DebugChatSender => _chatSender;
```

Builder may include or defer. The cost is one line.

### C.6 Import — add to using block

In the `using` block at the top of `EngineHost.cs`, add (alphabetical position after `QuestForge.Adapters.Dalamud.Actions`):

```csharp
using QuestForge.Adapters.Dalamud.Chat;
```

If the `IChatSender` interface is referenced via the optional debug accessor (C.5), also add:

```csharp
using QuestForge.Adapters.Chat;
```

---

## D. questforge-tools edits — concrete

### D.1 `FilenameLookup` row

Append to the array at `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs:18-38`, immediately after the existing `with-use-emote.json` line (around line 35 — exact line depends on intervening edits):

```csharp
(["step:say-chat-message", "step:talk", "step:travel"], "with-say-chat-message.json"),
```

### D.2 `DistinguishingCapPriority` row

Append to the array at lines 43-62, after the existing `("step:use-emote", "with-use-emote.json")` entry and before `("step:teleport", ...)`:

```csharp
("step:say-chat-message", "with-say-chat-message.json"),
```

### D.3 `TraceConstants.ActionSayChatMessage`

Append to `QuestForge.Tools.Trace/TraceConstants.cs` after the existing `ActionUseEmote` line (line 21):

```csharp
internal const string ActionSayChatMessage = "saychatmessage";  // lowercased from "SayChatMessage"
```

### D.4 Test: `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage`

Append to `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs` after the existing `SuggestFilename_WithUseEmote_ReturnsWithUseEmote` test (around line 615):

```csharp
[Fact]
public void SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage()
{
    var extractor = new TraceToFixtureExtractor();
    var fixture = new FixtureModel(
        SchemaVersion: "1.0.0",
        Description: "TODO",
        InitialState: "fresh",
        Capabilities: ["step:say-chat-message", "step:talk", "step:travel"],
        QuestFile: "quests/arr/sid/12346-say-the-magic-word.json",
        ExpectedTransitions: [],
        TerminalOutcome: "done");

    Assert.Equal("with-say-chat-message.json", extractor.SuggestFilename(fixture));
}
```

---

## E. Test scenarios (for the questforge-tools test)

Only one new test ships in this slice: SCD-T1.

### SCD-T1 — `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage`

**Given:** A `FixtureModel` with `Capabilities = ["step:say-chat-message", "step:talk", "step:travel"]` (alphabetically sorted, matching `SuggestFilename`'s `OrderBy(c => c)` invariant).

**When:** `extractor.SuggestFilename(fixture)`.

**Then:** Returns `"with-say-chat-message.json"`.

**What this pins:** the exact-match path in `SuggestFilename` (line 230 `foreach … if (stepCaps.SequenceEqual(caps))`) finds the new row from D.1.

**Why no fallback-priority test:** the fallback path (`DistinguishingCapPriority` from D.2) is exercised when no exact match exists. A test would need to construct a fixture with at least four step capabilities (so no exact entry matches) and verify the highest-priority one wins. The existing UseEmote / UseAction catch-up did not add such a test for the same reason: the fallback logic is generic and the per-entry priority is documentation, not behavior the test needs to pin per entry. Skip.

**Test file location:** `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs`.

---

## F. Implementation order

**Phase A — Dalamud shell, questforge (15 min)**
1. Create `QuestForge.Adapters.Dalamud/Chat/` directory.
2. Create `DalamudChatSender.cs` per §B.
3. Verify `dotnet build QuestForge.Adapters.Dalamud` succeeds (ECommons already referenced for the emote shell).

**Phase B — EngineHost wiring, questforge (10 min)**
1. Add field declaration per §C.1.
2. Add ctor construction per §C.2.
3. Add `using QuestForge.Adapters.Dalamud.Chat;` (and optionally `using QuestForge.Adapters.Chat;`) per §C.6.
4. Modify `BeginRun` engine construction per §C.3.
5. Add `DispatchAction` arm per §C.4.
6. (Optional) Add debug accessor per §C.5.
7. Run the verification checklist in §C.4.
8. Verify `dotnet build` succeeds with no warnings-as-errors regression.

**Phase C — Tooling catch-up, questforge-tools (15 min)**
1. Verify `CapabilityInferrer.StepCapabilities` entry is present per §SCD-9a (read the file — no edit expected).
2. Append `FilenameLookup` row per §D.1.
3. Append `DistinguishingCapPriority` row per §D.2.
4. Append `TraceConstants.ActionSayChatMessage` per §D.3.
5. Add the test SCD-T1 per §D.4.
6. Run `dotnet test QuestForge.Tools.Trace.Tests --filter SuggestFilename_WithSayChatMessage` — must be green.
7. Run full `dotnet test` to verify no regression.

**Phase D — Cross-repo PR coordination (5 min)**
1. Push questforge branch as a draft PR.
2. Push questforge-tools branch as a draft PR.
3. Mark both ready for review with cross-references in the descriptions ("paired Slice 3 catch-up — do not merge separately").
4. Per CLAUDE.md: tooling catch-up MUST land in the same slice as Dalamud impl. Merge order should be tools-first (the fixtures CI is data-side; merging questforge first leaves the data-side CI temporarily out-of-sync with what shipped a new capability — minor but avoidable).

**Phase E — In-game smoke (20-30 min, manual; see §G)**

**Phase F — Documentation refresh (5 min)**
1. `docs/FIXTURES.md` — verify `step:say-chat-message` row exists in the capabilities table (Slice 2 plan claims it does at line 116; verify).
2. `docs/FIXTURES.md` — add `"saychatmessage"` to the `actionType` canonical strings table if not present.

Total dev time: ~50 min code + ~30 min smoke ≈ 1.5 hours.

---

## G. In-game smoke test plan

**Pre-requisites:**
- Plugin builds and loads on a character.
- Both PRs (questforge + questforge-tools) merged or pushed-pending-review.
- No existing real quest in the corpus uses `SayChatMessageStep` — use the local-fixture approach in step 1.

**Smoke steps:**

1. **Author a local broadcast (no target) say step.** Create a test quest JSON at:
   ```
   %APPDATA%\XIVLauncher\pluginConfigs\QuestForge\quests\99996.json
   ```
   with one step. Use a quest id that is **not** in the official corpus (99996 reserved here; any 999xx works):

   ```json
   {
     "id": 99996,
     "name": "smoke-saychat-broadcast",
     "category": "test",
     "sequences": [
       {
         "sequence": 0,
         "steps": [
           {
             "type": "say-chat-message",
             "id": "smoke-broadcast",
             "message": "QuestForge says hi",
             "expect": "playerZone() == <currentZoneId>"
           }
         ]
       }
     ]
   }
   ```

   Use an `Expect` predicate that is trivially true (e.g. `playerZone() == 129` if you're standing in Limsa) so the step short-circuits after one dispatch. This isolates the "did the chat fire?" verification from "did the NPC react?"

2. **Run with no target (broadcast).** `/qf debug run 99996`. Observe:
   - The chat window shows `"<PlayerName> says: QuestForge says hi"` in the Say channel.
   - Dalamud's `/xllog` shows one `[SayChatMessage] message="QuestForge says hi" target=broadcast` line per ~10s (debounce window).
   - The quest completes (`Done` outcome) within one or two ticks.

3. **Run with NPC target.** Find a nearby NPC with a known BaseId (use `/qf debug game` or hover over the NPC to inspect). Edit the quest JSON to add the target:

   ```json
   {
     "type": "say-chat-message",
     "id": "smoke-targeted",
     "message": "Hello there",
     "targetNpcId": <baseId>,
     "expect": "playerZone() == <currentZoneId>"
   }
   ```

   Re-run. Observe:
   - The NPC is acquired as the player's target (target slot updates before the chat fires).
   - The chat shows `"<PlayerName> says: Hello there"`.
   - Log line: `[SayChatMessage] message="Hello there" target=<baseId>`.
   - The quest completes.

4. **Smoke-fail mode: NPC not in scene.** Edit the JSON with `"targetNpcId": 9999999` (a BaseId no NPC in the current zone has). Re-run. Observe:
   - No target acquisition.
   - No chat broadcast.
   - Engine logs `Result.Fail("targetNotFound", "no object in scene with BaseId 9999999")` (look at the `dalamud.log`).
   - The engine's stateless retry repeats every tick. Eventually trips `MaxConsecutiveStepFailures` and emits `AwaitUser` — confirms the failure path surfaces cleanly without crashing the plugin.

5. **Lazy-dismount smoke (optional but valuable).** Mount up (chocobo). Author a two-step quest: a TravelStep (any nearby destination) followed by a SayChatMessageStep with `targetNpcId` set to a nearby NPC. Run. Observe:
   - Travel completes; player is still mounted.
   - Before the chat fires, the player dismounts (per Decision SCD-7).
   - Chat fires while player is standing.

**Pass criteria:** steps 2-3 fire the chat correctly; step 4 fails cleanly without plugin crash; step 5 dismounts before speaking. If any step deviates (plugin crashes, chat doesn't fire, target acquisition fails on a valid NPC, dismount doesn't happen before chat), file a bug before merging.

**Estimated time:** 20-30 minutes including authoring + 4 quest restarts.

---

## H. Done criteria

1. `dotnet build` succeeds with no `TreatWarningsAsErrors` regression in `QuestForge.Adapters.Dalamud` or `QuestForge.Plugin` (questforge repo).
2. `QuestForge.Adapters.Dalamud/Chat/DalamudChatSender.cs` exists and implements `IChatSender` per §B.
3. `EngineHost.cs:213-219` constructs `QuestEngine` with `chatSender: _chatSender` (no longer absent).
4. The `case EngineAction.SayChatMessage sc:` arm exists in `EngineHost.DispatchAction`, positioned between `UseEmote` and `Wait`.
5. The condition at `EngineHost.cs:286` is unchanged (SayChatMessage is NOT in the dismount-exemption list).
6. Running the smoke quest from §G.2 causes the chat command to fire in-game (chat window shows the message in the Say channel). Manual verification.
7. The engine's `ResolveSayChatMessage` (`QuestEngine.cs:899-903`) no longer returns the `AwaitUser("…no IChatSender wired…")` fallback when `EngineHost` is the host.
8. §G.4 (unknown targetNpcId) surfaces `targetNotFound` cleanly without plugin crash.
9. `dotnet build` succeeds with no warnings-as-errors regression in `QuestForge.Tools.Trace` (questforge-tools repo).
10. `dotnet test QuestForge.Tools.Trace.Tests` reports `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage` green.
11. No regression in any existing test across either repo (`dotnet test` clean in both).
12. Both questforge and questforge-tools PRs reference each other in their descriptions; tools-first merge order observed.

---

## I. Exclusions (what this plan does NOT include)

- **`RecordingChatSender`** (recording proxy). Deferred per Decision SCD-8 / DED-6. The engine emits decision events for `EngineAction.SayChatMessage` via the existing trace machinery; the dispatch is visible without an adapter-level wrap.
- **Direct unit tests for `DalamudChatSender`.** Dalamud-bound; validated by smoke. If/when a `QuestForge.Adapters.Dalamud.Tests` project is set up, the four tests sketched in §SCD-10 are ready to copy in.
- **`/qf debug say <message> [<targetId>]` subcommand.** Optional `DebugChatSender` accessor in §C.5 makes it a one-liner to add later.
- **Multi-channel support** (`/yell`, `/shout`, `/party`, `/fc`). Slice 2 Decision SC2 — `/say` only. If a future quest needs `/yell`, add a new step type `YellChatMessageStep` rather than reviving the `channel` field.
- **Localization beyond literal English strings.** Slice 2 Decision SC3 documented limitation.
- **`TryCutsceneSkipConfirm()` in the dispatch arm.** Decision SCD-6 omits it. Add only if a future smoke surfaces a real cutscene-and-say scenario.
- **Authoring inference for SayChatMessageStep.** Slice 5 — Slice 2 Decision SC13 calls out the signal-research dependency (RaptureLogModule is the lead candidate; AgentChatLog needs research; outbound chat hook would require explicit user OK per CLAUDE memory).
- **Updating `docs/FIXTURES.md` capabilities row.** Slice 2 plan claims it already exists at line 116; verify in §F Phase F.6 — if missing, add (does not require code changes elsewhere).
- **`CapabilityInferrerTests` per-step assertion.** Decision SCD-9a — the entry is already present and tested transitively. Add only if `CapabilityInferrerTests` follows a per-entry pattern.

---

## J. Open questions / decisions to call out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| Any new pure-logic seam? | **No** | Slice 2 Decision SC8 pinned this; the candidates the brief raised (concat / exception map) are one-liners with tautological tests. | SCD-1 |
| Mirror `DalamudEmoteExecutor` structure? | **Yes, minus Lumina preload + minus pure resolver** | The remaining shape is structurally identical; preserving parallel shape aids the next reviewer. | SCD-2 |
| Lumina lookup for the message? | **No** | Slice 2 Decision SC3 — literal English strings only. | SCD-3 |
| Namespace shadow workaround? | **`using ECChat = ECommons.Automation.Chat;` alias** | Same hazard as the emote shell (handled at `DalamudEmoteExecutor.cs:7`); one-line fix; no refactor of `IChatSender` namespace. | SCD-4 |
| `Chat.SendMessage` exception handling? | **Try/catch all → `Result.Fail("chatSendFailed", …)`** | Same posture as DED-4; recovery identical for all exception types; future-proof against new ECommons types. | SCD-5 |
| Dispatch arm: include `TryCutsceneSkipConfirm`? | **No** | No real cutscene-and-say scenario; keeps the arm thin. | SCD-6 |
| Dispatch arm: debounce key includes full message text? | **No — key on message length** | Message text can contain weird chars (newlines, colons) that make the key unreadable in debug output; length is a safe proxy with negligible collision risk. | SCD-6 |
| Stop navigation first in dispatch arm? | **Yes** (cheap `IsNavigating` guard) | Mirrors UseEmote / UseAction / Interact / Purchase. | SCD-6 |
| Add SayChatMessage to dismount exemption list? | **No** | Slice 2 Decision SC6 pinned this; NPC scripts may check pose; standing-while-speaking is the cleanest pose. The existing dismount condition at line 286 already routes SayChatMessage correctly without modification. | SCD-7 |
| Optional `DebugChatSender` accessor? | **Optional one-liner** | Mirrors other Debug accessors; opens future `/qf debug say` subcommand path. | SCD-8 |
| `RecordingChatSender` wrapper? | **No in v1** | Write-only adapter; engine decision event already covers trace need. | SCD-8 |
| `CapabilityInferrer.StepCapabilities` entry? | **Already present (line 26) — no edit** | Verified by grep; the test surface is transitive. | SCD-9a |
| `FilenameLookup` placement? | **Append after `with-use-emote.json`** | Tuple is alphabetically sorted; matches `SuggestFilename` invariant. | SCD-9b |
| `DistinguishingCapPriority` placement? | **Between emote and teleport** | Content-interaction (more shape-defining than infrastructure steps) but text-only (less shape-defining than emote animations). | SCD-9c |
| `TraceConstants` constant name? | **`ActionSayChatMessage = "saychatmessage"`** | `EngineAction.SayChatMessage.GetType().Name.ToLowerInvariant()` confirms the lowercased form; no behavior change (only `done`/`awaituser` are terminal). | SCD-9d |
| New tests on questforge side? | **No** | Dalamud-bound; smoke covers. Engine-side already covered by SC1–SC9. | SCD-10 |
| New tests on questforge-tools side? | **One** — `SuggestFilename_WithSayChatMessage_ReturnsWithSayChatMessage` | Pins the exact-match path through the new `FilenameLookup` row. | SCD-9e |

---

✅ READY FOR TEST CREATION

Tester: Write one failing test in the questforge-tools repo per the GWT spec in §E.
- Happy paths: 1 scenario (SCD-T1)
- Edge cases: 0 scenarios
- Error cases: 0 scenarios
- Expected total: 1 new test in `QuestForge.Tools.Trace.Tests/TraceToFixtureExtractorTests.cs`.

On the questforge side: 0 new tests this slice. Engine-side dispatch is already covered by SC1–SC9 (Slice 2, shipped). Validator coverage is already in SC12–SC14 (Slice 2, shipped). Round-trip coverage is already in SC10–SC11 (Slice 2, shipped). The Dalamud shell (`DalamudChatSender`) is validated by in-game smoke per §G.
