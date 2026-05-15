using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Engine.Tests.Fakes;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine constructor null-guard behaviour (§5.3).
///
/// RED PHASE: The constructor is implemented in Phase A scaffolding with actual null-checks
/// (not NotImplementedException), so §5.3.1 (non-null succeeds) will be GREEN immediately.
/// The 12 null-guard tests (§5.3.2) will also be GREEN once the scaffolding is correct.
/// These tests are included here as regression guards and documentation of the contract.
/// </summary>
public sealed class QuestEngineConstructorTests
{
    // §5.3.1 — All 12 non-null parameters → construction succeeds.
    [Fact]
    public void Constructor_AllNonNull_Succeeds()
    {
        /*
         * CONTRACT: Given all 12 constructor parameters are non-null,
         *           When QuestEngine is constructed, Then no exception is thrown.
         *
         * BUILDER GUIDANCE: Store all references; assert non-null for each.
         */

        // Arrange & Act & Assert — no exception
        var _ = BuildEngine();
    }

    // §5.3.2 — Null gameState → ArgumentNullException with correct paramName.
    [Fact]
    public void Constructor_NullGameState_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given gameState is null, When constructed,
         *           Then ArgumentNullException is thrown with paramName "gameState".
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(null!, questState, navigator, teleporter, interactor,
                            combat, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("gameState", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullQuestState_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given questState is null, Then ArgumentNullException("questState").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, null!, navigator, teleporter, interactor,
                            combat, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("questState", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullNavigator_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given navigator is null, Then ArgumentNullException("navigator").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, null!, teleporter, interactor,
                            combat, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("navigator", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullTeleporter_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given teleporter is null, Then ArgumentNullException("teleporter").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, null!, interactor,
                            combat, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("teleporter", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullInteractor_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given interactor is null, Then ArgumentNullException("interactor").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, null!,
                            combat, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("interactor", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullCombat_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given combat is null, Then ArgumentNullException("combat").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            null!, gear, minigames, dialogue, timing, trace, logger));

        Assert.Equal("combat", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullGear_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given gear is null, Then ArgumentNullException("gear").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, null!, minigames, dialogue, timing, trace, logger));

        Assert.Equal("gear", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullMinigames_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given minigames is null, Then ArgumentNullException("minigames").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, gear, null!, dialogue, timing, trace, logger));

        Assert.Equal("minigames", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDialogue_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given dialogue is null, Then ArgumentNullException("dialogue").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, gear, minigames, null!, timing, trace, logger));

        Assert.Equal("dialogue", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullTiming_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given timing is null, Then ArgumentNullException("timing").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, gear, minigames, dialogue, null!, trace, logger));

        Assert.Equal("timing", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullTrace_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given trace is null, Then ArgumentNullException("trace").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, gear, minigames, dialogue, timing, null!, logger));

        Assert.Equal("trace", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        /*
         * CONTRACT: Given logger is null, Then ArgumentNullException("logger").
         */
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                            combat, gear, minigames, dialogue, timing, trace, null!));

        Assert.Equal("logger", ex.ParamName);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static QuestEngine BuildEngine()
    {
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        return new QuestEngine(gameState, questState, navigator, teleporter, interactor,
                               combat, gear, minigames, dialogue, timing, trace, logger);
    }

    private static (
        FakeGameStateProvider gameState,
        FakeQuestState questState,
        FakeNavigator navigator,
        FakeTeleporter teleporter,
        FakeInteractor interactor,
        FakeCombat combat,
        FakeGearManager gear,
        FakeMinigameSkipper minigames,
        FakeDialogueResolver dialogue,
        FakeTimingProfile timing,
        NullTraceWriter trace,
        Microsoft.Extensions.Logging.ILogger<QuestEngine> logger)
        GetAllArgs()
    {
        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();
        var navigator = new FakeNavigator(gameState);
        var teleporter = new FakeTeleporter(gameState);
        var interactor = new FakeInteractor(gameState, questState);
        var combat = new FakeCombat();
        var gear = new FakeGearManager();
        var minigames = new FakeMinigameSkipper();
        var dialogue = new FakeDialogueResolver();
        var timing = new FakeTimingProfile();
        var trace = new NullTraceWriter();
        var logger = NullLogger<QuestEngine>.Instance;
        return (gameState, questState, navigator, teleporter, interactor,
                combat, gear, minigames, dialogue, timing, trace, logger);
    }
}