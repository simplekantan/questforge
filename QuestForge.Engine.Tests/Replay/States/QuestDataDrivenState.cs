using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Engine.Tests.Engine;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Travel;
using QuestForge.Schema;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Generic data-driven fixture state machine. Derives game-state mutations from quest
/// expect predicates rather than from a recorded trace. See QD1-QD15 in
/// docs/QUEST_DATA_DRIVEN_FIXTURES_PLAN.md.
/// </summary>
internal sealed class QuestDataDrivenState : IFixtureState
{
    // -------------------------------------------------------------------------
    // StepPlan: step + pre-computed mutations
    // -------------------------------------------------------------------------

    internal sealed record StepPlan(
        Step Step,
        IReadOnlyList<StateMutation> Mutations,
        int SequenceNumber);

    // -------------------------------------------------------------------------
    // Adapters
    // -------------------------------------------------------------------------

    private readonly FakeGameStateProvider _gameState;
    private readonly FakeQuestState _questState;

    public IGameStateProvider GameState => _gameState;
    public IQuestState QuestState => _questState;
    public INavigator Navigator { get; }
    public ITeleporter Teleporter { get; }
    public IInteractor Interactor { get; }
    public ICombat Combat { get; }
    public IGearEquipper GearEquipper { get; }
    public IBestGearEquipper BestGearEquipper { get; }
    public IJobChanger JobChanger { get; }
    public IMinigameSkipper Minigames { get; }
    public IDialogueResolver Dialogue { get; }
    public ITimingProfile Timing { get; }
    public TimeProvider? Clock { get; }

    // -------------------------------------------------------------------------
    // Cursor and state
    // -------------------------------------------------------------------------

    private readonly IReadOnlyList<StepPlan> _stepPlans;
    private int _cursor;
    private List<StateMutation>? _pendingCombatMutations;
    private int _loadingZoneCountdown;

    // -------------------------------------------------------------------------
    // Private constructor
    // -------------------------------------------------------------------------

    private readonly ManualTimeProvider _clock;

    private QuestDataDrivenState(
        FakeGameStateProvider gameState,
        FakeQuestState questState,
        IReadOnlyList<StepPlan> stepPlans,
        QuestId questId)
    {
        _gameState = gameState;
        _questState = questState;
        _stepPlans = stepPlans;
        _questId = questId;
        _clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        Clock = _clock;

        Navigator = new FakeNavigator(_gameState);
        Teleporter = new FakeTeleporter(_gameState);
        Interactor = new FakeInteractor(_gameState, _questState);
        Combat = new FakeCombat();
        GearEquipper = new FakeGearEquipper();
        BestGearEquipper = new FakeBestGearEquipper();
        JobChanger = new FakeJobChanger();
        Minigames = new FakeMinigameSkipper();
        Dialogue = new FakeDialogueResolver();
        Timing = new FakeTimingProfile();
    }

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------

    public static QuestDataDrivenState Create(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments,
        string initialState,
        EngineFixtureTests.FixtureInitialOverrides? initialOverrides = null)
    {
        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();

        ConfigureInitialState(quest, initialState, gameState, questState);
        ApplyInitialOverrides(initialOverrides, gameState, questState, quest);

        var questId = new QuestId(quest.Id);
        var stepPlans = BuildStepPlans(quest, fragments, questId);

        var state = new QuestDataDrivenState(gameState, questState, stepPlans, questId);

        // Set initial sequence block number to avoid PrepareForStep treating block 0
        // as a transition when we first call it.
        if (stepPlans.Count > 0)
        {
            state._currentSequenceNumber = stepPlans[0].SequenceNumber;
            state.PrepareForStep(stepPlans[0]);
        }

        // Set initial Gil so PurchaseItemSteps pass the affordability check.
        gameState.SetGil(1_000_000);

        return state;
    }

    // -------------------------------------------------------------------------
    // Initial state configuration (QD6)
    // -------------------------------------------------------------------------

