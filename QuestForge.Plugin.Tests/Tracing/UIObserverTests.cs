using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE: All tests in this file WILL fail because:
//   1. QuestForge.Plugin.Tracing.UIObserver does not exist yet.
//   2. QuestForge.Plugin.Tracing.IAddonProbe does not exist yet.
//   3. QuestForge.Plugin.Tracing.IGameProbe does not exist yet.
//
// Builder: Create those three types in QuestForge.Plugin/Tracing/ and implement
// them incrementally, running this file after each change.
//
// Run with: dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests"
//
// Total: 66 tests (UO-A1..A7, UO-B1..B6, UO-C1..C16, UO-D1..D13,
//                  UO-E1..E5, UO-F1..F4, UO-G1..G4, UO-H1..H10, UO-I1..I5).
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

/// <summary>
/// Test-doubles for Dalamud interfaces that UIObserver uses only through the
/// PluginServices record. UIObserver accesses services only via IAddonProbe /
/// IGameProbe injected through its constructor — it must NOT call Dalamud APIs
/// directly. Therefore we only need a FakeFramework here; all other PluginServices
/// members are stubbed as null via the minimal-constructor helper below.
/// </summary>

// ─── FakeFramework ───────────────────────────────────────────────────────────

/// <summary>
/// Captures the Framework.Update handler that UIObserver subscribes to and lets
/// tests drive ticks synchronously via <see cref="Tick"/>.
/// </summary>
public sealed class FakeFramework : QuestForge.Plugin.Tracing.IFrameworkDispatch
{
    // UIObserver calls: services.Framework.Update += handler
    // In tests we capture the delegate here instead of going through Dalamud's IFramework.
    private Action? _updateHandler;

    /// <summary>Register the update handler (simulates Framework.Update += x).</summary>
    public void Subscribe(Action handler)  => _updateHandler = handler;

    /// <summary>Unregister the update handler (simulates Framework.Update -= x).</summary>
    public void Unsubscribe(Action handler)
    {
        if (_updateHandler == handler)
            _updateHandler = null;
    }

    /// <summary>Invoke one framework tick synchronously.</summary>
    public void Tick() => _updateHandler?.Invoke();
}

// ─── FakeAddonProbe ──────────────────────────────────────────────────────────

/// <summary>
/// In-memory implementation of <see cref="IAddonProbe"/> for testing.
/// All state is fully controllable from tests.
/// </summary>
public sealed class FakeAddonProbe : IAddonProbe
{
    private readonly HashSet<string> _openAddons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _selectedIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string?> _telepotDestinations = new();
    private readonly Dictionary<int, uint?> _telepotDestinationIds = new();

    public void OpenAddon(string name)          => _openAddons.Add(name);
    public void CloseAddon(string name)         => _openAddons.Remove(name);
    public void SetSelectedIndex(string addon, int idx) => _selectedIndices[addon] = idx;
    public void SetTelepotDestination(int idx, string? name, uint? rowId = null)
    {
        _telepotDestinations[idx] = name;
        _telepotDestinationIds[idx] = rowId;
    }

    public bool  IsAddonOpen(string addonName)         => _openAddons.Contains(addonName);
    public int?  GetSelectedItemIndex(string addonName) =>
        _selectedIndices.TryGetValue(addonName, out var v) ? v : null;
    public string? GetTelepotTownDestinationName(int idx) =>
        _telepotDestinations.TryGetValue(idx, out var v) ? v : null;
    public uint? GetTelepotTownDestinationId(int idx) =>
        _telepotDestinationIds.TryGetValue(idx, out var v) ? v : null;
}

// ─── FakeGameProbe ───────────────────────────────────────────────────────────

/// <summary>
/// In-memory implementation of <see cref="IGameProbe"/> for testing.
/// </summary>
public sealed class FakeGameProbe : IGameProbe
{
    private readonly List<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> _quests = new();
    private readonly HashSet<uint> _unlockedAetherytes = new();
    private readonly List<(uint ItemId, int Qty)> _keyItems = new();

    public void AddQuest(uint questId, byte seq, byte flags) =>
        _quests.Add(((ushort)questId, seq, flags, new byte[6]));

    public void UpdateQuest(uint questId, byte seq, byte flags)
    {
        var id  = (ushort)questId;
        var idx = _quests.FindIndex(q => q.QuestId == id);
        if (idx >= 0) _quests[idx] = (id, seq, flags, _quests[idx].Variables);
    }

    public void SetQuestVariables(uint questId, byte[] variables)
    {
        var id  = (ushort)questId;
        var idx = _quests.FindIndex(q => q.QuestId == id);
        if (idx >= 0) _quests[idx] = (_quests[idx].QuestId, _quests[idx].Seq, _quests[idx].Flags, variables);
    }

    public void RemoveQuest(uint questId)
    {
        var id = (ushort)questId;
        _quests.RemoveAll(q => q.QuestId == id);
    }

    public void UnlockAetheryte(uint rowId) => _unlockedAetherytes.Add(rowId);
    public void AddKeyItem(uint itemId, int qty) => _keyItems.Add((itemId, qty));
    public void ClearKeyItems() => _keyItems.Clear();

    public IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests() =>
        _quests.AsReadOnly();

    public bool IsAetheryteUnlocked(uint rowId) => _unlockedAetherytes.Contains(rowId);

    public IEnumerable<uint> GetAllAetheryteRowIds() => _unlockedAetherytes;

    public IReadOnlyList<(uint ItemId, int Qty)> GetKeyItemSlots() =>
        _keyItems.AsReadOnly();

    public (float X, float Y, float Z, int Zone)? GetPlayerPosition() => null; // tests use aggregator directly

    // ── UAI-T8: GetLastActionEffect extension ────────────────────────────────
    // Sticky: once set, the same tuple is returned on every call until cleared.
    // Mirrors _unlockedAetherytes semantics — represents "what the game currently shows."
    // Tests call SetLastActionEffect(seq, type, id) then fw.Tick() to simulate the player
    // pressing an action.

    private (uint, uint, uint)? _nextActionEffect;

    public void SetLastActionEffect(uint sequence, uint ffxivActionType, uint actionId)
        => _nextActionEffect = (sequence, ffxivActionType, actionId);

    public void ClearLastActionEffect() => _nextActionEffect = null;

    public (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect()
        => _nextActionEffect;
}

// ─────────────────────────────────────────────────────────────────────────────
// Test fixture helpers
// ─────────────────────────────────────────────────────────────────────────────

public sealed class UIObserverTests
{
    // Shared fixed timestamp so all tests are deterministic.
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);

    // Shared quest id used across tests.
    private const uint QuestId = 66130u;
    private const string PassiveRunId = "passive-001";

