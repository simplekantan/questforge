using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Types;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

// ---------------------------------------------------------------------------
// ManualTimeProvider — minimal hand-rolled controllable clock for test use.
//
// WHY hand-rolled: Microsoft.Extensions.Time.Testing.FakeTimeProvider exists but
// the test project does not reference Microsoft.Extensions.Time.Testing, and adding
// a test-only package is noted as a Builder task if preferred. For now we hand-roll
// to keep the RED phase self-contained. The BCL TimeProvider abstract class is
// sufficient — override GetUtcNow() and expose Advance(TimeSpan).
//
// BUILDER GUIDANCE: If you prefer Microsoft.Extensions.Time.Testing.FakeTimeProvider,
// add the package to QuestForge.Engine.Tests.csproj only and delete this class.
// ---------------------------------------------------------------------------

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset startTime)
    {
        _now = startTime;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Advance the fake clock forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// Tests for QuestEngine dispatch of WaitStep (WS-1 through WS-6).
///
/// RED PHASE: ALL tests will fail to compile until Builder:
///   1. Adds WaitStep class to QuestForge.Schema/Step.cs:
///        public class WaitStep : Step { public double Seconds { get; init; } }
///      with [JsonDerivedType(typeof(WaitStep), "wait")] on the Step base class.
///   2. Adds [JsonSerializable(typeof(WaitStep))] to QuestForgeJsonContext.
///   3. Adds optional TimeProvider parameter to QuestEngine constructor:
///        public QuestEngine(..., TimeProvider? clock = null)
///   4. Implements the WaitStep dispatch arm in QuestEngine (start-time tracking,
///      elapsed check, confirm-and-advance when >= Seconds).
///
/// Clock injection: tests construct QuestEngine directly (as in QuestEngineConstructorTests)
/// and pass a ManualTimeProvider as the optional 13th argument. The EngineTestHarness is
/// NOT used here because it is sealed and does not expose a clock parameter.
///
/// Decision table:
///   elapsed &lt; Seconds   → EngineAction.Wait(reason)
///   elapsed == Seconds  → confirms step, continues loop (>= semantics)
///   elapsed &gt; Seconds   → confirms step, continues loop
/// </summary>
public sealed class WaitStepTests
{
    // Stable epoch so all tests share a predictable T0.
    private static readonly DateTimeOffset T0 =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Engine construction helpers
    // =========================================================================

    /// <summary>
    /// Constructs a QuestEngine backed by all fake adapters plus the given clock.
    /// Returns the engine and all fakes needed to drive tests.
    ///
    /// RED: the `clock` argument does not compile until Builder adds the optional
    /// TimeProvider parameter to QuestEngine.
    /// </summary>
    private static (
        QuestEngine engine,
        FakeGameStateProvider gameState,
        FakeQuestState questState)
        BuildEngine(ManualTimeProvider clock)
    {
        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();
        var navigator = new FakeNavigator(gameState);
        var teleporter = new FakeTeleporter(gameState);
        var interactor = new FakeInteractor(gameState, questState);
        var combat = new FakeCombat();
        var minigames = new FakeMinigameSkipper();
        var dialogue = new FakeDialogueResolver();
        var timing = new FakeTimingProfile();
        var trace = new NullTraceWriter();
        var logger = NullLogger<QuestEngine>.Instance;

        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            combat, minigames, dialogue, timing, trace, logger,
            clock);

        return (engine, gameState, questState);
    }

    /// <summary>
    /// Builds a QuestDefinition with a single sequence containing <paramref name="steps"/>.
    /// </summary>
    private static QuestDefinition BuildQuest(uint questId, int sequence, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "WaitStep Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = sequence,
                    Steps = steps
                }
            ]
        };

    // =========================================================================
    // WS-1a: Deserialize JSON → WaitStep with correct Seconds value
    // =========================================================================

    [Fact]
    public void WS_1a_Deserialize_WaitStep_FromJson()
    {
        /*
         * Given JSON {"type":"wait","id":"pause","seconds":5},
         * When deserialized as Step via QuestFileOptions,
         * Then result is WaitStep with Seconds == 5.0.
         *
         * RED: Fails to compile — WaitStep type does not exist yet.
         *
         * BUILDER GUIDANCE:
         *   Add WaitStep to Step.cs:
         *     public class WaitStep : Step { public double Seconds { get; init; } }
         *   Add [JsonDerivedType(typeof(WaitStep), "wait")] on the Step base class.
         *   Add [JsonSerializable(typeof(WaitStep))] to QuestForgeJsonContext.
         */

        // Given
        const string json = """{"type":"wait","id":"pause","seconds":5}""";

        // When — RED: WaitStep does not exist
        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Then
        var waitStep = Assert.IsType<WaitStep>(step);
        Assert.Equal(5.0, waitStep.Seconds);
    }

    // =========================================================================
    // WS-1b: Serialize WaitStep → preserves "wait" discriminator and Seconds
    // =========================================================================

    [Fact]
    public void WS_1b_RoundTrip_WaitStep_PreservesDiscriminatorAndSeconds()
    {
        /*
         * Given new WaitStep { Id="pause", Seconds=3.5 },
         * When serialized then deserialized via QuestFileOptions,
         * Then the JSON contains the "wait" discriminator and the deserialized
         *      step is WaitStep with Seconds == 3.5.
         *
         * RED: Fails to compile — WaitStep type does not exist yet.
         */

        // Given — RED: WaitStep does not exist
        Step original = new WaitStep { Id = "pause", Seconds = 3.5 };

        // When
        var json = JsonSerializer.Serialize(original, QuestForgeJsonContext.QuestFileOptions);
        var restored = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Then
        Assert.Contains("\"type\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wait\"", json, StringComparison.Ordinal);
        var waitStep = Assert.IsType<WaitStep>(restored);
        Assert.Equal(3.5, waitStep.Seconds);
    }

    // =========================================================================
    // WS-1c: WaitStep is registered in QuestForgeJsonContext source-gen
    // =========================================================================

    [Fact]
    public void WS_1c_WaitStep_IsRegisteredInJsonContext()
    {
        /*
         * Given QuestForgeJsonContext.Default,
         * When GetTypeInfo(typeof(WaitStep)) is called,
         * Then it returns non-null (type is source-gen registered).
         *
         * RED: Fails to compile — WaitStep does not exist yet.
         *
         * BUILDER GUIDANCE: Add [JsonSerializable(typeof(WaitStep))] to QuestForgeJsonContext.
         * Mirror the existing [JsonSerializable(typeof(AttunementStep))] entry.
         *
         * NOTE on source-gen registration: because [JsonDerivedType] on the base Step class
         * is alone sufficient for STJ polymorphic dispatch at runtime, the explicit
         * [JsonSerializable] on QuestForgeJsonContext is required separately for source-gen
         * to emit a TypeInfo<WaitStep>. Both are needed; this test pins the latter.
         */

        // RED: WaitStep does not exist
        var typeInfo = QuestForgeJsonContext.Default.GetTypeInfo(typeof(WaitStep));

        Assert.NotNull(typeInfo);
    }

    // =========================================================================
    // WS-2: Active WaitStep, elapsed < Seconds → returns EngineAction.Wait
    // =========================================================================

    [Fact]
    public async Task WS_2_WaitStep_ZeroElapsed_ReturnsWait()
    {
        /*
         * Given a quest with one WaitStep { Seconds=5 },
         *       AND clock at T0 (0 seconds elapsed since the step is first reached),
         * When Tick,
         * Then EngineAction.Wait is returned (timer not expired).
         *
         * RED: Fails to compile — WaitStep and QuestEngine(clock) do not exist yet.
         *
         * BUILDER GUIDANCE:
         *   On the first tick the WaitStep is the active step: record startTime = clock.GetUtcNow().
         *   elapsed = clock.GetUtcNow() - startTime = 0 < 5 → return new EngineAction.Wait(reason).
         *   The reason string is not asserted here; any non-empty string is fine.
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "pause-5s", Seconds = 5.0 };
        const uint questId = 11001u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws2");

        // When — clock has not moved; 0s elapsed
        var action = await engine.Tick(CancellationToken.None);

        // Then
        Assert.IsType<EngineAction.Wait>(action);
    }

    [Fact]
    public async Task WS_2b_WaitStep_PartialElapsed_StillReturnsWait()
    {
        /*
         * Given WaitStep { Seconds=5 },
         *       AND clock advanced by 4.9 seconds after tick 1,
         * When Tick again,
         * Then EngineAction.Wait (4.9 < 5.0 → not expired).
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE: The guard is >= (not >). 4.9 < 5.0 must remain Wait.
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "pause-5s", Seconds = 5.0 };
        const uint questId = 11002u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws2b");

        // Tick 1 — records startTime at T0; elapsed=0 < 5 → Wait
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Advance to just under 5s
        clock.Advance(TimeSpan.FromSeconds(4.9));

        // When
        var action = await engine.Tick(CancellationToken.None);

        // Then — 4.9s elapsed < 5.0s threshold
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // WS-3: After elapsed >= Seconds → confirms step, advances to next step
    // =========================================================================

    [Fact]
    public async Task WS_3_WaitStep_ElapsedExceedsSeconds_AdvancesToNextStep()
    {
        /*
         * Given a quest with:
         *         Step 0: WaitStep { Id="pause", Seconds=5 }
         *         Step 1: TravelStep { Id="go", Destination.Position=(100,0,100) }
         *       AND clock advanced by 6s after tick 1,
         * When Tick,
         * Then WaitStep is confirmed and the engine returns EngineAction.Navigate
         *      (for the TravelStep), NOT EngineAction.Wait.
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE:
         *   When elapsed >= Seconds: _confirmedStepIds.Add(step.Id); continue;
         *   The loop then falls through to the TravelStep arm → Navigate.
         *   Do NOT return Wait at confirmation time.
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var waitStep = new WaitStep { Id = "pause", Seconds = 5.0 };
        var travelStep = new TravelStep
        {
            Id = "go-npc",
            Destination = new TravelDestination(Zone: 130, Position: new Position3(100f, 0f, 100f))
        };
        const uint questId = 11003u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, waitStep, travelStep));
        engine.BeginRun("run-ws3");

        // Tick 1 — records startTime; 0s elapsed → Wait
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Advance past threshold
        clock.Advance(TimeSpan.FromSeconds(6.0));

        // When
        var action = await engine.Tick(CancellationToken.None);

        // Then — WaitStep confirmed; engine advanced to TravelStep → Navigate
        Assert.IsType<EngineAction.Navigate>(action);
    }

    [Fact]
    public async Task WS_3b_WaitStep_LastStep_ElapsedConfirms_ReturnsSequenceGateWait()
    {
        /*
         * Given a quest with only WaitStep { Seconds=5 } (last step),
         *       AND clock advanced by 7s after tick 1,
         * When Tick,
         * Then WaitStep is confirmed and the engine returns the trailing Wait
         *      "all steps in current sequence satisfied; awaiting game sequence advance".
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE: After confirming, `continue` exhausts the step loop.
         * The trailing `return` at the end of ResolveAction returns Wait(sequence gate).
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "pause-only", Seconds = 5.0 };
        const uint questId = 11004u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws3b");

        // Tick 1
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        clock.Advance(TimeSpan.FromSeconds(7.0));

        // When
        var action = await engine.Tick(CancellationToken.None);

        // Then — WaitStep confirmed, loop exhausted, trailing Wait returned
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(
            "all steps in current sequence satisfied; awaiting game sequence advance",
            wait.Reason);
    }

    // =========================================================================
    // WS-4: Timer starts when WaitStep is REACHED, not at quest start
    // =========================================================================

    [Fact]
    public async Task WS_4_Timer_StartsWhenWaitStepIsReached_NotAtQuestStart()
    {
        /*
         * Given a quest with only WaitStep { Seconds=5 },
         *       AND the clock is set to T0 + 1000s BEFORE the step is first reached
         *           (simulating a lot of wall-clock time having passed before this step),
         *       AND the step is first reached on tick 1 (startTime = T0+1000s recorded),
         *       AND the clock is NOT advanced after tick 1,
         * When tick 2,
         * Then EngineAction.Wait is returned (elapsed from step's startTime == 0 < 5s).
         *
         * This pins: startTime = first-reached-tick, NOT quest-start or engine-start.
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE:
         *   Use a per-step start time, keyed by step.Id (or by which step is active).
         *   Record startTime = clock.GetUtcNow() on the FIRST tick the step is active.
         *   elapsed = clock.GetUtcNow() - startTime (NOT clock.GetUtcNow() - questStartTime).
         */

        // Given — clock starts at T0+1000s (large offset, but timer starts fresh when step reached)
        var clock = new ManualTimeProvider(T0 + TimeSpan.FromSeconds(1000));
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "w", Seconds = 5.0 };
        const uint questId = 11005u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws4");

        // Tick 1 — WaitStep first reached; startTime recorded = T0+1000; elapsed=0 < 5 → Wait
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Do NOT advance clock — elapsed since step reached is still 0

        // When — tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then — 0s elapsed since step was reached → still Wait
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    [Fact]
    public async Task WS_4b_Timer_StartsAfterPrecedingStepConfirms()
    {
        /*
         * Given a quest with:
         *         Step 0: AwaitUserStep { Id="skip-me",
         *                   SkipIf = PredicateExpect("questSequence(11006) >= 1") }
         *         Step 1: WaitStep { Id="wait-5s", Seconds=5 }
         *       AND questState.Sequence = 1 (so AwaitUserStep.SkipIf is true on tick 1),
         *       AND clock at T0 + 100s at the moment tick 1 executes,
         * When tick 1: AwaitUserStep is skipped (SkipIf true); WaitStep becomes active;
         *              startTime recorded = T0+100s; elapsed=0 → Wait.
         * When tick 2 WITHOUT advancing clock: elapsed=0 < 5 → still Wait.
         * When tick 3 WITH clock.Advance(5s): elapsed=5 >= 5 → confirms; loop ends → Wait(seq gate).
         *
         * This pins: the 5s is measured from WHEN the WaitStep becomes active,
         * not from quest start or from when the preceding step ran.
         *
         * RED: Same compile blockers as WS-2.
         */

        // Given
        var clock = new ManualTimeProvider(T0 + TimeSpan.FromSeconds(100)); // 100s into "world time"
        const uint questId = 11006u;

        // AwaitUserStep is skipped when questSequence >= 1
        var awaitStep = new AwaitUserStep
        {
            Id = "skip-me",
            Reason = "should be skipped",
            SkipIf = new PredicateExpect { Predicate = $"questSequence({questId}) >= 1" }
        };
        // RED: WaitStep and BuildEngine(clock) compile failures
        var waitStep = new WaitStep { Id = "wait-5s", Seconds = 5.0 };

        var (engine, _, questState) = BuildEngine(clock);
        // questState returns seq=1 so AwaitUserStep.SkipIf fires
        questState.SetQuestSequence(new QuestId(questId), 1);

        engine.StartQuest(BuildQuest(questId, sequence: 1, awaitStep, waitStep));
        engine.BeginRun("run-ws4b");

        // Tick 1 — clock at T0+100; AwaitUserStep skipped; WaitStep active; startTime=T0+100;
        //           elapsed=0 < 5 → Wait
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Tick 2 — no clock advance; elapsed=0 still < 5 → Wait
        var tick2 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        // Advance exactly 5s → elapsed from startTime = 5s
        clock.Advance(TimeSpan.FromSeconds(5.0));

        // Tick 3 — elapsed=5 >= 5 → WaitStep confirmed; seq gate returns Wait
        var tick3 = await engine.Tick(CancellationToken.None);
        var finalWait = Assert.IsType<EngineAction.Wait>(tick3);
        Assert.Equal(
            "all steps in current sequence satisfied; awaiting game sequence advance",
            finalWait.Reason);
    }

    // =========================================================================
    // WS-5: WaitStep with Expect == null completes on time alone (no game-state predicate)
    // =========================================================================

    [Fact]
    public async Task WS_5_WaitStep_NullExpect_CompletesOnTimeAlone_NoException()
    {
        /*
         * Given WaitStep { Id="w", Seconds=5, Expect=null },
         *       AND clock advanced by 6s after tick 1,
         * When tick 2,
         * Then step is confirmed and engine returns Wait("all steps satisfied...").
         *      No NotSupportedException or NullReferenceException is thrown.
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE:
         *   The standard per-step loop checks step.Expect first; since it is null,
         *   it does NOT confirm the step via the predicate path — the step falls through to
         *   the WaitStep dispatch arm. The arm manages confirmation by elapsed time only.
         *   The arm must NOT throw for Expect=null. No game-state call is made.
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "w", Seconds = 5.0 }; // Expect=null by default (init property)
        const uint questId = 11007u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws5");

        // Tick 1 — Expect=null → not confirmed via predicate; WaitStep arm records startTime;
        //           elapsed=0 < 5 → Wait (no exception)
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Advance past threshold — purely time, no game-state change
        clock.Advance(TimeSpan.FromSeconds(6.0));

        // When
        var action = await engine.Tick(CancellationToken.None);

        // Then — confirmed by time; no predicate evaluated; no exception; Wait(seq gate)
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(
            "all steps in current sequence satisfied; awaiting game sequence advance",
            wait.Reason);
    }

    // =========================================================================
    // WS-6 (boundary): elapsed exactly == Seconds → completes (>= semantics)
    // =========================================================================

    [Fact]
    public async Task WS_6_Boundary_ElapsedExactlyEqualsSeconds_Completes()
    {
        /*
         * Given WaitStep { Seconds=5 },
         *       AND clock advanced by EXACTLY 5 seconds after tick 1 records startTime,
         * When tick 2,
         * Then the step is confirmed (5.0 >= 5.0 → true).
         *
         * RED: Same compile blockers as WS-2.
         *
         * BUILDER GUIDANCE: Use >= in the elapsed guard:
         *   if (elapsed >= TimeSpan.FromSeconds(step.Seconds))
         *   NOT: if (elapsed > TimeSpan.FromSeconds(step.Seconds))
         * An off-by-one (strict greater-than) would fail this boundary test.
         */

        // Given
        var clock = new ManualTimeProvider(T0);
        // RED: WaitStep and BuildEngine(clock) compile failures
        var step = new WaitStep { Id = "w", Seconds = 5.0 };
        const uint questId = 11008u;
        var (engine, _, questState) = BuildEngine(clock);
        questState.SetQuestSequence(new QuestId(questId), 0);

        engine.StartQuest(BuildQuest(questId, 0, step));
        engine.BeginRun("run-ws6");

        // Tick 1 — records startTime at T0; elapsed=0 < 5 → Wait
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);

        // Advance by EXACTLY 5.0 seconds
        clock.Advance(TimeSpan.FromSeconds(5.0));

        // When — elapsed == 5.0 exactly
        var action = await engine.Tick(CancellationToken.None);

        // Then — 5.0 >= 5.0 → confirmed; seq gate Wait
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(
            "all steps in current sequence satisfied; awaiting game sequence advance",
            wait.Reason);
    }
}
