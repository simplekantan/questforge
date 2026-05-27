using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters;
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

    // §5.3.2 — Each parameter null in turn → ArgumentNullException with correct paramName.
    [Theory]
    [InlineData("gameState")]
    [InlineData("questState")]
    [InlineData("navigator")]
    [InlineData("teleporter")]
    [InlineData("interactor")]
    [InlineData("combat")]
    [InlineData("gear")]
    [InlineData("minigames")]
    [InlineData("dialogue")]
    [InlineData("timing")]
    [InlineData("trace")]
    [InlineData("logger")]
    public void Constructor_NullParam_ThrowsArgumentNullException(string nullParam)
    {
        var (gameState, questState, navigator, teleporter, interactor,
             combat, gear, minigames, dialogue, timing, trace, logger) = GetAllArgs();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new QuestEngine(
                nullParam == "gameState"   ? null! : gameState,
                nullParam == "questState"  ? null! : questState,
                nullParam == "navigator"   ? null! : navigator,
                nullParam == "teleporter"  ? null! : teleporter,
                nullParam == "interactor"  ? null! : interactor,
                nullParam == "combat"      ? null! : combat,
                nullParam == "gear"        ? null! : gear,
                nullParam == "minigames"   ? null! : minigames,
                nullParam == "dialogue"    ? null! : dialogue,
                nullParam == "timing"      ? null! : timing,
                nullParam == "trace"       ? null! : trace,
                nullParam == "logger"      ? null! : logger));

        Assert.Equal(nullParam, ex.ParamName);
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
