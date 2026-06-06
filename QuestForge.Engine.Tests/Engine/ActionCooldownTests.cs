using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Actions;
using QuestForge.Adapters.Fakes.Chat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Emotes;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Items;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Types;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Failing tests for the pre-arm action cooldown feature.
/// Spec: docs/ACTION_COOLDOWN_PLAN.md -- Decisions AC-1 through AC-7, scenarios CD-1 through CD-13.
///
/// RED PHASE: All tests will fail because:
///   - QuestEngine does not yet have an `actionCooldownSeconds` ctor parameter (compile error)
///   - Even if it compiled, the cooldown logic (CheckActionCooldown / RecordActionFired) does not exist,
///     so the engine would fire the action on every tick instead of returning Wait during cooldown.
///
/// Tests construct QuestEngine directly with a ManualTimeProvider (same pattern as WaitStepTests).
/// The EngineTestHarness is NOT used because it does not expose a clock or cooldown parameter.
/// </summary>
public sealed class ActionCooldownTests
{
    // Stable epoch so all tests share a predictable T0.
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Engine construction helper
    // =========================================================================

    /// <summary>
    /// Constructs a QuestEngine backed by all fake adapters plus the given clock and cooldown.
    /// Returns the engine and the fakes needed to drive tests.
    ///
    /// RED: the `actionCooldownSeconds` argument does not compile until Builder adds
    /// the optional double parameter to QuestEngine.
    /// </summary>
    private static (
        QuestEngine engine,
        FakeGameStateProvider gameState,
        FakeQuestState questState,
        FakeActionExecutor actionExecutor)
        BuildEngine(ManualTimeProvider clock, double actionCooldownSeconds)
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
        var chatSender = new FakeChatSender();
        var emoteExecutor = new FakeEmoteExecutor();
        var actionExecutor = new FakeActionExecutor();
        var itemUser = new FakeItemUser();

