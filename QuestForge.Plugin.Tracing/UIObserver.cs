using System.Text.Json;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;

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
    private readonly ITargetProbe? _targetProbe;
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
    private bool   _aethernetMenuWasOpen;
    private uint?  _pendingAethernetFromId;
    private uint?  _pendingAethernetToId;

    // ── SelectIconString (dialogue) tracking ─────────────────────────────
    private bool _dialogueIconStringWasOpen;
    private int? _pendingDialogueIdx;

    // ── SelectYesno tracking ──────────────────────────────────────────────
    private bool _selectYesnoWasOpen;

    // ── Target NPC tracking ───────────────────────────────────────────────
    private uint _lastTargetBaseId;

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
        IClock? clock = null,
        ITargetProbe? targetProbe = null)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(traceSession);
        ArgumentException.ThrowIfNullOrEmpty(passiveRunId);

        _framework    = framework;
        _traceSession = traceSession;
        _passiveRunId = passiveRunId;
        _addonProbe   = addonProbe;
        _gameProbe    = gameProbe;
        _targetProbe  = targetProbe;
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
        _aethernetMenuWasOpen    = false;
        _pendingAethernetFromId  = null;
        _pendingAethernetToId    = null;
        _dialogueIconStringWasOpen = false;
        _pendingDialogueIdx        = null;
        _selectYesnoWasOpen        = false;
        _lastTargetBaseId          = 0;

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
        PollTargetNpc();
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
            _aggregator?.OnAttunementChanged(new QuestForge.Adapters.Types.AetheryteId(rowId));
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
            if (_aethernetMenuWasOpen && _pendingAethernetToId.HasValue)
            {
                var toId  = _pendingAethernetToId.Value;
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("AethernetTeleportCompleted", toId, _pendingAethernetFromId ?? 0u, runId, now);
                _aggregator?.OnAethernetTeleportCompleted(
                    _pendingAethernetFromId.HasValue ? new QuestForge.Adapters.Types.AetheryteId(_pendingAethernetFromId.Value) : null,
                    new QuestForge.Adapters.Types.AethernetId(toId));
            }
            _aethernetMenuWasOpen    = false;
            _pendingAethernetFromId  = null;
            _pendingAethernetToId    = null;
            return;
        }

        if (!_aethernetMenuWasOpen)
        {
            _aethernetMenuWasOpen = true;
            // Capture departure shard from live target (player is standing at the shard)
            var aetheryteInfo = _targetProbe?.GetAetheryteTarget();
            if (aetheryteInfo.HasValue)
                _pendingAethernetFromId = aetheryteInfo.Value.BaseId;
        }

        // Latch destination RowId while menu is open; guard against same-shard hop
        var selectedIdx = _addonProbe.GetSelectedItemIndex("TelepotTown");
        if (selectedIdx.HasValue && selectedIdx.Value >= 0)
        {
            var destId = _addonProbe.GetTelepotTownDestinationId(selectedIdx.Value);
            if (destId.HasValue && destId != _pendingAethernetFromId)
                _pendingAethernetToId = destId;
        }
    }

    private void PollDialogueOption()
    {
        if (_addonProbe is null) return;

        var menuIsOpen = _addonProbe.IsAddonOpen("SelectIconString");

        if (menuIsOpen && !_dialogueIconStringWasOpen)
        {
            _dialogueIconStringWasOpen = true;
            // Capture NPC from ITargetProbe: tier 1 = hard target, tier 2 = previous target,
            // tier 3 = aggregator fallback (LastNpcInteracted from last PollTargetNpc heartbeat).
            var npcInfo = _targetProbe?.GetInteractableNpcTarget()
                       ?? _targetProbe?.GetInteractableNpcPreviousTarget();

            NpcLocation? npcLoc = null;
            if (npcInfo.HasValue)
            {
                npcLoc = new NpcLocation(
                    NpcId: npcInfo.Value.BaseId,
                    Zone: npcInfo.Value.Zone,
                    Position: new Position3(npcInfo.Value.X, npcInfo.Value.Y, npcInfo.Value.Z));
            }
            else if (_aggregator is not null)
            {
                var cur = _aggregator.Current;
                if (cur.LastNpcInteracted.HasValue && cur.LastNpcPosition.HasValue)
                    npcLoc = new NpcLocation(
                        NpcId: cur.LastNpcInteracted.Value.Value,
                        Zone: (int)cur.Zone.Value,
                        Position: new Position3(
                            cur.LastNpcPosition.Value.X,
                            cur.LastNpcPosition.Value.Y,
                            cur.LastNpcPosition.Value.Z));
            }

            if (npcLoc is not null)
            {
                _aggregator?.OnDialogueNpcCaptured(npcLoc);
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("DialogueNpcCaptured", npcLoc.NpcId,
                    new { zone = npcLoc.Zone, x = npcLoc.Position.X, y = npcLoc.Position.Y, z = npcLoc.Position.Z },
                    runId, now);
            }
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
            // NPC capture: same tier strategy as PollDialogueOption.
            var npcInfo = _targetProbe?.GetInteractableNpcTarget()
                       ?? _targetProbe?.GetInteractableNpcPreviousTarget();

            NpcLocation? npcLoc = null;
            if (npcInfo.HasValue)
            {
                npcLoc = new NpcLocation(
                    NpcId: npcInfo.Value.BaseId,
                    Zone: npcInfo.Value.Zone,
                    Position: new Position3(npcInfo.Value.X, npcInfo.Value.Y, npcInfo.Value.Z));
            }
            else if (_aggregator is not null)
            {
                var cur = _aggregator.Current;
                if (cur.LastNpcInteracted.HasValue && cur.LastNpcPosition.HasValue)
                    npcLoc = new NpcLocation(
                        NpcId: cur.LastNpcInteracted.Value.Value,
                        Zone: (int)cur.Zone.Value,
                        Position: new Position3(
                            cur.LastNpcPosition.Value.X,
                            cur.LastNpcPosition.Value.Y,
                            cur.LastNpcPosition.Value.Z));
            }

            if (npcLoc is not null)
            {
                _aggregator?.OnDialogueNpcCaptured(npcLoc);
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("DialogueNpcCaptured", npcLoc.NpcId,
                    new { zone = npcLoc.Zone, x = npcLoc.Position.X, y = npcLoc.Position.Y, z = npcLoc.Position.Z },
                    runId, now);
            }
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
                _aggregator?.OnSelectYesnoConfirmed(); // distinct from OnDialogueOptionSelected (SelectIconString list choices)
            }
            _selectYesnoWasOpen = false;
        }
    }

    private void PollTargetNpc()
    {
        if (_targetProbe is null) return;

        // Check aetheryte target first
        var aetheryteInfo = _targetProbe.GetAetheryteTarget();
        if (aetheryteInfo.HasValue)
        {
            if (aetheryteInfo.Value.BaseId != _lastTargetBaseId)
            {
                _lastTargetBaseId = aetheryteInfo.Value.BaseId;
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("GetTarget", 0u, aetheryteInfo.Value.BaseId, runId, now);
                WriteObservation("AethernetShardTargeted", aetheryteInfo.Value.BaseId, 0, runId, now);
                _aggregator?.OnAethernetShardTargeted(new QuestForge.Adapters.Types.AetheryteId(aetheryteInfo.Value.BaseId));
                // WHY: Rule 2.5 requires LastNpcInteracted == LastAethernetShardInteracted.
                // Without this, LastNpcInteracted stays as the previous NPC and attune inference never fires.
                _aggregator?.OnInteraction(
                    new QuestForge.Adapters.Types.NpcId(aetheryteInfo.Value.BaseId),
                    new QuestForge.Adapters.Types.WorldPosition(aetheryteInfo.Value.X, aetheryteInfo.Value.Y, aetheryteInfo.Value.Z));
            }
            return;
        }

        // Check interactable NPC target
        var npcInfo = _targetProbe.GetInteractableNpcTarget();
        if (npcInfo.HasValue)
        {
            if (npcInfo.Value.BaseId != _lastTargetBaseId)
            {
                _lastTargetBaseId = npcInfo.Value.BaseId;
                var now   = _clock.UtcNow;
                var runId = CurrentRunId;
                WriteObservation("GetTarget", 0u, npcInfo.Value.BaseId, runId, now);
                _aggregator?.OnInteraction(
                    new NpcId(npcInfo.Value.BaseId),
                    new WorldPosition(npcInfo.Value.X, npcInfo.Value.Y, npcInfo.Value.Z));
            }
            return;
        }

        // No valid target
        if (_lastTargetBaseId != 0)
            _lastTargetBaseId = 0;
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
