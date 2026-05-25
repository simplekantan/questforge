# Quest Variables (Work Bytes V0–V5) Display Implementation Plan (Issue #40)

**Status:** ready for test creation
**Input docs:** GitHub issue #40, `QuestForge.Adapters/State/IQuestState.cs`, `QuestForge.Engine/Authoring/{GameStateSnapshot,SnapshotAggregator,IAuthoringObserver}.cs`, `QuestForge.Plugin/UI/Authoring/QuestStatePanel.cs`, `QuestForge.Plugin/Commands/QfCommand.cs`, `QuestForge.Plugin.Tracing/{UIObserver,IGameProbe}.cs`, FFXIVClientStructs `QuestWork.cs` / `QuestManager.cs`.
**Output:** the authoring **Quest State panel** gains a 6-column table showing the active quest's work-byte variables V0–V5 (Author mode only), and `/qf debug quest <id>` gains a line block showing the same six bytes for any accepted quest. No CI-blocking behavior changes (this is plugin-side UI + an engine-side aggregator field); the verifiable changes are the new unit tests on the engine surface plus build-verification of the Dalamud layer.
**Depends on:** nothing. Additive across the existing `IQuestState` / `GameStateSnapshot` / `SnapshotAggregator` / `IGameProbe` surfaces.

---

## 1. Problem summary

### 1.1 Current-state correction (the issue's framing is wrong)

Issue #40 is framed as *"reformat an existing single-line V0–V5 display"* in the Quest State panel. **That display does not exist.** Verified:

- `QuestStatePanel.Draw()` (`QuestForge.Plugin/UI/Authoring/QuestStatePanel.cs:43-47`) renders exactly five lines: Active Quest, Quest Sequence, Quest Flags (`0x{...:X8}`), Accepted, Completed. There is no work-byte / variable line.
- `GameStateSnapshot` (`QuestForge.Engine/Authoring/GameStateSnapshot.cs`) has **no** work-byte field. Its quest fields are `ActiveQuest`, `QuestSequence`, `QuestFlags`, `QuestAccepted`, `QuestCompleted`.
- `SnapshotAggregator` (`QuestForge.Engine/Authoring/SnapshotAggregator.cs`) has `_questSequence` and `_questFlags` but no variables field, and no `OnQuestVariables*` method.
- `IGameProbe.GetNormalQuests()` (`QuestForge.Plugin.Tracing/IGameProbe.cs:10`) returns `(ushort QuestId, byte Seq, byte Flags)` tuples — **no variables**.
- `IQuestState` has `GetQuestFlags` but **no** `GetQuestVariables`.

So this issue is really: **read the six work bytes, plumb them through a new `IQuestState.GetQuestVariables`, store them on the snapshot via the aggregator, render them as a 6-column table, and add a line block to `/qf debug quest`.** The work is greenfield, not a reformat.

### 1.2 Vocabulary

- **Variables / work bytes** — the six `byte` values `V0`–`V5` that the game stores per accepted quest in `QuestWork.Variables[0..5]`. Quest scripts use them as scratch state (counters, sub-step progress).
- **Nibbles** — each byte splits into a high nibble `H = b >> 4` and a low nibble `L = b & 0x0F`.
- **Active quest** — `GameStateSnapshot.ActiveQuest`, set only in Author mode (see §2.1).

---

## 2. Fixed design decisions (from the user — do not relitigate)

These were confirmed before planning:

1. **Panel scope: Author mode only.** Variables show in the Quest State panel only while recording a quest (when `snapshot.ActiveQuest` is set). **NOT** Inspect mode. Rationale: `AuthoringHost` builds the `SnapshotAggregator` with the target quest in Author mode (`AuthoringHost.cs:128` — `new SnapshotAggregator(target, ...)`) but with `null` in the constructor/Inspect path (`AuthoringHost.cs:90` — `new SnapshotAggregator(null, ...)`). In Inspect mode `ActiveQuest` is null, so there is no quest whose variables to read. We accept that limitation; no Inspect support.

2. **Panel layout: Option B — a 6-column `ImGui.BeginTable`** (one column per variable V0–V5). Non-zero bytes render with full detail: value plus high/low nibbles (e.g. `0x10 H:1 L:0`). Zero bytes render compactly (just `0`). Readability over compactness. The panel's `MaximumSize` height may need to grow.

3. **Also surface variables in `/qf debug quest <id>`** (`QfCommand.HandleDebugQuest`). That command takes an explicit quest id, so it reads variables for any **accepted** quest regardless of authoring mode — add a line/block to its existing chat output.