    private static void ConfigureInitialState(
        QuestDefinition quest,
        string initialState,
        FakeGameStateProvider gameState,
        FakeQuestState questState)
    {
        switch (initialState)
        {
            case "fresh":
            {
                var acceptFrom = quest.AcceptFrom
                    ?? throw new InvalidOperationException(
                        $"Quest {quest.Id} has no AcceptFrom; cannot configure 'fresh' initial state.");
                gameState.SetZone(new ZoneId((uint)acceptFrom.Zone));
                var npcPos = acceptFrom.Position;
                // Offset from NPC so the engine needs to navigate (beyond default stopDistance of 3)
                gameState.SetPosition(new WorldPosition(npcPos.X + 15f, npcPos.Y, npcPos.Z + 15f));
                questState.SetQuestSequence(new QuestId(quest.Id), 0);
                questState.SetQuestStatus(new QuestId(quest.Id), QuestStatus.Available);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Unknown initialState '{initialState}' in fixture.");
        }
    }

    private static void ApplyInitialOverrides(
        EngineFixtureTests.FixtureInitialOverrides? overrides,
        FakeGameStateProvider gameState,
        FakeQuestState questState,
        QuestDefinition quest)
    {
        if (overrides is null) return;

        if (overrides.Zone is { } zone)
            gameState.SetZone(new ZoneId((uint)zone));

        if (overrides.PositionX is { } px && overrides.PositionY is { } py && overrides.PositionZ is { } pz)
            gameState.SetPosition(new WorldPosition(px, py, pz));

        if (overrides.QuestSequence is { } seq)
            questState.SetQuestSequence(new QuestId(quest.Id), seq);

        if (overrides.SlotsEquipped is { } slots)
            foreach (var slot in slots)
                gameState.SetEquippedItemLevelForSlot(slot, 1);

        if (overrides.Items is { } items)
            foreach (var (itemIdStr, qty) in items)
                if (uint.TryParse(itemIdStr, out var itemId))
                    gameState.SetItemCount(new ItemId(itemId), qty);

        if (overrides.Job is { } job)
            gameState.SetJob(new JobId((uint)job), 1);
    }

    // -------------------------------------------------------------------------
    // Step plan pre-computation (QD13)
    // -------------------------------------------------------------------------

    private static IReadOnlyList<StepPlan> BuildStepPlans(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments,
        QuestId activeQuestId)
        => ExpandAndBuildPlans(quest, fragments, activeQuestId);

    private static IReadOnlyList<StepPlan> ExpandAndBuildPlans(
        QuestDefinition quest,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments,
        QuestId activeQuestId)
    {
        var plans = new List<StepPlan>();

        foreach (var seq in quest.Sequences)
        {
            foreach (var step in ExpandSteps(seq.Steps, fragments))
            {
                var mutations = PredicateAnalyzer.ExtractMutations(step.Expect, activeQuestId);
                plans.Add(new StepPlan(step, mutations, seq.Sequence));
            }
        }

        return plans;
    }

    private static IEnumerable<Step> ExpandSteps(
        Step[] steps,
        IReadOnlyDictionary<string, FragmentDefinition>? fragments)
    {
        var usageCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (step is not FragmentStep fragmentStep)
            {
                yield return SynthesizeTeleportExpect(SynthesizePurchaseExpect(step));
                continue;
            }

            if (fragments is null)
                continue;

            if (!fragments.TryGetValue(fragmentStep.Ref, out var fragmentDef))
                continue;

            if (!usageCount.TryGetValue(fragmentStep.Id, out var count))
                count = 0;
            count++;
            usageCount[fragmentStep.Id] = count;
            var scopePrefix = count == 1 ? $"{fragmentStep.Id}:" : $"{fragmentStep.Id}#{count}:";

            foreach (var innerStep in fragmentDef.Steps)
            {
                if (innerStep is FragmentStep)
                    continue;

                var scopedId = $"{scopePrefix}{innerStep.Id}";
                var substituted = SubstituteExpect(innerStep.Expect, fragmentStep.Params);
                var cloned = CloneStepWith(innerStep, scopedId, substituted);
                yield return SynthesizeTeleportExpect(SynthesizePurchaseExpect(cloned));
            }
        }
    }

