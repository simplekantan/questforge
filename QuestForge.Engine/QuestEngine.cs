using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QuestForge.Adapters;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Schema;

namespace QuestForge.Engine;

public sealed class QuestEngine
{
    private readonly IGameStateProvider _gameState;
    private readonly IQuestState _questState;
    private readonly INavigator _navigator;
    private readonly ITeleporter _teleporter;
    private readonly IInteractor _interactor;
    private readonly ICombat _combat;
    private readonly IGearManager _gear;
    private readonly IMinigameSkipper _minigames;
    private readonly IDialogueResolver _dialogue;
    private readonly ITimingProfile _timing;
    private readonly ITraceWriter _trace;
    private readonly ILogger<QuestEngine> _logger;
    private readonly ExpectEvaluator _expectEvaluator;

    private QuestDefinition? _quest;
    private string? _runId;
    private bool _runStartEmitted;
    private readonly HashSet<string> _confirmedStepIds = new();
    private int _lastKnownSequence = -1;

    public QuestEngine(
        IGameStateProvider gameState,
        IQuestState questState,
        INavigator navigator,
        ITeleporter teleporter,
        IInteractor interactor,
        ICombat combat,
        IGearManager gear,
        IMinigameSkipper minigames,
        IDialogueResolver dialogue,
        ITimingProfile timing,
        ITraceWriter trace,
        ILogger<QuestEngine> logger)
    {
        _gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _questState = questState ?? throw new ArgumentNullException(nameof(questState));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _teleporter = teleporter ?? throw new ArgumentNullException(nameof(teleporter));
        _interactor = interactor ?? throw new ArgumentNullException(nameof(interactor));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _gear = gear ?? throw new ArgumentNullException(nameof(gear));
        _minigames = minigames ?? throw new ArgumentNullException(nameof(minigames));
        _dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _expectEvaluator = new ExpectEvaluator(new PredicateEvaluator(gameState, questState));
    }

    public void StartQuest(QuestDefinition quest)
        => StartQuest(quest, fragments: null);

    public void StartQuest(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments = null)
    {
        if (quest is null) throw new ArgumentNullException(nameof(quest));

        // Track per-Ref usage counts for scoped ID generation across the whole quest.
        var usageCount = new Dictionary<string, int>(StringComparer.Ordinal);

        var rewrittenSequences = quest.Sequences.Select(seq => new QuestSequence
        {
            Sequence = seq.Sequence,
            SkipIf = seq.SkipIf,
            Steps = ExpandSteps(seq.Steps, fragments, usageCount).ToArray()
        }).ToArray();

        _quest = quest with { Sequences = rewrittenSequences };
    }

    // -------------------------------------------------------------------------
    // Fragment expansion helpers
    // -------------------------------------------------------------------------

    private static IEnumerable<Step> ExpandSteps(
        Step[] steps,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments,
        Dictionary<string, int> usageCount)
    {
        foreach (var step in steps)
        {
            if (step is not FragmentStep fragmentStep)
            {
                yield return step;
                continue;
            }

            // Error: no fragments dict but quest contains a FragmentStep
            if (fragments is null)
                throw new InvalidOperationException(
                    $"Quest contains FragmentStep '{fragmentStep.Id}' (Ref='{fragmentStep.Ref}') " +
                    "but no fragment dictionary was provided to StartQuest.");

            // Error: referenced fragment not found
            if (!fragments.TryGetValue(fragmentStep.Ref, out var fragmentDef))
                throw new InvalidOperationException(
                    $"FragmentStep '{fragmentStep.Id}' references unknown fragment '{fragmentStep.Ref}'.");

            // Error: nested FragmentStep inside a fragment
            foreach (var innerStep in fragmentDef.Steps)
            {
                if (innerStep is FragmentStep nestedFrag)
                    throw new InvalidOperationException(
                        $"Fragment '{fragmentDef.FragmentId}' contains a nested FragmentStep " +
                        $"('{nestedFrag.Id}', Ref='{nestedFrag.Ref}'). Nested fragments are not supported.");
            }

            // Validate required parameters
            foreach (var param in fragmentDef.Parameters)
            {
                if (!param.Required) continue;
                if (fragmentStep.Params is null || !fragmentStep.Params.ContainsKey(param.Name))
                    throw new ArgumentException(
                        $"FragmentStep '{fragmentStep.Id}' is missing required parameter '{param.Name}' " +
                        $"declared by fragment '{fragmentDef.FragmentId}'.");
            }

            // Determine scope prefix (first use: "refId:", subsequent: "refId#N:")
            if (!usageCount.TryGetValue(fragmentStep.Id, out var count))
                count = 0;
            count++;
            usageCount[fragmentStep.Id] = count;
            var scopePrefix = count == 1 ? $"{fragmentStep.Id}:" : $"{fragmentStep.Id}#{count}:";

            // Expand each inner step
            foreach (var innerStep in fragmentDef.Steps)
            {
                var scopedId   = $"{scopePrefix}{innerStep.Id}";
                var substituted = SubstituteExpect(innerStep.Expect, fragmentStep.Params);
                yield return CloneStepWith(innerStep, scopedId, substituted);
            }
        }
    }

