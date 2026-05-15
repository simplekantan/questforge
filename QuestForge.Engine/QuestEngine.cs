using Microsoft.Extensions.Logging;
using QuestForge.Adapters;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
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
    }

    public void StartQuest(QuestDefinition quest) => throw new NotImplementedException();

    public Task<EngineAction> Tick(CancellationToken ct) => throw new NotImplementedException();
}