4. **Reading is mode-agnostic.** No recording session is needed to read variables — only an accepted quest and its id. The Author-mode restriction is a *display* restriction on the panel, not a read restriction.

---

## 3. Confirmed technical facts

### 3.1 `QuestWork.Variables` shape (confirmed)

From `FFXIVClientStructs/FFXIV/Application/Network/WorkDefinitions/QuestWork.cs:15`:

```csharp
[FieldOffset(0x0C), FixedSizeArray] internal FixedSizeArray6<byte> _variables;
```

- The backing field is `FixedSizeArray6<byte>` — **exactly six bytes**, offset `0x0C`. The `[GenerateInterop]` source generator exposes a public `Variables` accessor returning a `Span<byte>` of length 6 (the standard ClientStructs `FixedSizeArray*` → `Span<T>` projection; the same pattern as `NormalQuests` which is iterated via `foreach (ref var slot in mgr->NormalQuests)` in `DalamudGameProbe.cs:27`).
- Access pattern (mirrors `DalamudQuestState.GetQuestFlags`, `DalamudQuestState.cs:62-77`):
  ```csharp
  var qm = QuestManager.Instance();        // null → adapter failure
  if (qm == null) return Result.Fail(...);
  var q = qm->GetQuestById(ToInternal(quest));   // ushort internal id
  if (q == null) return Result.Ok(allZero or empty);   // not accepted / completed
  var span = q->Variables;                 // Span<byte>, length 6
  // copy to byte[6]
  ```
- `GetQuestById` (`QuestManager.cs:181-188`) returns non-null **only for accepted/in-progress quests** (it scans `NormalQuests` for a matching `QuestId`). For completed or not-accepted quests it returns `null`. This exactly mirrors how `GetQuestFlags`/`IsQuestFlagSet` already treat a null `q` as the "no data" case (they return `Result.Ok(0u)` / `Result.Ok(false)`).
- Nibbles: `H = (byte)(b >> 4)`, `L = (byte)(b & 0x0F)`.

### 3.2 The aggregator feed path (confirmed)

`SnapshotAggregator`'s `_questSequence` / `_questFlags` are fed by `UIObserver.PollQuestState()` (`QuestForge.Plugin.Tracing/UIObserver.cs:199-254`), which runs on the 250 ms heartbeat. It iterates `_gameProbe.GetNormalQuests()` and forwards changes via `_aggregator?.OnQuestSequenceChanged(publicId, seq)` and `_aggregator?.OnQuestFlagsChanged(publicId, flags)` (lines 221-236). `UIObserver` holds only an `IGameProbe` (not an `IQuestState`), so the variables must reach the aggregator through `IGameProbe.GetNormalQuests()` — see §6.4. `OnQuestVariablesUpdated` follows the exact `if (_activeQuest == quest)` guard pattern that `OnQuestSequenceChanged` / `OnQuestFlagsChanged` already use (`SnapshotAggregator.cs:94-104`).

### 3.3 The full list of `IQuestState` implementers (confirmed — adding a member breaks all of these until implemented)

| # | Type | File | Role |
|---|---|---|---|
| 1 | `DalamudQuestState` | `QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs` | **The real read** — `QuestManager`/`GetQuestById`. |
| 2 | `FakeQuestState` | `QuestForge.Adapters.Fakes/State/FakeQuestState.cs` | In-memory fake; scriptable + records reads. **Unit-test surface.** |
| 3 | `RecordingQuestState` | `QuestForge.Adapters/Recording/RecordingQuestState.cs` | Recording proxy; wraps inner + emits `ObservationEvent`. |
| 4 | `ReplayQuestState` | `QuestForge.Adapters.Fakes/Replay/ReplayQuestState.cs` | Replay; reads from recorded observations via scanner. |
| 5 | `FailingQuestState` (test) | `QuestForge.Engine.Tests/Engine/AwaitUserTests.cs:161` | `file sealed` test double; all methods return `Result.Fail("adapter-broken")`. |
| 6 | `FailingQuestState` (test) | `QuestForge.Engine.Tests/Recording/RecordingQuestStateTests.cs:280` | `file sealed` test double, same shape. |