    /// <summary>
    /// Substitutes <c>${name}</c> tokens in a <see cref="PredicateExpect"/> predicate string.
    /// Returns the original <paramref name="expect"/> unchanged when no tokens are present
    /// or when the expect is null / not a <see cref="PredicateExpect"/>.
    /// </summary>
    private static ExpectValue? SubstituteExpect(
        ExpectValue? expect,
        IReadOnlyDictionary<string, JsonElement>? parms)
    {
        if (expect is not PredicateExpect pe || parms is null) return expect;

        var predicate = pe.Predicate;
        var result = Regex.Replace(predicate, @"\$\{([^}]+)\}", match =>
        {
            var name = match.Groups[1].Value;
            if (!parms.TryGetValue(name, out var elem)) return match.Value; // leave unresolved token
            return elem.ValueKind switch
            {
                JsonValueKind.String => elem.GetString() ?? string.Empty,
                JsonValueKind.Number => elem.GetRawText(),
                _                   => elem.ToString()
            };
        });

        if (string.Equals(result, predicate, StringComparison.Ordinal)) return expect; // unchanged
        return new PredicateExpect { Predicate = result };
    }

    /// <summary>
    /// Clones <paramref name="source"/> with a new <paramref name="id"/> and
    /// optionally replaced <paramref name="expect"/>, using round-trip JSON serialization
    /// so that all concrete step subtypes are handled without a large switch.
    /// </summary>
    private static Step CloneStepWith(Step source, string id, ExpectValue? expect)
    {
        // Serialize as the base Step type so the [JsonPolymorphic] discriminator ("type") is written.
        var json = JsonSerializer.Serialize<Step>(source, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json)!.AsObject();

        node["id"] = JsonValue.Create(id);

        if (expect is null)
        {
            node.Remove("expect");
        }
        else
        {
            // Re-serialize the substituted ExpectValue through the registered converter.
            var expectJson = JsonSerializer.Serialize(expect, QuestForgeJsonContext.QuestFileOptions);
            node["expect"] = JsonNode.Parse(expectJson);
        }

        var patched = node.ToJsonString();
        return JsonSerializer.Deserialize<Step>(patched, QuestForgeJsonContext.QuestFileOptions)!;
    }

    public string? CurrentRunId => _runId;

