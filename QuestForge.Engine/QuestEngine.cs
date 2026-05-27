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
using QuestForge.Engine.Combat;
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
    private readonly TimeProvider _clock;
    private readonly ExpectEvaluator _expectEvaluator;

    /// <summary>Exposed for test inspection (EX-reset-on-advance). Internal to the engine assembly.</summary>
    internal readonly CombatController _combatController;

    private QuestDefinition? _quest;
    private string? _runId;
    private bool _runStartEmitted;
    private readonly HashSet<string> _confirmedStepIds = new();
    private DateTimeOffset? _waitStepStart;
    private string? _waitStepStartId;
    private int _lastKnownSequence = -1;
    private IReadOnlyDictionary<string, FragmentDefinition>? _fragments;
    private readonly HashSet<string> _resumePointExecutedIds = new();
    private ActiveResumeFragment? _activeResumeFragment;

    private sealed record ActiveResumeFragment(
        string ForStepId,
        string RequiredZone,
        Step MainStep,
        IReadOnlyList<Step> Steps,
        HashSet<string> ConfirmedFragmentStepIds);

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
        ILogger<QuestEngine> logger,
        TimeProvider? clock = null)
    {
        _gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _clock = clock ?? TimeProvider.System;
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

        _expectEvaluator    = new ExpectEvaluator(new PredicateEvaluator(gameState, questState));
        _combatController   = new CombatController(gameState, combat, navigator);
    }

    public void StartQuest(QuestDefinition quest)
        => StartQuest(quest, fragments: null);

    public void StartQuest(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments = null)
    {
        if (quest is null) throw new ArgumentNullException(nameof(quest));

        _fragments = fragments;

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
        _resumePointExecutedIds.Clear();
        _activeResumeFragment = null;
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

        // Read NG+ state once per tick, fail-open: a failed read treats IsActive=false
        // so the normal-play gate is preserved. A NG+ read failure must never suppress it.
        var ngpResult = await _gameState.GetNewGamePlusState(ct);
        var ngp = ngpResult is Result<NewGamePlusState>.Success { Value: var ngpVal }
            ? ngpVal
            : new NewGamePlusState(false, null, false);

        bool replayActive = ngp.IsActive && !ngp.IsSuspended;

        if (ngp.IsActive && ngp.IsSuspended)
            return (new EngineAction.Wait("ng+ replay suspended"), null);

        if (!replayActive)
        {
            var completeResult = await _questState.IsQuestComplete(questId, ct);
            if (completeResult is Result<bool>.Success { Value: true })
                return (new EngineAction.Done(), null);
        }
        // else: replay active and not suspended — skip the IsQuestComplete gate (bitmap lies)
        //       and fall through to the live-sequence loop below.

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

        // Read player zone once per tick for RequiredZone gating.
        // On adapter failure: null — a null zone never satisfies a RequiredZone gate and never triggers
        // a resume (we cannot prove the player is in the wrong zone).
        var zoneResult = await _gameState.GetPlayerZone(ct);
        var playerZone = zoneResult is Result<ZoneId>.Success { Value: var z } ? (ZoneId?)z : null;

        // Read the active quest's work variables (V0–V5) once per tick.
        // WHY (no consumer yet): this read exists purely so the recording proxy
        // (RecordingQuestState) captures a GetQuestVariables observation — it dedups and
        // emits one ONLY when the bytes change, so going-forward traces carry variable
        // changes. It is also anticipatory: the imminent questVariable(...) predicate will
        // read variables through this same IQuestState path. The result is intentionally
        // DISCARDED — it must never influence the engine's decision this tick. Fail-open
        // like the sibling reads (no branch, no throw).
        _ = await _questState.GetQuestVariables(questId, ct);

        // Detect sequence change and clear the confirmed-step cursor.
        // Confirmations are scoped to the current sequence block - when the game advances
        // (or rewinds) to a new sequence, prior confirmations are no longer meaningful.
        if (_lastKnownSequence != -1 && _lastKnownSequence != currentSeq)
        {
            _confirmedStepIds.Clear();
            _resumePointExecutedIds.Clear();
            _activeResumeFragment = null;
            _waitStepStart = null;
            _waitStepStartId = null;
            await _combatController.ResetAsync(ct);
        }
        _lastKnownSequence = currentSeq;

        // Process any active resume sub-loop FIRST, before the main step loop.
        if (_activeResumeFragment is { } resume)
        {
            var (resumeAction, resumeStepId, resumeDone) =
                await ProcessActiveResume(resume, playerZone, ui, playerPos, ct);

            if (!resumeDone)
                return (resumeAction!, resumeStepId);

            _resumePointExecutedIds.Add(resume.ForStepId);
            _activeResumeFragment = null;
        }

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
                // If the confirmed step was a CombatStep, reset the controller so the next
                // combat step starts with clean state.
                if (step is CombatStep)
                    await _combatController.ResetAsync(ct);
                continue;
            }

            // 3. SkipIf: skip but do NOT confirm (author logic, not a completion signal).
            if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct))
                continue;

            // 4. Resume-point trigger: arm the resume sub-loop iff all four conditions hold:
            //    (a) step not confirmed — guaranteed here (cursor check above)
            //    (b) step.RequiredZone set AND player is NOT already in it
            //    (c) step.ResumePointFragmentId is set
            //    (d) step.Id not already in _resumePointExecutedIds
            if (step.ResumePointFragmentId is { } fragId
                && step.RequiredZone is { } reqZone
                && !ZoneAlreadySatisfied(playerZone, reqZone)
                && !_resumePointExecutedIds.Contains(step.Id))
            {
                var armed = ArmResumeFragment(step, fragId, reqZone);
                var (action, stepId, done) = await ProcessActiveResume(armed, playerZone, ui, playerPos, ct);
                if (done)
                {
                    _resumePointExecutedIds.Add(step.Id);
                    _activeResumeFragment = null;
                    return (ResolveActionForStep(step, ui, playerPos), step.Id);
                }
                _activeResumeFragment = armed;
                return (action!, stepId);
            }

            // 5. CombatStep async arm — step-gated so GetHostileActors is NEVER called on the
            //    common per-tick path, preventing fixture-starvation for non-combat steps (D6).
            //    Option B: controller consulted FIRST every combat tick. The fixed Location is
            //    only a fall-back when no eligible target is in scan range (D1, D2).
            if (step is CombatStep combatStep)
            {
                // Controller FIRST: scan for targets, select, issue approach navigation if needed.
                var decision = await _combatController.Decide(combatStep, ct);

                if (decision.Target is not null)
                {
                    // A target is in range — controller already owns approach navigation (it issued
                    // NavigateTo inside Decide). Do NOT navigate to Location. Engage.
                    return (new EngineAction.Engage(combatStep, decision.Target), step.Id);
                }

                // No eligible target in scan range. If the player has drifted beyond StopDistance
                // of the spawn anchor, navigate back so respawns land in scan range again.
                if (combatStep.Location is not null)
                {
                    var fallback = ResolveInteractOrNavigate(
                        step, combatStep.Location.Position, playerPos,
                        new EngineAction.Wait("combat-roam-sentinel"));
                    if (fallback is EngineAction.Navigate navAction)
                        return (navAction, step.Id);
                }

                // No target AND at/within Location (or Location unset): idle — waits for respawns.
                // Engage(null) is a forward decision, never a stall (D6).
                return (new EngineAction.Engage(combatStep, null), step.Id);
            }

            // 6. WaitStep arm — time-based completion, no game-state predicate consulted.
            if (step is WaitStep waitStep)
            {
                if (_waitStepStartId != step.Id)
                {
                    _waitStepStart = _clock.GetUtcNow();
                    _waitStepStartId = step.Id;
                    return (new EngineAction.Wait($"waiting {waitStep.Seconds}s"), step.Id);
                }

                if (_clock.GetUtcNow() - _waitStepStart!.Value >= TimeSpan.FromSeconds(waitStep.Seconds))
                {
                    _confirmedStepIds.Add(step.Id);
                    _waitStepStart = null;
                    _waitStepStartId = null;
                    continue;
                }

                return (new EngineAction.Wait($"waiting {waitStep.Seconds}s"), step.Id);
            }

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

        AwaitUserStep au => new EngineAction.AwaitUser(au.Reason),

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

    private async Task<(EngineAction? action, string? stepId, bool done)> ProcessActiveResume(
        ActiveResumeFragment resume, ZoneId? playerZone, UiState ui, WorldPosition? playerPos,
        CancellationToken ct)
    {
        if (playerZone is { } pz && ZoneMatches(pz, resume.RequiredZone))
            return (null, null, done: true);

        foreach (var fstep in resume.Steps)
        {
            if (resume.ConfirmedFragmentStepIds.Contains(fstep.Id))
                continue;

            if (fstep.Expect is not null && await _expectEvaluator.Evaluate(fstep.Expect, ct))
            {
                resume.ConfirmedFragmentStepIds.Add(fstep.Id);
                continue;
            }

            if (fstep.SkipIf is not null && await _expectEvaluator.Evaluate(fstep.SkipIf, ct))
                continue;

            var action = ResolveActionForStep(fstep, ui, playerPos);

            if (action is EngineAction.AwaitUser && resume.MainStep.Recover?.OnResumeFail is { } onFail)
                return (MapRecoverAction(onFail, resume.MainStep), resume.MainStep.Id, done: false);

            return (action, fstep.Id, done: false);
        }

        return (new EngineAction.Wait(
            $"resume fragment '{resume.ForStepId}' exhausted but player not yet in zone {resume.RequiredZone}"),
            null, done: false);
    }

    private static bool ZoneAlreadySatisfied(ZoneId? playerZone, string requiredZone)
    {
        if (!uint.TryParse(requiredZone, out var rz)) return true;
        if (playerZone is not { } pz) return true;
        return pz.Value == rz;
    }

    private static bool ZoneMatches(ZoneId playerZone, string requiredZone)
        => uint.TryParse(requiredZone, out var rz) && playerZone.Value == rz;

    private ActiveResumeFragment ArmResumeFragment(Step mainStep, string fragId, string reqZone)
    {
        if (_fragments is null || !_fragments.TryGetValue(fragId, out var def))
            throw new InvalidOperationException(
                $"Step '{mainStep.Id}' declares ResumePointFragmentId '{fragId}' but no such fragment " +
                "was provided to StartQuest.");

        return new ActiveResumeFragment(
            ForStepId: mainStep.Id,
            RequiredZone: reqZone,
            MainStep: mainStep,
            Steps: def.Steps,
            ConfirmedFragmentStepIds: new HashSet<string>(StringComparer.Ordinal));
    }

    private static EngineAction MapRecoverAction(RecoverAction action, Step mainStep) => action switch
    {
        AwaitUserRecoverAction au => new EngineAction.AwaitUser(au.Reason),
        AbandonRecoverAction => new EngineAction.AwaitUser($"resume abandoned for step '{mainStep.Id}'"),
        _ => new EngineAction.AwaitUser($"resume recovery '{action.GetType().Name}' for step '{mainStep.Id}'")
    };
}
