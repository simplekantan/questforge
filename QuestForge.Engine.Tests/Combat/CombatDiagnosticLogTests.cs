using Microsoft.Extensions.Logging;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Combat diagnostic logging tests (CD1--CD2).
///
/// All tests fail to compile until Builder:
///   1. Adds optional ILogger parameter to CombatController constructor
///   2. Adds diagnostic log in Decide when target is null
///
/// Spec: docs/NAV_STUCK_DETECTION_SPEC.md section 3.4
/// </summary>
public sealed class CombatDiagnosticLogTests
{
    // -------------------------------------------------------------------------
    // CapturingLogger -- minimal test logger that stores log entries.
    //
    // WHY hand-rolled: The test project does not reference
    // Microsoft.Extensions.Diagnostics.Testing (which provides FakeLogger<T>).
    // Adding a test-only package is a Builder decision. For now we hand-roll
    // to keep the RED phase self-contained.
    //
    // BUILDER GUIDANCE: If you prefer Microsoft.Extensions.Diagnostics.Testing,
    // add the package to QuestForge.Engine.Tests.csproj and replace this class
    // with FakeLogger<CombatController> / FakeLogCollector.
    // -------------------------------------------------------------------------

    private sealed record CapturedLogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<CapturedLogEntry> _entries = new();

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HostileActor MakeHostile(ulong id, uint dataId, float distance = 5f)
        => new(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            distance,
            IsTargetable: true,
            IsDead: false,
            IsTargetingPlayer: false,
            OnPlayerEnmityList: false,
            HasQuestMarker: false);

    private static CombatStep MakeStep(uint[] killIds)
        => new()
        {
            Id = "test-combat",
            KillEnemyDataIds = killIds,
            Spawn = CombatSpawn.AutoOnEnterArea
        };

    // =========================================================================
    // CD1 -- No target found, diagnostic log emitted
    // =========================================================================

    [Fact]
    public async Task CD1_NoTargetFound_DiagnosticLogEmitted()
    {
        /*
         * RED: CombatController constructor does not accept ILogger yet.
         *
         * Given CombatStep with KillEnemyDataIds = [100, 200],
         *       FakeGameStateProvider returns 3 hostile actors with DataIds [300, 400, 500]
         *       (none matching kill set).
         * When Decide is called,
         * Then logger captured one Debug-level log entry containing:
         *   - "No target found"
         *   - "HostileActors in scan: 3"
         *   - The KillEnemyDataIds "100,200" (or "100" and "200")
         *   - The ActorDataIds "300,400,500" (or "300" and "400" and "500")
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var navigator = new FakeNavigator(gameState);
        var logger = new CapturingLogger<CombatController>();

        // RED: This constructor overload does not exist yet
        var controller = new CombatController(gameState, combat, navigator, logger);

        // Add hostiles that do NOT match the kill set
        gameState.AddHostileActor(MakeHostile(1, 300));
        gameState.AddHostileActor(MakeHostile(2, 400));
        gameState.AddHostileActor(MakeHostile(3, 500));

        var step = MakeStep([100u, 200u]);

        await controller.Decide(step, CancellationToken.None);

        // Assert: exactly one Debug-level log entry
        var debugEntries = logger.Entries
            .Where(e => e.Level == LogLevel.Debug)
            .ToList();
        Assert.Equal(1, debugEntries.Count);

        var message = debugEntries[0].Message;

        // Must mention "No target found" (or similar)
        Assert.Contains("No target found", message, StringComparison.OrdinalIgnoreCase);

        // Must include actor count
        Assert.Contains("3", message);

        // Must include the kill set DataIds
        Assert.Contains("100", message);
        Assert.Contains("200", message);

        // Must include the actual actor DataIds
        Assert.Contains("300", message);
        Assert.Contains("400", message);
        Assert.Contains("500", message);
    }

    // =========================================================================
    // CD2 -- Target found, no diagnostic log emitted
    // =========================================================================

    [Fact]
    public async Task CD2_TargetFound_NoDiagnosticLog()
    {
        /*
         * RED: CombatController constructor does not accept ILogger yet.
         *
         * Given same setup but hostile actor DataId 100 is present (matches kill set).
         * When Decide is called,
         * Then logger captured zero Debug-level log entries.
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var navigator = new FakeNavigator(gameState);
        var logger = new CapturingLogger<CombatController>();

        // RED: This constructor overload does not exist yet
        var controller = new CombatController(gameState, combat, navigator, logger);

        // Add a hostile that DOES match the kill set
        gameState.AddHostileActor(MakeHostile(1, 100));

        var step = MakeStep([100u, 200u]);

        await controller.Decide(step, CancellationToken.None);

        // Assert: zero Debug-level log entries
        var debugEntries = logger.Entries
            .Where(e => e.Level == LogLevel.Debug)
            .ToList();
        Assert.Empty(debugEntries);
    }
}