    private static Step SynthesizePurchaseExpect(Step step)
    {
        if (step is not PurchaseItemStep ps || ps.Expect is not null)
            return step;
        var synthesized = new PredicateExpect { Predicate = $"playerHasItem({ps.ItemId},{ps.Quantity})" };
        return CloneStepWith(ps, ps.Id, synthesized);
    }

    private static Step SynthesizeTeleportExpect(Step step)
    {
        if (step is not TeleportStep ts || ts.Expect is not null)
            return step;
        if (!AetheryteZoneMap.TryGetZone(ts.AetheryteId.Value, out var zoneId))
            return step;
        var synthesized = new PredicateExpect { Predicate = $"playerZone() == {zoneId}" };
        return CloneStepWith(ts, ts.Id, synthesized);
    }

    private static ExpectValue? SubstituteExpect(
        ExpectValue? expect,
        System.Collections.Generic.IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parms)
    {
        if (expect is not PredicateExpect pe || parms is null) return expect;

        var predicate = pe.Predicate;
        var result = System.Text.RegularExpressions.Regex.Replace(predicate, @"\$\{([^}]+)\}", match =>
        {
            var name = match.Groups[1].Value;
            if (!parms.TryGetValue(name, out var elem)) return match.Value;
            return elem.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => elem.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Number => elem.GetRawText(),
                _ => elem.ToString()
            };
        });

