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
///   Every frame : PollAethernetDestination, PollDialogueOption, PollSelectYesno, PollTargetNpc
///   Heartbeat (250 ms) : PollPlayerPosition, PollQuestState, PollAttunement, PollKeyItems,
///                        PollCombat, PollBattleNpcTarget
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
    private readonly ICombatProbe? _combatProbe;
    private readonly IVendorProbe? _vendorProbe;
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

    // ── combat tracking ───────────────────────────────────────────────────
    private readonly Dictionary<ulong, CombatHostileState> _hostileStates = new();
    private bool _lastInCombat;

    // ── vendor tracking ───────────────────────────────────────────────────
    private bool _lastShopOpen;
    private long _lastGil;
    private int _lastSeals;

    // ── key-items tracking ─────────────────────────────────────────────────
    private Dictionary<uint, int> _previousKeyItemsMap = new();

    // ── TelepotTown (aethernet) tracking ─────────────────────────────────
    private bool   _aethernetMenuWasOpen;
    private uint?  _pendingAethernetFromId;
    private uint?  _pendingAethernetToId;

    // ── Teleport (cross-region general action) tracking ───────────────────
    // The Teleport addon's internal struct (per FFXIVClientStructs) is out of date for the current
    // game version — reading SelectedItemIndex/Items returns garbage and crashes the game. Instead
    // we only track whether the addon was open recently; the actual destination is inferred at
    // zone-change time via AetheryteZoneMap reverse lookup (each zone has exactly one master
    // aetheryte). See AuthoringHost.OnTerritoryChanged for the inference.
    private const string TeleportAddonName = "Teleport";
    private DateTimeOffset _teleportAddonLastOpenAt = DateTimeOffset.MinValue;

    // ── SelectIconString (dialogue) tracking ─────────────────────────────
    private bool _dialogueIconStringWasOpen;
    private int? _pendingDialogueIdx;

    // ── SelectYesno tracking ──────────────────────────────────────────────
    private bool _selectYesnoWasOpen;

    // ── Action-effect tracking ────────────────────────────────────────────
    // Tracks the most recently seen ResponseGlobalSequence. null = not yet baselined.
    // First observation sets baseline without firing an event (see PollPlayerActionEffect).
    private uint? _lastObservedActionSequence;

    // ── Emote tracking ────────────────────────────────────────────────────
    // Tracks the most recently observed EmoteController.EmoteId.
    // 0 = player not emoting / not yet observed. Unlike _lastObservedActionSequence there is
    // no silent-baseline: a non-zero value on the first read fires immediately (Decision UEI9).
    private uint _lastObservedEmoteId;

    // ── Chat log tracking ─────────────────────────────────────────────────
    // Tracks the most recently seen MsgSourceArrayLength. null = not yet baselined.
    // First observation sets baseline silently (matches PollPlayerActionEffect pattern, Decision SCI9).
    private int? _lastObservedChatLogCount;

    // ── Equipment tracking ────────────────────────────────────────────────
    // Snapshot of the 14 equipment slot item IDs from the previous frame.
    // null = not yet baselined. First observation sets baseline silently (no fire).
    // Subsequent observations diff against the baseline; changes fire OnEquipmentChanged.
    // Baseline is NOT reset in ResetWindowState (equipment persists across recording windows).
    private uint[]? _lastEquippedItemIds;

    // ── Job tracking ─────────────────────────────────────────────────────
    // Tracks the most recently observed ClassJobId. null = not yet baselined.
    // First observation sets baseline silently (no fire).
    // Baseline is NOT reset in ResetWindowState (job persists across recording windows).
    private byte? _lastClassJobId;

    // ── Gearset count tracking ────────────────────────────────────────────
    // Tracks the most recently observed NumGearsets. null = not yet baselined.
    // First observation sets baseline silently (no fire).
    // On increase: fire OnGearsetRegistered. On decrease or same: silent baseline update.
    // Baseline is NOT reset in ResetWindowState (gearset count persists across recording windows).
    private byte? _lastGearsetCount;

    // ── Target tracking ───────────────────────────────────────────────────
    // _lastTargetBaseId dedups the every-frame PollTargetNpc (aetheryte + interactable-NPC).
    // _lastBattleNpcBaseId dedups the heartbeat PollBattleNpcTarget (hostile combat target).
    // The two fields are kept SEPARATE so the every-frame and heartbeat pollers never share a
    // last-emitted value — sharing would let one poller suppress the other's GetTarget emission
    // (e.g. an NPC target seen every-frame would dedup-block a same-id hostile re-confirm, and
    // vice-versa). Distinct ids in practice, but the separation makes the dedup contract explicit.
    private uint _lastTargetBaseId;
    private uint _lastBattleNpcBaseId;

    // ── EventObj interaction tracking ─────────────────────────────────────
    // _lastEventObjBaseId: the BaseId of the most recently latched EventObj target.
    // 0 = no EventObj latched (or cleared after firing). Reset by ResetWindowState.
    // _lastEventObjPosition: the world position of the most recently latched EventObj target.
    // _lastOccupiedInEvent: the OccupiedInEvent flag value from the previous frame.
    // Starts false (default) so the first frame where it is true fires a transition.
    private uint _lastEventObjBaseId;
    private (float X, float Y, float Z, int Zone) _lastEventObjPosition;
    private bool _lastOccupiedInEvent;

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
        ITargetProbe? targetProbe = null,
        ICombatProbe? combatProbe = null,
        IVendorProbe? vendorProbe = null)
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
        _combatProbe  = combatProbe;
        _vendorProbe  = vendorProbe;
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
        _lastBattleNpcBaseId       = 0;

        _teleportAddonLastOpenAt = DateTimeOffset.MinValue;

        _lastObservedActionSequence = null;
        _lastObservedEmoteId = 0u;
        _lastObservedChatLogCount = null;

        _aggregator?.OnAethernetTeleportConsumed();
        _aggregator?.OnDialogueOptionConsumed();
        _aggregator?.OnTeleportConsumed();
        _aggregator?.OnActionConsumed();
        _aggregator?.OnEmoteConsumed();
        _aggregator?.OnSayChatMessageConsumed();
        _aggregator?.OnItemUsedConsumed();
        _aggregator?.OnEquipmentChangedConsumed();
        // NOTE: _lastEquippedItemIds (baseline) is intentionally NOT reset here.
        // Equipment state persists across recording windows; only the signal is consumed.

        _aggregator?.OnJobChangedConsumed();
        // NOTE: _lastClassJobId (baseline) is intentionally NOT reset here.
        // Job state persists across recording windows; only the signal is consumed.

        _aggregator?.OnGearsetRegisteredConsumed();
        // NOTE: _lastGearsetCount (baseline) is intentionally NOT reset here.
        // Gearset count persists across recording windows; only the signal is consumed.

        _lastEventObjBaseId  = 0;
        _lastOccupiedInEvent = false;
        _aggregator?.OnObjectInteractedConsumed();
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
        _hostileStates.Clear();
        _lastInCombat = false;
        _lastShopOpen = false;
        _lastGil      = 0;
        _lastSeals    = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Framework update
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate()
    {
        // Every-frame pollers (menu/addon state + non-combat target capture).
        // PollTargetNpc runs every-frame so NPC interaction (LastNpcInteracted → talk Rule 7 /
        // attune Rule 2.5) and aethernet-shard capture are not throttled to the 250 ms heartbeat.
        PollAethernetDestination();
        PollTeleportAddonOpen();
        PollDialogueOption();
        PollSelectYesno();
        PollTargetNpc();
        PollPlayerActionEffect();
        PollPlayerEmote();
        PollPlayerChatMessage();
        PollEquipmentChange();
        PollJobChange();
        PollGearsetCount();
        PollEventObjInteraction();

        // Heartbeat pollers (throttled to 250 ms)
        var now = _clock.UtcNow;
        if (now - _lastHeartbeatAt < HeartbeatInterval) return;
        _lastHeartbeatAt = now;

        PollPlayerPosition();
        PollQuestState();
        PollAttunement();
        PollKeyItems();
        // WHY PollBattleNpcTarget immediately after PollCombat: OnInCombatChanged(true) must reach
        // the aggregator BEFORE OnBattleNpcTargeted, because the aggregator gates the span target
        // on _inCombat. Running both at heartbeat in this order guarantees the gate is open when
        // the hostile target is forwarded. (The non-combat PollTargetNpc stays every-frame above.)
        PollCombat();
        PollBattleNpcTarget();
        PollVendor();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Heartbeat pollers
    // ─────────────────────────────────────────────────────────────────────────

    private void PollPlayerPosition()
    {
        if (_gameProbe is null) return;
        var pos = _gameProbe.GetPlayerPosition();
        if (pos is null) return;
        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("GetPlayerPosition", 0u, new { x = pos.Value.X, y = pos.Value.Y, z = pos.Value.Z, zone = pos.Value.Zone }, runId, now);
        _aggregator?.OnPlayerMoved(new QuestForge.Adapters.Types.WorldPosition(pos.Value.X, pos.Value.Y, pos.Value.Z));
        // Also propagate zone if it differs from what we last emitted via OnZoneChanged
        // (OnZoneChanged fires from TerritoryChanged events; this handles the initial state)
    }

    private void PollQuestState()
    {
        if (_gameProbe is null) return;

        var quests  = _gameProbe.GetNormalQuests();
        var now     = _clock.UtcNow;
        var runId   = CurrentRunId;
        var seenIds = new HashSet<ushort>();

        foreach (var (id, seq, flags, variables) in quests)
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

            // Forward variables every poll — aggregator guard discards foreign quests.
            _aggregator?.OnQuestVariablesUpdated(publicId, variables);

            // Passive trace — dedup in TraceSession suppresses unchanged values.
            WriteObservation("GetQuestSequence",  publicId.Value, (int)seq, runId, now);
            WriteObservation("GetQuestFlags",     publicId.Value, (int)flags, runId, now);
            WriteObservation("GetQuestVariables", publicId.Value, variables.ToList(), runId, now);
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

        var hash = ComputeKeyItemHash(currentMap);
        var valueEl = JsonSerializer.SerializeToElement(
            new { gained = gained.ToArray(), lost = lost.ToArray(), newHash = hash },
            JsonOpts);
        // Write directly (not via WriteObservation dedup cache) — inventory changes are events, not polled state.
        _traceSession.Write(new ObservationEvent
        {
            RunId = CurrentRunId ?? "",
            Data = new ObservationEvent.ObservationData { Method = "InventoryChanged", Value = valueEl }
        });
    }

    private void PollCombat()
    {
        if (_combatProbe is null) return;

        var wasInCombat = _lastInCombat;
        var inCombat    = _combatProbe.IsInCombat();
        var now         = _clock.UtcNow;
        var runId       = CurrentRunId;

        if (inCombat != _lastInCombat)
        {
            _lastInCombat = inCombat;
            var valueEl = JsonSerializer.SerializeToElement(new { value = inCombat }, JsonOpts);
            _traceSession.Write(new ObservationEvent
            {
                RunId = runId ?? "",
                Data = new ObservationEvent.ObservationData { Method = "InCombat", Value = valueEl }
            });
            _aggregator?.OnInCombatChanged(inCombat);
        }

        var seen = new HashSet<ulong>();
        foreach (var (objectId, dataId, isDead) in _combatProbe.GetVisibleHostiles())
        {
            seen.Add(objectId);
            var hadPrior = _hostileStates.TryGetValue(objectId, out var prior);
            var wasDead  = hadPrior && prior.WasDead;

            if (isDead && !wasDead && hadPrior && wasInCombat)
            {
                var valueEl = JsonSerializer.SerializeToElement(new { dataId }, JsonOpts);
                _traceSession.Write(new ObservationEvent
                {
                    RunId = runId ?? "",
                    Data = new ObservationEvent.ObservationData { Method = "EnemyKilled", Value = valueEl }
                });
                // D9: EnemyKilled is kept as a presence corroborator but is no longer forwarded
                // to the aggregator — attribution uses the hostile target, not the kill event.
            }

            _hostileStates[objectId] = new CombatHostileState(dataId, isDead);
        }

        foreach (var goneId in _hostileStates.Keys.Where(id => !seen.Contains(id)).ToList())
            _hostileStates.Remove(goneId);
    }

    /// <summary>
    /// Detects the player's hostile (BattleNpc) hard target and forwards it for combat span
    /// attribution. Runs at heartbeat immediately after <see cref="PollCombat"/> so the
    /// aggregator's _inCombat gate is already open (D2). Contains ONLY the BattleNpc branch —
    /// the aetheryte / interactable-NPC branches live in the every-frame <see cref="PollTargetNpc"/>.
    /// </summary>
    private void PollBattleNpcTarget()
    {
        if (_targetProbe is null) return;

        var bn = _targetProbe.GetBattleNpcTarget();
        if (!bn.HasValue) return;

        if (bn.Value.BaseId == _lastBattleNpcBaseId) return;
        _lastBattleNpcBaseId = bn.Value.BaseId;

        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("GetTarget", 0u, new { baseId = bn.Value.BaseId, kind = "hostile" }, runId, now);
        _aggregator?.OnBattleNpcTargeted(bn.Value.BaseId);
    }

    private void PollVendor()
    {
        if (_vendorProbe is null) return;

        var now   = _clock.UtcNow;
        var runId = CurrentRunId;

        // Read GC axes once per heartbeat (cached locally — each Get* called exactly once).
        var activeCat  = _vendorProbe.GetActiveGcCategory();
        var activeTier = _vendorProbe.GetActiveGcRankTier();

        // Shop-open transition
        var open = _vendorProbe.IsShopOpen();
        if (open != _lastShopOpen)
        {
            _lastShopOpen = open;
            var valueEl = JsonSerializer.SerializeToElement(
                new { value = open, activeGcCategory = activeCat, activeGcRankTier = activeTier },
                JsonOpts);
            _traceSession.Write(new ObservationEvent
            {
                RunId = runId ?? "",
                Data = new ObservationEvent.ObservationData { Method = "ShopOpened", Value = valueEl }
            });
            _aggregator?.OnShopOpened(open, activeCat, activeTier);
        }

        // Heartbeat forward while shop is open (no new trace event)
        if (open)
            _aggregator?.OnActiveGcAxesObserved(activeCat, activeTier);

        // Currency balance
        var gil   = _vendorProbe.GetGil();
        var seals = _vendorProbe.GetGrandCompanySeals();
        if (gil != _lastGil || seals != _lastSeals)
        {
            _lastGil   = gil;
            _lastSeals = seals;
            var valueEl = JsonSerializer.SerializeToElement(new { gil, seals }, JsonOpts);
            _traceSession.Write(new ObservationEvent
            {
                RunId = runId ?? "",
                Data = new ObservationEvent.ObservationData { Method = "CurrencyBalance", Value = valueEl }
            });
            _aggregator?.OnCurrencyChanged(gil, seals);
        }

        // Item count changes (one-shot list)
        foreach (var (itemId, count) in _vendorProbe.GetChangedItemCounts())
        {
            var valueEl = JsonSerializer.SerializeToElement(new { itemId, count }, JsonOpts);
            _traceSession.Write(new ObservationEvent
            {
                RunId = runId ?? "",
                Data = new ObservationEvent.ObservationData { Method = "VendorItemCount", Value = valueEl }
            });
            _aggregator?.OnVendorItemCountChanged(itemId, count);
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

    private void PollTeleportAddonOpen()
    {
        if (_addonProbe is null) return;
        if (_addonProbe.IsAddonOpen(TeleportAddonName))
            _teleportAddonLastOpenAt = _clock.UtcNow;
    }

    /// <summary>
    /// True if the Teleport addon was observed open within the given window ending at the supplied
    /// instant. Used by AuthoringHost.OnTerritoryChanged to correlate a zone change with a recent
    /// teleport menu interaction.
    /// </summary>
    public bool WasTeleportAddonOpenWithin(TimeSpan window, DateTimeOffset asOf)
        => _teleportAddonLastOpenAt != DateTimeOffset.MinValue
           && (asOf - _teleportAddonLastOpenAt) <= window;

    /// <summary>
    /// Clears the teleport-addon-open timestamp. Called by AuthoringHost after a successful
    /// teleport inference fires so a subsequent zone change can't re-trigger from the same window.
    /// </summary>
    public void ClearTeleportAddonOpenTimestamp() => _teleportAddonLastOpenAt = DateTimeOffset.MinValue;

    private void PollPlayerActionEffect()
    {
        if (_gameProbe is null) return;

        var probe = _gameProbe.GetLastActionEffect();
        if (probe is null) return;
        var (sequence, ffxivActionType, actionId, tlX, tlY, tlZ) = probe.Value;

        if (_lastObservedActionSequence is null)
        {
            _lastObservedActionSequence = sequence;
            return;
        }

        if (sequence == _lastObservedActionSequence) return;

        _lastObservedActionSequence = sequence;

        // Item-first routing: check ItemKindMapper BEFORE ActionTypeMapper.
        // EventItem=3 is mapped by BOTH mappers; item-first ensures key item use
        // infers as "use-item" (via Rule 3.5i), not "use-action" (via Rule 3.5).
        var itemKind = QuestForge.Adapters.Items.ItemKindMapper.FromFFXIVActionType(ffxivActionType);
        if (itemKind is not null)
        {
            var targetBaseId = CaptureTargetBaseId();
            QuestForge.Schema.Position3? targetPosition =
                (tlX != 0f || tlY != 0f || tlZ != 0f)
                    ? new QuestForge.Schema.Position3(tlX, tlY, tlZ)
                    : null;
            var now   = _clock.UtcNow;
            var runId = CurrentRunId;
            WriteObservation("ItemUsed",
                actionId,
                new { kind = (int)itemKind.Value, targetBaseId = targetBaseId ?? 0u },
                runId, now);
            _aggregator?.OnItemUsed(itemKind.Value, actionId, targetBaseId, targetPosition);
            return;
        }

        var schemaType = QuestForge.Adapters.Actions.ActionTypeMapper.FromFFXIVActionType(ffxivActionType);
        if (schemaType is null) return;

        // GeneralAction IDs 5 (Teleport) and 6 (Return) are travel actions handled by
        // dedicated teleport inference (Rule 4.0), not use-action (Rule 3.5).
        if (schemaType == QuestForge.Schema.ActionType.GeneralAction && actionId is 5 or 6)
            return;

        {
            var targetBaseId = CaptureTargetBaseId();
            var now   = _clock.UtcNow;
            var runId = CurrentRunId;
            WriteObservation("ActionCompleted",
                actionId,
                new { actionType = (int)schemaType.Value, targetBaseId = targetBaseId ?? 0u },
                runId, now);
            _aggregator?.OnActionCompleted(schemaType.Value, actionId, targetBaseId);
        }
    }

    private uint? CaptureTargetBaseId()
    {
        uint? targetBaseId = null;
        var hostile      = _targetProbe?.GetBattleNpcTarget();
        var interactable = _targetProbe?.GetInteractableNpcTarget();
        if (hostile is { } h)
            targetBaseId = h.BaseId;
        else if (interactable is { } i)
            targetBaseId = i.BaseId;
        return targetBaseId;
    }

    private void PollPlayerEmote()
    {
        if (_gameProbe is null) return;

        var current = _gameProbe.GetPlayerEmoteId();
        if (current is null) return;   // no LocalPlayer
        var currentId = (uint)current.Value;

        // Same value — no transition.
        if (currentId == _lastObservedEmoteId) return;

        // Emote ended (X → 0) — reset state silently, no event.
        if (currentId == 0u)
        {
            _lastObservedEmoteId = 0u;
            return;
        }

        // Transition to a new non-zero emote (0 → X or X → Y). Fire.
        _lastObservedEmoteId = currentId;

        uint? targetBaseId = null;
        var hostile      = _targetProbe?.GetBattleNpcTarget();
        var interactable = _targetProbe?.GetInteractableNpcTarget();
        if (hostile is { } h)
            targetBaseId = h.BaseId;
        else if (interactable is { } i)
            targetBaseId = i.BaseId;

        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("EmoteCompleted",
            currentId,
            new { targetBaseId = targetBaseId ?? 0u },
            runId, now);
        _aggregator?.OnEmoteCompleted(currentId, targetBaseId);
    }

    private void PollPlayerChatMessage()
    {
        if (_gameProbe is null) return;

        var current = _gameProbe.GetChatLogMessageCount();
        if (current is null) return;
        var currentCount = current.Value;

        if (_lastObservedChatLogCount is null)
        {
            _lastObservedChatLogCount = currentCount;
            return;
        }

        if (currentCount == _lastObservedChatLogCount) return;

        if (currentCount < _lastObservedChatLogCount)
        {
            _lastObservedChatLogCount = currentCount;
            return;
        }

        var localPlayerName = _gameProbe.GetLocalPlayerName();

        var firstNewIndex = _lastObservedChatLogCount.Value;
        _lastObservedChatLogCount = currentCount;
        for (var idx = firstNewIndex; idx < currentCount; idx++)
        {
            var entry = _gameProbe.GetChatLogEntry(idx);
            if (entry is null) continue;

            if (!QuestForge.Adapters.Chat.ChatLogEntryFilter.IsPlayerSayMessage(
                    entry.LogKind, entry.SenderName, localPlayerName))
                continue;

            uint? targetBaseId = null;
            var hostile      = _targetProbe?.GetBattleNpcTarget();
            var interactable = _targetProbe?.GetInteractableNpcTarget();
            if (hostile is { } h)
                targetBaseId = h.BaseId;
            else if (interactable is { } i)
                targetBaseId = i.BaseId;

            var now   = _clock.UtcNow;
            var runId = CurrentRunId;
            WriteObservation("SayChatMessageSent",
                (uint)idx,
                new { message = entry.Message, targetBaseId = targetBaseId ?? 0u },
                runId, now);
            _aggregator?.OnSayChatMessageSent(entry.Message, targetBaseId);
        }
    }

    private void PollEquipmentChange()
    {
        if (_gameProbe is null) return;

        var current = _gameProbe.GetEquippedItemIds();
        if (current is null) return;

        if (_lastEquippedItemIds is null)
        {
            // First observation: establish baseline silently (no fire).
            _lastEquippedItemIds = new uint[current.Count];
            for (var i = 0; i < current.Count; i++)
                _lastEquippedItemIds[i] = current[i];
            return;
        }

        var newItems = new List<uint>();
        for (var i = 0; i < current.Count && i < _lastEquippedItemIds.Length; i++)
        {
            if (current[i] != _lastEquippedItemIds[i] && current[i] != 0u)
                newItems.Add(current[i]);
        }

        // Update baseline regardless of whether anything changed.
        for (var i = 0; i < current.Count && i < _lastEquippedItemIds.Length; i++)
            _lastEquippedItemIds[i] = current[i];

        if (newItems.Count == 0) return;

        _aggregator?.OnEquipmentChanged(newItems);
    }

    private void PollJobChange()
    {
        if (_gameProbe is null) return;

        var current = _gameProbe.GetCurrentClassJobId();
        if (current is null) return;

        if (_lastClassJobId is null)
        {
            // First observation: establish baseline silently (no fire).
            _lastClassJobId = current.Value;
            return;
        }

        if (current.Value == _lastClassJobId.Value) return;

        var oldJobId = _lastClassJobId.Value;
        _lastClassJobId = current.Value;

        _aggregator?.OnJobChanged(oldJobId, current.Value);
    }

    private void PollGearsetCount()
    {
        if (_gameProbe is null) return;

        var current = _gameProbe.GetGearsetCount();
        if (current is null) return;

        if (_lastGearsetCount is null)
        {
            // First observation: establish baseline silently (no fire).
            _lastGearsetCount = current.Value;
            return;
        }

        var old = _lastGearsetCount.Value;
        _lastGearsetCount = current.Value;

        // Only fire on increase (new gearset created). Decrease (deletion) and same: silent baseline update.
        if (current.Value > old)
            _aggregator?.OnGearsetRegistered(old, current.Value);
    }

    private void PollEventObjInteraction()
    {
        if (_targetProbe is null || _gameProbe is null) return;

        // Latch EventObj target every frame (before OccupiedInEvent check).
        // WHY: the game may clear the target once the event handler takes over, so we capture
        // the BaseId and position while the target is still available.
        var eventObj = _targetProbe.GetEventObjTarget();
        if (eventObj.HasValue)
        {
            _lastEventObjBaseId    = eventObj.Value.BaseId;
            _lastEventObjPosition  = (eventObj.Value.X, eventObj.Value.Y, eventObj.Value.Z, eventObj.Value.Zone);
        }

        // Detect OccupiedInEvent false→true transition.
        var occupied = _gameProbe.IsOccupiedInEvent();
        if (occupied && !_lastOccupiedInEvent && _lastEventObjBaseId != 0)
        {
            var now   = _clock.UtcNow;
            var runId = CurrentRunId;
            WriteObservation(
                "ObjectInteracted",
                _lastEventObjBaseId,
                new { x = _lastEventObjPosition.X, y = _lastEventObjPosition.Y,
                      z = _lastEventObjPosition.Z, zone = _lastEventObjPosition.Zone },
                runId,
                now);
            _aggregator?.OnObjectInteracted(
                _lastEventObjBaseId,
                _lastEventObjPosition.X,
                _lastEventObjPosition.Y,
                _lastEventObjPosition.Z);
            _lastEventObjBaseId = 0; // prevent re-fire until target is re-latched
        }
        _lastOccupiedInEvent = occupied;
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
                // Menu just closed with a selection — use captured NPC ID as argument so
                // qf-trace extract-quest can attribute the choice without time-proximity heuristics.
                var idx    = _pendingDialogueIdx.Value;
                var npcId  = _aggregator?.Current.DialogueNpcSource?.NpcId ?? 0u;
                var now    = _clock.UtcNow;
                var runId  = CurrentRunId;
                WriteObservation("DialogueOptionSelected", npcId, idx, runId, now);
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
                // Menu just closed — use captured NPC ID as argument so qf-trace extract-quest
                // can attribute the confirmation without time-proximity heuristics.
                const int yesOption = 0;
                var npcId  = _aggregator?.Current.DialogueNpcSource?.NpcId ?? 0u;
                var now    = _clock.UtcNow;
                var runId  = CurrentRunId;
                WriteObservation("SelectYesnoConfirmed", npcId, yesOption, runId, now);
                _aggregator?.OnSelectYesnoConfirmed(); // distinct from OnDialogueOptionSelected (SelectIconString list choices)
            }
            _selectYesnoWasOpen = false;
        }
    }

    /// <summary>
    /// Every-frame capture of the player's NON-hostile hard target: aethernet shards
    /// (OnAethernetShardTargeted / OnInteraction → Rule 2.5) and interactable NPCs
    /// (OnInteraction → LastNpcInteracted, drives talk Rule 7 / attune Rule 2.5).
    ///
    /// CRITICAL (D2 guarantee): a BattleNpc hard target must NOT be forwarded as OnInteraction,
    /// which would corrupt LastNpcInteracted with a mob id and emit a spurious talk step.
    /// GetInteractableNpcTarget() deliberately accepts BOTH EventNpc and BattleNpc (it serves the
    /// dialogue-capture path), so after splitting the hostile detection into the heartbeat
    /// PollBattleNpcTarget we guard here: if the hard target IS a BattleNpc, skip entirely. The
    /// hostile target is owned by PollBattleNpcTarget. This keeps the ITargetProbe contract intact
    /// (the dialogue-capture callers still see BattleNpc quest-givers) while localizing the
    /// combat / non-combat routing in the observer.
    /// </summary>
    private void PollTargetNpc()
    {
        if (_targetProbe is null) return;

        // GUARD: a BattleNpc hard target is owned by the heartbeat PollBattleNpcTarget.
        // Skip it here so it is never mis-routed to OnInteraction (D2 no-corruption guarantee).
        if (_targetProbe.GetBattleNpcTarget() is not null) return;

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

internal readonly record struct CombatHostileState(uint DataId, bool WasDead);
