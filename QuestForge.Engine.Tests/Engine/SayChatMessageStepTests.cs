using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Fakes.Chat;
using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Failing tests for QuestEngine dispatch of SayChatMessageStep.
/// Spec: docs/SAY_CHAT_MESSAGE_STEP_PLAN.md â€” Decisions SC1â€“SC13, scenarios SC1â€“SC9.
///
/// All types/members referenced below are NEW and do not yet exist:
///   - QuestForge.Adapters.Chat.IChatSender                     (TODO)
///   - QuestForge.Adapters.Fakes.Chat.FakeChatSender            (TODO)
///   - QuestForge.Engine.EngineAction.SayChatMessage            (TODO)
///   - EngineTestHarness.ChatSender property (FakeChatSender)   (TODO)
///   - QuestEngine constructor gains IChatSender? chatSender = null (TODO â€” last param)
///   - RunToCompletion gains EngineAction.SayChatMessage arm     (TODO)
///   - SayChatMessageStep rewritten: Channel + Target removed; Message + TargetNpcId: uint? (TODO)
/// Quest IDs reserved: 83001â€“83009.
/// </summary>
public sealed class SayChatMessageStepTests
{
    // =========================================================================
    // SC1 â€” Happy path, no target â†’ emits SayChatMessage(message, null, Origin: step)
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_NoTarget_EmitsSayChatMessage_SC1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83001), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-open-sesame",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83001, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(action);
        Assert.Equal("Open Sesame", sayChat.Message);
        Assert.Null(sayChat.TargetNpcId);
        Assert.NotNull(sayChat.Origin);
        // Engine emits the action; adapter is NOT called by Tick
        Assert.Equal(0, harness.ChatSender.RecordedCalls.Count);
    }

    // =========================================================================
    // SC2 â€” Happy path, NPC target â†’ emits SayChatMessage with TargetNpcId set
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_WithTargetNpcId_EmitsSayChatMessageWithTarget_SC2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83002), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-hello-friend",
            Message = "Hello friend",
            TargetNpcId = 1000789u,
            Expect = new PredicateExpect { Predicate = "questFlag(83002, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(action);
        Assert.Equal("Hello friend", sayChat.Message);
        Assert.NotNull(sayChat.TargetNpcId);
        Assert.Equal(new NpcId(1000789), sayChat.TargetNpcId!.Value);
        Assert.NotNull(sayChat.Origin);
    }

    // =========================================================================
    // SC3 â€” Player casting â†’ Wait; no SayChatMessage emitted
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_PlayerCasting_EmitsWait_NoSayChatMessage_SC3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83003), 0);
        harness.GameState.SetCasting(true);

        var step = new SayChatMessageStep
        {
            Id = "say-casting-guard",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83003, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ChatSender.RecordedCalls.Count);
    }

    // =========================================================================
    // SC4 â€” Adapter Send returns Result.Failure â†’ stateless retry on next tick
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_AdapterFailure_StatelessRetryOnNextTick_SC4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83004), 0);

        harness.ChatSender.ScriptNextFailure("chat-failed", "Chat.SendMessage threw");

        var step = new SayChatMessageStep
        {
            Id = "say-retry",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83004, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83004, 0, step));

        // Tick 1: engine emits SayChatMessage
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick1);

        // Manually consume the scripted failure (simulates RunToCompletion dispatch arm)
        var failResult = await harness.ChatSender.Send("Open Sesame", null, CancellationToken.None);
        Assert.False(failResult.IsSuccess);

        // Tick 2: Expect still false; engine emits SayChatMessage again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick2);

        // Only the manual call is recorded; Tick() does not invoke the adapter
        Assert.Equal(1, harness.ChatSender.RecordedCalls.Count);
    }

    // =========================================================================
    // SC5 â€” Cancellation propagates from Tick
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_CancelledToken_ThrowsOperationCanceledException_SC5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83005), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-cancel",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83005, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83005, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // SC6 â€” Mounted + prior Navigate: lazy-dismount fires before SayChatMessage
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_AfterNavigate_MountedPlayer_LazyDismountFires_SC6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83006), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 83006,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-say",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new SayChatMessageStep
            {
                Id = "say-after-navigate",
                Message = "Open Sesame",
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questFlag(83006, 3)" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: SayChatMessage â€” lazy-dismount fires (SayChatMessage is NOT in the exemption list per Decision SC6)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.SayChatMessage>(tick2);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}");
    }

    // =========================================================================
    // SC7 â€” Standalone SayChatMessage + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_Standalone_MountedPlayer_NoDismount_SC7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83007), 0);
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new SayChatMessageStep
        {
            Id = "say-standalone-mounted",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83007, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83007, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.SayChatMessage>(action);
        // No prior Navigate â†’ lazy-dismount does NOT fire
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // SC8 â€” Authored Expect already satisfied â†’ step skipped â†’ no SayChatMessage emitted
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_ExpectAlreadySatisfied_StepSkipped_EmitsWait_SC8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83008), 0);
        // Make the Expect predicate true before the step runs
        harness.GameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(8), true);

        var step = new SayChatMessageStep
        {
            Id = "say-expect-skip",
            Message = "Open Sesame",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" } // true from above
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits dispatch; step confirmed done; no more steps â†’ Wait
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.ChatSender.RecordedCalls.Count);
    }

    // =========================================================================
    // SC9 â€” Integration two-tick: SayChatMessage fires, Expect satisfies, step completes
    // =========================================================================

    [Fact]
    public async Task SayChatMessageStep_TwoTickIntegration_SayChatFires_ExpectSatisfies_StepCompletes_SC9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(83009), 0);

        var step = new SayChatMessageStep
        {
            Id = "say-integration",
            Message = "Open Sesame",
            TargetNpcId = 1000789u,
            Expect = new PredicateExpect { Predicate = "questFlag(83009, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(83009, 0, step));

        // Tick 1: engine emits SayChatMessage
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var sayChat = Assert.IsType<EngineAction.SayChatMessage>(tick1);
        Assert.Equal("Open Sesame", sayChat.Message);
        Assert.Equal(new NpcId(1000789), sayChat.TargetNpcId!.Value);

        // Simulate the RunToCompletion dispatch arm: call the adapter
        await harness.ChatSender.Send("Open Sesame", new NpcId(1000789), CancellationToken.None);

        // Simulate the game effect: quest flag bit 3 on quest 83009 is now true
        harness.QuestState.SetQuestFlagBit(new QuestId(83009), 3, true);

        // Tick 2: Expect now satisfies â†’ step confirmed done; no more steps â†’ Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        Assert.Equal(1, harness.ChatSender.RecordedCalls.Count);
        var call = harness.ChatSender.RecordedCalls[0];
        Assert.Equal("Open Sesame", call.Message);
        Assert.Equal(new NpcId(1000789), call.TargetNpcId);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SayChatMessage Step Test Quest",
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
                    Steps = [step]
                }
            ]
        };

    private static QuestDefinition BuildTwoStepQuest(
        uint questId,
        int sequence,
        Step step0,
        Step step1) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SayChatMessage Step Two-Step Test Quest",
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
                    Steps = [step0, step1]
                }
            ]
        };
}