        if (string.Equals(result, predicate, StringComparison.Ordinal)) return expect;
        return new PredicateExpect { Predicate = result };
    }

    private static Step CloneStepWith(Step source, string id, ExpectValue? expect)
    {
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(source, QuestForgeJsonContext.QuestFileOptions);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["id"] = System.Text.Json.Nodes.JsonValue.Create(id);

        if (expect is null)
            node.Remove("expect");
        else
        {
            var expectJson = System.Text.Json.JsonSerializer.Serialize(expect, QuestForgeJsonContext.QuestFileOptions);
            node["expect"] = System.Text.Json.Nodes.JsonNode.Parse(expectJson);
        }

        return System.Text.Json.JsonSerializer.Deserialize<Step>(node.ToJsonString(), QuestForgeJsonContext.QuestFileOptions)!;
    }

    // -------------------------------------------------------------------------
    // PrepareForStep: set up pending combat mutations and advance sequence block
    // -------------------------------------------------------------------------

    private int _currentSequenceNumber = -1;

    private void PrepareForStep(StepPlan plan)
    {
        if (plan.Step is CombatStep)
            _pendingCombatMutations = new List<StateMutation>(plan.Mutations);
        else
            _pendingCombatMutations = null;

        // When advancing to a new sequence block, set the quest sequence so the engine
        // can match the block. The engine reads quest sequence and finds the block whose
        // Sequence value matches. Without this, the engine loops on
        // Wait("all steps in current sequence satisfied; awaiting game sequence advance").
        if (_currentSequenceNumber != plan.SequenceNumber && _questId.HasValue)
        {
            _currentSequenceNumber = plan.SequenceNumber;
            _questState.SetQuestSequence(_questId.Value, plan.SequenceNumber);
        }

        // Pre-requisite setup: for steps whose expect contains "not(X)", we must ensure X
        // is currently TRUE so the expect is NOT pre-satisfied. Otherwise the engine confirms
        // the step silently without dispatching any action.
        // Example: hand-over-item with expect "not(playerHasItem(2000104))" requires the
        // player to HAVE the item so the engine doesn't skip the step.
        foreach (var mutation in plan.Mutations)
        {
            if (mutation is StateMutation.SetNotPredicate { Inner: StateMutation.SetPlayerHasItem hasItem })
                _gameState.SetItemCount(new ItemId(hasItem.ItemId), hasItem.Quantity);
        }
    }

    private QuestId? _questId;

    // -------------------------------------------------------------------------
    // OnTick (QD10)
    // -------------------------------------------------------------------------

    public void OnTick(EngineAction action, int tick)
    {
        // Advance clock 1 second per tick so WaitSteps complete without real-time delay.
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Loading-zone countdown (QD9): decrement and clear when done.
        if (_loadingZoneCountdown > 0)
        {
            _loadingZoneCountdown--;
            if (_loadingZoneCountdown == 0)
                _gameState.SetUiState(new UiState(false, false, false, false, false, false, false, false, null));
            return;
        }

        if (_cursor >= _stepPlans.Count)
            return;

        var currentPlan = _stepPlans[_cursor];

        // Combat: apply mutations after first Engage (QD3).
        if (action is EngineAction.Engage && _pendingCombatMutations is not null)
        {
            ApplyMutations(_pendingCombatMutations);
            _gameState.SetInCombat(false);
            _pendingCombatMutations = null;
            return;
        }

        // Navigate for TravelStep:
        // - NpcDialogue route: navigate to the NPC position only (zone change happens on Interact).
        // - Aethernet route: navigate to shard position only (zone change happens on UseAethernet).
        // - Plain movement: set player to final destination position and zone.
        if (action is EngineAction.Navigate && currentPlan.Step is TravelStep travelStep)
        {
            if (travelStep.RouteHint?.NpcDialogue is { } npcHint)
            {
                var npcPos = npcHint.Target.Position;
                _gameState.SetPosition(new WorldPosition(npcPos.X, npcPos.Y, npcPos.Z));
            }
            else if (travelStep.RouteHint?.Aethernet is not null)
            {
                // Aethernet route: move to shard position only. Zone change deferred to UseAethernet.
                if (travelStep.Destination.Position is { } shardPos)
                    _gameState.SetPosition(new WorldPosition(shardPos.X, shardPos.Y, shardPos.Z));
            }
            else
            {
                var hasPosOrNearMutation = currentPlan.Mutations.Any(
                    m => m is StateMutation.SetPlayerPosition or StateMutation.SetPlayerNear);
                var zoneBefore = _gameState.GetPlayerZone(CancellationToken.None).GetAwaiter().GetResult()
                    is Result<ZoneId>.Success { Value: var z } ? (ZoneId?)z : null;
                ApplyMutations(currentPlan.Mutations);
                if (!hasPosOrNearMutation && travelStep.Destination.Position is { } destPos)
                    _gameState.SetPosition(new WorldPosition(destPos.X, destPos.Y, destPos.Z));
                var zoneAfter = _gameState.GetPlayerZone(CancellationToken.None).GetAwaiter().GetResult()
                    is Result<ZoneId>.Success { Value: var za } ? (ZoneId?)za : null;
                if (zoneBefore != zoneAfter)
                {
                    _gameState.SetUiState(new UiState(false, false, true, false, false, false, false, false, null));
                    _loadingZoneCountdown = 1;
                }
            }
            return;
        }

        // Navigate for AethernetStep: move to source shard position.
        if (action is EngineAction.Navigate && currentPlan.Step is AethernetStep aethernetStep)
        {
            var shardPos = aethernetStep.FromPosition;
            _gameState.SetPosition(new WorldPosition(shardPos.X, shardPos.Y, shardPos.Z));
            return;
        }

        // Implied navigation for non-TravelStep (QD8): set player to target position.
        if (action is EngineAction.Navigate && currentPlan.Step is not TravelStep)
        {
            var targetPos = GetTargetPosition(currentPlan.Step);
            if (targetPos is not null)
                _gameState.SetPosition(new WorldPosition(targetPos.X, targetPos.Y, targetPos.Z));
            return;
        }

        // Teleport: apply mutations (zone change).
        if (action is EngineAction.Teleport)
        {
            ApplyMutations(currentPlan.Mutations);
            return;
        }

        // UseAethernet: apply mutations + start loading zone simulation (QD9).
        if (action is EngineAction.UseAethernet)
        {
            ApplyMutations(currentPlan.Mutations);
            _gameState.SetUiState(new UiState(false, false, true, false, false, false, false, false, null));
            _loadingZoneCountdown = 1;
            return;
        }

        // Interact for TravelStep with NpcDialogue route: apply step mutations (zone change)
        // and simulate loading zone if zone actually changes (produces the wait transition).
        if (action is EngineAction.Interact && currentPlan.Step is TravelStep npcDialogueTravelStep
            && npcDialogueTravelStep.RouteHint?.NpcDialogue is not null)
        {
            var zoneBefore2 = _gameState.GetPlayerZone(CancellationToken.None).GetAwaiter().GetResult()
                is Result<ZoneId>.Success { Value: var z2 } ? (ZoneId?)z2 : null;
            ApplyMutations(currentPlan.Mutations);
            var zoneAfter2 = _gameState.GetPlayerZone(CancellationToken.None).GetAwaiter().GetResult()
                is Result<ZoneId>.Success { Value: var za2 } ? (ZoneId?)za2 : null;
            if (zoneBefore2 != zoneAfter2)
            {
                _gameState.SetUiState(new UiState(false, false, true, false, false, false, false, false, null));
                _loadingZoneCountdown = 1;
            }
            return;
        }

        // Primary actions: apply expect mutations.
        if (action is EngineAction.Interact
            or EngineAction.HandOver
            or EngineAction.UseAction
            or EngineAction.UseEmote
            or EngineAction.SayChatMessage
            or EngineAction.InteractObject
            or EngineAction.Purchase
            or EngineAction.EquipGear
            or EngineAction.EquipBestGear
            or EngineAction.ChangeJob
            or EngineAction.RegisterGearset
            or EngineAction.OpenCoffer
            or EngineAction.EnterSinglePlayerDuty
            or EngineAction.EnterDuty)
        {
            // Special handling for ChangeJob: update game state job directly.
            if (action is EngineAction.ChangeJob cj)
                _gameState.SetJob(cj.Job, 90);

            ApplyMutations(currentPlan.Mutations);
            return;
        }
    }

    // -------------------------------------------------------------------------
    // OnTransitionRecorded (QD7) — IFixtureState 2-arg version (no-op)
    // -------------------------------------------------------------------------

    public void OnTransitionRecorded(EngineAction action, int tick)
    {
        // No-op: the 3-arg version is used by the test's RunEngineToCompletion helper.
    }

    // -------------------------------------------------------------------------
    // OnTransitionRecorded 3-arg version (QD7)
    // -------------------------------------------------------------------------

    public void OnTransitionRecorded(EngineAction action, int tick, string? stepId)
    {
        if (stepId is null)
            return;

        // Scan forward from current cursor to find the step with this ID.
        for (var i = _cursor; i < _stepPlans.Count; i++)
        {
            if (_stepPlans[i].Step.Id == stepId)
            {
                _cursor = i;
                break;
            }
        }

        if (_cursor >= _stepPlans.Count)
            return;

        var currentPlan = _stepPlans[_cursor];

        // Advance cursor when the primary action for this step is recorded.
        if (IsPrimaryActionForStep(currentPlan.Step, action))
        {
            _cursor++;
            if (_cursor < _stepPlans.Count)
                PrepareForStep(_stepPlans[_cursor]);
        }
    }

    // -------------------------------------------------------------------------
    // ApplyMutations (QD2 table + QD14 + QD15)
    // -------------------------------------------------------------------------

    private void ApplyMutations(IReadOnlyList<StateMutation> mutations)
    {
        foreach (var m in mutations)
        {
            switch (m)
            {
                case StateMutation.SetQuestSequence sq:
                    _questState.SetQuestSequence(sq.Quest, sq.Value);
                    break;

                case StateMutation.SetQuestComplete qc:
                    _questState.SetQuestStatus(qc.Quest, QuestStatus.Complete);
                    break;

                case StateMutation.SetQuestAccepted qa:
                    _questState.SetQuestStatus(qa.Quest, QuestStatus.Accepted);
                    _questState.AddAcceptedQuest(qa.Quest);
                    break;

                case StateMutation.SetPlayerZone z:
                    _gameState.SetZone(new ZoneId((uint)z.Zone));
                    break;

                case StateMutation.SetPlayerPosition p:
                    _gameState.SetPosition(new WorldPosition(p.X, p.Y, p.Z));
                    break;

                case StateMutation.SetPlayerNear pn:
                    _gameState.SetPosition(new WorldPosition(pn.X, pn.Y, pn.Z));
                    break;

                case StateMutation.SetQuestFlag f:
                    _questState.SetQuestFlagBit(f.Quest, f.Bit, f.Value);
                    break;

                case StateMutation.SetQuestVariable v:
                    ApplyVariableMutation(v);
                    break;

                case StateMutation.SetAttuned a:
                    _gameState.SetAetheryteAttuned(new Adapters.Types.AetheryteId(a.AetheryteId), true);
                    break;

                case StateMutation.SetPlayerHasItem item:
                    _gameState.SetItemCount(new ItemId(item.ItemId), item.Quantity);
                    break;

                case StateMutation.SetObjectExistsInRange obj:
                    _gameState.AddInteractable(new InteractableReference(
                        new InteractableId(obj.DataId),
                        _gameState.GetPlayerPosition(CancellationToken.None).GetAwaiter().GetResult()
                            is Result<WorldPosition>.Success { Value: var p2 } ? p2 : new WorldPosition(0f, 0f, 0f),
                        0f,
                        InteractableKind.Other));
                    break;

                case StateMutation.SetNpcExistsNearby npc:
                {
                    var playerPos = _gameState.GetPlayerPosition(CancellationToken.None).GetAwaiter().GetResult()
                        is Result<WorldPosition>.Success { Value: var p3 } ? p3 : new WorldPosition(0f, 0f, 0f);
                    _gameState.AddNpc(new NpcReference(new NpcId(npc.DataId), playerPos, 0f));
                    break;
                }

                case StateMutation.SetSlotEquipped slot:
                    _gameState.SetEquippedItemLevelForSlot(slot.SlotIndex, slot.ItemLevel);
                    break;

                case StateMutation.SetItemEquipped equipped:
                    _gameState.SetItemEquipped(new ItemId(equipped.ItemId), true);
                    break;

                case StateMutation.SetInCombat combat:
                    _gameState.SetInCombat(combat.Value);
                    break;

                case StateMutation.SetGearsetExistsForJob gearset:
                    _gameState.SetGearsetExistsForJob(gearset.JobId, true);
                    break;

                case StateMutation.SetHasCoffers coffers:
                    _gameState.SetHasCoffers(coffers.Value);
                    break;

                case StateMutation.SetAetherCurrentAttuned ac:
                    _gameState.SetAetherCurrentAttuned(ac.Id, true);
                    break;

                case StateMutation.SetNotPredicate notM:
                    // "not X" means X should be FALSE after the step completes.
                    // Clear item counts to make "not(playerHasItem(...))" true.
                    if (notM.Inner is StateMutation.SetPlayerHasItem clearItem)
                        _gameState.SetItemCount(new ItemId(clearItem.ItemId), 0);
                    break;
            }
        }
    }

    private void ApplyVariableMutation(StateMutation.SetQuestVariable v)
    {
        var current = _questState.GetQuestVariables(v.Quest, CancellationToken.None)
            .GetAwaiter().GetResult().ValueOrThrow;
        var bytes = current.ToArray();

        switch (v.Nibble)
        {
            case Nibble.Whole:
                bytes[v.Index] = (byte)v.Value;
                break;
            case Nibble.Low:
                bytes[v.Index] = (byte)((bytes[v.Index] & 0xF0) | (v.Value & 0x0F));
                break;
            case Nibble.High:
                bytes[v.Index] = (byte)((bytes[v.Index] & 0x0F) | ((v.Value & 0x0F) << 4));
                break;
        }

        _questState.SetQuestVariables(v.Quest, bytes);
    }

    // -------------------------------------------------------------------------
    // IsPrimaryActionForStep
    // -------------------------------------------------------------------------

    private static bool IsPrimaryActionForStep(Step step, EngineAction action) => step switch
    {
        TravelStep { RouteHint.NpcDialogue: not null } => action is EngineAction.Interact,
        TravelStep { RouteHint.Aethernet: not null } => action is EngineAction.UseAethernet,
        TravelStep => action is EngineAction.Navigate,
        AethernetStep => action is EngineAction.UseAethernet,
        TalkStep => action is EngineAction.Interact,
        AcceptStep => action is EngineAction.Interact,
        TurnInStep => action is EngineAction.Interact,
        CombatStep => action is EngineAction.Engage,
        AttunementStep => action is EngineAction.Interact,
        HandOverItemStep => action is EngineAction.HandOver,
        PurchaseItemStep => action is EngineAction.Purchase,
        TeleportStep => action is EngineAction.Teleport,
        UseActionStep => action is EngineAction.UseAction,
        UseEmoteStep => action is EngineAction.UseEmote,
        SayChatMessageStep => action is EngineAction.SayChatMessage,
        EquipGearForQuestStep => action is EngineAction.EquipGear,
        EquipBestGearStep => action is EngineAction.EquipBestGear,
        ChangeJobStep => action is EngineAction.ChangeJob,
        RegisterGearsetStep => action is EngineAction.RegisterGearset,
        OpenCoffersStep => action is EngineAction.OpenCoffer,
        InteractObjectStep => action is EngineAction.InteractObject,
        PickupItemStep => action is EngineAction.InteractObject,
        DutyStep { Kind: "spd" } => action is EngineAction.EnterSinglePlayerDuty,
        DutyStep => action is EngineAction.EnterDuty,
        CutsceneStep => action is EngineAction.Wait,
        WaitStep => action is EngineAction.Wait,
        _ => false
    };

    // -------------------------------------------------------------------------
    // GetTargetPosition: extract the Position3 for implied navigation (QD8)
    // -------------------------------------------------------------------------

    private static Position3? GetTargetPosition(Step step) => step switch
    {
        TalkStep { Target: { } loc } => loc.Position,
        TalkStep { Targets: { Length: > 0 } targets } => targets[0].Position,
        AcceptStep acc => acc.Target.Position,
        TurnInStep ti => ti.Target.Position,
        AttunementStep { Location: { } loc } => loc.Position,
        HandOverItemStep ho => ho.Target.Position,
        PurchaseItemStep p => p.Target.Position,
        UseActionStep { Location: { } loc } => loc.Position,
        UseEmoteStep { Location: { } loc } => loc.Position,
        CombatStep { Location: { } loc } => loc.Position,
        InteractObjectStep { Position: { } pos } => pos,
        PickupItemStep { Position: { } pos } => pos,
        _ => null
    };

    // -------------------------------------------------------------------------
    // NullTraceWriter (local helper)
    // -------------------------------------------------------------------------

    private sealed class NullTraceWriter : ITraceWriter
    {
        public void Write(TraceEvent evt) { }
    }
}
