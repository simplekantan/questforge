using Microsoft.Extensions.Logging;
using QuestForge.Adapters;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
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
    {
        _quest = quest ?? throw new ArgumentNullException(nameof(quest));
    }

    public async Task<EngineAction> Tick(CancellationToken ct)
    {
        if (_quest is null)
            return new EngineAction.AwaitUser("no quest loaded");

        var questId = new QuestId(_quest.Id);

        var seqResult = await _questState.GetQuestSequence(questId, ct);
        if (seqResult is Result<int>.Failure f1)
            return new EngineAction.AwaitUser($"adapter failure reading sequence: {f1.Reason}");

        var completeResult = await _questState.IsQuestComplete(questId, ct);
        if (completeResult is Result<bool>.Success { Value: true })
            return new EngineAction.Done();

        var currentSeq = seqResult.ValueOrThrow;
        var matchingBlock = _quest.Sequences.FirstOrDefault(s => s.Sequence == currentSeq);
        if (matchingBlock is null)
            return new EngineAction.AwaitUser($"no sequence block matches current sequence {currentSeq}");

        if (matchingBlock.SkipIf is not null)
        {
            if (await _expectEvaluator.Evaluate(matchingBlock.SkipIf, ct))
                return new EngineAction.AwaitUser("sequence skipped by skipIf — engine cannot self-advance in Phase 4");
        }

        foreach (var step in matchingBlock.Steps)
        {
            if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
                continue;
            if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct))
                continue;
            return ResolveActionForStep(step);
        }

        return new EngineAction.Wait("all steps in current sequence satisfied; awaiting game sequence advance");
    }

    private EngineAction ResolveActionForStep(Step step) => step switch
    {
        TravelStep travel when travel.Destination.Position is { } pos =>
            new EngineAction.Navigate(
                new WorldPosition(pos.X, pos.Y, pos.Z),
                new NavigationOptions(StoppingDistance: step.StopDistance ?? 3.0f)),

        TravelStep travel when travel.Destination.Position is null =>
            throw new NotSupportedException("Phase 4 does not support aetheryte-only travel steps"),

        TalkStep talk when talk.Target is not null =>
            new EngineAction.Interact(new NpcId(talk.Target.NpcId)),

        TalkStep talk when talk.Target is null && talk.Targets is { Length: > 0 } =>
            throw new NotSupportedException("Phase 4 does not support multi-target talk steps"),

        _ => throw new NotSupportedException($"Phase 4 does not support step type {step.GetType().Name}")
    };
}