        // RED: actionCooldownSeconds parameter does not exist on QuestEngine yet.
        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            combat, minigames, dialogue, timing, trace, logger,
            clock,
            actionExecutor: actionExecutor,
            emoteExecutor: emoteExecutor,
            chatSender: chatSender,
            itemUser: itemUser,
            actionCooldownSeconds: actionCooldownSeconds);

        return (engine, gameState, questState, actionExecutor);
    }

    /// <summary>
    /// Builds a QuestDefinition with a single sequence containing the given steps.
    /// </summary>
    private static QuestDefinition BuildQuest(uint questId, int sequence, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ActionCooldown Test Quest",
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

    /// <summary>
    /// Builds a QuestDefinition with two sequences, each containing one step.
    /// Used for CD-12 (sequence change resets cooldown).
    /// </summary>
    private static QuestDefinition BuildTwoSequenceQuest(
        uint questId,
        Step seq0Step,
        Step seq1Step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ActionCooldown TwoSeq Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [seq0Step] },
                new QuestSequence { Sequence = 1, Steps = [seq1Step] }
            ]
        };

    // =========================================================================
    // CD-1: SayChatMessage first tick fires immediately (no cooldown on first dispatch)
    // =========================================================================

    [Fact]
    public async Task CD1_SayChatMessage_FirstTick_FiresImmediately()
    {
        // Given: quest with a SayChatMessageStep, cooldown 5s, clock at T0
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90001), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-hello",
            Message = "Open Sesame",
            Expect = new PredicateExpect { Predicate = "questFlag(90001, 3)" }
        };

        engine.StartQuest(BuildQuest(90001, 0, step));
        engine.BeginRun("run-cd1");

        // When: first tick
        var action = await engine.Tick(CancellationToken.None);

        // Then: action fires, NOT Wait
        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(action);
        Assert.Equal("Open Sesame", sayChat.Message);
    }

    // =========================================================================
    // CD-2: SayChatMessage second tick within cooldown returns Wait
    // =========================================================================

    [Fact]
    public async Task CD2_SayChatMessage_SecondTickWithinCooldown_ReturnsWait()
    {
        // Given: same quest, tick 1 returned SayChatMessage, clock advanced 2s (within 5s cooldown)
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90001), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-hello",
            Message = "Open Sesame",
            Expect = new PredicateExpect { Predicate = "questFlag(90001, 3)" }
        };

        engine.StartQuest(BuildQuest(90001, 0, step));
        engine.BeginRun("run-cd2");

        // Tick 1: action fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Advance 2s (within 5s cooldown)
        clock.Advance(TimeSpan.FromSeconds(2.0));

        // When: tick 2 (Expect still false)
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: Wait with cooldown reason
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0s / 5.0s", wait.Reason, StringComparison.Ordinal);
    }

    // =========================================================================
    // CD-3: SayChatMessage tick after cooldown expires fires again (>= boundary)
    // =========================================================================

    [Fact]
    public async Task CD3_SayChatMessage_AtCooldownBoundary_FiresAgain()
    {
        // Given: tick 1 returned SayChatMessage, clock advanced exactly 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90001), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-hello",
            Message = "Open Sesame",
            Expect = new PredicateExpect { Predicate = "questFlag(90001, 3)" }
        };

        engine.StartQuest(BuildQuest(90001, 0, step));
        engine.BeginRun("run-cd3");

        // Tick 1: action fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Advance exactly 5s (at cooldown boundary; >= semantics)
        clock.Advance(TimeSpan.FromSeconds(5.0));

        // When: tick 2 (Expect still false)
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: action re-fires (5.0 >= 5.0)
        Assert.IsType<EngineAction.SayChatMessage>(tick2);
    }

    // =========================================================================
    // CD-4: UseEmote first tick fires, second tick within cooldown returns Wait
    // =========================================================================

    [Fact]
    public async Task CD4_UseEmote_FirstTickFires_SecondTickWithinCooldown_ReturnsWait()
    {
        // Given: UseEmoteStep, cooldown 3s, clock at T0
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 3.0);
        questState.SetQuestSequence(new QuestId(90002), 0);

        var step = new UseEmoteStep
        {
            Id = "emote-dance",
            EmoteId = 7,
            Expect = new PredicateExpect { Predicate = "questFlag(90002, 3)" }
        };

        engine.StartQuest(BuildQuest(90002, 0, step));
        engine.BeginRun("run-cd4");

        // Tick 1: UseEmote fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseEmote>(tick1);

        // Advance 1.5s (within 3s cooldown)
        clock.Advance(TimeSpan.FromSeconds(1.5));

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: Wait with cooldown reason
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CD-5: UseAction first tick fires, second tick within cooldown returns Wait
    // =========================================================================

    [Fact]
    public async Task CD5_UseAction_FirstTickFires_SecondTickWithinCooldown_ReturnsWait()
    {
        // Given: UseActionStep, cooldown 5s, ActionExecutor returns Ready
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, actionExecutor) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90003), 0);

        var step = new UseActionStep
        {
            Id = "use-sprint",
            ActionType = ActionType.Action,
            ActionId = 4,
            Expect = new PredicateExpect { Predicate = "questFlag(90003, 3)" }
        };

        engine.StartQuest(BuildQuest(90003, 0, step));
        engine.BeginRun("run-cd5");

        // Tick 1: UseAction fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAction>(tick1);

        // Advance 2s (within 5s cooldown)
        clock.Advance(TimeSpan.FromSeconds(2.0));

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: Wait with cooldown reason
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CD-6: UseItem first tick fires, second tick within cooldown returns Wait
    // =========================================================================

    [Fact]
    public async Task CD6_UseItem_FirstTickFires_SecondTickWithinCooldown_ReturnsWait()
    {
        // Given: UseItemStep, cooldown 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90004), 0);

        var step = new UseItemStep
        {
            Id = "use-potion",
            Kind = ItemKind.InventoryItem,
            ItemId = 4551,
            Expect = new PredicateExpect { Predicate = "questFlag(90004, 3)" }
        };

        engine.StartQuest(BuildQuest(90004, 0, step));
        engine.BeginRun("run-cd6");

        // Tick 1: UseItem fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItem>(tick1);

        // Advance 1s (within 5s cooldown)
        clock.Advance(TimeSpan.FromSeconds(1.0));

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: Wait with cooldown reason
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CD-7: Different step resets cooldown -- fires immediately
    // =========================================================================

    [Fact]
    public async Task CD7_DifferentStep_CooldownCarriesAcrossSteps()
    {
        // Given: two SayChatMessageSteps with different IDs, cooldown 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90007), 0);

        var step1 = new SayChatMessageStep
        {
            Id = "say-first",
            Message = "Hello",
            Expect = new PredicateExpect { Predicate = "questFlag(90007, 1)" }
        };
        var step2 = new SayChatMessageStep
        {
            Id = "say-second",
            Message = "World",
            Expect = new PredicateExpect { Predicate = "questFlag(90007, 2)" }
        };

        engine.StartQuest(BuildQuest(90007, 0, step1, step2));
        engine.BeginRun("run-cd7");

        // Tick 1: say-first fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Satisfy say-first's Expect so engine advances to say-second
        questState.SetQuestFlagBit(new QuestId(90007), 1, true);

        // Advance only 1s (within cooldown window)
        clock.Advance(TimeSpan.FromSeconds(1.0));

        // When: tick 2 — cooldown carries across steps
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: say-second is deferred (Wait from confirm)
        Assert.IsType<EngineAction.Wait>(tick2);

        // After cooldown expires, say-second fires
        clock.Advance(TimeSpan.FromSeconds(5.5));
        var tick3 = await engine.Tick(CancellationToken.None);
        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(tick3);
        Assert.Equal("World", sayChat.Message);
    }

    // =========================================================================
    // CD-8: Expect satisfies during cooldown -- step confirms immediately
    // =========================================================================

    [Fact]
    public async Task CD8_ExpectSatisfiesDuringCooldown_StepConfirmsImmediately()
    {
        // Given: SayChatMessageStep with long cooldown (10s)
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 10.0);
        questState.SetQuestSequence(new QuestId(90005), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-expect",
            Message = "Magic words",
            Expect = new PredicateExpect { Predicate = "questFlag(90005, 3)" }
        };

        engine.StartQuest(BuildQuest(90005, 0, step));
        engine.BeginRun("run-cd8");

        // Tick 1: action fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Advance 1s (within cooldown), but satisfy Expect
        clock.Advance(TimeSpan.FromSeconds(1.0));
        questState.SetQuestFlagBit(new QuestId(90005), 3, true);

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: step confirms and defers evaluation of next step
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // CD-9: Cooldown of 0 disables throttling
    // =========================================================================

    [Fact]
    public async Task CD9_CooldownZero_DisablesThrottling_FiresEveryTick()
    {
        // Given: SayChatMessageStep, cooldown 0s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 0.0);
        questState.SetQuestSequence(new QuestId(90006), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-no-cd",
            Message = "Spam",
            Expect = new PredicateExpect { Predicate = "questFlag(90006, 3)" }
        };

        engine.StartQuest(BuildQuest(90006, 0, step));
        engine.BeginRun("run-cd9");

        // Tick 1: action fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Do NOT advance clock

        // When: tick 2 (no cooldown, fires again)
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: action fires again (no throttling)
        Assert.IsType<EngineAction.SayChatMessage>(tick2);
    }

    // =========================================================================
    // CD-10: Casting guard takes priority over cooldown
    // =========================================================================

    [Fact]
    public async Task CD10_CastingGuard_TakesPriorityOverCooldown()
    {
        // Given: UseActionStep, player is casting, cooldown 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90010), 0);
        gameState.SetCasting(true);

        var step = new UseActionStep
        {
            Id = "use-sprint-casting",
            ActionType = ActionType.Action,
            ActionId = 4,
            Expect = new PredicateExpect { Predicate = "questFlag(90010, 3)" }
        };

        engine.StartQuest(BuildQuest(90010, 0, step));
        engine.BeginRun("run-cd10");

        // When: tick 1 (player is casting)
        var action = await engine.Tick(CancellationToken.None);

        // Then: Wait with "player casting" reason, NOT "action cooldown"
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CD-11: UseAction action-status OnCooldown takes priority over engine cooldown
    // =========================================================================

    [Fact]
    public async Task CD11_ActionStatusOnCooldown_TakesPriorityOverEngineCooldown()
    {
        // Given: UseActionStep, NOT casting, ActionExecutor returns OnCooldown(2.5s), engine cooldown 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, actionExecutor) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90011), 0);
        actionExecutor.ScriptNextStatus(new ActionStatus.OnCooldown(TimeSpan.FromSeconds(2.5)));

        var step = new UseActionStep
        {
            Id = "use-action-on-cd",
            ActionType = ActionType.Action,
            ActionId = 10,
            Expect = new PredicateExpect { Predicate = "questFlag(90011, 3)" }
        };

        engine.StartQuest(BuildQuest(90011, 0, step));
        engine.BeginRun("run-cd11");

        // When: tick 1
        var action = await engine.Tick(CancellationToken.None);

        // Then: Wait with game's own cooldown reason, NOT engine's "action cooldown"
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("on cooldown: 2.5s", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CD-12: Sequence change resets cooldown implicitly
    // =========================================================================

    [Fact]
    public async Task CD12_SequenceChange_ResetsCooldown_FiresImmediately()
    {
        // Given: seq 0 has SayChatMessageStep "say-seq0", seq 1 has SayChatMessageStep "say-seq1"
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90012), 0);

        var seq0Step = new SayChatMessageStep
        {
            Id = "say-seq0",
            Message = "Sequence 0",
            Expect = new PredicateExpect { Predicate = "questFlag(90012, 1)" }
        };
        var seq1Step = new SayChatMessageStep
        {
            Id = "say-seq1",
            Message = "Sequence 1",
            Expect = new PredicateExpect { Predicate = "questFlag(90012, 2)" }
        };

        engine.StartQuest(BuildTwoSequenceQuest(90012, seq0Step, seq1Step));
        engine.BeginRun("run-cd12");

        // Tick 1: seq 0 fires SayChatMessage for "say-seq0"
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Game advances sequence to 1
        questState.SetQuestSequence(new QuestId(90012), 1);

        // Advance only 0.5s (within cooldown window)
        clock.Advance(TimeSpan.FromSeconds(0.5));

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: say-seq1 fires immediately (different step ID; sequence change implicit reset)
        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(tick2);
        Assert.Equal("Sequence 1", sayChat.Message);
    }

    // =========================================================================
    // CD-13: Cooldown Wait message includes Origin for trace context
    // =========================================================================

    [Fact]
    public async Task CD13_CooldownWait_IncludesOriginStep()
    {
        // Given: UseEmoteStep, cooldown 5s
        var clock = new ManualTimeProvider(T0);
        var (engine, _, questState, _) = BuildEngine(clock, actionCooldownSeconds: 5.0);
        questState.SetQuestSequence(new QuestId(90013), 0);

        var step = new UseEmoteStep
        {
            Id = "emote-cd-origin",
            EmoteId = 7,
            Expect = new PredicateExpect { Predicate = "questFlag(90013, 3)" }
        };

        engine.StartQuest(BuildQuest(90013, 0, step));
        engine.BeginRun("run-cd13");

        // Tick 1: UseEmote fires
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseEmote>(tick1);

        // Advance 2s (within cooldown)
        clock.Advance(TimeSpan.FromSeconds(2.0));

        // When: tick 2
        var tick2 = await engine.Tick(CancellationToken.None);

        // Then: Wait has Origin set to the UseEmoteStep (not null)
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.NotNull(wait.Origin);
        Assert.Equal("emote-cd-origin", wait.Origin!.Id);
    }
}
