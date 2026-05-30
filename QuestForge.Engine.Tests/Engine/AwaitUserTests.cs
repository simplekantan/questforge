using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Adapters;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for EngineAction.AwaitUser return cases (§5.7).
///
/// RED PHASE: All tests throw NotImplementedException from QuestEngine.Tick().
/// </summary>
public sealed class AwaitUserTests
{
    // -------------------------------------------------------------------------
    // §5.7.1 — No quest loaded → AwaitUser("no quest loaded")
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoQuestLoaded_ReturnsAwaitUser()
    {
        /*
         * RED: NotImplementedException from QuestEngine.Tick
         *
         * CONTRACT: Given engine is constructed but StartQuest has not been called,
         *           When Tick is called,
         *           Then EngineAction.AwaitUser with Reason "no quest loaded" is returned.
         *
         * BUILDER GUIDANCE:
         *   First check in Tick: if no quest is loaded, return AwaitUser("no quest loaded").
         *   The exact string is part of the contract (§4.2 algorithm).
         */

        // Arrange
        var harness = new EngineTestHarness();
        // Deliberately do NOT call StartQuest

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Equal("no quest loaded", awaitUser.Reason);
    }

    // -------------------------------------------------------------------------
    // §5.7.2 — Unknown sequence number → AwaitUser mentioning the sequence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnknownSequence_ReturnsAwaitUser()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given sequence number 42 has no matching block in the fixture,
         *           When Tick is called,
         *           Then EngineAction.AwaitUser is returned with Reason mentioning sequence 42.
         *
         * BUILDER GUIDANCE:
         *   After reading sequence = 42, FirstOrDefault on quest.Sequences returns null.
         *   Return AwaitUser($"no sequence block matches current sequence {currentSeq}") or similar.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 42); // No block for 42 in fixture
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("42", awaitUser.Reason, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // §5.7.3 — Adapter failure on GetQuestSequence → AwaitUser with failure reason
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AdapterFailure_OnSequenceRead_ReturnsAwaitUser()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given IQuestState.GetQuestSequence returns Failure("adapter-broken"),
         *           When Tick is called,
         *           Then EngineAction.AwaitUser is returned with Reason including "adapter-broken".
         *
         * BUILDER GUIDANCE:
         *   The engine must not crash on adapter failure. It returns AwaitUser to signal
         *   "I cannot proceed; try again next tick" without throwing an exception.
         *   The failure.Reason string must appear somewhere in AwaitUser.Reason.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var failingQuestState = new FailingQuestState();
        var navigator = new FakeNavigator(gameState);
        var teleporter = new FakeTeleporter(gameState);
        var interactor = new FakeInteractor(gameState, new FakeQuestState());

        var engine = new QuestEngine(
            gameState,
            failingQuestState,
            navigator,
            teleporter,
            interactor,
            new FakeCombat(),
            new FakeMinigameSkipper(),
            new FakeDialogueResolver(),
            new FakeTimingProfile(),
            new NullTraceWriter(),
            NullLogger<QuestEngine>.Instance);

        var quest = LoadFixture();
        engine.StartQuest(quest);

        // Act
        var action = await engine.Tick(CancellationToken.None);

        // Assert
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("adapter-broken", awaitUser.Reason, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Fixture loader
    // -------------------------------------------------------------------------

    private static QuestDefinition LoadFixture()
    {
        var json = File.ReadAllText("Fixtures/66130.json");
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }
}

/// <summary>
/// IQuestState implementation that always returns Failure from GetQuestSequence.
/// Used for §5.7.3 adapter-failure testing.
/// </summary>
file sealed class FailingQuestState : IQuestState
{
    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<int>>(Result.Fail<int>("adapter-broken"));

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<QuestStatus>>(Result.Fail<QuestStatus>("adapter-broken"));

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<uint>>(Result.Fail<uint>("adapter-broken"));

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Fail<bool>("adapter-broken"));

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Fail<bool>("adapter-broken"));

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Fail<bool>("adapter-broken"));

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Fail<bool>("adapter-broken"));

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<QuestUnlockReason?>>(Result.Fail<QuestUnlockReason?>("adapter-broken"));

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<QuestId>>>(Result.Fail<IReadOnlyList<QuestId>>("adapter-broken"));

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<QuestReward>>>(Result.Fail<IReadOnlyList<QuestReward>>("adapter-broken"));

    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Fail<IReadOnlyList<byte>>("adapter-broken"));

    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Fail<bool>("adapter-broken"));
}
