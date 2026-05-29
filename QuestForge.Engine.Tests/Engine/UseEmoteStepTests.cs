using QuestForge.Adapters.Emotes;    // TODO: IEmoteExecutor, FakeEmoteExecutor live here
using QuestForge.Adapters.Fakes.Emotes; // TODO: FakeEmoteExecutor
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
/// Failing tests for QuestEngine dispatch of UseEmoteStep.
/// Spec: docs/USE_EMOTE_STEP_PLAN.md â€” Decisions UE1â€“UE19, scenarios UE1â€“UE9.
///
/// All types referenced below are NEW and do not yet exist:
///   - QuestForge.Adapters.Emotes.IEmoteExecutor          (TODO)
///   - QuestForge.Adapters.Fakes.Emotes.FakeEmoteExecutor  (TODO)
///   - QuestForge.Engine.EngineAction.UseEmote             (TODO)
///   - EngineTestHarness.EmoteExecutor property (FakeEmoteExecutor) (TODO)
///   - QuestEngine constructor gains IEmoteExecutor? emoteExecutor = null (TODO)
///   - RunToCompletion gains EngineAction.UseEmote arm      (TODO)
///   - UseEmoteStep schema rewritten to { EmoteId, TargetNpcId: uint?, Motion: bool = true } (TODO)
/// </summary>
public sealed class UseEmoteStepTests
{
    // =========================================================================
    // UE1 â€” Happy path, no target, motion=true â†’ emits UseEmote(17, null, true)
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_NoTarget_MotionTrue_EmitsUseEmote_UE1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82001), 0);

        var step = new UseEmoteStep
        {
            Id = "cheer-self",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82001, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useEmote = Assert.IsType<EngineAction.UseEmote>(action); // TODO: EngineAction.UseEmote
        Assert.Equal(17u, useEmote.EmoteId);
        Assert.Null(useEmote.TargetNpcId);
        Assert.True(useEmote.Motion);
        Assert.NotNull(useEmote.Origin);
        // Engine emits the action; adapter is NOT called by Tick
        Assert.Equal(0, harness.EmoteExecutor.RecordedCalls.Count); // TODO: harness.EmoteExecutor
    }

    // =========================================================================
    // UE2 â€” Happy path, NPC target â†’ emits UseEmote with TargetNpcId set
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_WithTargetNpcId_EmitsUseEmoteWithTarget_UE2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82002), 0);

        var step = new UseEmoteStep
        {
            Id = "cheer-npc",
            EmoteId = 17,
            TargetNpcId = 1000789u,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82002, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useEmote = Assert.IsType<EngineAction.UseEmote>(action); // TODO: EngineAction.UseEmote
        Assert.Equal(17u, useEmote.EmoteId);
        Assert.NotNull(useEmote.TargetNpcId);
        Assert.Equal(new NpcId(1000789), useEmote.TargetNpcId!.Value);
        Assert.True(useEmote.Motion);
        Assert.NotNull(useEmote.Origin);
    }

    // =========================================================================
    // UE3 â€” Player casting â†’ Wait; no UseEmote emitted
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_PlayerCasting_EmitsWait_NoUseEmote_UE3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82003), 0);
        harness.GameState.SetCasting(true);

        var step = new UseEmoteStep
        {
            Id = "cheer-casting-guard",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82003, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.EmoteExecutor.RecordedCalls.Count); // TODO: harness.EmoteExecutor
    }

    // =========================================================================
    // UE4 â€” Adapter UseEmote returns Result.Failure â†’ stateless retry on next tick
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_AdapterFailure_StatelessRetryOnNextTick_UE4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82004), 0);

        harness.EmoteExecutor.ScriptNextFailure("adapter-error", "Chat.SendMessage threw");

        var step = new UseEmoteStep
        {
            Id = "cheer-retry",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82004, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82004, 0, step));

        // Tick 1: engine emits UseEmote
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseEmote>(tick1); // TODO: EngineAction.UseEmote

        // Manually consume the scripted failure (simulates RunToCompletion dispatch arm)
        var failResult = await harness.EmoteExecutor.UseEmote(17u, null, true, CancellationToken.None);
        Assert.False(failResult.IsSuccess);

        // Tick 2: Expect still false; engine emits UseEmote again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseEmote>(tick2); // TODO: EngineAction.UseEmote

        // Only the manual call is recorded; Tick() does not invoke the adapter
        Assert.Equal(1, harness.EmoteExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // UE5 â€” Cancellation propagates from dispatch arm
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_CancelledToken_ThrowsOperationCanceledException_UE5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82005), 0);

        var step = new UseEmoteStep
        {
            Id = "cheer-cancel",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82005, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82005, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // UE6 â€” Mounted + prior Navigate: lazy-dismount fires before UseEmote
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_AfterNavigate_MountedPlayer_LazyDismountFires_UE6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82006), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 82006,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-use-emote",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new UseEmoteStep
            {
                Id = "cheer-after-navigate",
                EmoteId = 17,
                TargetNpcId = null,
                Motion = true,
                Expect = new PredicateExpect { Predicate = "questFlag(82006, 3)" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: UseEmote â€” lazy-dismount fires (UseEmote is NOT in the exemption list per Decision UE6)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseEmote>(tick2); // TODO: EngineAction.UseEmote

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}");
    }

    // =========================================================================
    // UE7 â€” Standalone UseEmote + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_Standalone_MountedPlayer_NoDismount_UE7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82007), 0);
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new UseEmoteStep
        {
            Id = "cheer-standalone-mounted",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82007, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82007, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.UseEmote>(action); // TODO: EngineAction.UseEmote
        // No prior Navigate â†’ lazy-dismount does NOT fire
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // UE8 â€” Authored Expect already satisfied â†’ step skipped â†’ no UseEmote emitted
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_ExpectAlreadySatisfied_StepSkipped_EmitsWait_UE8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82008), 0);
        // Make the Expect predicate true before the step runs
        harness.GameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(8), true);

        var step = new UseEmoteStep
        {
            Id = "cheer-expect-skip",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" } // true from above
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits dispatch; step confirmed done; no more steps â†’ Wait
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.EmoteExecutor.RecordedCalls.Count); // TODO: harness.EmoteExecutor
    }

    // =========================================================================
    // UE9 â€” Integration two-tick: UseEmote fires, Expect satisfies, step completes
    // =========================================================================

    [Fact]
    public async Task UseEmoteStep_TwoTickIntegration_EmoteFires_ExpectSatisfies_StepCompletes_UE9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(82009), 0);

        var step = new UseEmoteStep
        {
            Id = "cheer-integration",
            EmoteId = 17,
            TargetNpcId = 1000789u,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(82009, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(82009, 0, step));

        // Tick 1: engine emits UseEmote
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var useEmote = Assert.IsType<EngineAction.UseEmote>(tick1); // TODO: EngineAction.UseEmote
        Assert.Equal(17u, useEmote.EmoteId);
        Assert.Equal(new NpcId(1000789), useEmote.TargetNpcId!.Value);
        Assert.True(useEmote.Motion);

        // Simulate the RunToCompletion dispatch arm: call the adapter
        await harness.EmoteExecutor.UseEmote(17u, new NpcId(1000789), true, CancellationToken.None);

        // Simulate the game effect: quest flag bit 3 on quest 82009 is now true
        harness.QuestState.SetQuestFlagBit(new QuestId(82009), 3, true);

        // Tick 2: Expect now satisfies â†’ step confirmed done; no more steps â†’ Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        Assert.Equal(1, harness.EmoteExecutor.RecordedCalls.Count);
        var call = harness.EmoteExecutor.RecordedCalls[0]; // TODO: FakeEmoteExecutor.UseEmoteCall shape
        Assert.Equal(17u, call.EmoteId);
        Assert.Equal(new NpcId(1000789), call.TargetNpcId);
        Assert.True(call.Motion);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "UseEmote Step Test Quest",
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
            Name = "UseEmote Step Two-Step Test Quest",
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