There is **also a reflection guard** that will fail until the proxy is updated: `RecordingProxyCoverageTests.RecordingQuestState_ImplementsAllIQuestStateMethods` (`QuestForge.Engine.Tests/Recording/RecordingProxyCoverageTests.cs:39-56`) asserts every public `IQuestState` method is *explicitly declared* on `RecordingQuestState`. Adding `GetQuestVariables` to the interface without implementing it on `RecordingQuestState` turns this test **red** — a free safety net that proves the proxy was updated.

`FileDraftStorage` does **not** implement `IQuestState`, but it does construct `GameStateSnapshot` (§6.3) — accounted for separately.

---

## 4. Chosen `GetQuestVariables` return type and rationale

```csharp
// in IQuestState
Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct);
```

**Decision:** return `Task<Result<IReadOnlyList<byte>>>`.

- **Consistency:** every other `IQuestState` member returns `Task<Result<T>>`; collections return `Task<Result<IReadOnlyList<T>>>` (`GetAcceptedQuests`, `GetAvailableQuestRewards`). `IReadOnlyList<byte>` matches that convention exactly.
- **"Not accepted / completed" → `Result.Ok` with an all-zero 6-element list, NOT `Result.Fail`.** Rationale: this mirrors the sibling read `GetQuestFlags`, which returns `Result.Ok(0u)` (not a failure) when `GetQuestById` is null (`DalamudQuestState.cs:72-73`). A not-accepted quest is a *routine* state (the `Result<T>` doctrine reserves `Fail` for adapter breakage, not for ordinary "no data"). Returning a fixed-length all-zero list keeps every consumer's indexing (`vars[0..5]`) total — no length guards, no null checks beyond the `Result` unwrap.
- **`Result.Fail` is reserved for genuine adapter breakage:** `QuestManager.Instance() == null` → `Result.Fail<IReadOnlyList<byte>>("questManagerNull", "QuestManager instance unavailable")` (byte-identical reason string to the existing `GetQuestFlags` failure).
- **Length is always 6 on success** (the `QuestWork.Variables` span is fixed at 6; the not-accepted fallback is also length-6 all-zero). Consumers may assert `vars.Count == 6`.

**What breaks if you pick `Result.Fail` for not-accepted:** every panel/command consumer would have to branch on failure-vs-not-accepted vs real-error, and `FakeQuestState` tests would need to script failures to represent the common "no variables" case — both noisier than an all-zero list. Picking a nullable/empty list instead of fixed-6 forces length guards at every read site.

---

## 5. Component change: `GameStateSnapshot`

`GameStateSnapshot` (`QuestForge.Engine/Authoring/GameStateSnapshot.cs`) is a positional `record` with a tail of **non-positional init-only fields** (`KeyItems`, `KeyItemsAdded`, … `SelectYesnoConfirmed`). Add the work-bytes field following that exact convention — do **not** add a positional parameter (that would churn every constructor call site).

```csharp
// Non-positional: does not affect existing constructor call sites.
// The six quest work bytes (V0–V5) of the ActiveQuest, captured in Author mode.
// Null when no variables have been observed (e.g. Inspect mode, or before the first
// heartbeat poll). Always length 6 when non-null.
public IReadOnlyList<byte>? QuestVariables { get; init; }
```

`SnapshotAggregator.Current` sets it inside the existing object initializer block (alongside `KeyItems = …`).

### 5.1 `FileDraftStorage` round-trip (confirmed safe)

`FileDraftStorage.ToSnapshotDto` / `FromSnapshotDto` (`FileDraftStorage.cs:244-284`) construct `GameStateSnapshot` via its **positional** constructor and persist only a fixed `SnapshotFileDto` field set. Because `QuestVariables` is a **non-positional init-only optional** field (like `KeyItems`, which `FileDraftStorage` already does not persist), adding it:

- does **not** change the positional constructor signature → `FileDraftStorage` still compiles unchanged;
- means a round-tripped draft snapshot has `QuestVariables == null` (not persisted), exactly as `KeyItems`/`LastAttuned` are already dropped (`FileDraftStorage.cs:261,283`).

**No `FileDraftStorage` change is required.** Persisting variables into the draft file is explicitly out of scope (§Exclusions). This is a build-verified non-change (a compile of `QuestForge.Adapters.Dalamud` confirms it).

---

## 6. Component changes

### 6.1 `IQuestState` + all implementers

Add the member from §4 to `IQuestState`, then implement on every implementer in §3.3:

**`DalamudQuestState`** (the real read):
```csharp
public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
{
    unsafe
    {
        var qm = QuestManager.Instance();
        if (qm == null)
            return Task.FromResult<Result<IReadOnlyList<byte>>>(
                Result.Fail<IReadOnlyList<byte>>("questManagerNull", "QuestManager instance unavailable"));

        var q = qm->GetQuestById(ToInternal(quest));
        if (q == null)
            // Not accepted / completed → routine "no variables": all-zero length-6 list (mirrors GetQuestFlags → Ok(0)).
            return Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Ok<IReadOnlyList<byte>>(new byte[6]));

        var span = q->Variables;          // Span<byte>, length 6
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++) bytes[i] = span[i];
        return Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Ok<IReadOnlyList<byte>>(bytes));
    }
}
```

**`FakeQuestState`** (unit-test surface): add a scriptable backing store + `Record`, mirroring `_flags` / `SetQuestFlags`:
```csharp
private readonly Dictionary<QuestId, IReadOnlyList<byte>> _variables = new();

public void SetQuestVariables(QuestId quest, params byte[] variables)
{
    if (variables.Length != 6)
        throw new ArgumentException("Quest variables must be exactly 6 bytes (V0–V5).", nameof(variables));
    _variables[quest] = variables;
}

public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Record(nameof(GetQuestVariables));
    IReadOnlyList<byte> vars = _variables.TryGetValue(quest, out var v) ? v : new byte[6];
    return Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Ok(vars));
}
```
(Default — unscripted — returns an all-zero length-6 list, matching the `DalamudQuestState` not-accepted fallback.)

**`RecordingQuestState`** (proxy — also satisfies the reflection guard):
```csharp
public async Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
{
    var result = await _inner.GetQuestVariables(quest, ct);
    Record(nameof(GetQuestVariables), quest, result);
    return result;
}
```

**`ReplayQuestState`** (scanner-backed):
```csharp
public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
{
    var obs = ScanNext(nameof(GetQuestVariables), quest);
    return Task.FromResult(Materialize<IReadOnlyList<byte>>(obs.Value));
}
```

**Both test `FailingQuestState` doubles** (`AwaitUserTests.cs:161`, `RecordingQuestStateTests.cs:280`): add the failing override so the test files compile:
```csharp
public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
    => Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Fail<IReadOnlyList<byte>>("adapter-broken"));
```

> Builder note: adding the member breaks the build of `QuestForge.Adapters`, `QuestForge.Adapters.Dalamud`, `QuestForge.Adapters.Fakes`, and `QuestForge.Engine.Tests` until every implementer above is updated. Update all six in the same change.

### 6.2 `SnapshotAggregator` + `IAuthoringObserver`

Add the field, the `Current` wiring, and the observer method (mirrors `OnQuestFlagsChanged`, `SnapshotAggregator.cs:100-104`). This is the **pure-C# unit-testable core**.

```csharp
// field
private IReadOnlyList<byte>? _questVariables;

// in Current's initializer block (alongside KeyItems = _keyItems, ...)
QuestVariables = _questVariables,

// new method
/// <summary>
/// Called when the active quest's work bytes (V0–V5) are observed.
/// Stores the latest values; ignored when the quest does not match the authored quest
/// (matches OnQuestSequenceChanged / OnQuestFlagsChanged semantics).
/// </summary>
public void OnQuestVariablesUpdated(QuestId quest, IReadOnlyList<byte> variables)
{
    if (_activeQuest == quest)
        _questVariables = variables;
}
```

**Add the matching declaration to `IAuthoringObserver`** (`QuestForge.Engine/Authoring/IAuthoringObserver.cs`) so the interface and the concrete aggregator stay in sync, alongside `OnQuestFlagsChanged`:
```csharp
void OnQuestVariablesUpdated(QuestId quest, IReadOnlyList<byte> variables);
```

> WHY a `QuestId` parameter (not just the bytes): every other quest-scoped `On*` method on the aggregator carries the `QuestId` and applies the `_activeQuest == quest` guard so foreign-quest signals are ignored. `OnQuestVariablesUpdated` must do the same — a heartbeat poll iterates *all* accepted quests (`UIObserver.PollQuestState`), so the guard prevents a foreign quest's variables from leaking into the authored quest's snapshot.

### 6.3 `IGameProbe.GetNormalQuests()` — extend the tuple

`UIObserver` reaches the aggregator's quest state exclusively through `IGameProbe.GetNormalQuests()` (it has no `IQuestState`). Extend the returned tuple to carry the six variable bytes so `PollQuestState` can forward them, keeping the read in the Dalamud adapter:

```csharp
// IGameProbe.cs
IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests();
```

**`DalamudGameProbe.GetNormalQuests()`** (`QuestForge.Plugin/Tracing/DalamudGameProbe.cs:22-33`) reads `slot.Variables` while iterating:
```csharp
foreach (ref var slot in mgr->NormalQuests)
{
    if (slot.QuestId == 0) continue;
    var span = slot.Variables;            // Span<byte>, length 6
    var vars = new byte[6];
    for (var i = 0; i < 6; i++) vars[i] = span[i];
    result.Add((slot.QuestId, slot.Sequence, slot.Flags, vars));
}
```

> Builder note: this changes the `IGameProbe` contract, so the in-test `FakeGameProbe` (in `QuestForge.Plugin.Tests/Tracing/`) and any other `IGameProbe` implementer must update their `GetNormalQuests()` tuple shape. The `UIObserverTests` that script quests (`UO_C11`, `UO_C12`) construct these tuples and must add the variables element. This is the only non-additive change in the plan and is build-verified by `dotnet build QuestForge.Plugin.Tests`.

### 6.4 `UIObserver.PollQuestState` — forward variables to the aggregator

In `PollQuestState` (`UIObserver.cs:199-254`), destructure the new tuple element and forward it on every poll for the iterated quest (the aggregator's `_activeQuest == quest` guard discards non-authored quests). Forward unconditionally each heartbeat (variables change frequently and are cheap to copy; no change-detection bookkeeping needed):

```csharp
foreach (var (id, seq, flags, variables) in quests)
{
    if (id == 0) continue;
    seenIds.Add(id);
    var publicId = ToPublicQuestId(id);
    // ... existing seq/flags change-detection unchanged ...

    _aggregator?.OnQuestVariablesUpdated(publicId, variables);   // NEW — forward every poll
}
```

(This is Dalamud-layer plumbing; build-verified. The aggregator-side logic is the unit-tested part.)

### 6.5 `QuestStatePanel` — render the 6-column table (Option B)

In `QuestStatePanel.Draw()` (`QuestForge.Plugin/UI/Authoring/QuestStatePanel.cs`), after the existing five lines, render the variables table **only when `snapshot.QuestVariables` is present** (Author mode → `ActiveQuest` set → aggregator fed). Bump `MaximumSize` height (e.g. `400 → 460`) to fit the table.

```csharp
if (snapshot.QuestVariables is { Count: 6 } vars)
{
    ImGui.Separator();
    ImGui.TextUnformatted("Variables (V0–V5):");
    if (ImGui.BeginTable("qf-quest-variables", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
    {
        for (var i = 0; i < 6; i++)
            ImGui.TableSetupColumn($"V{i}");
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        for (var i = 0; i < 6; i++)
        {
            ImGui.TableSetColumnIndex(i);
            var b = vars[i];
            if (b == 0)
                ImGui.TextUnformatted("0");                       // zero → compact
            else
                ImGui.TextUnformatted($"0x{b:X2} H:{b >> 4} L:{b & 0x0F}");  // non-zero → full detail
        }
        ImGui.EndTable();
    }
}
```

(Build-verified — no ImGui unit-test harness.)

### 6.6 `QfCommand.HandleDebugQuest` — add a variables block

`HandleDebugQuest(string questIdStr)` (`QfCommand.cs:381-459`) already parses `rawId`, prints quest name/level/jobs, and has access to `_host.QuestState` (an `IQuestState`, exposed at `EngineHost.cs:102`). Add a variables block after the existing prereq/issuer line, reading via the new adapter method for the explicit id (works for any **accepted** quest, mode-agnostic):

```csharp
var ct = CancellationToken.None;
var varsResult = _host.QuestState.GetQuestVariables(new QuestId(rawId), ct).GetAwaiter().GetResult();
if (varsResult is Result<IReadOnlyList<byte>>.Success { Value: var vars } && vars.Count == 6)
{
    var parts = new string[6];
    for (var i = 0; i < 6; i++)
    {
        var b = vars[i];
        parts[i] = b == 0 ? $"V{i}=0" : $"V{i}=0x{b:X2}(H:{b >> 4} L:{b & 0x0F})";
    }
    var line = $"  variables: {string.Join("  ", parts)}";
    _chat.Print(line);
    _log.Info($"[debug quest] {line}");
}
else
{
    _chat.Print("  variables: (quest not accepted — no work bytes)");
}
```

> Note: `_host.QuestState` is the (possibly recording-wrapped) `IQuestState`. Because the recording proxy delegates to the inner Dalamud adapter, the read works the same whether or not tracing is on. All-zero is a legitimate result for an accepted quest at seq 0; the "(quest not accepted)" branch only fires on an adapter `Result.Fail` (e.g. `QuestManager` unavailable), since the not-accepted case returns an all-zero `Result.Ok` list per §4. (Build-verified.)

---

## 7. Acceptance criteria

Grouped by **how they are verified**. Group **U** (unit-testable) lands as failing xUnit tests *before* implementation. Group **V** (build-verified) is confirmed by a clean build of the named project plus a manual in-game smoke per the Done criteria — no automated test exists for the Dalamud layer.

Unit tests live in:
- `QuestForge.Engine.Tests/Authoring/SnapshotAggregatorTests.cs` (aggregator — extend the existing file).
- A `GameStateSnapshot` field test (same project; can co-locate in `SnapshotAggregatorTests` or a small `GameStateSnapshotTests`).
- `QuestForge.Adapters.Tests` or `QuestForge.Engine.Tests` for the `FakeQuestState.GetQuestVariables` behavior (follow where existing `FakeQuestState` flag tests live; if none, add to `SnapshotAggregatorTests`'s project using the fake directly).

### Group U — unit-testable (engine / fakes)

- **U1 (snapshot field default null)** — Given a fresh `SnapshotAggregator(activeQuest: Q)`, then `aggregator.Current.QuestVariables` is `null` (no variables observed yet).
- **U2 (aggregator stores variables for the active quest)** — Given `SnapshotAggregator(activeQuest: new QuestId(2054))`, when `OnQuestVariablesUpdated(new QuestId(2054), new byte[]{0x10,0,0,5,0,0})`, then `Current.QuestVariables` equals `[0x10,0,0,5,0,0]` (sequence-equal, count 6).
- **U3 (aggregator ignores variables for a different quest)** — Given `SnapshotAggregator(activeQuest: new QuestId(2054))`, when `OnQuestVariablesUpdated(new QuestId(9999), new byte[]{1,2,3,4,5,6})`, then `Current.QuestVariables` is `null` (the `_activeQuest == quest` guard rejected the foreign quest). Mirror `SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest`.
- **U4 (latest write wins)** — Given the active quest, when `OnQuestVariablesUpdated(Q, [1,0,0,0,0,0])` then `OnQuestVariablesUpdated(Q, [2,0,0,0,0,0])`, then `Current.QuestVariables[0] == 2`.
- **U5 (GameStateSnapshot QuestVariables round-trips through `with`)** — Given a `GameStateSnapshot` constructed positionally, when `snap with { QuestVariables = new byte[]{0,0,0,0,0,9} }`, then the result's `QuestVariables[5] == 9` and all positional fields are preserved (proves it's a non-positional init field that doesn't disturb the record's positional members).
- **U6 (FakeQuestState default → all-zero length-6 Result.Ok)** — Given a `FakeQuestState` with no scripted variables for quest Q, when `GetQuestVariables(Q, ct)`, then the result is `Result.Ok` with a list of `Count == 6` all `0`.
- **U7 (FakeQuestState returns scripted variables)** — Given `fake.SetQuestVariables(Q, 0x10,0,0,5,0,0)`, when `GetQuestVariables(Q, ct)`, then the result is `Result.Ok` with `[0x10,0,0,5,0,0]`.
- **U8 (FakeQuestState rejects wrong-length script)** — Given `fake.SetQuestVariables(Q, 1, 2, 3)` (3 bytes), then it throws `ArgumentException` (the setter enforces exactly 6).
- **U9 (FakeQuestState records the read)** — Given `fake.GetQuestVariables(Q, ct)`, then `fake.RecordedReads` contains a `StateRead` with `Method == "GetQuestVariables"` (mirrors how the other fake reads are recorded).
- **U10 (recording-proxy coverage guard stays green)** — `RecordingProxyCoverageTests.RecordingQuestState_ImplementsAllIQuestStateMethods` passes after the proxy gains `GetQuestVariables` (this test goes red the moment the interface member is added without the proxy method — it is the proof the proxy was updated). The Tester does not add a new test; they confirm the **existing** guard is satisfied.
- **U11 (nibble helper, if extracted)** — *Only if the Builder extracts a `static (byte H, byte L) Nibbles(byte b)` helper.* Given `b = 0x10`, then `H == 1, L == 0`; given `b = 0xAB`, then `H == 0xA, L == 0xB`; given `b = 0`, then `H == 0, L == 0`. If no helper is extracted (the nibble math is inline in the panel/command), this case is dropped — the panel/command are build-verified, so inline nibble math is not unit-tested.

### Group V — build-verified (Dalamud layer + non-additive contract change)

- **V1** — `IQuestState.GetQuestVariables` exists; `DalamudQuestState` implements it reading `QuestManager.Instance()->GetQuestById(internalId)->Variables[0..5]`, returning `Result.Fail("questManagerNull", …)` when the manager is null and an all-zero length-6 `Result.Ok` when `GetQuestById` is null. `dotnet build QuestForge.Adapters.Dalamud` succeeds.
- **V2** — `IGameProbe.GetNormalQuests()` returns the 4-tuple including `Variables`; `DalamudGameProbe` populates it; every other `IGameProbe` implementer (incl. test `FakeGameProbe`) is updated. `dotnet build` of `QuestForge.Plugin` and `QuestForge.Plugin.Tests` succeeds (the latter proves `UO_C11`/`UO_C12` tuple sites were fixed).
- **V3** — `UIObserver.PollQuestState` forwards `OnQuestVariablesUpdated(publicId, variables)` each heartbeat. `dotnet build QuestForge.Plugin` succeeds; existing `UIObserverTests` still pass.
- **V4** — `QuestStatePanel.Draw` renders a 6-column `ImGui.BeginTable` from `snapshot.QuestVariables` only when present, zero bytes as `0` and non-zero as `0x{X2} H:{} L:{}`, with `MaximumSize` height increased. `dotnet build QuestForge.Plugin` succeeds. **In-game smoke:** `/qf author <id>` on an in-progress quest shows the table with the live work bytes; switching to `/qf inspect` shows no table.
- **V5** — `QfCommand.HandleDebugQuest` prints a `variables:` line for an accepted quest and a "(quest not accepted — no work bytes)" line on adapter failure. `dotnet build QuestForge.Plugin` succeeds. **In-game smoke:** `/qf debug quest <accepted-id>` prints the six bytes; the values match the panel and `/xldata`'s `QuestWork`.
- **V6** — `FileDraftStorage` compiles unchanged (the new snapshot field is non-positional optional); a draft round-trip leaves `QuestVariables == null` (not persisted). Build-verified by compiling `QuestForge.Adapters.Dalamud`; no code change.

---

## 8. Implementation order

**Phase A — Engine surface (unit-tested core), ~0.5 day.**
1. Add `QuestVariables` init field to `GameStateSnapshot`.
2. Add `_questVariables` field, `Current` wiring, and `OnQuestVariablesUpdated(QuestId, IReadOnlyList<byte>)` to `SnapshotAggregator`; add the method to `IAuthoringObserver`.
3. Make group U tests U1–U5 pass.
Done before C.

**Phase B — Adapter surface, ~0.5 day.**
1. Add `GetQuestVariables` to `IQuestState`.
2. Implement on `DalamudQuestState`, `FakeQuestState` (+ `SetQuestVariables`), `RecordingQuestState`, `ReplayQuestState`, and both test `FailingQuestState` doubles.
3. Make U6–U10 pass (incl. the reflection guard staying green).
Done before D and E.

**Phase C — Probe + feed (Dalamud plumbing), ~0.5 day.**
1. Extend `IGameProbe.GetNormalQuests()` tuple; update `DalamudGameProbe` and all test `IGameProbe` implementers (fix `UO_C11`/`UO_C12` tuple sites).
2. Forward `OnQuestVariablesUpdated` from `UIObserver.PollQuestState`.
Build-verify `QuestForge.Plugin` + `QuestForge.Plugin.Tests` (V2, V3).

**Phase D — Panel, ~0.25 day.**
Render the 6-column table in `QuestStatePanel`; bump `MaximumSize`. Build-verify + in-game smoke (V4).

**Phase E — Command, ~0.25 day.**
Add the variables block to `HandleDebugQuest`. Build-verify + in-game smoke (V5).

> A and B are independent of each other and can land in either order; C depends on both (it calls the aggregator method and the probe). D depends on A (snapshot field). E depends on B (adapter method).

---

## 9. Build / verification note (net10 SDK)

This machine's default `dotnet` is .NET 8; the projects target `net10.0`. The net10 SDK lives at `C:\Users\publi\.dotnet` (10.0.202) and `global.json` pins `10.0.202`. **All builds/tests must use the net10 muxer.** From the Bash tool, prefix every `dotnet` invocation:

```bash
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH dotnet test QuestForge.Engine.Tests
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH dotnet build QuestForge.Plugin
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH dotnet build QuestForge.Plugin.Tests
```

A bare `dotnet build` will pick .NET 8 and fail to resolve the `net10.0` TFM.

---

## 10. Done criteria

1. `IQuestState.GetQuestVariables(QuestId, CancellationToken)` returns `Task<Result<IReadOnlyList<byte>>>`, implemented on all six implementers (§3.3); the `DalamudQuestState` read uses `GetQuestById(internalId)->Variables[0..5]`, fails only on null `QuestManager`, and returns an all-zero length-6 list for a not-accepted/completed quest (U6, V1).
2. `RecordingProxyCoverageTests.RecordingQuestState_ImplementsAllIQuestStateMethods` is green (proves the proxy was updated) (U10).
3. `GameStateSnapshot` has a non-positional `IReadOnlyList<byte>? QuestVariables { get; init; }` field that survives `with` and does not disturb the positional constructor; `FileDraftStorage` compiles unchanged and round-trips it as `null` (U5, V6).
4. `SnapshotAggregator.OnQuestVariablesUpdated` stores variables for the active quest and ignores foreign quests, exposed via `Current.QuestVariables` (U1–U4); the method is declared on `IAuthoringObserver`.
5. `FakeQuestState` supports `SetQuestVariables(...)`, returns all-zero by default, records the read, and rejects non-6-length scripts (U6–U9).
6. `IGameProbe.GetNormalQuests()` carries the six bytes; `UIObserver.PollQuestState` forwards them to the aggregator each heartbeat; `QuestForge.Plugin` + `QuestForge.Plugin.Tests` build clean (V2, V3).
7. The Quest State panel renders a 6-column V0–V5 table **only in Author mode** (`snapshot.QuestVariables` present), zero bytes compact and non-zero bytes with nibbles; no table in Inspect mode (V4, in-game smoke).
8. `/qf debug quest <id>` prints a `variables:` block for an accepted quest whose values match the panel and `/xldata` (V5, in-game smoke).
9. All existing engine/plugin tests still pass (the snapshot field is optional/appended; the aggregator method is additive; the only contract change — the `IGameProbe` tuple — is fixed at every call site).

---

## 11. Exclusions

This issue does **NOT** include:

- **Inspect-mode variable display.** Per fixed decision 1: the aggregator is built with `null` activeQuest in Inspect mode, so no variables are read or shown there. The panel renders the table only when `ActiveQuest` is set (Author mode).
- **Persisting variables into draft files.** `FileDraftStorage` / `SnapshotFileDto` are unchanged; round-tripped snapshots have `QuestVariables == null`. Adding variables to the draft format is a future enhancement.
- **Variable-based predicates.** This issue is display-only. No `questVariable(id, index)`-style predicate function, no validator rule, no schema change. The predicate language and `IGameStateProvider`/`IQuestState` predicate bindings are untouched.
- **Trace observation of variables for replay.** `RecordingQuestState.GetQuestVariables` will emit an `ObservationEvent` when the engine calls it, but the engine does not call `GetQuestVariables` (no consumer in `QuestEngine`); the panel/command read it directly off the aggregator / adapter. No new replay determinism guarantees are claimed for variables.
- **Per-byte change highlighting / history.** The panel shows the current values only — no "changed since last poll" highlight (unlike the existing `RecentChange` banner, which covers quest sequence/flags, not variables).
- **Reading variables for daily/leve/beast-tribe quests.** Only `NormalQuests` (`QuestWork`) are read, matching `GetQuestById`. Daily/leve work bytes are out of scope.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §7 (group U only — group V is build-verified, no automated tests).
- Happy paths: 4 scenarios (U2, U5, U7, U10)
- Edge cases: 5 scenarios (U1, U3, U4, U6, U9)
- Error cases: 1 scenario (U8) plus the optional U11 nibble cases if a helper is extracted
- Expected total: ~10 tests in `QuestForge.Engine.Tests` (aggregator + snapshot + `FakeQuestState`), plus the existing `RecordingProxyCoverageTests` guard (U10) which requires no new test — it simply must stay green. Group V (V1–V6) is verified by `dotnet build` of the named projects (net10 muxer) and two in-game smokes.