    public void BeginRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId must be non-empty", nameof(runId));
        _runId = runId;
        _runStartEmitted = false;
        _confirmedStepIds.Clear();
        _lastKnownSequence = -1;
    }

    public async Task<EngineAction> Tick(CancellationToken ct)
    {
        if (_quest is null)
            return new EngineAction.AwaitUser("no quest loaded");

        EmitRunStartIfNeeded();

        var (action, stepId) = await ResolveAction(ct);

        if (_runId is not null)
        {
            if (action is EngineAction.Done)
            {
                // Done terminates the run Ã¢â‚¬â€ emit run.end instead of a decision event.
                TraceSafe(new RunEndEvent(_runId, Outcome: "done", DateTimeOffset.UtcNow));
                _runStartEmitted = false;
            }
            else if (action is EngineAction.AwaitUser)
            {
                // AwaitUser terminates the run Ã¢â‚¬â€ emit run.end.
                // Also emit the decision so the caller knows why we stopped.
                TraceSafe(new DecisionEvent(
                    RunId: _runId,
                    StepId: stepId,
                    ActionType: action.GetType().Name,
                    At: DateTimeOffset.UtcNow));
                TraceSafe(new RunEndEvent(_runId, Outcome: "awaitUser", DateTimeOffset.UtcNow));
            }
            else
            {
                TraceSafe(new DecisionEvent(
                    RunId: _runId,
                    StepId: stepId,
                    ActionType: action.GetType().Name,
                    At: DateTimeOffset.UtcNow));
            }
        }

        return action;
    }

    private void EmitRunStartIfNeeded()
    {
        if (_runStartEmitted || _runId is null || _quest is null) return;
        TraceSafe(new RunStartEvent(
            RunId: _runId,
            QuestId: _quest.Id,
            QuestSchemaId: _quest.Id,
            At: DateTimeOffset.UtcNow));
        _runStartEmitted = true;
    }

    private void TraceSafe(TraceEvent evt)
    {
        try { _trace.Write(evt); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trace write failed for event {Type}; continuing without trace", evt.Type);
        }
    }

    private async Task<(EngineAction action, string? stepId)> ResolveAction(CancellationToken ct)
    {
        var questId = new QuestId(_quest!.Id);

        var seqResult = await _questState.GetQuestSequence(questId, ct);
        if (seqResult is Result<int>.Failure f1)
            return (new EngineAction.AwaitUser($"adapter failure reading sequence: {f1.Reason}"), null);

        var completeResult = await _questState.IsQuestComplete(questId, ct);
        if (completeResult is Result<bool>.Success { Value: true })
            return (new EngineAction.Done(), null);

        var currentSeq = seqResult.ValueOrThrow;
        var matchingBlock = _quest.Sequences.FirstOrDefault(s => s.Sequence == currentSeq);
        if (matchingBlock is null)
            return (new EngineAction.AwaitUser($"no sequence block matches current sequence {currentSeq}"), null);

        if (matchingBlock.SkipIf is not null)
        {
            if (await _expectEvaluator.Evaluate(matchingBlock.SkipIf, ct))
                return (new EngineAction.AwaitUser("sequence skipped by skipIf Ã¢â‚¬â€ engine cannot self-advance in Phase 4"), null);
        }

        // Read UiState once per tick so step dispatch arms can inspect UI without async.
        // On adapter failure: use a safe default (CutscenePlaying=false) so non-cutscene
        // steps are completely unaffected. A CutsceneStep will emit
        // Wait("cutscene ended; awaiting sequence advance") Ã¢â‚¬â€ recoverable on the next tick.
        var uiResult = await _gameState.GetUiState(ct);
        var ui = uiResult is Result<UiState>.Success { Value: var uiValue }
            ? uiValue
            : new UiState(false, false, false, false, false, false, false, false, null);

        // Read player position once per tick for implied navigation distance checks.
        // On adapter failure: use null so distance checks fail-open (emit Interact, not Navigate).
        var posResult = await _gameState.GetPlayerPosition(ct);
        var playerPos = posResult is Result<WorldPosition>.Success { Value: var p } ? p : (WorldPosition?)null;

        // Detect sequence change and clear the confirmed-step cursor.
        // Confirmations are scoped to the current sequence block - when the game advances
        // (or rewinds) to a new sequence, prior confirmations are no longer meaningful.
        if (_lastKnownSequence != -1 && _lastKnownSequence != currentSeq)
            _confirmedStepIds.Clear();
        _lastKnownSequence = currentSeq;

        foreach (var step in matchingBlock.Steps)
        {
            // 1. Cursor check FIRST - confirmed steps always skipped, predicate not re-evaluated.
            //    Confirming a step means "this step was satisfied at some point this sequence";
            //    do not re-check the predicate (the player may have moved away between ticks).
            if (_confirmedStepIds.Contains(step.Id))
                continue;

            // 2. Expect: if true, confirm and skip. Confirmation persists until BeginRun or
            //    sequence change clears the cursor.
            if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
            {
                _confirmedStepIds.Add(step.Id);
                continue;
            }

            // 3. SkipIf: skip but do NOT confirm (author logic, not a completion signal).
            if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct))
                continue;

            return (ResolveActionForStep(step, ui, playerPos), step.Id);
        }

        return (new EngineAction.Wait("all steps in current sequence satisfied; awaiting game sequence advance"), null);
    }

    private const float DefaultStopDistance        = 3.0f;
    private const float DefaultAetheryteStopDistance = 7.0f; // crystals are large; need more clearance

    private EngineAction ResolveActionForStep(Step step, UiState ui, WorldPosition? playerPos) => step switch
    {
        // NpcDialogue routing: navigate to NPC position, then interact with the NPC.
        // DialogueChoiceDispatcher reads choices from TravelStep.RouteHint.NpcDialogue via Origin.
        TravelStep travel when travel.RouteHint?.NpcDialogue is { } npcHint =>
            ResolveInteractOrNavigate(
                step, npcHint.Target.Position, playerPos,
                new EngineAction.Interact(new NpcId(npcHint.Target.NpcId), Origin: step)),

        // Aethernet routing: navigate to the source shard (Destination.Position) first,
        // then emit UseAethernet once in range. Lifestream chains from there automatically.
        // If no source position is specified, emit UseAethernet directly (caller ensures proximity).
        TravelStep travel when travel.RouteHint?.Aethernet is { To: > 0 } hop =>
            travel.Destination.Position is { } sourcePos
                ? ResolveInteractOrNavigate(step, sourcePos, playerPos,
                    new EngineAction.UseAethernet(
                        new AethernetId(hop.To),
                        new WorldPosition(sourcePos.X, sourcePos.Y, sourcePos.Z)))
                : new EngineAction.UseAethernet(new AethernetId(hop.To)),

        TravelStep travel when travel.Destination.Position is { } pos =>
            new EngineAction.Navigate(
                new WorldPosition(pos.X, pos.Y, pos.Z),
                new NavigationOptions(StoppingDistance: step.StopDistance ?? DefaultStopDistance)),

        TravelStep travel when travel.Destination.Position is null =>
            throw new NotSupportedException("Phase 4 does not support aetheryte-only travel steps"),

        TalkStep talk when talk.Target is not null =>
            ResolveInteractOrNavigate(
                step, talk.Target.Position, playerPos,
                new EngineAction.Interact(new NpcId(talk.Target.NpcId), Origin: step)),

        TalkStep talk when talk.Target is null && talk.Targets is { Length: > 0 } =>
            throw new NotSupportedException("Phase 4 does not support multi-target talk steps"),

        AcceptStep accept =>
            ResolveInteractOrNavigate(
                step, accept.Target.Position, playerPos,
                new EngineAction.Interact(new NpcId(accept.Target.NpcId), Origin: step)),

        TurnInStep turnIn =>
            ResolveInteractOrNavigate(
                step, turnIn.Target.Position, playerPos,
                new EngineAction.Interact(new NpcId(turnIn.Target.NpcId), Origin: step)),

        // TODO: replace with EngineAction.InteractObject(InteractableId) once that action type exists.
        // Coercing InteractableId into NpcId is a shim Ã¢â‚¬â€ the in-range Interact path is not yet
        // reachable from tests (only the Navigate half is exercised by B5).
        InteractObjectStep interactObj =>
            ResolveInteractOrNavigate(
                step, interactObj.Target.Position, playerPos,
                new EngineAction.Interact(new NpcId(interactObj.Target.InteractableId), Origin: step)),

        AttunementStep attune when attune.Location is not null =>
            ResolveInteractOrNavigate(
                step, attune.Location.Position, playerPos,
                new EngineAction.Interact(new NpcId(attune.Target.Value), Origin: step),
                defaultStopDistance: DefaultAetheryteStopDistance),

        AttunementStep attune => new EngineAction.Interact(new NpcId(attune.Target.Value), Origin: step),

        HandOverItemStep handOver =>
            ResolveInteractOrNavigate(
                step, handOver.Target.Position, playerPos,
                new EngineAction.HandOver(
                    new NpcId(handOver.Target.NpcId),
                    handOver.Items.Select(id => new ItemId(id)).ToArray())),

        CutsceneStep => ui.CutscenePlaying
            ? new EngineAction.Wait("cutscene playing")
            : new EngineAction.Wait("cutscene ended; awaiting sequence advance"),

        _ => throw new NotSupportedException($"Phase 4 does not support step type {step.GetType().Name}")
    };

    private static EngineAction ResolveInteractOrNavigate(
        Step step, Position3 targetPos, WorldPosition? playerPos, EngineAction interactAction,
        float defaultStopDistance = DefaultStopDistance)
    {
        if (playerPos is null) return interactAction; // fail-open: position unavailable
        var stopDist = step.StopDistance ?? defaultStopDistance;
        var target = new WorldPosition(targetPos.X, targetPos.Y, targetPos.Z);
        if (playerPos.Value.DistanceTo(target) <= stopDist) return interactAction;
        return new EngineAction.Navigate(target, new NavigationOptions(StoppingDistance: stopDist));
    }
}
