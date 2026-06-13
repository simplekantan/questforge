using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QuestForge.Adapters;
using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Items;
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
using QuestForge.Engine.Dialogue;
using QuestForge.Engine.Navigation;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Travel;
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
    private readonly IMinigameSkipper _minigames;
    private readonly IDialogueResolver _dialogue;
    private readonly ITimingProfile _timing;
    private readonly IVendor? _vendor;
    private readonly IActionExecutor? _actionExecutor;
    private readonly IEmoteExecutor? _emoteExecutor;
    private readonly IChatSender? _chatSender;
    private readonly IItemUser? _itemUser;
    private readonly IGearEquipper? _gearEquipper;
    private readonly IBestGearEquipper? _bestGearEquipper;
    private readonly IJobChanger? _jobChanger;
    private readonly IGearsetManager? _gearsetManager;
    private readonly ICofferOpener? _cofferOpener;
    private readonly IObjectInteractor? _objectInteractor;
    private readonly IQuestBattleRunner? _questBattleRunner;
    private readonly IDutyRunner? _dutyRunner;
    private readonly ICfcResolver? _cfcResolver;
    private readonly ITraceWriter _trace;
    private readonly ILogger<QuestEngine> _logger;
    private readonly TimeProvider _clock;
    private readonly double _actionCooldownSeconds;
    private readonly ExpectEvaluator _expectEvaluator;
    private readonly NavigationWatchdog _watchdog;

    /// <summary>Exposed for test inspection (EX-reset-on-advance). Internal to the engine assembly.</summary>
    internal readonly CombatController _combatController;

    private QuestDefinition? _quest;
    private string? _runId;
    private bool _runStartEmitted;
    private readonly HashSet<string> _confirmedStepIds = new();
    private readonly HashSet<string> _fireOnceDispatchedIds = new();
    private DateTimeOffset? _waitStepStart;
    private string? _waitStepStartId;
    private DateTimeOffset? _lastActionFiredAt;
    private string? _lastActionFiredStepId;
    private int _lastKnownSequence = -1;
    private IReadOnlyDictionary<string, FragmentDefinition>? _fragments;
    private readonly HashSet<string> _resumePointExecutedIds = new();
    private ActiveResumeFragment? _activeResumeFragment;
    private Step? _lastResolvedStep;

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
        IMinigameSkipper minigames,
        IDialogueResolver dialogue,
        ITimingProfile timing,
        ITraceWriter trace,
        ILogger<QuestEngine> logger,
        TimeProvider? clock = null,
        IVendor? vendor = null,
        IActionExecutor? actionExecutor = null,
        IEmoteExecutor? emoteExecutor = null,
        IChatSender? chatSender = null,
        IItemUser? itemUser = null,
        IGearEquipper? gearEquipper = null,
        IBestGearEquipper? bestGearEquipper = null,
        IJobChanger? jobChanger = null,
        IGearsetManager? gearsetManager = null,
        ICofferOpener? cofferOpener = null,
        IObjectInteractor? objectInteractor = null,
        IQuestBattleRunner? questBattleRunner = null,
        IDutyRunner? dutyRunner = null,
        ICfcResolver? cfcResolver = null,
        double actionCooldownSeconds = 5.0,
        double navStallTimeoutSeconds = 5.0,
        float navStallDistanceThreshold = 2.0f,
        int navMaxJumpAttempts = 3)
    {
        _vendor = vendor;
        _actionExecutor = actionExecutor;
        _emoteExecutor = emoteExecutor;
        _chatSender = chatSender;
        _itemUser = itemUser;
        _gearEquipper = gearEquipper;
        _bestGearEquipper = bestGearEquipper;
        _jobChanger = jobChanger;
        _gearsetManager = gearsetManager;
        _cofferOpener = cofferOpener;
        _objectInteractor = objectInteractor;
        _questBattleRunner = questBattleRunner;
        _dutyRunner = dutyRunner;
        _cfcResolver = cfcResolver;
        _gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _clock = clock ?? TimeProvider.System;
        _actionCooldownSeconds = actionCooldownSeconds;
        _questState = questState ?? throw new ArgumentNullException(nameof(questState));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _teleporter = teleporter ?? throw new ArgumentNullException(nameof(teleporter));
        _interactor = interactor ?? throw new ArgumentNullException(nameof(interactor));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _minigames = minigames ?? throw new ArgumentNullException(nameof(minigames));
        _dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _expectEvaluator    = new ExpectEvaluator(new PredicateEvaluator(gameState, questState));
        _combatController   = new CombatController(gameState, combat, navigator, logger);
        _watchdog = new NavigationWatchdog(
            TimeSpan.FromSeconds(navStallTimeoutSeconds),
            navStallDistanceThreshold,
            navMaxJumpAttempts,
            _clock);
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
                yield return SynthesizeTeleportExpect(SynthesizePurchaseExpect(step));
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
                yield return SynthesizeTeleportExpect(SynthesizePurchaseExpect(CloneStepWith(innerStep, scopedId, substituted)));
            }
        }
    }

    /// <summary>
    /// When <paramref name="step"/> is a <see cref="PurchaseItemStep"/> with no authored <c>Expect</c>,
    /// synthesises a default <c>playerHasItem(ItemId,Quantity)</c> expect so the Expect-first loop
    /// in <see cref="ResolveAction"/> confirms and skips the step when the item is already owned.
    /// Authored <c>Expect</c> values are left untouched.
    /// </summary>
    private static Step SynthesizePurchaseExpect(Step step)
    {
        if (step is not PurchaseItemStep ps || ps.Expect is not null)
            return step;
        var synthesized = new PredicateExpect { Predicate = $"playerHasItem({ps.ItemId},{ps.Quantity})" };
        return CloneStepWith(ps, ps.Id, synthesized);
    }

    /// <summary>
    /// When <paramref name="step"/> is a <see cref="TeleportStep"/> with no authored <c>Expect</c>,
    /// synthesises a default <c>playerZone() == expectedZone</c> expect from <see cref="AetheryteZoneMap"/>.
    /// Authored <c>Expect</c> values are left untouched. Steps for unknown aetheryte IDs are left untouched
    /// (the dispatch arm handles the unknown-ID case at runtime).
    ///
    /// Synthesis is also suppressed when the immediately preceding step already carries a
    /// <c>playerZone() == N</c> expect for the same target zone N. This prevents the step from
    /// being spuriously confirmed in the same tick that the preceding step is confirmed (e.g. a
    /// TravelStep that navigates to zone 130 followed immediately by a TeleportStep targeting zone 130).
    /// </summary>
    private static Step SynthesizeTeleportExpect(Step step)
    {
        if (step is not TeleportStep ts || ts.Expect is not null)
            return step;
        if (!AetheryteZoneMap.TryGetZone(ts.AetheryteId.Value, out var zoneId))
            return step;

        var synthesized = new PredicateExpect { Predicate = $"playerZone() == {zoneId}" };
        return CloneStepWith(ts, ts.Id, synthesized);
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

    public YesNoAnswer? CurrentYesNoAnswer => ExtractYesNo(_lastResolvedStep);

    private static YesNoAnswer? ExtractYesNo(Step? step)
    {
        DialogueChoice[] choices = step switch
        {
            TalkStep t    => t.DialogueChoices,
            TurnInStep ti => ti.DialogueChoices,
            TravelStep tr => tr.RouteHint?.NpcDialogue?.DialogueChoices ?? [],
            DutyStep { Kind: "spd" } => [new DialogueChoice("yesno", null, "yes")],
            _             => []
        };
        foreach (var c in choices)
            if (string.Equals(c.Type, "yesno", StringComparison.OrdinalIgnoreCase))
                return string.Equals(c.Answer, "no", StringComparison.OrdinalIgnoreCase)
                    ? YesNoAnswer.No : YesNoAnswer.Yes;
        return null;
    }

    public void BeginRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId must be non-empty", nameof(runId));
        _runId = runId;
        _runStartEmitted = false;
        _confirmedStepIds.Clear();
        _fireOnceDispatchedIds.Clear();
        _lastKnownSequence = -1;
        _resumePointExecutedIds.Clear();
        _activeResumeFragment = null;
    }

    public void NotifyEquipBestGearComplete(string stepId)
    {
        _confirmedStepIds.Add(stepId);
    }

    public async Task<EngineAction> Tick(CancellationToken ct)
    {
        if (_quest is null)
            return new EngineAction.AwaitUser("no quest loaded");

        EmitRunStartIfNeeded();

        var (action, stepId, playerPos) = await ResolveAction(ct);

        // Watchdog consultation (applies to ALL Navigate actions from any source)
        if (action is EngineAction.Navigate nav && playerPos.HasValue)
        {
            var advice = _watchdog.Update(playerPos.Value, isNavigating: true);
            action = advice switch
            {
                WatchdogAdvice.Jump => new EngineAction.Jump(Origin: _lastResolvedStep),
                WatchdogAdvice.CastReturn => ResolveObstacleRecovery(stepId),
                _ => action
            };
        }
        else if (playerPos.HasValue)
        {
            _watchdog.Update(playerPos.Value, isNavigating: false);
        }
        else
        {
            _watchdog.Reset();
        }

        if (_runId is not null)
        {
            if (action is EngineAction.Done)
            {
                // Done terminates the run Ã¢â‚¬â€ emit run.end instead of a decision event.
                TraceSafe(new RunEndEvent { RunId = _runId, Data = new RunEndEvent.RunEndData { Outcome = "done" } });
                _runStartEmitted = false;
            }
            else if (action is EngineAction.AwaitUser)
            {
                // AwaitUser terminates the run Ã¢â‚¬â€ emit run.end.
                // Also emit the decision so the caller knows why we stopped.
                TraceSafe(new DecisionEvent
                {
                    RunId = _runId,
                    Data = new DecisionEvent.DecisionData { StepId = stepId, ActionType = action.GetType().Name }
                });
                TraceSafe(new RunEndEvent { RunId = _runId, Data = new RunEndEvent.RunEndData { Outcome = "awaitUser" } });
            }
            else
            {
                TraceSafe(new DecisionEvent
                {
                    RunId = _runId,
                    Data = new DecisionEvent.DecisionData { StepId = stepId, ActionType = action.GetType().Name }
                });
            }
        }

        return action;
    }

    private void EmitRunStartIfNeeded()
    {
        if (_runStartEmitted || _runId is null || _quest is null) return;
        TraceSafe(new RunStartEvent
        {
            RunId = _runId,
            Data = new RunStartEvent.RunStartData { QuestId = _quest.Id, SchemaVer = "1.0" }
        });
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

    private async Task<(EngineAction action, string? stepId, WorldPosition? playerPos)> ResolveAction(CancellationToken ct)
    {
        var questId = new QuestId(_quest!.Id);

        var seqResult = await _questState.GetQuestSequence(questId, ct);
        if (seqResult is Result<int>.Failure f1)
            return (new EngineAction.AwaitUser($"adapter failure reading sequence: {f1.Reason}"), null, null);

        // Read NG+ state once per tick, fail-open: a failed read treats IsActive=false
        // so the normal-play gate is preserved. A NG+ read failure must never suppress it.
        var ngpResult = await _gameState.GetNewGamePlusState(ct);
        var ngp = ngpResult is Result<NewGamePlusState>.Success { Value: var ngpVal }
            ? ngpVal
            : new NewGamePlusState(false, null, false);

        // Inside instanced content the game marks the NG+ HUD as "suspended" even though
        // the replay is still running. Don't block the engine in that case.
        // Only check instance kind when NG+ is active+suspended to avoid starving replay fixtures.
        var inInstance = false;
        if (ngp.IsActive && ngp.IsSuspended)
        {
            var instanceKindResult = await _gameState.GetCurrentInstanceKind(ct);
            inInstance = instanceKindResult is Result<InstanceKind>.Success { Value: not InstanceKind.None };
        }

        bool replayActive = ngp.IsActive && (!ngp.IsSuspended || inInstance);

        if (ngp.IsActive && ngp.IsSuspended && !inInstance)
            return (new EngineAction.Wait("ng+ replay suspended"), null, null);

        if (!replayActive)
        {
            var completeResult = await _questState.IsQuestComplete(questId, ct);
            if (completeResult is Result<bool>.Success { Value: true })
                return (new EngineAction.Done(), null, null);
        }
        // else: replay active and not suspended — skip the IsQuestComplete gate (bitmap lies)
        //       and fall through to the live-sequence loop below.

        var currentSeq = seqResult.ValueOrThrow;
        // Block selection: prefer exact match. When no exact match exists, fall back to the
        // highest-Sequence block that is <= currentSeq, but ONLY if that floor block is also
        // the highest defined block overall. This accommodates single-phase quests where
        // questSequence advances sub-objectively within block 0 (Expect predicates gate step
        // confirmation rather than block selection). In multi-block quests, a questSequence
        // landing in a gap between defined blocks is treated as an authoring error → AwaitUser.
        var exactBlock = _quest.Sequences.FirstOrDefault(s => s.Sequence == currentSeq);
        QuestSequence? matchingBlock;
        if (exactBlock is not null)
        {
            matchingBlock = exactBlock;
        }
        else
        {
            var maxSequence = _quest.Sequences.Max(s => s.Sequence);
            var floorBlock = _quest.Sequences
                .Where(s => s.Sequence <= currentSeq)
                .OrderByDescending(s => s.Sequence)
                .FirstOrDefault();
            // Use the floor block only when it is also the highest-defined block.
            // If a higher block exists (gap scenario), the gap is an authoring error.
            matchingBlock = (floorBlock is not null && floorBlock.Sequence == maxSequence)
                ? floorBlock
                : null;
        }
        if (matchingBlock is null)
            return (new EngineAction.AwaitUser($"no sequence block covers current sequence {currentSeq}"), null, null);

        if (matchingBlock.SkipIf is not null)
        {
            if (await _expectEvaluator.Evaluate(matchingBlock.SkipIf, ct))
                return (new EngineAction.AwaitUser("sequence skipped by skipIf Ã¢â‚¬â€ engine cannot self-advance in Phase 4"), null, null);
        }

        // Read UiState once per tick so step dispatch arms can inspect UI without async.
        // On adapter failure: use a safe default (CutscenePlaying=false) so non-cutscene
        // steps are completely unaffected. A CutsceneStep will emit
        // Wait("cutscene ended; awaiting sequence advance") Ã¢â‚¬â€ recoverable on the next tick.
        var uiResult = await _gameState.GetUiState(ct);
        var ui = uiResult is Result<UiState>.Success { Value: var uiValue }
            ? uiValue
            : new UiState(false, false, false, false, false, false, false, false, null);

        if (ui.LoadingZone)
            return (new EngineAction.Wait("loading zone"), null, null);

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
            _fireOnceDispatchedIds.Clear();
            _resumePointExecutedIds.Clear();
            _activeResumeFragment = null;
            _waitStepStart = null;
            _waitStepStartId = null;
            _lastActionFiredAt = null;
            _lastActionFiredStepId = null;
            await _combatController.ResetAsync(ct);
        }
        _lastKnownSequence = currentSeq;

        // Global defense rule: fires on every tick, before the cursor walk.
        // If the player is in combat and an attacker is in scan range, engage them
        // immediately — regardless of which step type the cursor is on.
        // Fail-open: a failed IsPlayerInCombat read returns null (cursor walk proceeds).
        var defense = await ResolveDefenseOrNull(ct);
        if (defense is not null) return (defense.Value.action, defense.Value.stepId, playerPos);

        // Process any active resume sub-loop FIRST, before the main step loop.
        if (_activeResumeFragment is { } resume)
        {
            var (resumeAction, resumeStepId, resumeDone) =
                await ProcessActiveResume(resume, playerZone, ui, playerPos, ct);

            if (!resumeDone)
                return (resumeAction!, resumeStepId, playerPos);

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

            // 2. Expect: if true, confirm and skip.
            if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
            {
                if (step is CombatStep cs)
                {
                    // Objective met. Global defense (above) handles any remaining aggro.
                    // Confirm and continue — no in-combat check needed here.
                    _confirmedStepIds.Add(cs.Id);
                    await _combatController.ResetAsync(ct);
                    continue;
                }

                // Non-combat steps: unchanged.
                _confirmedStepIds.Add(step.Id);
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
                    _lastResolvedStep = step;
                    return (ResolveActionForStep(step, ui, playerPos), step.Id, playerPos);
                }
                _activeResumeFragment = armed;
                return (action!, stepId, playerPos);
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
                    return (new EngineAction.Engage(combatStep, decision.Target), step.Id, playerPos);
                }

                // No eligible target in scan range. If the player has drifted beyond StopDistance
                // of the spawn anchor, navigate back so respawns land in scan range again.
                if (combatStep.Location is not null)
                {
                    var fallback = ResolveInteractOrNavigate(
                        step, combatStep.Location.Position, playerPos,
                        new EngineAction.Wait("combat-roam-sentinel"));
                    if (fallback is EngineAction.Navigate navAction)
                        return (navAction, step.Id, playerPos);
                }

                // No target AND at/within Location (or Location unset): idle — waits for respawns.
                // Engage(null) is a forward decision, never a stall (D6).
                return (new EngineAction.Engage(combatStep, null), step.Id, playerPos);
            }

            // 6. PurchaseItemStep async arm — step-gated so GetGil/GetGrandCompanySeals are NEVER
            //    called on the common per-tick path, preventing fixture starvation for non-purchase steps.
            if (step is PurchaseItemStep purchaseStep)
            {
                var purchaseAction = await ResolvePurchaseAction(purchaseStep, playerPos, ct);
                return (purchaseAction, step.Id, playerPos);
            }

            // 6a. UseActionStep async arm — step-gated so GetPlayerState/GetActionStatus are only
            //     read when the cursor is on a UseActionStep.
            if (step is UseActionStep useActionStep)
            {
                var useAction = await ResolveUseAction(useActionStep, playerPos, ct);
                return (useAction, step.Id, playerPos);
            }

            // 6a2. UseEmoteStep async arm — step-gated so GetPlayerState is only read when
            //      the cursor is on a UseEmoteStep.
            if (step is UseEmoteStep useEmoteStep)
            {
                var useEmote = await ResolveUseEmote(useEmoteStep, playerPos, ct);
                return (useEmote, step.Id, playerPos);
            }

            // 6a3. SayChatMessageStep async arm — step-gated so GetPlayerState is only read
            //      when the cursor is on a SayChatMessageStep.
            if (step is SayChatMessageStep sayChatMessageStep)
            {
                var sayChat = await ResolveSayChatMessage(sayChatMessageStep, ct);
                return (sayChat, step.Id, playerPos);
            }

            // 6a4. UseItemStep async arm — step-gated so GetPlayerState is only read when the
            //      cursor is on a UseItemStep.
            if (step is UseItemStep useItemStep)
            {
                var useItem = await ResolveUseItem(useItemStep, ct);
                return (useItem, step.Id, playerPos);
            }

            // 6a4b. UseItemOnObjectStep async arm
            if (step is UseItemOnObjectStep useItemOnObjStep)
            {
                var useItemOnObj = await ResolveUseItemOnObject(useItemOnObjStep, playerPos, ct);
                return (useItemOnObj, step.Id, playerPos);
            }

            // 6a5. EquipGearForQuestStep async arm — implicit postcondition: all ItemIds equipped.
            if (step is EquipGearForQuestStep equipStep)
            {
                var equipAction = await ResolveEquipGear(equipStep, ct);
                if (equipAction is null)
                {
                    // All items already equipped -- self-confirm.
                    _confirmedStepIds.Add(step.Id);
                    continue;
                }
                return (equipAction, step.Id, playerPos);
            }

            // 6a6. EquipBestGearStep async arm — re-dispatches every tick until confirmed by host.
            if (step is EquipBestGearStep bestGearStep)
            {
                var bestGearAction = await ResolveEquipBestGear(bestGearStep, ct);
                return (bestGearAction, step.Id, playerPos);
            }

            // 6a7. ChangeJobStep async arm — implicit postcondition: GetCurrentJob == targetJobId.
            if (step is ChangeJobStep changeJobStep)
            {
                var changeJobAction = await ResolveChangeJob(changeJobStep, ct);
                if (changeJobAction is null)
                {
                    // Already on the right job -- self-confirm.
                    _confirmedStepIds.Add(step.Id);
                    continue;
                }
                return (changeJobAction, step.Id, playerPos);
            }

            // 6a8. RegisterGearsetStep async arm — fire-once: self-confirms after first dispatch.
            if (step is RegisterGearsetStep registerGearsetStep)
            {
                if (_fireOnceDispatchedIds.Contains(step.Id))
                {
                    _confirmedStepIds.Add(step.Id);
                    continue;
                }
                var registerGearsetAction = await ResolveRegisterGearset(registerGearsetStep, ct);
                if (registerGearsetAction is not EngineAction.Wait)
                    _fireOnceDispatchedIds.Add(step.Id);
                return (registerGearsetAction, step.Id, playerPos);
            }

            // 6a9. OpenCoffersStep async arm — no implicit postcondition (relies on authored Expect).
            if (step is OpenCoffersStep openCoffersStep)
            {
                var openCoffersAction = await ResolveOpenCoffers(openCoffersStep, ct);
                return (openCoffersAction, step.Id, playerPos);
            }

            // 6b0. DutyStep async arm — step-gated so IsBossModAvailable is only read when
            //      the cursor is on a DutyStep. Routes by Kind.
            if (step is DutyStep dutyStep)
            {
                var dutyAction = await ResolveDuty(dutyStep, ct);
                return (dutyAction, step.Id, playerPos);
            }

            // 6b0a. DungeonTrialStep async arm
            if (step is DungeonTrialStep dungeonTrialStep)
            {
                var action = await ResolveDungeonTrial(dungeonTrialStep, ct);
                return (action, step.Id, playerPos);
            }

            // 6b0b. SinglePlayerDutyStep async arm
            if (step is SinglePlayerDutyStep spdStep)
            {
                var action = await ResolveSinglePlayerDuty(spdStep, playerPos, ct);
                return (action, step.Id, playerPos);
            }

            // 6b. TeleportStep async arm — step-gated so IsPlayerInCombat is only read when the
            //     cursor is on a TeleportStep. Pre-flight guards (unknown aetheryte, in combat)
            //     run inside ResolveTeleportAction.
            if (step is TeleportStep teleportStep)
            {
                var teleportAction = await ResolveTeleportAction(teleportStep, ct);
                return (teleportAction, step.Id, playerPos);
            }

            // 7. WaitStep arm — time-based completion, no game-state predicate consulted.
            if (step is WaitStep waitStep)
            {
                if (_waitStepStartId != step.Id)
                {
                    _waitStepStart = _clock.GetUtcNow();
                    _waitStepStartId = step.Id;
                    return (new EngineAction.Wait($"waiting {waitStep.Seconds}s"), step.Id, playerPos);
                }

                if (_clock.GetUtcNow() - _waitStepStart!.Value >= TimeSpan.FromSeconds(waitStep.Seconds))
                {
                    _confirmedStepIds.Add(step.Id);
                    _waitStepStart = null;
                    _waitStepStartId = null;
                    continue;
                }

                return (new EngineAction.Wait($"waiting {waitStep.Seconds}s"), step.Id, playerPos);
            }

            _lastResolvedStep = step;
            return (ResolveActionForStep(step, ui, playerPos), step.Id, playerPos);
        }

        return (new EngineAction.Wait("all steps in current sequence satisfied; awaiting game sequence advance"), null, playerPos);
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
        // DEPRECATED: prefer AethernetStep for new quests. This arm handles legacy
        // TravelStep quests that encode aethernet hops via routeHint.aethernet.
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
                new NavigationOptions(
                    StoppingDistance: step.StopDistance ?? DefaultStopDistance,
                    UseMount: travel.UseMount ?? true)),

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

        // IO4: object interaction dispatches via IObjectInteractor (not NpcId shim) Ã¢â‚¬â€ the in-range Interact path is not yet
        InteractObjectStep interactObj when _objectInteractor is null =>
            new EngineAction.AwaitUser("IObjectInteractor not configured - supply an IObjectInteractor to dispatch InteractObjectStep"),

        InteractObjectStep interactObj when interactObj.Position is not null =>
            ResolveInteractOrNavigate(
                step, interactObj.Position, playerPos,
                new EngineAction.InteractObject(new InteractableId(interactObj.InteractableId), Origin: step)),

        InteractObjectStep interactObj =>
            new EngineAction.InteractObject(new InteractableId(interactObj.InteractableId), Origin: step),

        PickupItemStep pickup when _objectInteractor is null =>
            new EngineAction.AwaitUser("IObjectInteractor not configured - supply an IObjectInteractor to dispatch PickupItemStep"),

        PickupItemStep pickup when pickup.Position is not null =>
            ResolveInteractOrNavigate(
                step, pickup.Position, playerPos,
                new EngineAction.InteractObject(new InteractableId(pickup.InteractableId), Origin: step)),

        PickupItemStep pickup =>
            new EngineAction.InteractObject(new InteractableId(pickup.InteractableId), Origin: step),

        AttunementStep attune when attune.Location is not null =>
            ResolveInteractOrNavigate(
                step, attune.Location.Position, playerPos,
                new EngineAction.Interact(new NpcId(attune.Target.Value), Origin: step),
                defaultStopDistance: DefaultAetheryteStopDistance),

        AttunementStep attune => new EngineAction.Interact(new NpcId(attune.Target.Value), Origin: step),

        AethernetStep aethernet =>
            ResolveInteractOrNavigate(
                step, aethernet.FromPosition, playerPos,
                new EngineAction.UseAethernet(
                    new AethernetId(aethernet.To),
                    new WorldPosition(aethernet.FromPosition.X, aethernet.FromPosition.Y, aethernet.FromPosition.Z))),

        HandOverItemStep handOver =>
            ResolveInteractOrNavigate(
                step, handOver.Target.Position, playerPos,
                new EngineAction.HandOver(
                    new NpcId(handOver.Target.NpcId),
                    handOver.Items.Select(id => new ItemId(id)).ToArray(),
                    step)),

        CutsceneStep => ui.CutscenePlaying
            ? new EngineAction.Wait("cutscene playing")
            : new EngineAction.Wait("cutscene ended; awaiting sequence advance"),

        AwaitUserStep au => new EngineAction.AwaitUser(au.Reason),

        TeleportStep tp => new EngineAction.Teleport(new Adapters.Types.AetheryteId(tp.AetheryteId.Value), Origin: step),

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
        UseReturnRecoverAction => new EngineAction.UseReturn(Origin: mainStep),
        UseTeleportRecoverAction tp => new EngineAction.Teleport(
            new Adapters.Types.AetheryteId(tp.AetheryteId), Origin: mainStep),
        AbandonRecoverAction => new EngineAction.AwaitUser($"resume abandoned for step '{mainStep.Id}'"),
        _ => new EngineAction.AwaitUser($"resume recovery '{action.GetType().Name}' for step '{mainStep.Id}'")
    };

    private EngineAction ResolveObstacleRecovery(string? stepId)
    {
        var step = FindStepById(stepId);
        if (step?.Recover?.OnObstacle is { } onObstacle)
            return MapRecoverAction(onObstacle, step);
        return new EngineAction.UseReturn(Origin: step);
    }

    private Step? FindStepById(string? stepId)
    {
        if (stepId is null || _quest is null) return null;
        foreach (var seq in _quest.Sequences)
            foreach (var step in seq.Steps)
                if (step.Id == stepId) return step;
        return null;
    }

    private EngineAction.Wait? CheckActionCooldown(Step step)
    {
        if (_actionCooldownSeconds <= 0) return null;
        if (_lastActionFiredAt is null) return null;

        var elapsed = _clock.GetUtcNow() - _lastActionFiredAt.Value;
        if (elapsed.TotalSeconds >= _actionCooldownSeconds) return null;

        return new EngineAction.Wait(
            $"action cooldown ({elapsed.TotalSeconds:F1}s / {_actionCooldownSeconds:F1}s)",
            Origin: step);
    }

    private void RecordActionFired(Step step)
    {
        _lastActionFiredAt = _clock.GetUtcNow();
        _lastActionFiredStepId = step.Id;
    }

    private async Task<EngineAction> ResolvePurchaseAction(
        PurchaseItemStep step, WorldPosition? playerPos, CancellationToken ct)
    {
        var purchaseAction = new EngineAction.Purchase(
            new NpcId(step.Target.NpcId),
            new ItemId(step.ItemId),
            step.Quantity,
            step.Currency,
            Origin: step,
            GcCategory: step.GcCategory,
            GcRankTier: step.GcRankTier,
            VendorCategory: step.VendorCategory);

        // Implied navigation: if player is out of range, navigate first.
        var navOrPurchase = ResolveInteractOrNavigate(
            step, step.Target.Position, playerPos, purchaseAction);

        if (navOrPurchase is EngineAction.Navigate)
            return navOrPurchase;

        // Affordability pre-check (coarse gate — adapter is the real backstop).
        long balance;
        if (step.Currency == PurchaseCurrency.GcSeals)
        {
            var sealsResult = await _gameState.GetGrandCompanySeals(ct);
            if (sealsResult is not Result<int>.Success { Value: var sealsVal })
                return purchaseAction; // fail-open: read failed
            balance = sealsVal;
        }
        else
        {
            var gilResult = await _gameState.GetGil(ct);
            if (gilResult is not Result<long>.Success { Value: var gilVal })
                return purchaseAction; // fail-open: read failed
            balance = gilVal;
        }

        if (balance <= 0)
        {
            var currencyName = step.Currency == PurchaseCurrency.GcSeals ? "GcSeals" : "Gil";
            return new EngineAction.AwaitUser(
                $"cannot afford item {step.ItemId}: 0 {currencyName}");
        }

        return purchaseAction;
    }

    private async Task<EngineAction> ResolveUseAction(UseActionStep step, WorldPosition? playerPos, CancellationToken ct)
    {
        // Guard 0 (AL3): navigate to target if Location is present and player is far
        if (step.Location is { } loc)
        {
            var navCheck = ResolveInteractOrNavigate(step, loc.Position, playerPos,
                new EngineAction.Wait("navigate-sentinel"));
            if (navCheck is EngineAction.Navigate nav)
                return nav;
        }

        if (_actionExecutor is null)
            return new EngineAction.AwaitUser(
                "UseActionStep dispatched but no IActionExecutor wired — host must supply one");

        var cooldownUa = CheckActionCooldown(step);
        if (cooldownUa is not null) return cooldownUa;

        // Guard 1: casting → Wait (defer until cast finishes)
        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring use-action", Origin: step);

        // Guard 2/3: action status — Unusable → AwaitUser; OnCooldown → Wait
        var statusResult = await _actionExecutor.GetActionStatus(step.ActionType, step.ActionId, ct);
        if (statusResult is Result<ActionStatus>.Success { Value: var status })
        {
            switch (status)
            {
                case ActionStatus.Unusable u:
                    return new EngineAction.AwaitUser(
                        $"action {step.ActionId} unusable: {u.Reason}");
                case ActionStatus.OnCooldown oc:
                    return new EngineAction.Wait(
                        $"action {step.ActionId} on cooldown: {oc.Remaining.TotalSeconds:F1}s",
                        Origin: step);
                case ActionStatus.Ready:
                    break;
            }
        }
        // Fail-open: status read failure → proceed to emit

        var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
        RecordActionFired(step);
        return new EngineAction.UseAction(step.ActionType, step.ActionId, target, Origin: step);
    }

    private async Task<EngineAction> ResolveUseEmote(UseEmoteStep step, WorldPosition? playerPos, CancellationToken ct)
    {
        // Guard 0 (AL3): navigate to target if Location is present and player is far
        if (step.Location is { } loc)
        {
            var navCheck = ResolveInteractOrNavigate(step, loc.Position, playerPos,
                new EngineAction.Wait("navigate-sentinel"));
            if (navCheck is EngineAction.Navigate nav)
                return nav;
        }

        if (_emoteExecutor is null)
            return new EngineAction.AwaitUser(
                "UseEmoteStep dispatched but no IEmoteExecutor wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring use-emote", Origin: step);

        var cooldownUe = CheckActionCooldown(step);
        if (cooldownUe is not null) return cooldownUe;

        var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
        RecordActionFired(step);
        return new EngineAction.UseEmote(step.EmoteId, target, step.Motion, Origin: step);
    }

    private async Task<EngineAction> ResolveSayChatMessage(SayChatMessageStep step, CancellationToken ct)
    {
        if (_chatSender is null)
            return new EngineAction.AwaitUser(
                "SayChatMessageStep dispatched but no IChatSender wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring say-chat-message", Origin: step);

        var cooldownSc = CheckActionCooldown(step);
        if (cooldownSc is not null) return cooldownSc;

        var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
        RecordActionFired(step);
        return new EngineAction.SayChatMessage(step.Message, target, Origin: step);
    }

    private async Task<EngineAction> ResolveUseItem(UseItemStep step, CancellationToken ct)
    {
        if (_itemUser is null)
            return new EngineAction.AwaitUser(
                "UseItemStep dispatched but no IItemUser wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring use-item", Origin: step);

        var cooldownUi = CheckActionCooldown(step);
        if (cooldownUi is not null) return cooldownUi;

        var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
        RecordActionFired(step);
        return new EngineAction.UseItem(step.Kind, step.ItemId, target, step.TargetPosition, Origin: step);
    }

    private async Task<EngineAction> ResolveUseItemOnObject(UseItemOnObjectStep step, WorldPosition? playerPos, CancellationToken ct)
    {
        if (_objectInteractor is null)
            return new EngineAction.AwaitUser(
                "UseItemOnObjectStep dispatched but no IObjectInteractor wired — host must supply one");

        var action = new EngineAction.UseItemOnObject(
            new InteractableId(step.InteractableId),
            step.Kind,
            step.ItemId,
            Origin: step);

        var navOrAction = ResolveInteractOrNavigate(step, step.Position, playerPos, action);
        if (navOrAction is EngineAction.Navigate)
            return navOrAction;

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring use-item-on-object", Origin: step);

        var cooldown = CheckActionCooldown(step);
        if (cooldown is not null) return cooldown;

        RecordActionFired(step);
        return action;
    }

    private async Task<EngineAction?> ResolveEquipGear(EquipGearForQuestStep step, CancellationToken ct)
    {
        if (_gearEquipper is null)
            return new EngineAction.AwaitUser(
                "EquipGearForQuestStep dispatched but no IGearEquipper wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring equip-gear", Origin: step);

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        if (inCombatResult is Result<bool>.Success { Value: true })
            return new EngineAction.Wait("player in combat; deferring equip-gear", Origin: step);

        foreach (var itemId in step.ItemIds)
        {
            var equipped = await _gearEquipper.IsItemEquipped(itemId, ct);
            if (equipped is Result<bool>.Success { Value: false })
                return new EngineAction.EquipGear(itemId, Origin: step);
        }

        return null;
    }

    private async Task<EngineAction> ResolveEquipBestGear(EquipBestGearStep step, CancellationToken ct)
    {
        if (_bestGearEquipper is null)
            return new EngineAction.AwaitUser(
                "EquipBestGearStep dispatched but no IBestGearEquipper wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring equip-best-gear", Origin: step);

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        if (inCombatResult is Result<bool>.Success { Value: true })
            return new EngineAction.Wait("player in combat; deferring equip-best-gear", Origin: step);

        return new EngineAction.EquipBestGear(Origin: step);
    }

    private async Task<EngineAction> ResolveRegisterGearset(RegisterGearsetStep step, CancellationToken ct)
    {
        if (_gearsetManager is null)
            return new EngineAction.AwaitUser(
                "RegisterGearsetStep dispatched but no IGearsetManager wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring register-gearset", Origin: step);

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        if (inCombatResult is Result<bool>.Success { Value: true })
            return new EngineAction.Wait("player in combat; deferring register-gearset", Origin: step);

        return new EngineAction.RegisterGearset(Origin: step);
    }

    private async Task<EngineAction> ResolveOpenCoffers(OpenCoffersStep step, CancellationToken ct)
    {
        if (_cofferOpener is null)
            return new EngineAction.AwaitUser(
                "OpenCoffersStep dispatched but no ICofferOpener wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring open-coffers", Origin: step);

        var slotsResult = await _gameState.GetFreeInventorySlots(ct);
        if (slotsResult is Result<int>.Success { Value: 0 })
            return new EngineAction.AwaitUser(
                "inventory full — free space before opening coffers");

        var coffersResult = await _cofferOpener.GetCofferItemIds(ct);
        if (coffersResult is not Result<IReadOnlyList<uint>>.Success { Value: var cofferIds }
            || cofferIds.Count == 0)
            return new EngineAction.Wait("no coffers in inventory", Origin: step);

        return new EngineAction.OpenCoffer(cofferIds[0], Origin: step);
    }

    private async Task<EngineAction?> ResolveChangeJob(ChangeJobStep step, CancellationToken ct)
    {
        if (_jobChanger is null)
            return new EngineAction.AwaitUser(
                "ChangeJobStep dispatched but no IJobChanger wired — host must supply one");

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring change-job", Origin: step);

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        if (inCombatResult is Result<bool>.Success { Value: true })
            return new EngineAction.Wait("player in combat; deferring change-job", Origin: step);

        // Implicit postcondition: already on the right job?
        var jobResult = await _gameState.GetCurrentJob(ct);
        if (jobResult is Result<JobId>.Success { Value: var currentJob } && currentJob.Value == step.JobId)
            return null; // Self-confirm.

        // Pre-flight: gearset must exist.
        var gearsetResult = await _jobChanger.GearsetExistsForJob(new JobId(step.JobId), ct);
        if (gearsetResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                $"no gearset found for job {step.JobId} — create one via /gearset before running this quest");

        return new EngineAction.ChangeJob(new JobId(step.JobId), Origin: step);
    }

    private async Task<EngineAction> ResolveTeleportAction(TeleportStep step, CancellationToken ct)
    {
        if (!AetheryteZoneMap.TryGetZone(step.AetheryteId.Value, out _))
            return new EngineAction.AwaitUser(
                $"teleport target aetheryte {step.AetheryteId.Value} not in zone map — author must extend AetheryteZoneMap");

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        if (inCombatResult is Result<bool>.Success { Value: true })
            return new EngineAction.AwaitUser("cannot teleport while in combat");

        return new EngineAction.Teleport(new Adapters.Types.AetheryteId(step.AetheryteId.Value), Origin: step);
    }

    private async Task<EngineAction> ResolveDuty(DutyStep step, CancellationToken ct)
    {
        return step.Kind switch
        {
            "spd"  => await ResolveSpd(step, ct),
            "duty" => await ResolveDungeonTrial(step, ct),
            _ => throw new NotSupportedException(
                $"DutyStep kind '{step.Kind}' is not supported. Only 'spd' and 'duty' are implemented.")
        };
    }

    private async Task<EngineAction> ResolveDungeonTrial(DutyStep step, CancellationToken ct)
    {
        if (_dutyRunner is null)
            return new EngineAction.AwaitUser(
                "DutyStep(kind:duty) dispatched but no IDutyRunner configured — " +
                "install AutoDuty or complete this duty manually");

        if (_cfcResolver is null)
            return new EngineAction.AwaitUser(
                "DutyStep(kind:duty) dispatched but no ICfcResolver configured — " +
                "host must supply one");

        if (step.ContentFinderConditionId is null or 0)
            return new EngineAction.AwaitUser(
                $"DutyStep '{step.Id}' has no ContentFinderConditionId — " +
                "quest file must specify it for kind 'duty'");

        var territoryType = _cfcResolver.GetTerritoryType(step.ContentFinderConditionId.Value);
        if (territoryType is null)
            return new EngineAction.AwaitUser(
                $"ContentFinderConditionId {step.ContentFinderConditionId} could not be " +
                "resolved to a territory type — unknown duty");

        var availResult = await _dutyRunner.IsAvailable(ct);
        if (availResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                "AutoDuty required for dungeon/trial automation. " +
                "Install AutoDuty or complete this duty manually.");

        var pathResult = await _dutyRunner.ContentHasPath(territoryType.Value, ct);
        if (pathResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                $"AutoDuty does not have a path for territory {territoryType.Value} " +
                $"(CFC {step.ContentFinderConditionId}). Complete this duty manually " +
                "or wait for AutoDuty path support.");

        return new EngineAction.EnterDuty(
            step.ContentFinderConditionId.Value, Origin: step);
    }

    private async Task<EngineAction> ResolveSpd(DutyStep step, CancellationToken ct)
    {
        if (_questBattleRunner is null)
            return new EngineAction.AwaitUser(
                "DutyStep(kind:spd) dispatched but no IQuestBattleRunner configured — host must supply one");

        var availResult = await _questBattleRunner.IsBossModAvailable(ct);
        if (availResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                "BossMod required for Single Player Duties. Complete manually or install BossMod.");

        return new EngineAction.EnterSinglePlayerDuty(
            ContentFinderConditionId: step.ContentFinderConditionId,
            EntryTargetId: step.EntryTargetId,
            EntryPosition: step.EntryPosition,
            Origin: step);
    }

    private async Task<EngineAction> ResolveDungeonTrial(DungeonTrialStep step, CancellationToken ct)
    {
        if (_dutyRunner is null)
            return new EngineAction.AwaitUser(
                "DungeonTrialStep dispatched but no IDutyRunner configured -- " +
                "install AutoDuty or complete this duty manually");

        if (_cfcResolver is null)
            return new EngineAction.AwaitUser(
                "DungeonTrialStep dispatched but no ICfcResolver configured -- " +
                "host must supply one");

        if (step.ContentFinderConditionId == 0)
            return new EngineAction.AwaitUser(
                $"DungeonTrialStep '{step.Id}' has ContentFinderConditionId == 0 -- " +
                "quest file must specify a valid CFC ID");

        var availResult = await _dutyRunner.IsAvailable(ct);
        if (availResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                "AutoDuty required for dungeon/trial automation. " +
                "Install AutoDuty or complete this duty manually.");

        var territoryType = _cfcResolver.GetTerritoryType(step.ContentFinderConditionId);
        if (territoryType is null)
            return new EngineAction.AwaitUser(
                $"ContentFinderConditionId {step.ContentFinderConditionId} could not be " +
                "resolved to a territory type -- unknown duty");

        var pathResult = await _dutyRunner.ContentHasPath(territoryType.Value, ct);
        if (pathResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                $"AutoDuty does not have a path for territory {territoryType.Value} " +
                $"(CFC {step.ContentFinderConditionId}). Complete this duty manually " +
                "or wait for AutoDuty path support.");

        var cooldown = CheckActionCooldown(step);
        if (cooldown is not null) return cooldown;

        RecordActionFired(step);
        return new EngineAction.EnterDuty(step.ContentFinderConditionId, Origin: step);
    }

    private async Task<EngineAction> ResolveSinglePlayerDuty(
        SinglePlayerDutyStep step, WorldPosition? playerPos, CancellationToken ct)
    {
        if (_questBattleRunner is null)
            return new EngineAction.AwaitUser(
                "SinglePlayerDutyStep dispatched but no IQuestBattleRunner configured -- " +
                "install BossMod or complete this duty manually");

        var availResult = await _questBattleRunner.IsBossModAvailable(ct);
        if (availResult is Result<bool>.Success { Value: false })
            return new EngineAction.AwaitUser(
                "BossMod required for Single Player Duties. " +
                "Complete manually or install BossMod.");

        var entryWorldPos = new WorldPosition(
            step.EntryPosition.X, step.EntryPosition.Y, step.EntryPosition.Z);
        var stopDist = step.StopDistance ?? DefaultStopDistance;
        if (playerPos is not null && playerPos.Value.DistanceTo(entryWorldPos) > stopDist)
            return new EngineAction.Navigate(entryWorldPos,
                new NavigationOptions(StoppingDistance: stopDist));

        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
            return new EngineAction.Wait("player casting; deferring single-player-duty entry",
                Origin: step);

        var cooldown = CheckActionCooldown(step);
        if (cooldown is not null) return cooldown;

        RecordActionFired(step);
        return new EngineAction.EnterSinglePlayerDuty(
            ContentFinderConditionId: step.ContentFinderConditionId,
            EntryTargetId: step.EntryTargetId,
            EntryPosition: step.EntryPosition,
            Origin: step);
    }

    /// <summary>
    /// Global defense rule. Fires on every tick before the cursor walk.
    /// Returns a defense Engage when the player is in combat and an attacker is in scan range.
    /// Returns null when defense is not needed (fail-open on read failure, no attacker found).
    /// </summary>
    private async Task<(EngineAction action, string? stepId)?> ResolveDefenseOrNull(
        CancellationToken ct)
    {

        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        // Fail-open: read failure → no defense action this tick. Cursor walk runs normally.
        if (inCombatResult is not Result<bool>.Success { Value: true })
            return null;

        // If mounted AND still navigating, skip defense — outrun the attacker. The whole point
        // of mounting is to skip overworld trash mobs en route to objectives. Only engage when
        // we're at the destination (vnavmesh stopped) or on foot. On read failure, fail-open
        // by NOT skipping (treat as "no opinion, proceed with normal defense logic").
        var stateResult = await _gameState.GetPlayerState(ct);
        if (stateResult is Result<PlayerStateSnapshot>.Success { Value: var ps }
            && ps.MountState != MountState.Dismounted)
        {
            var navResult = await _navigator.IsNavigating(ct);
            if (navResult is Result<bool>.Success { Value: true })
                return null;
        }

        var decision = await _combatController.DecideClearAggro(ct);
        if (decision.Target is null)
            return null; // in combat but no attacker in scan range → cursor walk normally

        return (new EngineAction.Engage(Step: null, Target: decision.Target), stepId: null);
    }
}
