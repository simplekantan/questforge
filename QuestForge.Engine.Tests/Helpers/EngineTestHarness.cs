using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;

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

    /// <summary>
    /// The FakeTraceWriter used for assertions in tests. In the default constructor,
    /// this is also the writer passed to the engine and recording proxies.
    /// In the external-trace constructor, this captures a copy of all events for
    /// assertion purposes while the external writer handles persistence.
    /// </summary>
    public FakeTraceWriter TraceWriter { get; }

    public QuestEngine Engine { get; }

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

        // Wrap GameState and QuestState with recording proxies.
        // The proxy reads runId lazily from the engine via the accessor.
        // engineRef is a local that will be assigned after construction.
        QuestEngine? engineRef = null;
        IGameStateProvider gameStateForEngine = new RecordingGameStateProvider(
            GameState, _effectiveTrace, () => engineRef?.CurrentRunId, skipIfNoRunId: true);
        IQuestState questStateForEngine = new RecordingQuestState(
            QuestState, _effectiveTrace, () => engineRef?.CurrentRunId, skipIfNoRunId: true);

        Engine = engineRef = new QuestEngine(
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
            NullLogger<QuestEngine>.Instance);
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

        for (var i = 0; i < maxTicks; i++)
        {
            var action = await Engine.Tick(ct);

            switch (action)
            {
                case EngineAction.Done:
                    return actions;

                case EngineAction.AwaitUser au:
                    throw new InvalidOperationException(
                        $"Engine returned AwaitUser unexpectedly at tick {i + 1}: {au.Reason}");

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

                case EngineAction.Wait:
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