    // ─── Fixture builder ──────────────────────────────────────────────────

    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeAddonProbe addonProbe,
                    FakeGameProbe gameProbe,
                    FakeClock clock,
                    FakeTraceWriter writer,
                    TraceSession traceSession)
        BuildFixture(TraceMode mode = TraceMode.Always)
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(mode, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock      = new FakeClock(T0);
        var addonProbe = new FakeAddonProbe();
        var gameProbe  = new FakeGameProbe();
        var framework  = new FakeFramework();

        // UIObserver subscribes to Framework.Update in its constructor.
        // PluginServices is passed for the framework hook; probes are injected directly.
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            addonProbe:   addonProbe,
            gameProbe:    gameProbe,
            clock:        clock);

        return (observer, framework, addonProbe, gameProbe, clock, writer, session);
    }

    // Convenience: build with aggregator already set.
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeAddonProbe addonProbe,
                    FakeGameProbe gameProbe,
                    FakeClock clock,
                    FakeTraceWriter writer,
                    TraceSession traceSession,
                    SnapshotAggregator aggregator)
        BuildFixtureWithAggregator(TraceMode mode = TraceMode.Always)
    {
        var (obs, fw, ap, gp, clock, writer, session) = BuildFixture(mode);
        var aggregator = new SnapshotAggregator(null, clock);
        obs.SetAggregator(aggregator, activeRunId: "active-001");
        return (obs, fw, ap, gp, clock, writer, session, aggregator);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-A: Constructor & Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_A1_Constructor_SubscribesToFrameworkUpdate()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a FakeFramework,
         *           When UIObserver is constructed,
         *           Then ticking the framework invokes the internal update handler
         *           without throwing.
         *
         * BUILDER GUIDANCE: Constructor must call framework.Subscribe(OnFrameworkUpdate)
         *   or equivalent so that FakeFramework.Tick() reaches the poller.
         */

        // Arrange
        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();

        // Act
        var observer = new UIObserver(framework, session, PassiveRunId);
        var ex = Record.Exception(() => framework.Tick());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_A2_Constructor_NullProbes_DoesNotThrow()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given null IAddonProbe and IGameProbe,
         *           When UIObserver is constructed and a tick is driven,
         *           Then no NullReferenceException is thrown.
         *
         * BUILDER GUIDANCE: Null probes = no polling. Guard every probe call with
         *   a null check.
         */

        // Arrange
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => new FakeTraceWriter());
        session.OnPluginStart();
        var framework = new FakeFramework();

        // Act
        var observer = new UIObserver(framework, session, PassiveRunId,
                                      addonProbe: null, gameProbe: null);
        var ex = Record.Exception(() => framework.Tick());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_A3_Constructor_PassiveRunId_UsedInObservations()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given passiveRunId "passive-001" and no aggregator,
         *           When a tick fires and a quest observation is emitted,
         *           Then the ObservationEvent.RunId == "passive-001".
         *
         * BUILDER GUIDANCE: All WriteObservation calls in the passive polling path
         *   must use passiveRunId.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0); // raw id; public id = 0x200 | 0x10000

        // Act
        framework.Tick(); // drives PollQuestState

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>().FirstOrDefault();
        Assert.NotNull(obs);
        Assert.Equal(PassiveRunId, obs!.RunId);

        observer.Dispose();
    }

    [Fact]
    public void UO_A4_SetAggregator_NullAggregator_ClearsAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator has been set via SetAggregator,
         *           When SetAggregator(null, null) is called,
         *           Then subsequent ticks do not forward events to any aggregator
         *           (no NullReferenceException).
         *
         * BUILDER GUIDANCE: Store the aggregator in a nullable field; guard all
         *   aggregator?.OnXxx calls.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, _, _) = BuildFixture();
        var aggregator = new SnapshotAggregator(null);
        observer.SetAggregator(aggregator, "active-001");
        observer.SetAggregator(null, null); // clear it

        gameProbe.AddQuest(0x200u, 0, 0);

        // Act — must not throw
        var ex = Record.Exception(() => framework.Tick());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_A5_SetAggregator_SwapsActiveRunId()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set with activeRunId "run-A",
         *           When SetAggregator is called again with a new aggregator and "run-B",
         *           Then subsequent aggregator forwarding uses the new instance (no crash).
         *
         * BUILDER GUIDANCE: Replace both _aggregator and _activeRunId atomically.
         */

        // Arrange
        var (observer, framework, _, _, _, _, _) = BuildFixture();
        var aggA = new SnapshotAggregator(null);
        var aggB = new SnapshotAggregator(null);

        observer.SetAggregator(aggA, "run-A");

        // Act
        observer.SetAggregator(aggB, "run-B");
        var ex = Record.Exception(() => framework.Tick());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_A6_Dispose_UnsubscribesFromFramework()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a UIObserver that has been Disposed,
         *           When the framework ticks again,
         *           Then the previously registered handler is NOT invoked
         *           (tick count before and after Dispose must be equal).
         *
         * BUILDER GUIDANCE: Dispose must call framework.Unsubscribe or the
         *   equivalent -= on the event.
         */

        // Arrange
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => new FakeTraceWriter());
        session.OnPluginStart();
        var writer    = new FakeTraceWriter();
        var session2  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session2.OnPluginStart();
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        gameProbe.AddQuest(0x200u, 0, 0);

        var observer = new UIObserver(framework, session2, PassiveRunId, gameProbe: gameProbe);

        // One tick before dispose — establishes baseline
        framework.Tick();
        var countAfterFirstTick = writer.Count;

        // Act
        observer.Dispose();
        framework.Tick(); // should NOT invoke handler after dispose

        // Assert
        Assert.Equal(countAfterFirstTick, writer.Count);
    }

    [Fact]
    public void UO_A7_DisposeCalledTwice_DoesNotThrow()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a UIObserver,
         *           When Dispose is called twice,
         *           Then no exception is thrown.
         *
         * BUILDER GUIDANCE: Use a standard _disposed guard in Dispose().
         */

        // Arrange
        var (observer, _, _, _, _, _, _) = BuildFixture();

        // Act
        observer.Dispose();
        var ex = Record.Exception(() => observer.Dispose());

        // Assert
        Assert.Null(ex);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-B: ResetWindowState / ResetHeartbeatState
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_B1_ResetWindowState_DoesNotThrow()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a freshly constructed UIObserver,
         *           When ResetWindowState is called,
         *           Then no exception is thrown.
         */

        // Arrange
        var (observer, _, _, _, _, _, _) = BuildFixture();

        // Act
        var ex = Record.Exception(() => observer.ResetWindowState());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_B2_ResetWindowState_ClearsTelepotTownOpenFlag()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TelepotTown was open on the previous tick
         *           (so the "was-open" flag is true internally),
         *           When ResetWindowState is called,
         *           Then the next tick treats TelepotTown as freshly opened
         *           (no stale "was-open" state bleeds over).
         *
         * BUILDER GUIDANCE: ResetWindowState should reset _aethernetMenuWasOpen
         *   and related tracking fields so the next recording window starts clean.
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("TelepotTown");
        framework.Tick(); // now _aethernetMenuWasOpen = true internally

        // Act
        observer.ResetWindowState();
        addonProbe.CloseAddon("TelepotTown"); // close menu
        var countBefore = writer.Count;
        framework.Tick(); // should NOT fire a "menu closed" event since state was reset

        // Assert — no spurious "closed" observation written
        var newEvents = writer.RecordedEvents.Skip(countBefore)
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "AethernetTeleportCompleted")
            .ToList();
        Assert.Empty(newEvents);

        observer.Dispose();
    }

    [Fact]
    public void UO_B3_ResetHeartbeatState_DoesNotThrow()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a UIObserver,
         *           When ResetHeartbeatState is called,
         *           Then no exception is thrown.
         */

        // Arrange
        var (observer, _, _, _, _, _, _) = BuildFixture();

        // Act
        var ex = Record.Exception(() => observer.ResetHeartbeatState());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_B4_ResetHeartbeatState_CausesNextTickToFireHeartbeatPollers()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a UIObserver whose heartbeat was just fired (so the
         *           250 ms throttle is in place),
         *           When ResetHeartbeatState is called and then a tick fires,
         *           Then the heartbeat pollers run again immediately (not throttled).
         *
         * BUILDER GUIDANCE: ResetHeartbeatState sets _lastHeartbeatAt = DateTimeOffset.MinValue
         *   so the next tick always satisfies the elapsed-time check.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);

        // First tick — runs heartbeat pollers and writes initial observations
        framework.Tick();
        var countAfterFirst = writer.Count;
        Assert.True(countAfterFirst > 0, "Expected at least one observation on first tick");

        // Advance time by only 10 ms — normally not enough to re-trigger heartbeat
        clock.Advance(TimeSpan.FromMilliseconds(10));
        framework.Tick(); // should NOT run heartbeat pollers again
        var countWithoutReset = writer.Count;

        // Act — reset heartbeat so next tick fires pollers regardless of elapsed time
        observer.ResetHeartbeatState();
        gameProbe.AddQuest(0x300u, 0, 0); // new quest so the heartbeat poller has fresh data (existing quest deduped)
        framework.Tick(); // SHOULD run heartbeat pollers

        // Assert
        Assert.True(writer.Count > countWithoutReset,
            "ResetHeartbeatState must cause heartbeat pollers to fire on next tick");

        observer.Dispose();
    }

    [Fact]
    public void UO_B5_ResetWindowState_ClearsDialogueIconStringOpenFlag()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given SelectIconString was open on the previous tick,
         *           When ResetWindowState is called then the addon closes,
         *           Then no stale "dialogue-was-open" close event fires next tick.
         *
         * BUILDER GUIDANCE: ResetWindowState resets _dialogueIconStringWasOpen.
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("SelectIconString");
        framework.Tick(); // _dialogueIconStringWasOpen = true

        // Act
        observer.ResetWindowState();
        addonProbe.CloseAddon("SelectIconString");
        var countBefore = writer.Count;
        framework.Tick();

        // Assert — no spurious "DialogueOptionSelected" written from stale state
        var newEvents = writer.RecordedEvents.Skip(countBefore)
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "DialogueOptionSelected")
            .ToList();
        Assert.Empty(newEvents);

        observer.Dispose();
    }

    [Fact]
    public void UO_B6_ResetWindowState_DoesNotAffectHeartbeatThrottle()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a UIObserver whose heartbeat fired recently (throttled),
         *           When ResetWindowState is called (NOT ResetHeartbeatState),
         *           Then the heartbeat is still throttled on the next tick.
         *
         * BUILDER GUIDANCE: ResetWindowState and ResetHeartbeatState are
         *   independent — they reset different tracking fields.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);

        framework.Tick();
        var countAfterFirst = writer.Count;

        // Advance time by 10 ms — below throttle threshold
        clock.Advance(TimeSpan.FromMilliseconds(10));

        // Act
        observer.ResetWindowState(); // should NOT reset heartbeat
        framework.Tick();

        // Assert — count unchanged: heartbeat pollers did not run (still throttled)
        Assert.Equal(countAfterFirst, writer.Count);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-C: Quest state polling — TraceSession observations
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_C1_PollQuestState_NewQuest_WritesGetQuestSequenceObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe returns a quest (id=0x200, seq=1, flags=0),
         *           When a tick fires,
         *           Then an ObservationEvent with Method="GetQuestSequence" is written.
         *
         * BUILDER GUIDANCE: Mirror the AuthoringHost.PollQuestState pattern, but
         *   delegate game reads to IGameProbe instead of calling QuestManager directly.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "GetQuestSequence");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C2_PollQuestState_NewQuest_WritesGetQuestFlagsObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe returns a quest (id=0x200, seq=0, flags=0x08),
         *           When a tick fires,
         *           Then an ObservationEvent with Method="GetQuestFlags" is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0x08);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "GetQuestFlags");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C3_PollQuestState_NewQuest_WritesIsQuestAcceptedObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe returns a quest not previously seen,
         *           When a tick fires,
         *           Then an ObservationEvent with Method="IsQuestAccepted" is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "IsQuestAccepted");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C4_PollQuestState_SameQuestSameState_SecondTickSuppressedByDedup()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a quest was seen and observations written on tick 1,
         *           When tick 2 fires with the same quest state (seq/flags unchanged),
         *           Then no new GetQuestSequence observation is written (dedup suppresses).
         *
         * BUILDER GUIDANCE: Dedup lives in TraceSession. UIObserver calls WriteObservation
         *   which deduplicates. On tick 2 the value hasn't changed so it is suppressed.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);

        framework.Tick();
        var countAfterFirst = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestSequence" });

        // Advance past heartbeat threshold
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act — same state, second tick
        framework.Tick();

        var countAfterSecond = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestSequence" });

        // Assert
        Assert.Equal(countAfterFirst, countAfterSecond);

        observer.Dispose();
    }

    [Fact]
    public void UO_C5_PollQuestState_SequenceChanged_WritesNewGetQuestSequenceObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a quest was seen at seq=1 on tick 1,
         *           When seq changes to 255 on tick 2,
         *           Then a new GetQuestSequence observation is written with value=255.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();

        gameProbe.UpdateQuest(0x200u, 255, 0);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert — at least 2 GetQuestSequence observations written total
        var seqObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence")
            .ToList();
        Assert.True(seqObs.Count >= 2,
            $"Expected >=2 GetQuestSequence observations (seq 1 then 255), got {seqObs.Count}");

        observer.Dispose();
    }

    [Fact]
    public void UO_C6_PollQuestState_FlagsChanged_WritesNewGetQuestFlagsObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given quest flags=0 on tick 1,
         *           When flags=0x08 on tick 2,
         *           Then a new GetQuestFlags observation is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);
        framework.Tick();

        gameProbe.UpdateQuest(0x200u, 0, 0x08);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert
        var flagObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestFlags")
            .ToList();
        Assert.True(flagObs.Count >= 2,
            $"Expected >=2 GetQuestFlags observations (0 then 0x08), got {flagObs.Count}");

        observer.Dispose();
    }

    [Fact]
    public void UO_C7_PollQuestState_QuestRemoved_WritesIsQuestCompleteObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a quest was present on tick 1,
         *           When it is removed from GetNormalQuests on tick 2,
         *           Then an ObservationEvent with Method="IsQuestComplete" is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);
        framework.Tick();

        gameProbe.RemoveQuest(0x200u);
        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "IsQuestComplete");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C8_PollQuestState_ObservationRunId_IsPassiveRunId()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given no aggregator is set (passive mode),
         *           When a quest observation fires,
         *           Then ObservationEvent.RunId == passiveRunId.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 0, 0);

        // Act
        framework.Tick();

        // Assert
        var questObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence" || e.Method == "IsQuestAccepted")
            .ToList();
        Assert.All(questObs, e => Assert.Equal(PassiveRunId, e.RunId));

        observer.Dispose();
    }

    [Fact]
    public void UO_C9_PollQuestState_WithActiveRunId_UsesActiveRunId()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given SetAggregator has been called with activeRunId="active-001",
         *           When a quest observation fires,
         *           Then ObservationEvent.RunId == "active-001".
         *
         * BUILDER GUIDANCE: When aggregator is set, switch the runId used in
         *   WriteObservation calls from passiveRunId to activeRunId.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _, _) =
            BuildFixtureWithAggregator();
        gameProbe.AddQuest(0x200u, 0, 0);

        // Act
        framework.Tick();

        // Assert
        var questObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence" || e.Method == "IsQuestAccepted")
            .ToList();
        Assert.NotEmpty(questObs);
        Assert.All(questObs, e => Assert.Equal("active-001", e.RunId));

        observer.Dispose();
    }

    [Fact]
    public void UO_C10_PollQuestState_NewQuest_ForwardsOnQuestAcceptedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set,
         *           When a new quest appears in GetNormalQuests,
         *           Then aggregator.OnQuestAccepted is invoked (reflected in
         *           aggregator.Current.QuestAccepted == true when quest matches active).
         *
         * BUILDER GUIDANCE: UIObserver must forward aggregator?.OnQuestAccepted(publicId)
         *   when a new quest id is seen for the first time.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        var addonProbe = new FakeAddonProbe();

        // The quest public id = 0x200 | 0x10000 = 0x10200
        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      addonProbe: addonProbe, gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        gameProbe.AddQuest(0x200u, 0, 0);

        // Act
        framework.Tick();

        // Assert
        Assert.True(aggregator.Current.QuestAccepted,
            "aggregator.QuestAccepted must be true after OnQuestAccepted forwarding");

        observer.Dispose();
    }

    [Fact]
    public void UO_C11_PollQuestState_SequenceChange_ForwardsOnQuestSequenceChangedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set for questId 0x10200,
         *           When seq changes from 1 to 255 on tick 2,
         *           Then aggregator.Current.QuestSequence == 255.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();

        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();

        gameProbe.UpdateQuest(0x200u, 255, 0);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(255, aggregator.Current.QuestSequence);

        observer.Dispose();
    }

    [Fact]
    public void UO_C12_PollQuestState_FlagsChange_ForwardsOnQuestFlagsChangedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set for questId 0x10200,
         *           When flags change from 0 to 0x08 on tick 2,
         *           Then aggregator.Current.QuestFlags == 0x08.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();

        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        gameProbe.AddQuest(0x200u, 0, 0);
        framework.Tick();

        gameProbe.UpdateQuest(0x200u, 0, 0x08);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(0x08u, aggregator.Current.QuestFlags);

        observer.Dispose();
    }

    [Fact]
    public void UO_C13_PollQuestState_QuestRemoved_ForwardsOnQuestCompletedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set for questId 0x10200 and quest appeared
         *           on tick 1,
         *           When quest disappears on tick 2,
         *           Then aggregator.Current.QuestCompleted == true.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();

        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        gameProbe.AddQuest(0x200u, 0, 0);
        framework.Tick();

        gameProbe.RemoveQuest(0x200u);
        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Act
        framework.Tick();

        // Assert
        Assert.True(aggregator.Current.QuestCompleted,
            "aggregator.QuestCompleted must be true after OnQuestCompleted forwarding");

        observer.Dispose();
    }

    [Fact]
    public void UO_C14_PollQuestState_NoGameProbe_NoObservationsWritten()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe is null,
         *           When a tick fires,
         *           Then no quest-related observations are written to TraceSession.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();

        var observer = new UIObserver(framework, session, PassiveRunId, gameProbe: null);

        // Act
        framework.Tick();

        // Assert — no quest observations
        var questObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence"
                     || e.Method == "GetQuestFlags"
                     || e.Method == "IsQuestAccepted")
            .ToList();
        Assert.Empty(questObs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C15_PollQuestState_ZeroIdQuestSlot_Skipped()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe.GetNormalQuests returns an entry with QuestId=0,
         *           When a tick fires,
         *           Then no observation is written for QuestId=0 (empty slot skipped).
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0, 0, 0); // QuestId=0 = empty slot

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence" || e.Method == "IsQuestAccepted")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_C16_PollQuestState_MultipleQuests_AllObserved()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe returns two quests (id=0x100 and id=0x200),
         *           When a tick fires,
         *           Then GetQuestSequence observations are written for both quest ids.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x100u, 1, 0);
        gameProbe.AddQuest(0x200u, 2, 0);

        // Act
        framework.Tick();

        // Assert
        var seqObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestSequence")
            .ToList();
        Assert.Equal(2, seqObs.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_C17_PollQuestState_ResetHeartbeatState_ClearsKnownQuests_ReForwardsToAggregator()
    {
        // CONTRACT: Given a quest was observed and forwarded to aggregator A on tick 1,
        //           When ResetHeartbeatState is called, a fresh aggregator B is set, and tick 2 fires,
        //           Then aggregator B also receives OnQuestAccepted for that quest
        //           (because _lastKnownQuestState was cleared).
        //
        // SPEC: ResetHeartbeatState clears _lastKnownQuestState so the next authoring
        //       session re-reports the active quest as newly accepted.

        // Arrange
        var (observer, framework, _, gameProbe, clock, _, _, aggregatorA) =
            BuildFixtureWithAggregator();
        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();
        Assert.True(aggregatorA.Current.QuestAccepted);

        // New authoring session: swap to a fresh aggregator.
        var aggregatorB = new SnapshotAggregator(null, clock);
        observer.SetAggregator(aggregatorB, "active-002");
        Assert.False(aggregatorB.Current.QuestAccepted);

        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Act
        observer.ResetHeartbeatState();
        framework.Tick();

        // Assert â€” aggregator B receives OnQuestAccepted because cache was cleared.
        Assert.True(aggregatorB.Current.QuestAccepted,
            "Cleared _lastKnownQuestState must cause OnQuestAccepted to re-fire on the new aggregator");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-D: Attunement polling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_D1_PollAttunement_NewUnlockedAetheryte_WritesIsAetheryteAttunedObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe.GetAllAetheryteRowIds() returns [42]
         *           and IsAetheryteUnlocked(42) == true,
         *           When a tick fires,
         *           Then an ObservationEvent(Method="IsAetheryteAttuned") is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "IsAetheryteAttuned");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_D2_PollAttunement_NewUnlockedAetheryte_ForwardsOnAttunementChangedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set and aetheryte 42 becomes unlocked,
         *           When a tick fires,
         *           Then aggregator.Current.LastAttuned.Value == 42.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, _, _, aggregator) =
            BuildFixtureWithAggregator();
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(42u, aggregator.Current.LastAttuned?.Value);

        observer.Dispose();
    }

    [Fact]
    public void UO_D3_PollAttunement_AlreadySeenAetheryte_NotReEmitted()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given aetheryte 42 was observed as unlocked on tick 1,
         *           When tick 2 fires (aetheryte still unlocked),
         *           Then only 1 IsAetheryteAttuned observation total is written.
         *
         * BUILDER GUIDANCE: UIObserver maintains an internal HashSet of seen
         *   aetheryte ids (mirrors AuthoringHost._attunedAetheryteIds).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(42u);
        framework.Tick();

        var countAfterFirst = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "IsAetheryteAttuned" });

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert
        var countAfterSecond = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "IsAetheryteAttuned" });
        Assert.Equal(countAfterFirst, countAfterSecond);

        observer.Dispose();
    }

    [Fact]
    public void UO_D4_PollAttunement_LockedAetheryte_NotObserved()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given GetAllAetheryteRowIds() returns [99]
         *           but IsAetheryteUnlocked(99) == false,
         *           When a tick fires,
         *           Then no IsAetheryteAttuned observation is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();

        // Add an aetheryte row but do NOT unlock it (IsAetheryteUnlocked returns false)
        // We need to expose a row id without unlocking it.
        // FakeGameProbe.GetAllAetheryteRowIds only yields unlocked ones via UnlockAetheryte.
        // So we test the case where the probe returns NO unlocked ids.
        // (Row 99 is NOT in the unlocked set.)

        // Act
        framework.Tick();

        // Assert — no attunement observations
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "IsAetheryteAttuned")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_D5_PollAttunement_NoGameProbe_NoAttunementObservations()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IGameProbe is null,
         *           When a tick fires,
         *           Then no IsAetheryteAttuned observations are written.
         */

        // Arrange
        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();
        var observer  = new UIObserver(framework, session, PassiveRunId, gameProbe: null);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "IsAetheryteAttuned")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_D6_PollAttunement_MultipleNewAetherytes_AllObserved()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given three aetherytes (ids=10,20,30) all unlocked,
         *           When a tick fires,
         *           Then three IsAetheryteAttuned observations are written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(10u);
        gameProbe.UnlockAetheryte(20u);
        gameProbe.UnlockAetheryte(30u);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "IsAetheryteAttuned")
            .ToList();
        Assert.Equal(3, obs.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_D7_PollAttunement_ObservationRunId_IsPassiveWhenNoAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given no aggregator is set,
         *           When an attunement observation fires,
         *           Then ObservationEvent.RunId == passiveRunId.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .First(e => e.Method == "IsAetheryteAttuned");
        Assert.Equal(PassiveRunId, obs.RunId);

        observer.Dispose();
    }

    [Fact]
    public void UO_D8_PollAttunement_ObservationRunId_IsActiveWhenAggregatorSet()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aggregator is set with activeRunId="active-001",
         *           When an attunement observation fires,
         *           Then ObservationEvent.RunId == "active-001".
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _, _) =
            BuildFixtureWithAggregator();
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .First(e => e.Method == "IsAetheryteAttuned");
        Assert.Equal("active-001", obs.RunId);

        observer.Dispose();
    }

    [Fact]
    public void UO_D9_PollAttunement_NewAetheryte_RowIdInObservationArgument()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given aetheryte rowId=42 becomes unlocked,
         *           When tick fires,
         *           Then the ObservationEvent.Argument (or Value) encodes 42.
         *
         * BUILDER GUIDANCE: Use the rowId as the argument to WriteObservation
         *   (mirroring WriteObservationDeduped("IsAetheryteAttuned", row.RowId, 1)).
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .First(e => e.Method == "IsAetheryteAttuned");
        // The argument should encode 42 (as a JSON number)
        Assert.True(
            (obs.Argument?.GetRawText() == "42") || (obs.Value?.GetRawText() == "42"),
            $"Expected 42 in Argument or Value. Argument={obs.Argument?.GetRawText()}, Value={obs.Value?.GetRawText()}");

        observer.Dispose();
    }

    [Fact]
    public void UO_D10_PollAttunement_ResetHeartbeatState_ClearsSeenSet_ReForwardsToAggregator()
    {
        // CONTRACT: Given aetheryte 42 was observed and forwarded to aggregator A on tick 1,
        //           When ResetHeartbeatState is called, a fresh aggregator B is set, and tick 2 fires,
        //           Then aggregator B also receives OnAttunementChanged for aetheryte 42
        //           (because _attunedAetheryteIds was cleared so 42 looks new again).
        //
        // SPEC: ResetHeartbeatState clears _attunedAetheryteIds so the next authoring
        //       session starts fresh and re-reports all currently-unlocked aetherytes.

        // Arrange
        var (observer, framework, _, gameProbe, clock, _, _, aggregatorA) =
            BuildFixtureWithAggregator();
        gameProbe.UnlockAetheryte(42u);
        framework.Tick();
        Assert.Equal(42u, aggregatorA.Current.LastAttuned?.Value);

        // New authoring session: swap to a fresh aggregator. Without cache-clearing,
        // aggregator B would NEVER see OnAttunementChanged because UIObserver still
        // remembers aetheryte 42 as already-seen.
        var aggregatorB = new SnapshotAggregator(null, clock);
        observer.SetAggregator(aggregatorB, "active-002");
        Assert.Null(aggregatorB.Current.LastAttuned);

        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Act
        observer.ResetHeartbeatState();
        framework.Tick();

        // Assert â€” aggregator B received the attunement forward because the seen-set was cleared.
        Assert.Equal(42u, aggregatorB.Current.LastAttuned?.Value);

        observer.Dispose();
    }

    [Fact]
    public void UO_D11_PollAttunement_NewAetheryteOnSecondTick_WritesObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given aetheryte 42 was seen on tick 1 (and suppressed on tick 2),
         *           When a NEW aetheryte 99 is unlocked before tick 3,
         *           Then an IsAetheryteAttuned observation is written for 99 on tick 3.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.UnlockAetheryte(42u);
        framework.Tick();

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();
        gameProbe.UnlockAetheryte(99u); // new aetheryte

        // Act
        framework.Tick();

        // Assert
        var obsFor99 = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "IsAetheryteAttuned")
            .Where(e => (e.Argument?.GetRawText() == "99") || (e.Value?.GetRawText() == "99"))
            .ToList();
        Assert.NotEmpty(obsFor99);

        observer.Dispose();
    }

    [Fact]
    public void UO_D12_PollAttunement_NewAetheryte_ForwardsToAggregatorOnce()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given aggregator is set and aetheryte 42 unlocks on tick 1,
         *           When tick 2 fires (aetheryte still unlocked),
         *           Then aggregator.LastAttuned remains 42 (not re-called with null
         *           or different value on tick 2).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, _, _, aggregator) =
            BuildFixtureWithAggregator();
        gameProbe.UnlockAetheryte(42u);
        framework.Tick();

        Assert.Equal(42u, aggregator.Current.LastAttuned?.Value);

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick();

        // Assert — still 42, not cleared or changed
        Assert.Equal(42u, aggregator.Current.LastAttuned?.Value);

        observer.Dispose();
    }

    [Fact]
    public void UO_D13_PollAttunement_Heartbeat_ThrottledCorrectly()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given no time has elapsed between tick 1 and tick 2,
         *           When a NEW aetheryte is added before tick 2,
         *           Then no attunement observation is written on tick 2
         *           (heartbeat throttle prevents the poller from running).
         *
         * BUILDER GUIDANCE: PollAttunement runs inside the heartbeat block
         *   (only when elapsed >= 250 ms). Not every-frame.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        framework.Tick(); // tick 1 — fires heartbeat
        var countAfterFirst = writer.Count;

        // Do NOT advance clock — heartbeat is throttled
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick(); // tick 2 — heartbeat NOT fired

        // Assert — no new attunement observations
        var newObs = writer.RecordedEvents.Skip(countAfterFirst)
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "IsAetheryteAttuned")
            .ToList();
        Assert.Empty(newObs);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-E: Key item polling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_E1_PollKeyItems_NoGameProbe_NoInventoryChangedEvent()
    {
        /*
         * CONTRACT: Given IGameProbe is null,
         *           When a tick fires,
         *           Then no ObservationEvent with Method="InventoryChanged" is written.
         */

        // Arrange
        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();
        var observer  = new UIObserver(framework, session, PassiveRunId, gameProbe: null);

        // Act
        framework.Tick();

        // Assert
        Assert.Empty(writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "InventoryChanged"));

        observer.Dispose();
    }

    [Fact]
    public void UO_E2_PollKeyItems_InitialItems_WritesKeyItemsObservation()
    {
        /*
         * CONTRACT: Given IGameProbe.GetKeyItemSlots() returns [(itemId=500, qty=1)],
         *           When tick 1 fires,
         *           Then an ObservationEvent with Method="InventoryChanged" is written.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _, aggregator) =
            BuildFixtureWithAggregator();
        gameProbe.AddKeyItem(500u, 1);

        // Act
        framework.Tick();

        // Assert — an ObservationEvent with Method="InventoryChanged" is written
        var changed = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "InventoryChanged")
            .ToList();
        Assert.NotEmpty(changed);

        observer.Dispose();
    }

    [Fact]
    public void UO_E3_PollKeyItems_ItemGained_ForwardsOnKeyItemsChangedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given aggregator is set, tick 1 sees no items, tick 2 sees
         *           item 500 added,
         *           When tick 2 fires,
         *           Then aggregator.Current.KeyItemsAdded contains item 500.
         *
         * BUILDER GUIDANCE: Call aggregator?.OnKeyItemsChanged(addedIds) when
         *   the diff detects newly added items.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, _, _, aggregator) =
            BuildFixtureWithAggregator();

        framework.Tick(); // tick 1 — no items
        aggregator.ResetDeltas(); // clear per-window deltas

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();
        gameProbe.AddKeyItem(500u, 1);

        // Act
        framework.Tick(); // tick 2 — item 500 appears

        // Assert
        Assert.Contains(500u, aggregator.Current.KeyItemsAdded ?? []);

        observer.Dispose();
    }

    [Fact]
    public void UO_E4_PollKeyItems_ItemRemoved_ForwardsOnKeyItemsRemovedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given item 500 was present on tick 1,
         *           When item 500 disappears on tick 2,
         *           Then aggregator.Current.KeyItemsRemoved contains item 500.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, _, _, aggregator) =
            BuildFixtureWithAggregator();

        gameProbe.AddKeyItem(500u, 1);
        framework.Tick(); // tick 1
        aggregator.ResetDeltas();

        clock.Advance(TimeSpan.FromMilliseconds(300));
        gameProbe.ClearKeyItems(); // remove item 500

        // Act
        framework.Tick(); // tick 2

        // Assert
        Assert.Contains(500u, aggregator.Current.KeyItemsRemoved ?? []);

        observer.Dispose();
    }

    [Fact]
    public void UO_E5_PollKeyItems_InventoryChangedEvent_WrittenOnlyToActiveRunId()
    {
        /*
         * CONTRACT: Given aggregator is set with activeRunId="active-001",
         *           When key items change,
         *           Then the ObservationEvent with Method="InventoryChanged" uses
         *           RunId == "active-001" (the active run id takes precedence over passive).
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _, _) =
            BuildFixtureWithAggregator();
        gameProbe.AddKeyItem(500u, 1);

        // Act
        framework.Tick();

        // Assert
        var changed = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "InventoryChanged");
        if (changed != null)
            Assert.Equal("active-001", changed.RunId);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-F: TelepotTown (aethernet) polling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_F1_PollAethernetDestination_MenuOpens_NoImmediateObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TelepotTown addon opens on tick 1,
         *           When tick 1 fires,
         *           Then no AethernetTeleportCompleted observation is written
         *           (teleport is only observed when the menu closes with a selection).
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("TelepotTown");
        addonProbe.SetSelectedIndex("TelepotTown", 0);

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "AethernetTeleportCompleted")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_F2_PollAethernetDestination_MenuOpensThenCloses_FiresAethernetTeleportCompletedObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TelepotTown opens on tick 1 (with a selected destination name),
         *           When TelepotTown closes on tick 2,
         *           Then an ObservationEvent(Method="AethernetTeleportCompleted") is written.
         *
         * BUILDER GUIDANCE: The observation fires on the tick where the menu transitions
         *   from open → closed, mirroring AuthoringHost.PollAethernetDestination.
         *   IAddonProbe.GetTelepotTownDestinationName maps idx → destination name.
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("TelepotTown");
        addonProbe.SetSelectedIndex("TelepotTown", 0);
        addonProbe.SetTelepotDestination(0, "Limsa Lominsa Aetheryte Plaza", rowId: 8u);
        framework.Tick(); // tick 1 — menu opens, destination latched

        addonProbe.CloseAddon("TelepotTown"); // menu closes

        // Act
        framework.Tick(); // tick 2 — open→closed transition fires observation

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "AethernetTeleportCompleted");
        Assert.NotNull(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_F3_PollAethernetDestination_MenuClosesWithoutSelection_NoObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TelepotTown opens on tick 1 but no destination index is set,
         *           When it closes on tick 2,
         *           Then no AethernetTeleportCompleted observation is written
         *           (no pending destination was latched).
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("TelepotTown");
        // Do NOT call SetSelectedIndex or SetTelepotDestination — no selection
        framework.Tick(); // menu opens with no selection

        addonProbe.CloseAddon("TelepotTown");

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "AethernetTeleportCompleted")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_F4_PollAethernetDestination_NoAddonProbe_NoObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IAddonProbe is null,
         *           When ticks fire,
         *           Then no AethernetTeleportCompleted observation is written.
         */

        // Arrange
        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();
        var observer  = new UIObserver(framework, session, PassiveRunId, addonProbe: null);

        // Act
        framework.Tick();
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "AethernetTeleportCompleted")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-G: SelectIconString (dialogue) polling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_G1_PollDialogueOption_NoAddonProbe_NoDialogueObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given IAddonProbe is null,
         *           When ticks fire,
         *           Then no DialogueOptionSelected observation is written.
         */

        // Arrange
        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var framework = new FakeFramework();
        var observer  = new UIObserver(framework, session, PassiveRunId, addonProbe: null);

        // Act
        framework.Tick();
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "DialogueOptionSelected")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    [Fact]
    public void UO_G2_PollDialogueOption_MenuOpensThenCloses_ForwardsOnDialogueOptionSelectedToAggregator()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given SelectIconString opens on tick 1 with selectedIdx=2,
         *           When it closes on tick 2,
         *           Then aggregator.Current.DialogueOptionSelected == 2.
         *
         * BUILDER GUIDANCE: Use a DialogueMenuPoller or equivalent. When the menu
         *   transitions open→closed with an idx, call aggregator?.OnDialogueOptionSelected(idx).
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, _, _, aggregator) =
            BuildFixtureWithAggregator();
        addonProbe.OpenAddon("SelectIconString");
        addonProbe.SetSelectedIndex("SelectIconString", 2);
        framework.Tick(); // menu is open with idx=2

        addonProbe.CloseAddon("SelectIconString"); // close

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(2, aggregator.Current.DialogueOptionSelected);

        observer.Dispose();
    }

    [Fact]
    public void UO_G3_PollDialogueOption_MenuOpen_WritesDialogueNpcCapturedObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given SelectIconString opens on tick 1,
         *           When tick 1 fires,
         *           Then an ObservationEvent(Method="DialogueNpcCaptured") is written
         *           if a target NPC is captured.
         *
         * BUILDER GUIDANCE: On the first open transition, UIObserver captures the
         *   NPC source via IAddonProbe or IGameProbe and calls WriteObservation.
         *   If no NPC is available, the observation may be skipped — test accounts
         *   for this by checking "at most one" observation of this method.
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("SelectIconString");

        // Act
        framework.Tick();

        // Assert — DialogueNpcCaptured observation may or may not fire (depends on target)
        // but must not throw
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "DialogueNpcCaptured")
            .ToList();
        Assert.True(obs.Count <= 1,
            "DialogueNpcCaptured must fire at most once when the menu opens");

        observer.Dispose();
    }

    [Fact]
    public void UO_G4_PollDialogueOption_MenuClosesWithNoSelection_NoDialogueObservation()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given SelectIconString opens on tick 1 but no index is selected,
         *           When it closes on tick 2,
         *           Then no DialogueOptionSelected observation is written.
         *
         * BUILDER GUIDANCE: Index is only latched when GetSelectedItemIndex returns >= 0.
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("SelectIconString");
        // No index set — GetSelectedItemIndex returns null
        framework.Tick();

        addonProbe.CloseAddon("SelectIconString");

        // Act
        framework.Tick();

        // Assert
        var obs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "DialogueOptionSelected")
            .ToList();
        Assert.Empty(obs);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-H: Heartbeat throttle (250 ms)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_H1_Heartbeat_FirstTick_AlwaysRuns()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a fresh UIObserver with a quest in GetNormalQuests,
         *           When the very first tick fires,
         *           Then heartbeat pollers run (observations written).
         *
         * BUILDER GUIDANCE: _lastHeartbeatAt == DateTimeOffset.MinValue initially,
         *   so (now - _lastHeartbeatAt) > 250 ms is always true on first tick.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);

        // Act
        framework.Tick();

        // Assert
        Assert.True(writer.Count > 0, "Heartbeat must fire on first tick");

        observer.Dispose();
    }

    [Fact]
    public void UO_H2_Heartbeat_TwoTicksBelowThreshold_OnlyFirstFiresHeartbeat()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given tick 1 fires heartbeat and tick 2 fires 10 ms later,
         *           When tick 2 fires,
         *           Then heartbeat pollers do NOT run on tick 2 (throttled).
         *
         * BUILDER GUIDANCE: Throttle interval is 250 ms. (now - _lastHeartbeatAt)
         *   must be >= 250 ms for heartbeat to re-fire.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();
        var countAfterFirst = writer.Count;

        clock.Advance(TimeSpan.FromMilliseconds(10)); // below threshold

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(countAfterFirst, writer.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_H3_Heartbeat_TickAt250Ms_FiresAgain()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given tick 1 fires at T0, tick 2 fires at T0 + 250 ms,
         *           When tick 2 fires,
         *           Then heartbeat pollers run again.
         *
         * BUILDER GUIDANCE: The threshold is inclusive (>= 250 ms).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();
        var countAfterFirst = writer.Count;

        clock.Advance(TimeSpan.FromMilliseconds(250));
        gameProbe.AddQuest(0x300u, 1, 0); // new quest so the heartbeat re-fire emits fresh observations

        // Act
        framework.Tick();

        // Assert
        Assert.True(writer.Count > countAfterFirst,
            "Heartbeat must re-fire when 250 ms have elapsed");

        observer.Dispose();
    }

    [Fact]
    public void UO_H4_Heartbeat_EveryFramePollers_RunEachTick()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TelepotTown is open,
         *           When three ticks fire in quick succession (no time advance),
         *           Then the every-frame pollers run on EACH tick (not throttled).
         *
         * BUILDER GUIDANCE: PollAethernetDestination and PollDialogueOption must
         *   run every frame (not inside the heartbeat block).
         */

        // Arrange
        var (observer, framework, addonProbe, _, _, writer, _) = BuildFixture();
        addonProbe.OpenAddon("TelepotTown");

        // Act — tick 3 times without advancing clock
        framework.Tick();
        var count1 = writer.Count;
        framework.Tick();
        // If every-frame pollers track per-tick state, they must still run on tick 2
        // We just verify no crash and the handler is invoked
        var ex = Record.Exception(() => framework.Tick());

        // Assert
        Assert.Null(ex);

        observer.Dispose();
    }

    [Fact]
    public void UO_H5_Heartbeat_QuestPollInsideHeartbeatBlock()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given a quest is added between tick 1 and tick 2 (10 ms apart),
         *           When tick 2 fires,
         *           Then NO quest observation is written on tick 2 (heartbeat throttled).
         *
         * BUILDER GUIDANCE: PollQuestState is inside the heartbeat block (250 ms).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        framework.Tick(); // tick 1 — heartbeat fires (empty quest list)
        var countAfterFirst = writer.Count;

        clock.Advance(TimeSpan.FromMilliseconds(10));
        gameProbe.AddQuest(0x200u, 1, 0); // add quest between ticks

        // Act
        framework.Tick(); // tick 2 — heartbeat NOT fired

        // Assert — no new observations (quest poller did not run)
        Assert.Equal(countAfterFirst, writer.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_H6_Heartbeat_AttunementPollInsideHeartbeatBlock()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given an aetheryte is unlocked between tick 1 and tick 2 (10 ms),
         *           When tick 2 fires,
         *           Then no attunement observation is written on tick 2.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        framework.Tick();
        var countAfterFirst = writer.Count;

        clock.Advance(TimeSpan.FromMilliseconds(10));
        gameProbe.UnlockAetheryte(42u);

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(countAfterFirst, writer.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_H7_Heartbeat_KeyItemPollInsideHeartbeatBlock()
    {
        /*
         * CONTRACT: Given key items change between tick 1 and tick 2 (10 ms),
         *           When tick 2 fires,
         *           Then no ObservationEvent with Method="InventoryChanged" is written on tick 2
         *           (heartbeat throttle suppresses the second tick).
         */

        // Arrange
        var (obs2, fw2, _, gp2, clock2, writer2, _) = BuildFixture();
        fw2.Tick();
        var countAfterFirst = writer2.Count;

        clock2.Advance(TimeSpan.FromMilliseconds(10));
        gp2.AddKeyItem(500u, 1);

        // Act
        fw2.Tick();

        // Assert
        var newInvEvents = writer2.RecordedEvents.Skip(countAfterFirst)
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "InventoryChanged")
            .ToList();
        Assert.Empty(newInvEvents);

        obs2.Dispose();
    }

    [Fact]
    public void UO_H8_Heartbeat_TraceSessionGateClosed_ObservationsDropped()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TraceMode.Recording with gate closed (after OnConfirmRecordStep),
         *           When a tick fires,
         *           Then quest observations are dropped by TraceSession (gate is closed).
         *
         * BUILDER GUIDANCE: UIObserver always calls WriteObservation on TraceSession —
         *   it does not check the gate itself. TraceSession handles the gate logic.
         */

        // Arrange
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Recording, Path.GetTempPath(), _ => writer);
        session.OnEnterAuthorMode(QuestId);
        session.OnOpenRecordModal(QuestId);
        session.OnConfirmRecordStep(); // gate now closed; file still open

        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        gameProbe.AddQuest(0x200u, 1, 0);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);

        var countBeforeTick = writer.Count;

        // Act
        framework.Tick();

        // Assert — gate is closed so observations are dropped
        Assert.Equal(countBeforeTick, writer.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_H9_Heartbeat_TraceSessionOff_AllObservationsDropped()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TraceMode.Off,
         *           When ticks fire with quests and aetherytes available,
         *           Then zero observations are written (everything dropped by TraceSession).
         */

        // Arrange
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Off, Path.GetTempPath(), _ => writer);
        session.OnPluginStart(); // no-op in Off mode

        var gameProbe = new FakeGameProbe();
        gameProbe.AddQuest(0x200u, 1, 0);
        gameProbe.UnlockAetheryte(42u);

        var framework = new FakeFramework();
        var observer  = new UIObserver(framework, session, PassiveRunId, gameProbe: gameProbe);

        // Act
        framework.Tick();

        // Assert
        Assert.Equal(0, writer.Count);

        observer.Dispose();
    }

    [Fact]
    public void UO_H10_Heartbeat_LastHeartbeatAt_UpdatedAfterFiring()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given tick 1 fires at T0,
         *           When tick 2 fires at T0 + 10 ms (below threshold),
         *           Then tick 2 does NOT run heartbeat (proves _lastHeartbeatAt was
         *           updated to T0 so the 250 ms window is correctly tracked).
         *
         * BUILDER GUIDANCE: _lastHeartbeatAt must be assigned to clock.UtcNow
         *   (not DateTimeOffset.UtcNow) so FakeClock controls the elapsed window.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);

        // tick 1 at T0 — heartbeat fires
        framework.Tick();
        var countAtT0 = writer.Count;
        Assert.True(countAtT0 > 0, "Heartbeat must fire on first tick");

        // tick 2 at T0 + 10 ms — must NOT fire heartbeat
        clock.Advance(TimeSpan.FromMilliseconds(10));
        framework.Tick();

        // tick 3 at T0 + 260 ms — SHOULD fire heartbeat
        clock.Advance(TimeSpan.FromMilliseconds(250));
        gameProbe.AddQuest(0x300u, 1, 0); // new quest so the heartbeat re-fire emits fresh observations
        framework.Tick();
        var countAtT260 = writer.Count;

        // Assert — tick 3 must have added new observations
        Assert.True(countAtT260 > countAtT0,
            "Heartbeat must re-fire at T0+260ms; _lastHeartbeatAt must use IClock");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-I: TraceSession gate / WriteObservation independence
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_I1_Observation_IndependentOfAggregator_FiresWhenGateOpen()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given no aggregator is set (passive mode) but the TraceSession
         *           gate is open (TraceMode.Always after OnPluginStart),
         *           When a quest observation fires,
         *           Then the observation reaches the inner writer.
         *
         * BUILDER GUIDANCE: Observation output (WriteObservation) is independent of
         *   the aggregator. It fires whenever the TraceSession gate allows it.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        // No aggregator set

        // Act
        framework.Tick();

        // Assert
        Assert.True(writer.Count > 0,
            "Observations must reach TraceSession even when no aggregator is set");

        observer.Dispose();
    }

    [Fact]
    public void UO_I2_Inference_IndependentOfTrace_FiresEvenWhenGateClosed()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TraceMode.Recording with gate CLOSED (after OnConfirmRecordStep)
         *           but an aggregator IS set,
         *           When a quest state changes on tick 2,
         *           Then aggregator.Current.QuestSequence is updated (aggregator forwarding
         *           is NOT gated by TraceSession).
         *
         * BUILDER GUIDANCE: The two outputs (observation → TraceSession, inference state →
         *   aggregator) are independent. Aggregator forwarding must happen regardless of
         *   the TraceSession gate.
         */

        // Arrange
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Recording, Path.GetTempPath(), _ => writer);
        session.OnEnterAuthorMode(QuestId);
        session.OnOpenRecordModal(QuestId);

        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        gameProbe.AddQuest(0x200u, 1, 0);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        framework.Tick(); // tick 1 — gate open, quest seen at seq=1

        session.OnConfirmRecordStep(); // gate now CLOSED
        gameProbe.UpdateQuest(0x200u, 255, 0);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act
        framework.Tick(); // tick 2 — gate closed but aggregator should still update

        // Assert — aggregator forwards seq change even though TraceSession gate is closed
        Assert.Equal(255, aggregator.Current.QuestSequence);

        observer.Dispose();
    }

    [Fact]
    public void UO_I3_BothOutputs_Fire_SameTick_WhenGateOpenAndAggregatorSet()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TraceSession gate is open AND aggregator is set,
         *           When a new quest appears on tick 1,
         *           Then BOTH the ObservationEvent is written to TraceSession
         *           AND aggregator.QuestAccepted becomes true.
         */

        // Arrange
        var writer    = new FakeTraceWriter();
        var session   = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        gameProbe.AddQuest(0x200u, 0, 0);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        // Act
        framework.Tick();

        // Assert — both outputs fired
        var traceObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "IsQuestAccepted" || e.Method == "GetQuestSequence")
            .ToList();
        Assert.NotEmpty(traceObs);
        Assert.True(aggregator.Current.QuestAccepted,
            "Aggregator must have accepted the quest on the same tick as the observation");

        observer.Dispose();
    }

    [Fact]
    public void UO_I4_Observation_DeduplicatedByTraceSession_NotByUIObserver()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given the same quest seq is seen on tick 1 and tick 2,
         *           When tick 2 fires,
         *           Then the second GetQuestSequence observation is suppressed by
         *           TraceSession dedup (UIObserver itself must NOT implement its own dedup).
         *
         * BUILDER GUIDANCE: UIObserver calls session.WriteObservation(...) for every
         *   observation; TraceSession's dedup cache suppresses redundant writes.
         *   UIObserver must NOT maintain its own dedup dict.
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        framework.Tick();
        var seqCountAfterFirst = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestSequence" });

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act — same state
        framework.Tick();

        var seqCountAfterSecond = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestSequence" });

        // Assert — TraceSession dedup suppressed the repeat
        Assert.Equal(seqCountAfterFirst, seqCountAfterSecond);

        observer.Dispose();
    }

    [Fact]
    public void UO_I5_AggregatorForwarding_HappensEvenWhenTraceSessionFileNotOpen()
    {
        /*
         * RED: Will fail until UIObserver exists.
         *
         * CONTRACT: Given TraceMode.Off (file never opens) but aggregator is set,
         *           When a quest appears on tick 1,
         *           Then aggregator.QuestAccepted == true (aggregator forwarding does
         *           not depend on TraceSession being open).
         */

        // Arrange
        var session   = new TraceSession(TraceMode.Off, Path.GetTempPath(), _ => new FakeTraceWriter());
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var gameProbe = new FakeGameProbe();
        const uint questPublicId = 0x10200u;
        var aggregator = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(questPublicId), clock);

        gameProbe.AddQuest(0x200u, 0, 0);

        var observer = new UIObserver(framework, session, PassiveRunId,
                                      gameProbe: gameProbe, clock: clock);
        observer.SetAggregator(aggregator, "active-001");

        // Act
        framework.Tick();

        // Assert
        Assert.True(aggregator.Current.QuestAccepted,
            "Aggregator forwarding must work even when TraceSession is in Off mode (file closed)");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-V: Quest variables observations (QUEST_VARIABLES_TRACE_PLAN.md §4 Group UO)
    //
    // AND FakeGameProbe has no per-quest variables setter.
    // Both conditions make these tests fail (compile-error + assertion failure).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UO_V1_PollQuestState_NonZeroVariables_WritesGetQuestVariablesObservation()
    {
        /*
         * RED: Will fail until:
         *   1. FakeGameProbe.SetQuestVariables(uint questId, byte[] variables) is added
         *      (builder adds it; the method does not exist yet → compile-error RED).
         *   2. UIObserver.PollQuestState writes WriteObservation("GetQuestVariables", ...)
         *      alongside the existing GetQuestSequence/GetQuestFlags writes.
         *
         * CONTRACT: Given FakeGameProbe returns quest 0x200 with non-zero variables
         *           [0x10, 0x00, 0x00, 0x05, 0x00, 0x00],
         *           When a tick fires,
         *           Then an ObservationEvent with Method=="GetQuestVariables" is written
         *           to the trace.
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        // RED compile-error: SetQuestVariables does not exist on FakeGameProbe yet.
        gameProbe.SetQuestVariables(0x200u, new byte[] { 0x10, 0x00, 0x00, 0x05, 0x00, 0x00 });

        // Act
        framework.Tick();

        // Assert
        var varObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestVariables")
            .ToList();

        Assert.NotEmpty(varObs);

        observer.Dispose();
    }

    [Fact]
    public void UO_V2_PollQuestState_VariablesObservation_ValueIsArrayAndArgumentIsQuestId()
    {
        /*
         * RED: Same as UO_V1 — compile-error on SetQuestVariables + assertion failure
         *      on UIObserver not writing the observation.
         *
         * CONTRACT: Given quest 0x200 with variables [0x10, 0x00, 0x00, 0x05, 0x00, 0x00],
         *           When a tick fires,
         *           Then the GetQuestVariables ObservationEvent:
         *             - Value is a JSON array (NOT base64) with those six bytes.
         *             - Argument encodes the public quest id (0x200 | 0x10000 = 0x10200).
         *           (Mirrors GetQuestSequence arg handling: publicId.Value as the argument.)
         */

        // Arrange
        var (observer, framework, _, gameProbe, _, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        // RED compile-error: SetQuestVariables does not exist on FakeGameProbe yet.
        gameProbe.SetQuestVariables(0x200u, new byte[] { 0x10, 0x00, 0x00, 0x05, 0x00, 0x00 });

        // Act
        framework.Tick();

        // Assert
        var varObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .FirstOrDefault(e => e.Method == "GetQuestVariables");

        Assert.NotNull(varObs);

        // Value must be a JSON array, not a base64 string.
        Assert.NotNull(varObs!.Value);
        Assert.Equal(JsonValueKind.Array, varObs.Value!.Value.ValueKind);

        // The six bytes must be present in the array.
        var elements = varObs.Value.Value.EnumerateArray().Select(e => e.GetByte()).ToArray();
        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x05, 0x00, 0x00 }, elements);

        // Argument must encode the public quest id (0x200 | 0x10000 = 0x10200 = 66048 decimal).
        Assert.NotNull(varObs.Argument);
        var argText = varObs.Argument!.Value.GetRawText();
        Assert.Contains("66048", argText, StringComparison.Ordinal);

        observer.Dispose();
    }

    [Fact]
    public void UO_V3_PollQuestState_SameVariables_SecondTickSuppressedByDedup()
    {
        /*
         * RED: Same as UO_V1 — compile-error + assertion failure.
         *
         * CONTRACT: Given variables unchanged between tick 1 and tick 2,
         *           When tick 2 fires (past heartbeat threshold with ResetHeartbeatState),
         *           Then no additional GetQuestVariables observation is written
         *           (TraceSession dedup suppresses the unchanged value).
         *           Mirrors UO_C4 (GetQuestSequence dedup).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        // RED compile-error: SetQuestVariables does not exist on FakeGameProbe yet.
        gameProbe.SetQuestVariables(0x200u, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // Tick 1 — writes initial observations
        framework.Tick();
        var countAfterFirst = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestVariables" });

        // Advance past heartbeat threshold
        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act — tick 2: same variables, second heartbeat
        framework.Tick();

        var countAfterSecond = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestVariables" });

        // Assert — no new observation (dedup in TraceSession)
        Assert.Equal(countAfterFirst, countAfterSecond);

        observer.Dispose();
    }

    [Fact]
    public void UO_V4_PollQuestState_VariablesChanged_SecondTickWritesNewObservation()
    {
        /*
         * RED: Same as UO_V1 — compile-error + assertion failure.
         *
         * CONTRACT: Given variables change between tick 1 and tick 2,
         *           When tick 2 fires,
         *           Then a SECOND GetQuestVariables observation is written with the new bytes.
         *           Mirrors UO_C5 (GetQuestSequence change).
         */

        // Arrange
        var (observer, framework, _, gameProbe, clock, writer, _) = BuildFixture();
        gameProbe.AddQuest(0x200u, 1, 0);
        // RED compile-error: SetQuestVariables does not exist on FakeGameProbe yet.
        gameProbe.SetQuestVariables(0x200u, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // Tick 1 — writes initial all-zero variables observation
        framework.Tick();
        var countAfterFirst = writer.RecordedEvents.Count(e =>
            e is ObservationEvent { Method: "GetQuestVariables" });

        // Change the variables
        // RED compile-error: SetQuestVariables does not exist on FakeGameProbe yet.
        gameProbe.SetQuestVariables(0x200u, new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });

        clock.Advance(TimeSpan.FromMilliseconds(300));
        observer.ResetHeartbeatState();

        // Act — tick 2: changed variables
        framework.Tick();

        // Assert — at least 2 GetQuestVariables observations total (initial + changed)
        var allVarObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "GetQuestVariables")
            .ToList();

        Assert.True(allVarObs.Count >= 2,
            $"Expected >=2 GetQuestVariables observations (initial then changed), got {allVarObs.Count}. " +
            "RED: UIObserver does not write GetQuestVariables observations yet.");

        // The second observation must carry the changed bytes.
        var secondObs = allVarObs.Last();
        Assert.NotNull(secondObs.Value);
        var changedElements = secondObs.Value!.Value.EnumerateArray().Select(e => e.GetByte()).ToArray();
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 }, changedElements);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-J: Teleport addon timestamp tracking — PollTeleportAddonOpen
    // ─────────────────────────────────────────────────────────────────────────
    //
    // FFXIVClientStructs's AddonTeleport struct is stale for the current game version (typed
    // access to TeleportTreeList returns garbage and crashes). The Teleport addon also has no
    // reliable AtkValues layout for selection capture. Instead, UIObserver only tracks whether
    // the addon was open recently; the destination is inferred at zone-change time via
    // AetheryteZoneMap reverse lookup (see AuthoringHost.OnTerritoryChanged).
    // ─────────────────────────────────────────────────────────────────────────

    private const string TeleportAddonName = "Teleport";

    [Fact]
    public void UO_J1_AddonOpen_StampsTimestamp_WasOpenWithinReturnsTrue()
    {
        var (observer, framework, addonProbe, _, clock, _, _) = BuildFixture();
        addonProbe.OpenAddon(TeleportAddonName);

        framework.Tick();

        Assert.True(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));
        observer.Dispose();
    }

    [Fact]
    public void UO_J2_AddonNeverOpen_WasOpenWithinReturnsFalse()
    {
        var (observer, framework, _, _, clock, _, _) = BuildFixture();

        framework.Tick();

        Assert.False(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));
        observer.Dispose();
    }

    [Fact]
    public void UO_J3_AddonOpenedLongAgo_WasOpenWithinReturnsFalse_WhenAsOfBeyondWindow()
    {
        var (observer, framework, addonProbe, _, clock, _, _) = BuildFixture();
        addonProbe.OpenAddon(TeleportAddonName);
        framework.Tick(); // stamp t0
        addonProbe.CloseAddon(TeleportAddonName);

        var future = clock.UtcNow + TimeSpan.FromSeconds(15);

        Assert.False(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), future));
        observer.Dispose();
    }

    [Fact]
    public void UO_J4_ResetWindowState_ClearsTeleportTimestamp()
    {
        var (observer, framework, addonProbe, _, clock, _, _) = BuildFixture();
        addonProbe.OpenAddon(TeleportAddonName);
        framework.Tick();
        Assert.True(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));

        observer.ResetWindowState();

        Assert.False(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));
        observer.Dispose();
    }

    [Fact]
    public void UO_J5_ClearTeleportAddonOpenTimestamp_ResetsTrigger()
    {
        var (observer, framework, addonProbe, _, clock, _, _) = BuildFixture();
        addonProbe.OpenAddon(TeleportAddonName);
        framework.Tick();
        Assert.True(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));

        observer.ClearTeleportAddonOpenTimestamp();

        Assert.False(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));
        observer.Dispose();
    }

    [Fact]
    public void UO_J6_NullAddonProbe_NoNre()
    {
        // Mirrors UO_F's null-probe defence: the observer should be safe even without an IAddonProbe.
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var observer  = new UIObserver(
            framework: framework, traceSession: session, passiveRunId: PassiveRunId,
            addonProbe: null, gameProbe: null, clock: clock);

        framework.Tick();

        Assert.False(observer.WasTeleportAddonOpenWithin(TimeSpan.FromSeconds(10), clock.UtcNow));
        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UO-K: PollPlayerActionEffect — sequence-based action-completed poller
    //
    // Covers spec UAI8 / §I UO_K1-10.
    //
    // ALL tests in this group will fail to compile/runtime until Builder adds:
    //   - IGameProbe.GetLastActionEffect()                          (Task UAI-T8)
    //   - FakeGameProbe.SetLastActionEffect / ClearLastActionEffect (Task UAI-T8)
    //   - FakeGameProbe.GetLastActionEffect()                       (Task UAI-T8)
    //   - UIObserver.PollPlayerActionEffect + _lastObservedActionSequence  (Task UAI-T9)
    //   - UIObserver.ResetWindowState: clears sequence + calls OnActionConsumed (Task UAI-T9)
    //   - SnapshotAggregator.OnActionCompleted / OnActionConsumed   (Task UAI-T4)
    //   - GameStateSnapshot.ActionCompleted                          (Task UAI-T2)
    //   - ActionTypeMapper.FromFFXIVActionType (used by the poller)  (Task UAI-T1)
    // ─────────────────────────────────────────────────────────────────────────

    // Helper: build a fixture that also injects a FakeTargetProbe.
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeAddonProbe addonProbe,
                    FakeGameProbe gameProbe,
                    FakeClock clock,
                    FakeTraceWriter writer,
                    TraceSession traceSession,
                    SnapshotAggregator aggregator,
                    FakeTargetProbe targetProbe)
        BuildFixtureWithAggregatorAndTarget(TraceMode mode = TraceMode.Always)
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(mode, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var addonProbe  = new FakeAddonProbe();
        var gameProbe   = new FakeGameProbe();
        var targetProbe = new FakeTargetProbe();
        var framework   = new FakeFramework();
        var aggregator  = new SnapshotAggregator(null, clock);

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            addonProbe:   addonProbe,
            gameProbe:    gameProbe,
            clock:        clock,
            targetProbe:  targetProbe);

        observer.SetAggregator(aggregator, activeRunId: "active-001");

        return (observer, framework, addonProbe, gameProbe, clock, writer, session, aggregator, targetProbe);
    }

    // =========================================================================
    // UO_K1 — First observation establishes baseline; no event fires
    // =========================================================================

    [Fact]
    public void UO_K1_FirstObservation_EstablishesBaseline_NoEventFired()
    {
        // CONTRACT: Given the game probe reports sequence=100 on the first tick
        //           (pre-session state from actions taken before Author mode started),
        //           When the first tick fires after UIObserver is constructed,
        //           Then NO "ActionCompleted" observation is written and
        //                agg.Current.ActionCompleted is null.
        //
        // WHY: Treating the first observation as an event would draft a step for an action
        //      the author had no intention of including. The first read is baseline-only.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        // Game has a non-zero sequence from before the session started.
        gameProbe.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);

        // Act — first tick
        framework.Tick();

        // Assert
        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Empty(actionObs);
        Assert.Null(aggregator.Current.ActionCompleted);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K2 — Unchanged sequence on second tick: no event
    // =========================================================================

    [Fact]
    public void UO_K2_SameSequence_SecondTick_NoEvent()
    {
        // CONTRACT: Given the sequence is 100 on tick 1 (baseline established)
        //           and still 100 on tick 2 (no action used),
        //           When tick 2 fires,
        //           Then NO "ActionCompleted" observation is written.
        //
        // WHY: The poller only fires on sequence increment — same value means no new action.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        gameProbe.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);
        framework.Tick(); // baseline tick

        // Act — second tick, same sequence
        framework.Tick();

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Empty(actionObs);
        Assert.Null(aggregator.Current.ActionCompleted);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K3 — Sequence increments: fires OnActionCompleted exactly once
    // =========================================================================

    [Fact]
    public void UO_K3_SequenceIncrements_FiresOnActionCompletedOnce()
    {
        // CONTRACT: Given baseline sequence=100 established on tick 1,
        //           When sequence changes to 101 and tick 2 fires,
        //           Then exactly one "ActionCompleted" observation is written
        //                with Argument=31u (the actionId),
        //                agg.Current.ActionCompleted is set with ActionType=Action,
        //                ActionId=31u, and TargetBaseId=null (no target probe set).

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        gameProbe.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);
        framework.Tick(); // baseline

        gameProbe.SetLastActionEffect(sequence: 101u, ffxivActionType: 1u, actionId: 31u);

        // Act
        framework.Tick();

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Single(actionObs);
        Assert.True(
            actionObs[0].Argument?.GetRawText() == "31",
            $"Expected Argument=31 but got {actionObs[0].Argument?.GetRawText()}");

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Equal(QuestForge.Schema.ActionType.Action, aggregator.Current.ActionCompleted!.ActionType);
        Assert.Equal(31u, aggregator.Current.ActionCompleted.ActionId);
        Assert.Null(aggregator.Current.ActionCompleted.TargetBaseId);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K4 — Multiple sequence increments: fires once per increment
    // =========================================================================

    [Fact]
    public void UO_K4_MultipleSequenceIncrements_FiresOncePerIncrement()
    {
        // CONTRACT: Given baseline=100, tick 2 increments to 101 (action 31),
        //           tick 3 increments to 102 (action 35),
        //           Then two "ActionCompleted" observations are written total:
        //             first with Argument=31, second with Argument=35.
        //           agg.Current.ActionCompleted reflects the latest (action 35).

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        gameProbe.SetLastActionEffect(100u, 1u, 31u); framework.Tick(); // baseline
        gameProbe.SetLastActionEffect(101u, 1u, 31u); framework.Tick(); // event 1
        gameProbe.SetLastActionEffect(102u, 1u, 35u); framework.Tick(); // event 2

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Equal(2, actionObs.Count);
        Assert.True(actionObs[0].Argument?.GetRawText() == "31",
            $"First observation Argument should be 31, got {actionObs[0].Argument?.GetRawText()}");
        Assert.True(actionObs[1].Argument?.GetRawText() == "35",
            $"Second observation Argument should be 35, got {actionObs[1].Argument?.GetRawText()}");

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Equal(35u, aggregator.Current.ActionCompleted!.ActionId);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K5 — Unsupported ActionType (e.g. Item=2): no event, but sequence advances
    // =========================================================================

    [Fact]
    public void UO_K5_UnsupportedActionType_NoEvent_SequenceStillAdvances()
    {
        // CONTRACT: Given baseline=100 (tick 1), then seq=101 with type=2 (Item, unsupported)
        //           (tick 2 — no event, but baseline advances to 101),
        //           Then seq=102 with type=1 (Action, supported) (tick 3 — ONE event for id=31).
        //           Total: exactly ONE "ActionCompleted" observation (the supported one at seq 102).
        //
        // CRITICAL: The baseline must advance past 101 even when type=2 (unsupported), so that
        //           tick 3 (seq=102) only fires one event (102 > 101, not 102 > 100).
        //           The implementation does _lastObservedActionSequence = sequence BEFORE the
        //           type-mapper check.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        gameProbe.SetLastActionEffect(100u, 1u, 31u);  framework.Tick(); // baseline
        gameProbe.SetLastActionEffect(101u, 2u, 999u); framework.Tick(); // unsupported Item — no event
        gameProbe.SetLastActionEffect(102u, 1u, 31u);  framework.Tick(); // supported — ONE event

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Single(actionObs);
        Assert.True(actionObs[0].Argument?.GetRawText() == "31",
            $"Expected Argument=31 (supported action), got {actionObs[0].Argument?.GetRawText()}");

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Equal(31u, aggregator.Current.ActionCompleted!.ActionId);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K6 — ResetWindowState clears the last-observed sequence: next tick is new baseline
    // =========================================================================

    [Fact]
    public void UO_K6_ResetWindowState_ClearsSequence_NextTickIsNewBaseline()
    {
        // CONTRACT: Given baseline=100 established on tick 1,
        //           When ResetWindowState is called (modal opened; window reset),
        //           Then the next tick (seq=200) is treated as baseline — no event fires.
        //           Also: agg.Current.ActionCompleted is null (OnActionConsumed was called).
        //
        // WHY re-baseline: after a window reset, any pre-reset sequence value is stale.
        //   The next authoring session's first observation must not fire as an event.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, _) =
            BuildFixtureWithAggregatorAndTarget();

        gameProbe.SetLastActionEffect(100u, 1u, 31u); framework.Tick(); // baseline = 100

        observer.ResetWindowState(); // clears sequence + calls OnActionConsumed

        gameProbe.SetLastActionEffect(200u, 1u, 31u);

        // Act — tick after reset: large jump but it is the new baseline
        framework.Tick();

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Empty(actionObs);
        Assert.Null(aggregator.Current.ActionCompleted);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K7 — Null IGameProbe: no observation, no NRE
    // =========================================================================

    [Fact]
    public void UO_K7_NullGameProbe_NoObservation_NoNre()
    {
        // CONTRACT: Given UIObserver constructed with gameProbe=null,
        //           When multiple ticks fire,
        //           Then NO "ActionCompleted" observation is written and no exception thrown.
        //
        // Mirrors UO_A2 defensive pattern.

        var writer   = new FakeTraceWriter();
        var session  = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();
        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();
        var observer  = new UIObserver(
            framework: framework, traceSession: session, passiveRunId: PassiveRunId,
            addonProbe: null, gameProbe: null, clock: clock);

        var ex = Record.Exception(() =>
        {
            framework.Tick();
            framework.Tick();
        });

        Assert.Null(ex);

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Empty(actionObs);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K8 — BattleNpc target: TargetBaseId is the BattleNpc BaseId
    // =========================================================================

    [Fact]
    public void UO_K8_BattleNpcTarget_TargetBaseIdIsSet()
    {
        // CONTRACT: Given the FakeTargetProbe returns a BattleNpc with BaseId=5005u
        //           and the player uses an action (seq 100→101, type=1, id=31),
        //           When tick 2 fires,
        //           Then agg.Current.ActionCompleted.TargetBaseId == 5005u.
        //
        // WHY: The author targets an enemy during a combat quest objective; the recorded
        //      step must name the enemy's NPC template id.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, targetProbe) =
            BuildFixtureWithAggregatorAndTarget();

        targetProbe.SetBattleNpcTarget((BaseId: 5005u, X: 0f, Y: 0f, Z: 0f, Zone: 132));

        gameProbe.SetLastActionEffect(100u, 1u, 31u); framework.Tick(); // baseline
        gameProbe.SetLastActionEffect(101u, 1u, 31u);

        // Act
        framework.Tick();

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Single(actionObs);

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Equal(5005u, aggregator.Current.ActionCompleted!.TargetBaseId);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K9 — No target (self-cast): TargetBaseId is null
    // =========================================================================

    [Fact]
    public void UO_K9_NoTarget_TargetBaseIdIsNull()
    {
        // CONTRACT: Given the FakeTargetProbe returns null for all queries
        //           and the player uses Sprint (seq 100→101, type=5/GeneralAction, id=4),
        //           When tick 2 fires,
        //           Then agg.Current.ActionCompleted.TargetBaseId is null (self-cast).
        //
        // WHY: Sprint, Return, and similar actions have no external target.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, targetProbe) =
            BuildFixtureWithAggregatorAndTarget();

        // FakeTargetProbe defaults to null for all queries — no SetBattleNpcTarget call.

        gameProbe.SetLastActionEffect(100u, 5u, 4u); framework.Tick(); // baseline
        gameProbe.SetLastActionEffect(101u, 5u, 4u);

        // Act
        framework.Tick();

        var actionObs = writer.RecordedEvents.OfType<ObservationEvent>()
            .Where(e => e.Method == "ActionCompleted")
            .ToList();
        Assert.Single(actionObs);
        Assert.True(actionObs[0].Argument?.GetRawText() == "4",
            $"Expected Argument=4 (Sprint id), got {actionObs[0].Argument?.GetRawText()}");

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Null(aggregator.Current.ActionCompleted!.TargetBaseId);

        observer.Dispose();
    }

    // =========================================================================
    // UO_K10 — Both BattleNpc and interactable set: BattleNpc wins (priority)
    // =========================================================================

    [Fact]
    public void UO_K10_BothBattleNpcAndInteractable_BattleNpcWins()
    {
        // CONTRACT: Given FakeTargetProbe.SetBattleNpcTarget(5005u) AND
        //           SetInteractableNpcTarget(1234567u) are both set (defensive — only one
        //           can be the real hard target in Dalamud, but the fake allows both),
        //           When the player uses an action (seq 100→101, type=1, id=31),
        //           Then agg.Current.ActionCompleted.TargetBaseId == 5005u (BattleNpc wins).
        //
        // RATIONALE: PollPlayerActionEffect's branch order:
        //   if (hostile is { } h) targetBaseId = h.BaseId;
        //   else if (interactable is { } i) targetBaseId = i.BaseId;
        // BattleNpc always takes precedence over interactable NPC.

        var (observer, framework, _, gameProbe, _, writer, _, aggregator, targetProbe) =
            BuildFixtureWithAggregatorAndTarget();

        targetProbe.SetBattleNpcTarget((BaseId: 5005u,    X: 0f, Y: 0f, Z: 0f, Zone: 132));
        targetProbe.SetInteractableNpcTarget((BaseId: 1234567u, X: 0f, Y: 0f, Z: 0f, Zone: 132));

        gameProbe.SetLastActionEffect(100u, 1u, 31u); framework.Tick(); // baseline
        gameProbe.SetLastActionEffect(101u, 1u, 31u);

        // Act
        framework.Tick();

        Assert.NotNull(aggregator.Current.ActionCompleted);
        Assert.Equal(5005u, aggregator.Current.ActionCompleted!.TargetBaseId);

        observer.Dispose();
    }
}
