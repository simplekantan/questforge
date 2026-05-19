using System.Text.Json;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;

namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Dalamud-free polling observer. Subscribes to a framework dispatch (abstracted via
/// <see cref="IFrameworkDispatch"/>) and emits trace observations and inference signals.
///
/// Two outputs per poll:
///   1. <see cref="TraceSession.WriteObservation"/> — always, when TraceSession gate is open.
///   2. <see cref="SnapshotAggregator"/> forwarding — only when an aggregator is set.
///
/// Polling cadence:
///   Every frame : PollAethernetDestination, PollDialogueOption, PollSelectYesno
///   Heartbeat (250 ms) : PollQuestState, PollAttunement, PollKeyItems
/// </summary>
public sealed class UIObserver : IDisposable
{
    // ── dependencies ────────────────────────────────────────────────────────
    private readonly IFrameworkDispatch _framework;
    private readonly TraceSession _traceSession;
    private readonly string _passiveRunId;
    private readonly IAddonProbe? _addonProbe;
    private readonly IGameProbe? _gameProbe;
    private readonly IClock _clock;

    // ── aggregator (swappable) ───────────────────────────────────────────────
    private SnapshotAggregator? _aggregator;
    private string? _activeRunId;

    // ── heartbeat throttle ────────────────────────────────────────────────
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;

    // ── quest-state tracking ─────────────────────────────────────────────
    private readonly Dictionary<ushort, (byte Seq, byte Flags)> _lastKnownQuestState = new();

    // ── attunement tracking ───────────────────────────────────────────────
    // Cleared by ResetHeartbeatState so a new authoring session re-emits attunements.
    private readonly HashSet<uint> _attunedAetheryteIds = new();

    // ── key-items tracking ─────────────────────────────────────────────────
    private Dictionary<uint, int> _previousKeyItemsMap = new();

    // ── TelepotTown (aethernet) tracking ─────────────────────────────────
    private bool _aethernetMenuWasOpen;
    private string? _pendingAethernetTo;  // destination name from GetTelepotTownDestinationName

    // ── SelectIconString (dialogue) tracking ─────────────────────────────
    private bool _dialogueIconStringWasOpen;
    private int? _pendingDialogueIdx;

    // ── SelectYesno tracking ──────────────────────────────────────────────
    private bool _selectYesnoWasOpen;

    // ── disposal ──────────────────────────────────────────────────────────
    private bool _disposed;

    // ── JSON options ──────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────────

    public UIObserver(
        IFrameworkDispatch framework,
        TraceSession traceSession,
        string passiveRunId,
        IAddonProbe? addonProbe = null,
        IGameProbe? gameProbe = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(traceSession);
        ArgumentException.ThrowIfNullOrEmpty(passiveRunId);

        _framework    = framework;
        _traceSession = traceSession;
        _passiveRunId = passiveRunId;
        _addonProbe   = addonProbe;
        _gameProbe    = gameProbe;
        _clock        = clock ?? SystemClock.Instance;

        _framework.Subscribe(OnFrameworkUpdate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Aggregator management
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets or clears the active aggregator.
    /// When <paramref name="aggregator"/> is non-null, <paramref name="activeRunId"/> must also be non-null.
    /// When <paramref name="aggregator"/> is null, <paramref name="activeRunId"/> must also be null.
    /// </summary>
    public void SetAggregator(SnapshotAggregator? aggregator, string? activeRunId)
    {
        if (aggregator is not null && activeRunId is null)
            throw new ArgumentException("activeRunId must be non-null when aggregator is non-null.", nameof(activeRunId));
        if (aggregator is null && activeRunId is not null)
            throw new ArgumentException("activeRunId must be null when aggregator is null.", nameof(activeRunId));

        _aggregator  = aggregator;
        _activeRunId = activeRunId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Reset helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets per-window menu tracking so the next recording window starts clean.
    /// Does NOT affect the heartbeat throttle.
    /// </summary>
    public void ResetWindowState()
    {
        _aethernetMenuWasOpen      = false;
        _pendingAethernetTo        = null;
        _dialogueIconStringWasOpen = false;
        _pendingDialogueIdx        = null;
        _selectYesnoWasOpen        = false;

        _aggregator?.OnAethernetTeleportConsumed();
        _aggregator?.OnDialogueOptionConsumed();
    }

    /// <summary>
    /// Resets the heartbeat timestamp so the next tick fires the heartbeat pollers immediately,
    /// and clears the per-session caches (_lastKnownQuestState, _attunedAetheryteIds,
    /// _previousKeyItemsMap) so the next authoring session starts fresh.
    /// Does NOT touch window-state flags.
    /// </summary>
    public void ResetHeartbeatState()
    {
        _lastHeartbeatAt = DateTimeOffset.MinValue;
        _lastKnownQuestState.Clear();
        _attunedAetheryteIds.Clear();
        _previousKeyItemsMap.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Framework update
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate()
    {
        // Every-frame pollers
        PollAethernetDestination();
        PollDialogueOption();
        PollSelectYesno();

        // Heartbeat pollers (throttled to 250 ms)
        var now = _clock.UtcNow;
        if (now - _lastHeartbeatAt < HeartbeatInterval) return;
        _lastHeartbeatAt = now;

        PollQuestState();
        PollAttunement();
        PollKeyItems();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Heartbeat pollers
    // ─────────────────────────────────────────────────────────────────────────

    private void PollQuestState()
    {
        if (_gameProbe is null) return;

        var quests  = _gameProbe.GetNormalQuests();
        var now     = _clock.UtcNow;
        var runId   = CurrentRunId;
        var seenIds = new HashSet<ushort>();

        foreach (var (id, seq, flags) in quests)
        {
            if (id == 0) continue;
            seenIds.Add(id);

            var publicId = ToPublicQuestId(id);

            if (_lastKnownQuestState.TryGetValue(id, out var last))
            {
                // Quest already known — emit passive observations every heartbeat (dedup suppresses repeats).
                // Detect state changes and forward to aggregator.
                if (seq != last.Seq)
                {
                    _aggregator?.OnQuestSequenceChanged(publicId, seq);
                    _lastKnownQuestState[id] = (seq, flags);
                }
                else if (flags != last.Flags)
                {
                    _aggregator?.OnQuestFlagsChanged(publicId, flags);
                    _lastKnownQuestState[id] = (seq, flags);
                }
            }
            else
            {
                // New quest
                _lastKnownQuestState[id] = (seq, flags);
                _aggregator?.OnQuestAccepted(publicId);
                _aggregator?.OnQuestSequenceChanged(publicId, seq);
                _aggregator?.OnQuestFlagsChanged(publicId, flags);
                WriteObservation("IsQuestAccepted", publicId.Value, true, runId, now);
            }

            // Passive trace — dedup in TraceSession suppresses unchanged values.
            WriteObservation("GetQuestSequence", publicId.Value, (int)seq, runId, now);
            WriteObservation("GetQuestFlags",    publicId.Value, (int)flags, runId, now);
        }

        // Detect removed quests
        var removedIds = _lastKnownQuestState.Keys.Where(id => !seenIds.Contains(id)).ToList();
        foreach (var id in removedIds)
        {
            var publicId = ToPublicQuestId(id);
            _aggregator?.OnQuestCompleted(publicId);
            WriteObservation("IsQuestComplete", publicId.Value, true, runId, now);
            _lastKnownQuestState.Remove(id);
        }
    }

    private void PollAttunement()
    {
        if (_gameProbe is null) return;

        var now   = _clock.UtcNow;
        var runId = CurrentRunId;

        foreach (var rowId in _gameProbe.GetAllAetheryteRowIds())
        {
            if (_attunedAetheryteIds.Contains(rowId)) continue;
            if (!_gameProbe.IsAetheryteUnlocked(rowId)) continue;

            _attunedAetheryteIds.Add(rowId);
            _aggregator?.OnAttunementChanged(new AetheryteId(rowId));
            WriteObservation("IsAetheryteAttuned", rowId, 1, runId, now);
        }
    }

    private void PollKeyItems()
    {
        if (_gameProbe is null) return;

        var now      = _clock.UtcNow;
        var slots    = _gameProbe.GetKeyItemSlots();
        var runId    = _activeRunId; // only written when actively recording

        // Build current map
        var currentMap = new Dictionary<uint, int>();
        foreach (var (itemId, qty) in slots)
        {
            if (currentMap.TryGetValue(itemId, out var existing))
                currentMap[itemId] = existing + qty;
            else
                currentMap[itemId] = qty;
        }

        // Diff
        var addedIds   = new List<uint>();
        var removedIds = new List<uint>();
        var gained     = new List<KeyItemDelta>();
        var lost       = new List<KeyItemDelta>();
        var changed    = false;

        foreach (var (id, qty) in currentMap)
        {
            if (_previousKeyItemsMap.TryGetValue(id, out var prevQty))
            {
                if (qty != prevQty)
                {
                    changed = true;
                    if (qty > prevQty)
                        gained.Add(new KeyItemDelta(id, qty - prevQty));
                    else
                        lost.Add(new KeyItemDelta(id, prevQty - qty));
                }
            }
            else
            {
                changed = true;
                addedIds.Add(id);
                gained.Add(new KeyItemDelta(id, qty));
            }
        }

        foreach (var (id, prevQty) in _previousKeyItemsMap)
        {
            if (!currentMap.ContainsKey(id))
            {
                changed = true;
                removedIds.Add(id);
                lost.Add(new KeyItemDelta(id, prevQty));
            }
        }

        _previousKeyItemsMap = currentMap;

        if (!changed) return;

        if (addedIds.Count > 0)
            _aggregator?.OnKeyItemsChanged(addedIds);
        if (removedIds.Count > 0)
            _aggregator?.OnKeyItemsRemoved(removedIds);

        // Write InventoryChangedEvent only when actively recording
        if (runId is not null)
        {
            // Compute a simple hash for the inventory state
            var hash = ComputeKeyItemHash(currentMap);
            _traceSession.Write(new InventoryChangedEvent(
                RunId:   runId,
                Gained:  gained,
                Lost:    lost,
                NewHash: hash,
                At:      now));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Every-frame pollers
    // ─────────────────────────────────────────────────────────────────────────

    private void PollAethernetDestination()
    {
        if (_addonProbe is null) return;

        var menuIsOpen = _addonProbe.IsAddonOpen("TelepotTown");

        if (!menuIsOpen)
        {
            if (_aethernetMenuWasOpen && _pendingAethernetTo is not null)
            {
                // Menu just closed after a selection — fire teleport-completed observation.
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("AethernetTeleportCompleted", 0u, _pendingAethernetTo, runId, now);
            }
            _aethernetMenuWasOpen = false;
            _pendingAethernetTo   = null;
            return;
        }

        if (!_aethernetMenuWasOpen)
        {
            _aethernetMenuWasOpen = true;
            // Departure shard not tracked in UIObserver (no TargetManager access)
        }

        // Latch destination while menu is open
        var idx = _addonProbe.GetSelectedItemIndex("TelepotTown");
        if (idx.HasValue && idx.Value >= 0)
        {
            var destName = _addonProbe.GetTelepotTownDestinationName(idx.Value);
            if (!string.IsNullOrEmpty(destName))
                _pendingAethernetTo = destName;
        }
    }

    private void PollDialogueOption()
    {
        if (_addonProbe is null) return;

        var menuIsOpen = _addonProbe.IsAddonOpen("SelectIconString");

        if (menuIsOpen && !_dialogueIconStringWasOpen)
        {
            _dialogueIconStringWasOpen = true;
            // TODO: NPC capture requires ITargetProbe — added during AuthoringHost integration.
            // Currently UIObserver has no TargetManager source; DialogueNpcCaptured is not emitted.
            // UO-G3 is weakened to "at most one" to accommodate this design gap.
        }

        if (!menuIsOpen)
        {
            if (_dialogueIconStringWasOpen && _pendingDialogueIdx.HasValue)
            {
                // Menu just closed with a selection
                var idx   = _pendingDialogueIdx.Value;
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("DialogueOptionSelected", 0u, idx, runId, now);
                _aggregator?.OnDialogueOptionSelected(idx);
            }
            _dialogueIconStringWasOpen = false;
            _pendingDialogueIdx        = null;
            return;
        }

        // Latch selected index while menu is open
        var selectedIdx = _addonProbe.GetSelectedItemIndex("SelectIconString");
        if (selectedIdx.HasValue && selectedIdx.Value >= 0)
            _pendingDialogueIdx = selectedIdx.Value;
    }

    private void PollSelectYesno()
    {
        if (_addonProbe is null) return;

        var menuIsOpen = _addonProbe.IsAddonOpen("SelectYesno");

        if (menuIsOpen && !_selectYesnoWasOpen)
        {
            _selectYesnoWasOpen = true;
            // TODO: NPC capture requires ITargetProbe — added during AuthoringHost integration.
        }

        if (!menuIsOpen)
        {
            if (_selectYesnoWasOpen)
            {
                // Menu just closed — hardcode yes (option 0)
                const int yesOption = 0;
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("SelectYesnoConfirmed", 0u, yesOption, runId, now);
                _aggregator?.OnDialogueOptionSelected(yesOption);
            }
            _selectYesnoWasOpen = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string CurrentRunId => _activeRunId ?? _passiveRunId;

    private static QuestId ToPublicQuestId(ushort rawId)
        => new(rawId | 0x10000u);

    private void WriteObservation(string method, object argument, object value, string runId, DateTimeOffset at)
    {
        try
        {
            var argEl = JsonSerializer.SerializeToElement(argument, JsonOpts);
            var valEl = JsonSerializer.SerializeToElement(value, JsonOpts);
            _traceSession.WriteObservation(method, argEl, valEl, runId, at);
        }
        catch
        {
            // Trace write failure must not affect observation.
        }
    }

    private static uint ComputeKeyItemHash(Dictionary<uint, int> map)
    {
        // Simple hash: XOR of (id * qty) for stability in tests
        uint hash = 0;
        foreach (var (id, qty) in map)
            hash ^= id * (uint)qty;
        return hash;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _framework.Unsubscribe(OnFrameworkUpdate);
    }
}
