using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestForge.Adapters.Actions;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of UseActionStep.
/// Spec: docs/USE_ACTION_STEP_PLAN.md â€” Decisions UA1â€“UA14, scenarios U1â€“U14.
///
/// All types referenced below are NEW and do not yet exist:
///   - QuestForge.Adapters.Actions.IActionExecutor
///   - QuestForge.Adapters.Actions.ActionType (enum)
///   - QuestForge.Adapters.Actions.ActionStatus (abstract record + Ready/OnCooldown/Unusable)
///   - QuestForge.Adapters.Fakes.Actions.FakeActionExecutor
///   - QuestForge.Engine.EngineAction.UseAction (sealed record)
///   - QuestForge.Schema.UseActionStep (rewritten â€” ActionType, ActionId, TargetNpcId?; old fields removed)
///   - EngineTestHarness.ActionExecutor property (FakeActionExecutor)
///   - QuestEngine constructor gains IActionExecutor? actionExecutor = null (last param)
///   - RunToCompletion gains EngineAction.UseAction arm
/// </summary>
public sealed class UseActionStepTests
{
    // =========================================================================
    // U1 â€” Happy path, self-cast: action Ready â†’ emits UseAction(Action, id, null)
    // =========================================================================

    [Fact]
    public async Task UseActionStep_NullTarget_ActionReady_EmitsUseAction_U1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81001), 0);

        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        //       replacing the old ActionId/Target/RepeatUntilExpect shape
        var step = new UseActionStep
        {
            Id = "use-action-self-cast",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81001, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useAction = Assert.IsType<EngineAction.UseAction>(action);
        Assert.Equal(ActionType.Action, useAction.Type);
        Assert.Equal(31u, useAction.ActionId);
        Assert.Null(useAction.TargetNpcId);
        Assert.NotNull(useAction.Origin);
        // Engine emits the action; adapter is NOT called by Tick â€” harness RunToCompletion would call it
        Assert.Equal(0, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U2 â€” Happy path, NPC target: action Ready â†’ emits UseAction with TargetNpcId set
    // =========================================================================

    [Fact]
    public async Task UseActionStep_WithTargetNpcId_ActionReady_EmitsUseActionWithTarget_U2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81002), 0);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        var step = new UseActionStep
        {
            Id = "use-action-on-npc",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = 2001234u,
            Expect = new PredicateExpect { Predicate = "questFlag(81002, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useAction = Assert.IsType<EngineAction.UseAction>(action);
        Assert.Equal(ActionType.Action, useAction.Type);
        Assert.Equal(31u, useAction.ActionId);
        // TargetNpcId must be a NpcId wrapping 2001234
        Assert.NotNull(useAction.TargetNpcId);
        Assert.Equal(new NpcId(2001234), useAction.TargetNpcId!.Value);
        Assert.NotNull(useAction.Origin);
    }

    // =========================================================================
    // U3 â€” Player casting â†’ Wait; GetActionStatus NOT called; UseAction NOT emitted
    // =========================================================================

    [Fact]
    public async Task UseActionStep_PlayerCasting_EmitsWait_NoUseAction_U3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81003), 0);
        harness.GameState.SetCasting(true);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready()); // irrelevant; guard 1 short-circuits

        var step = new UseActionStep
        {
            Id = "use-action-casting-guard",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81003, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U4 â€” Action on cooldown â†’ Wait with cooldown seconds in message; UseAction NOT emitted
    // =========================================================================

    [Fact]
    public async Task UseActionStep_ActionOnCooldown_EmitsWaitWithCooldownInfo_U4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81004), 0);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.OnCooldown(TimeSpan.FromSeconds(12.5)));

        var step = new UseActionStep
        {
            Id = "use-action-cooldown",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81004, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("on cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.5", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U5 â€” Action Unusable â†’ AwaitUser with actionId and reason in message
    // =========================================================================

    [Fact]
    public async Task UseActionStep_ActionUnusable_EmitsAwaitUser_NoUseAction_U5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81005), 0);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Unusable("wrong job"));

        var step = new UseActionStep
        {
            Id = "use-action-unusable",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81005, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("31", awaitUser.Reason); // actionId appears in message
        Assert.Contains("wrong job", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U6 â€” Adapter UseAction returns Result.Failure â†’ stateless retry on next tick
    // =========================================================================

    [Fact]
    public async Task UseActionStep_AdapterFailure_StatelessRetryOnNextTick_U6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81006), 0);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready()); // sticky

        harness.ActionExecutor.ScriptNextFailure("adapter-error", "ActionManager.UseAction returned false");

        var step = new UseActionStep
        {
            Id = "use-action-retry",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81006, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81006, 0, step));

        // Tick 1: engine emits UseAction (it does not call the adapter itself)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAction>(tick1);

        // Manually consume the scripted failure (simulates RunToCompletion dispatch arm)
        var failResult = await harness.ActionExecutor.UseAction(
            ActionType.Action, 31u, null, CancellationToken.None);
        Assert.False(failResult.IsSuccess);

        // Tick 2: Expect still false; engine emits UseAction again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAction>(tick2);

        // Only the manual call is recorded; Tick() does not invoke the adapter
        Assert.Equal(1, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U7 â€” Cancellation propagates from dispatch arm
    // =========================================================================

    [Fact]
    public async Task UseActionStep_CancelledToken_ThrowsOperationCanceledException_U7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81007), 0);

        var step = new UseActionStep
        {
            Id = "use-action-cancel",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81007, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81007, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // U8 â€” Mounted + prior Navigate: lazy-dismount fires before UseAction is returned
    // =========================================================================

    [Fact]
    public async Task UseActionStep_AfterNavigate_MountedPlayer_LazyDismountFires_U8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81008), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        var quest = BuildTwoStepQuest(
            questId: 81008,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-use-action",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new UseActionStep
            {
                Id = "use-action-after-navigate",
                ActionType = ActionType.Action,
                ActionId = 31u,
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questFlag(81008, 3)" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true in HarnessEngine
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: UseAction â€” lazy-dismount hook fires (UseAction is NOT in the exemption list)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAction>(tick2);

        // Pins Decision UA6: UseAction triggers lazy-dismount (unlike Teleport which is exempt)
        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}");
    }

    // =========================================================================
    // U9 â€” Standalone UseAction + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task UseActionStep_Standalone_MountedPlayer_NoDismount_U9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81009), 0);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        var step = new UseActionStep
        {
            Id = "use-action-standalone-mounted",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81009, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.UseAction>(action);
        // No prior Navigate â†’ lazy-dismount bound-to-prior-Navigate does NOT fire
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // U10 â€” Authored Expect already satisfied â†’ step skipped â†’ no UseAction emitted
    // =========================================================================

    [Fact]
    public async Task UseActionStep_ExpectAlreadySatisfied_StepSkipped_EmitsWait_U10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81010), 0);
        // Make the Expect predicate true before the step runs
        harness.GameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(8), true);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready()); // should never be consulted

        var step = new UseActionStep
        {
            Id = "use-action-expect-skip",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" } // true from above
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81010, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits dispatch; step confirmed done; no more steps â†’ Wait
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.ActionExecutor.RecordedCalls.Count);
    }

    // =========================================================================
    // U11 â€” Integration two-tick: UseAction fires, Expect satisfies, step completes
    // =========================================================================

    [Fact]
    public async Task UseActionStep_TwoTickIntegration_ActionFires_ExpectSatisfies_StepCompletes_U11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81011), 0);
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        var step = new UseActionStep
        {
            Id = "use-action-integration",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = 2001234u,
            Expect = new PredicateExpect { Predicate = "questFlag(81011, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81011, 0, step));

        // Tick 1: engine emits UseAction
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var useAction = Assert.IsType<EngineAction.UseAction>(tick1);
        Assert.Equal(ActionType.Action, useAction.Type);
        Assert.Equal(31u, useAction.ActionId);
        Assert.Equal(new NpcId(2001234), useAction.TargetNpcId!.Value);

        // Simulate the RunToCompletion dispatch arm: call the adapter, then advance game state
        await harness.ActionExecutor.UseAction(
            ActionType.Action, 31u, new NpcId(2001234), CancellationToken.None);

        // Simulate the game effect of the action: quest flag bit 3 on quest 81011 is now true
        harness.QuestState.SetQuestFlagBit(new QuestId(81011), 3, true);

        // Tick 2: Expect now satisfies â†’ step confirmed done; no more steps â†’ Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        Assert.Equal(1, harness.ActionExecutor.RecordedCalls.Count);
        var call = harness.ActionExecutor.RecordedCalls[0];
        Assert.Equal(ActionType.Action, call.Type);
        Assert.Equal(31u, call.ActionId);
        Assert.Equal(new NpcId(2001234), call.TargetNpcId);
    }

    // =========================================================================
    // U13 â€” InCombat is NOT a guard: UseAction fires while player is in combat
    // =========================================================================

    [Fact]
    public async Task UseActionStep_InCombat_NoInCombatGuard_EmitsUseAction_U13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81013), 0);
        harness.GameState.SetInCombat(true);
        // No hostile actors in scan range: prevents the global defense rule from emitting Engage
        // before the cursor walk reaches UseActionStep (default fake state has no hostiles, but
        // ClearHostileActors is called defensively)
        harness.GameState.ClearHostileActors();
        harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());

        var step = new UseActionStep
        {
            Id = "use-action-in-combat",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81013, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81013, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Pins Decision UA5: no InCombat guard for UseActionStep
        Assert.IsType<EngineAction.UseAction>(action);
    }

    // =========================================================================
    // U14 (defensive) â€” GetActionStatus read failure â†’ fail-open, UseAction still emitted
    // =========================================================================

    [Fact]
    public async Task UseActionStep_StatusReadFailure_FailOpen_EmitsUseAction_U14()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(81014), 0);

        harness.ActionExecutor.ScriptNextStatusFailure("adapter-error", "ActionManager.GetActionStatus threw");

        var step = new UseActionStep
        {
            Id = "use-action-status-fail-open",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(81014, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(81014, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Fail-open: status read failure â†’ proceed to emit UseAction (mirrors ResolveTeleportAction's posture)
        // Pins Decision UA5's fail-open comment
        var useAction = Assert.IsType<EngineAction.UseAction>(action);
        Assert.NotNull(useAction.Origin);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "UseAction Step Test Quest",
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
            Name = "UseAction Step Two-Step Test Quest",
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
