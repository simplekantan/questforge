using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Actions;
using QuestForge.Adapters.Fakes.Chat;
using QuestForge.Adapters.Fakes.Emotes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Dialogue;
using QuestForge.Schema;

namespace QuestForge.Engine.Tests.Helpers;

/// <summary>
/// Creates a fully-wired engine instance backed by all fakes.
/// Use this in flow tests: configure state, wire callbacks, call RunToCompletion.
/// </summary>
public sealed class EngineTestHarness
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FakeGameStateProvider GameState { get; } = new FakeGameStateProvider();
    public FakeQuestState QuestState { get; }
    public FakeNavigator Navigator { get; }
    public FakeTeleporter Teleporter { get; }
    public FakeInteractor Interactor { get; }
    public FakeCombat Combat { get; } = new FakeCombat();
    public FakeGearManager GearManager { get; } = new FakeGearManager();
    public FakeMinigameSkipper MinigameSkipper { get; } = new FakeMinigameSkipper();
    public FakeDialogueResolver DialogueResolver { get; }
    public FakeTimingProfile TimingProfile { get; } = new FakeTimingProfile();
    public FakeVendor Vendor { get; }

    /// <summary>
    /// FakeMount exposed so tests can assert MountCallCount / DismountCallCount.
    /// The Builder will wire this into RunToCompletion's Navigate pre-switch hook.
    /// </summary>
    public FakeMount Mount { get; } = new FakeMount();

    public FakeActionExecutor ActionExecutor { get; } = new FakeActionExecutor();
    public FakeEmoteExecutor EmoteExecutor { get; } = new FakeEmoteExecutor();
    public FakeChatSender ChatSender { get; } = new FakeChatSender();

    /// <summary>
    /// The FakeTraceWriter used for assertions in tests. In the default constructor,
    /// this is also the writer passed to the engine and recording proxies.
    /// In the external-trace constructor, this captures a copy of all events for
    /// assertion purposes while the external writer handles persistence.
    /// </summary>
    public FakeTraceWriter TraceWriter { get; }

    /// <summary>
    /// Engine wrapper that mirrors EngineHost's pre-switch mount/dismount hooks inside Tick().
    /// Tests that call harness.Engine.Tick() directly will see mount/dismount side effects
    /// just as EngineHost.DispatchAction does — without requiring RunToCompletion.
    /// </summary>
    public HarnessEngine Engine { get; }

    // The effective writer used by the engine, proxies, and harness action emissions.
    private readonly ITraceWriter _effectiveTrace;

    /// <summary>
    /// Default constructor: uses an internal FakeTraceWriter.
    /// Recording proxies are wired so that observations are captured alongside engine events.
    /// </summary>
    public EngineTestHarness() : this(null) { }

    /// <summary>
    /// Constructor for end-to-end tests: accepts an external ITraceWriter
    /// (e.g. a real TraceWriter over a MemoryStream).
    /// All events (observations, engine events, action events) are written to the external writer.
    /// A copy is also written to the internal FakeTraceWriter (TraceWriter property).
    /// </summary>
    public EngineTestHarness(ITraceWriter? externalTrace)
    {
        TraceWriter = new FakeTraceWriter();

        // The effective writer routes to both the FakeTraceWriter and the external writer (if any).
        _effectiveTrace = externalTrace is not null
            ? new MultiplexTraceWriter(externalTrace, TraceWriter)
            : TraceWriter;

        QuestState = new FakeQuestState();
        Navigator = new FakeNavigator(GameState);
        Teleporter = new FakeTeleporter(GameState);
        Interactor = new FakeInteractor(GameState, QuestState);
        DialogueResolver = new FakeDialogueResolver();
        Vendor = new FakeVendor(GameState);

        // Wrap GameState and QuestState with recording proxies.
        // The proxy reads runId lazily from the engine via the accessor.
        // engineRef is a local that will be assigned after construction.
        QuestEngine? engineRef = null;
        IGameStateProvider gameStateForEngine = new RecordingGameStateProvider(
            GameState, _effectiveTrace, () => engineRef?.CurrentRunId, skipIfNoRunId: true);
        IQuestState questStateForEngine = new RecordingQuestState(
            QuestState, _effectiveTrace, () => engineRef?.CurrentRunId, skipIfNoRunId: true);

        var inner = engineRef = new QuestEngine(
            gameStateForEngine,
            questStateForEngine,
            Navigator,
            Teleporter,
            Interactor,
            Combat,
            GearManager,
            MinigameSkipper,
            DialogueResolver,
            TimingProfile,
            _effectiveTrace,
            NullLogger<QuestEngine>.Instance,
            vendor: Vendor,
            actionExecutor: ActionExecutor,
            emoteExecutor: EmoteExecutor,
            chatSender: ChatSender);
        Engine = new HarnessEngine(inner, GameState, Mount, Navigator);
    }

    /// <summary>
    /// Ticks the engine and executes actions against fakes until Done is returned.
    /// Records every action emitted (not including Done itself).
    /// Emits action.submitted and action.completed around each adapter call.
    /// Throws if AwaitUser is returned or maxTicks is exceeded.
    /// </summary>
    public async Task<List<EngineAction>> RunToCompletion(int maxTicks = 10)
    {
        var actions = new List<EngineAction>();
        var ct = CancellationToken.None;
        // Mirrors EngineHost: track whether the previous tick's action was Purchase so
        // we can lazily close the shop addon on the NEXT non-Purchase action (after the
        // engine confirms postcondition and moves on).
        var lastDispatchedWasPurchase = false;

        for (var i = 0; i < maxTicks; i++)
        {
            // Engine.Tick() is HarnessEngine.Tick(), which applies the mount/dismount
            // pre-switch hooks before returning the action (mirrors EngineHost.DispatchAction).
            var action = await Engine.Tick(ct);

            if (lastDispatchedWasPurchase && action is not EngineAction.Purchase)
            {
                await Vendor.Close(ct);
                lastDispatchedWasPurchase = false;
            }

            switch (action)
            {
                case EngineAction.Done:
                    return actions;

                case EngineAction.AwaitUser au:
                    throw new InvalidOperationException(
                        $"Engine returned AwaitUser unexpectedly at tick {i + 1}: {au.Reason}");

                case EngineAction.UseAethernet ua:
                    actions.Add(action);
                    EmitActionSubmitted("UseAethernet", JsonSerializer.SerializeToElement(ua.Destination, _jsonOpts));
                    var aethernetResult = await Teleporter.TeleportToAethernet(ua.Destination, ct);
                    EmitActionCompleted("UseAethernet", aethernetResult.IsSuccess ? aethernetResult.ValueOrThrow.ToString() : "Failed");
                    break;

                case EngineAction.Teleport tp:
                    actions.Add(action);
                    EmitActionSubmitted("Teleport", JsonSerializer.SerializeToElement(tp.Destination, _jsonOpts));
                    var tpResult = await Teleporter.TeleportToAetheryte(tp.Destination, ct);
                    EmitActionCompleted("Teleport", tpResult.IsSuccess ? tpResult.ValueOrThrow.ToString() : "Failed");
                    break;

                case EngineAction.Navigate nav:
                    actions.Add(action);
                    EmitActionSubmitted("Navigate", JsonSerializer.SerializeToElement(nav.Destination, _jsonOpts));
                    var navResult = await Navigator.NavigateTo(nav.Destination, nav.Options, ct);
                    EmitActionCompleted("Navigate", navResult.IsSuccess ? navResult.ValueOrThrow.ToString() : "Failed");
                    break;

                case EngineAction.Interact interact:
                    actions.Add(action);
                    EmitActionSubmitted("Interact", JsonSerializer.SerializeToElement(interact.Target, _jsonOpts));
                    var interactResult = await Interactor.InteractWith(interact.Target, ct);
                    EmitActionCompleted("Interact", interactResult.IsSuccess ? "Done" : "Failed");
                    break;

                case EngineAction.HandOver handOver:
                    actions.Add(action);
                    EmitActionSubmitted("HandOver", JsonSerializer.SerializeToElement(handOver.Target, _jsonOpts));
                    var handOverResult = await Interactor.HandOverItem(handOver.Items, handOver.Target, ct);
                    EmitActionCompleted("HandOver", handOverResult.IsSuccess ? handOverResult.ValueOrThrow.ToString() : "Failed");
                    break;

                case EngineAction.Purchase purchase:
                    actions.Add(action);
                    EmitActionSubmitted("Purchase", JsonSerializer.SerializeToElement(purchase.Vendor, _jsonOpts));
                    var purchaseResult = await Vendor.Purchase(purchase.Vendor, purchase.Item, purchase.Quantity, purchase.Currency, ct, purchase.GcCategory, purchase.GcRankTier);
                    EmitActionCompleted("Purchase", purchaseResult.IsSuccess ? purchaseResult.ValueOrThrow.ToString() : "Failed");
                    lastDispatchedWasPurchase = true;
                    break;

                case EngineAction.UseAction ua:
                    actions.Add(action);
                    EmitActionSubmitted("UseAction", JsonSerializer.SerializeToElement(
                        new { type = ua.Type.ToString(), actionId = ua.ActionId, targetNpcId = ua.TargetNpcId?.Value },
                        _jsonOpts));
                    var uaResult = await ActionExecutor.UseAction(ua.Type, ua.ActionId, ua.TargetNpcId, ct);
                    EmitActionCompleted("UseAction", uaResult.IsSuccess ? "Done" : "Failed");
                    break;

                case EngineAction.UseEmote ue:
                    actions.Add(action);
                    EmitActionSubmitted("UseEmote", JsonSerializer.SerializeToElement(
                        new { emoteId = ue.EmoteId, targetNpcId = ue.TargetNpcId?.Value, motion = ue.Motion },
                        _jsonOpts));
                    var ueResult = await EmoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
                    EmitActionCompleted("UseEmote", ueResult.IsSuccess ? "Done" : "Failed");
                    break;

                case EngineAction.SayChatMessage sc:
                    actions.Add(action);
                    EmitActionSubmitted("SayChatMessage", JsonSerializer.SerializeToElement(
                        new { message = sc.Message, targetNpcId = sc.TargetNpcId?.Value },
                        _jsonOpts));
                    var scResult = await ChatSender.Send(sc.Message, sc.TargetNpcId, ct);
                    EmitActionCompleted("SayChatMessage", scResult.IsSuccess ? "Done" : "Failed");
                    break;

                case EngineAction.Wait:
                    break;

                case EngineAction.Engage:
                    // Combat actions are recorded but require no adapter call in the harness.
                    // The test's wired callbacks (FakeQuestState / FakeGameStateProvider setters)
                    // advance the fake world so the next tick can re-evaluate expect.
                    actions.Add(action);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled EngineAction subtype: {action.GetType().Name}");
            }
        }

        throw new InvalidOperationException(
            $"Engine did not reach Done within {maxTicks} ticks. Actions so far: [{string.Join(", ", actions)}]");
    }

    private void EmitActionSubmitted(string actionType, System.Text.Json.JsonElement? parameters)
    {
        try
        {
            _effectiveTrace.Write(new ActionSubmittedEvent(
                Engine.CurrentRunId, actionType, parameters, DateTimeOffset.UtcNow));
        }
        catch { /* best effort */ }
    }

    private void EmitActionCompleted(string actionType, string outcome)
    {
        try
        {
            _effectiveTrace.Write(new ActionCompletedEvent(
                Engine.CurrentRunId, actionType, outcome, DateTimeOffset.UtcNow));
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Wraps QuestEngine and applies the EngineHost mount/dismount pre-switch hooks inside Tick().
/// Mount predicate fires before Navigate returns; dismount fires before the first non-Navigate
/// after a Navigate. This mirrors EngineHost.DispatchAction so that direct Tick() calls in
/// tests see the same mount/dismount side effects as a full RunToCompletion dispatch loop.
///
/// NOTE (Q4): mount/dismount logic lives here (host boundary), not inside QuestEngine itself,
/// so replay tests against QuestEngine.Tick() directly do not see the extra GetPlayerState reads
/// and existing replay fixtures are unaffected.
/// </summary>
public sealed class HarnessEngine
{
    private readonly QuestEngine _inner;
    private readonly FakeGameStateProvider _gameState;
    private readonly FakeMount _mount;
    private readonly FakeNavigator _navigator;
    private bool _lastDispatchedWasNavigate;
    private const float MountDistanceThresholdMeters = 20f;

    internal HarnessEngine(QuestEngine inner, FakeGameStateProvider gameState, FakeMount mount, FakeNavigator navigator)
    {
        _inner = inner;
        _gameState = gameState;
        _mount = mount;
        _navigator = navigator;
    }

    public string? CurrentRunId => _inner.CurrentRunId;
    public YesNoAnswer? CurrentYesNoAnswer => _inner.CurrentYesNoAnswer;

    public void StartQuest(QuestDefinition quest) => _inner.StartQuest(quest);

    public void StartQuest(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments)
        => _inner.StartQuest(quest, fragments);

    public void BeginRun(string runId) => _inner.BeginRun(runId);

    /// <summary>
    /// Ticks the underlying engine, applies the mount/dismount hooks (mirroring
    /// EngineHost.DispatchAction), and returns the resulting action.
    /// </summary>
    public async Task<EngineAction> Tick(CancellationToken ct)
    {
        var action = await _inner.Tick(ct);

        // Lazy dismount: if the previous dispatch was Navigate and the engine has now
        // moved past it (postcondition met → emitted a non-Navigate action), dismount
        // BEFORE the action is returned to the caller. Flying-dismount may cause fall damage
        // (v1 limitation); vnavmesh usually grounds at stop-distance.
        // Teleport is exempt: the game dismounts on arrival if the destination prohibits mounts.
        if (_lastDispatchedWasNavigate && action is not EngineAction.Navigate and not EngineAction.Teleport)
        {
            // Mirror EngineHost: only consider dismount when vnavmesh has actually stopped.
            // The engine emits Engage early (target in scan range) while the CombatController
            // is internally navigating toward the target; dismounting on that transition drops
            // a flying player out of the sky mid-approach.
            var navResult = await _navigator.IsNavigating(ct);
            var stillNavigating = navResult is Result<bool>.Success { Value: true };

            if (!stillNavigating)
            {
                var stateResult = await _gameState.GetPlayerState(ct);
                if (stateResult is Result<PlayerStateSnapshot>.Success { Value: var ps })
                {
                    if (ps.MountState != MountState.Dismounted)
                    {
                        // Flying dismount is two-step: first call lands, second dismounts.
                        // Re-fire each tick until MountState confirms Dismounted.
                        await _mount.Dismount(ct);
                    }
                    else
                    {
                        _lastDispatchedWasNavigate = false;
                    }
                }
                else
                {
                    _lastDispatchedWasNavigate = false;
                }
            }
            // If still navigating: keep the flag set; re-check next tick.
        }

        // Mount predicate: fire Mount Roulette when a Navigate is dispatched and all
        // preconditions hold (per MOUNT_SUPPORT_PLAN.md §2 decision + §3 Q2 cast safeguard).
        if (action is EngineAction.Navigate nav && nav.Options.UseMount)
        {
            var stateResult = await _gameState.GetPlayerState(ct);
            if (stateResult is Result<PlayerStateSnapshot>.Success { Value: var ps }
                && ps.MountState == MountState.Dismounted
                && !ps.InCombat
                && ps.CanMount
                && !ps.Casting
                && ps.Position.DistanceTo(nav.Destination) >= MountDistanceThresholdMeters)
            {
                await _mount.Mount(ct);
            }
            _lastDispatchedWasNavigate = true;
            // Note: do NOT call _navigator.NavigateTo here. HarnessEngine.Tick mirrors only
            // EngineHost's pre-switch hooks (mount predicate + lazy dismount), NOT the
            // dispatch arm itself. RunToCompletion's Navigate case is the dispatch path.
        }

        return action;
    }
}

/// <summary>
/// A trace writer that forwards to two underlying writers.
/// Used when the harness needs to write to both an external writer and the internal FakeTraceWriter.
/// </summary>
file sealed class MultiplexTraceWriter : ITraceWriter
{
    private readonly ITraceWriter _primary;
    private readonly ITraceWriter _secondary;

    public MultiplexTraceWriter(ITraceWriter primary, ITraceWriter secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public void Write(TraceEvent evt)
    {
        _primary.Write(evt);
        _secondary.Write(evt);
    }
}