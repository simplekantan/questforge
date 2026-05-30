using QuestForge.Adapters.Fakes.Items;
using QuestForge.Adapters.Items;
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
/// Failing tests for QuestEngine dispatch of UseItemStep.
/// Spec: docs/USE_ITEM_STEP_PLAN.md â€” Decisions UI1â€“UI21, scenarios UI1â€“UI10.
///
/// All types referenced below are NEW and do not yet exist:
///   - ItemKind enum in QuestForge.Schema                              (TODO)
///   - UseItemStep rewritten shape { Kind, ItemId, TargetNpcId, TargetPosition } (TODO)
///   - QuestForge.Adapters.Items.IItemUser                             (TODO)
///   - QuestForge.Adapters.Fakes.Items.FakeItemUser                    (TODO)
///   - QuestForge.Engine.EngineAction.UseItem record                   (TODO)
///   - EngineTestHarness.ItemUser property (FakeItemUser)              (TODO)
///   - QuestEngine constructor gains IItemUser? itemUser = null         (TODO)
///   - RunToCompletion gains EngineAction.UseItem arm                   (TODO)
/// </summary>
public sealed class UseItemStepTests
{
    // =========================================================================
    // UI1 â€” Self-cast (no target) â†’ emits UseItem with both targets null
    // =========================================================================

    [Fact]
    public async Task UseItemStep_SelfCast_EmitsUseItem_BothTargetsNull_UI1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84001), 0);

        var step = new UseItemStep
        {
            Id = "drink-elixir",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84001, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useItem = Assert.IsType<EngineAction.UseItem>(action);
        Assert.Equal(ItemKind.InventoryItem, useItem.Kind);
        Assert.Equal(4554u, useItem.ItemId);
        Assert.Null(useItem.TargetNpcId);
        Assert.Null(useItem.TargetPosition);
        Assert.NotNull(useItem.Origin);
        // Engine emits the action; adapter is NOT called by Tick
        Assert.Equal(0, harness.ItemUser.RecordedCalls.Count);
    }

    // =========================================================================
    // UI2 â€” NPC target â†’ emits UseItem with TargetNpcId set, TargetPosition null
    // =========================================================================

    [Fact]
    public async Task UseItemStep_NpcTarget_EmitsUseItem_WithTargetNpcId_UI2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84002), 0);

        var step = new UseItemStep
        {
            Id = "ring-bell-at-recruit",
            Kind = ItemKind.KeyItem,
            ItemId = 2000456u,
            TargetNpcId = 1000789u,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84002, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useItem = Assert.IsType<EngineAction.UseItem>(action);
        Assert.Equal(ItemKind.KeyItem, useItem.Kind);
        Assert.Equal(2000456u, useItem.ItemId);
        Assert.NotNull(useItem.TargetNpcId);
        Assert.Equal(new NpcId(1000789), useItem.TargetNpcId!.Value);
        Assert.Null(useItem.TargetPosition);
        Assert.NotNull(useItem.Origin);
    }

    // =========================================================================
    // UI3 â€” AoE position target â†’ emits UseItem with TargetPosition set, TargetNpcId null
    // =========================================================================

    [Fact]
    public async Task UseItemStep_AoEPosition_EmitsUseItem_WithTargetPosition_UI3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84003), 0);

        var step = new UseItemStep
        {
            Id = "smoke-bomb-the-camp",
            Kind = ItemKind.KeyItem,
            ItemId = 2000123u,
            TargetNpcId = null,
            TargetPosition = new Position3(123.4f, 0f, -45.6f),
            Expect = new PredicateExpect { Predicate = "questSequence(84003) >= 4" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useItem = Assert.IsType<EngineAction.UseItem>(action);
        Assert.Equal(ItemKind.KeyItem, useItem.Kind);
        Assert.Equal(2000123u, useItem.ItemId);
        Assert.Null(useItem.TargetNpcId);
        Assert.NotNull(useItem.TargetPosition);
        Assert.Equal(123.4f, useItem.TargetPosition!.X, precision: 3);
        Assert.Equal(0f, useItem.TargetPosition!.Y, precision: 3);
        Assert.Equal(-45.6f, useItem.TargetPosition!.Z, precision: 3);
    }

    // =========================================================================
    // UI4 â€” Player casting â†’ Wait; no UseItem emitted
    // =========================================================================

    [Fact]
    public async Task UseItemStep_PlayerCasting_EmitsWait_NoUseItem_UI4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84004), 0);
        harness.GameState.SetCasting(true);

        var step = new UseItemStep
        {
            Id = "drink-elixir-casting-guard",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84004, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ItemUser.RecordedCalls.Count);
    }

    // =========================================================================
    // UI5 â€” Adapter UseItem returns Result.Failure â†’ stateless retry on next tick
    // =========================================================================

    [Fact]
    public async Task UseItemStep_AdapterFailure_StatelessRetryOnNextTick_UI5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84005), 0);

        harness.ItemUser.ScriptNextFailure("adapter-error", "ActionManager.UseAction returned false");

        var step = new UseItemStep
        {
            Id = "drink-elixir-retry",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84005, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84005, 0, step));

        // Tick 1: engine emits UseItem
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItem>(tick1);

        // Manually consume the scripted failure (simulates RunToCompletion dispatch arm)
        var failResult = await harness.ItemUser.UseItem(
            ItemKind.InventoryItem, 4554u, null, null, CancellationToken.None);
        Assert.False(failResult.IsSuccess);

        // Tick 2: Expect still false; engine emits UseItem again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItem>(tick2);

        // Only the manual call is recorded; Tick() does not invoke the adapter
        Assert.Equal(1, harness.ItemUser.RecordedCalls.Count);
    }

    // =========================================================================
    // UI6 â€” Cancellation propagates from dispatch arm
    // =========================================================================

    [Fact]
    public async Task UseItemStep_CancelledToken_ThrowsOperationCanceledException_UI6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84006), 0);

        var step = new UseItemStep
        {
            Id = "drink-elixir-cancel",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84006, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84006, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // UI7 â€” Mounted + prior Navigate: lazy-dismount fires before UseItem
    // =========================================================================

    [Fact]
    public async Task UseItemStep_AfterNavigate_MountedPlayer_LazyDismountFires_UI7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84007), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 84007,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-use-item",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new UseItemStep
            {
                Id = "use-item-after-navigate",
                Kind = ItemKind.KeyItem,
                ItemId = 2000456u,
                TargetNpcId = null,
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(84007, 3)" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: UseItem â€” lazy-dismount fires (UseItem is NOT in the exemption list per Decision UI18)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItem>(tick2);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}");
    }

    // =========================================================================
    // UI8 â€” Standalone UseItem + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task UseItemStep_Standalone_MountedPlayer_NoDismount_UI8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84008), 0);
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new UseItemStep
        {
            Id = "drink-elixir-standalone-mounted",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84008, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.UseItem>(action);
        // No prior Navigate â†’ lazy-dismount does NOT fire
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // UI9 â€” Authored Expect already satisfied â†’ step skipped â†’ no UseItem emitted
    // =========================================================================

    [Fact]
    public async Task UseItemStep_ExpectAlreadySatisfied_StepSkipped_EmitsWait_UI9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84009), 0);
        // Make the Expect predicate true before the step runs
        harness.GameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(8), true);

        var step = new UseItemStep
        {
            Id = "drink-elixir-expect-skip",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" } // true from above
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits dispatch; step confirmed done; no more steps â†’ Wait
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.ItemUser.RecordedCalls.Count);
    }

    // =========================================================================
    // UI10 â€” Integration two-tick: UseItem fires, Expect satisfies, step completes
    // =========================================================================

    [Fact]
    public async Task UseItemStep_TwoTickIntegration_ItemFires_ExpectSatisfies_StepCompletes_UI10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(84010), 0);

        var step = new UseItemStep
        {
            Id = "ring-bell-integration",
            Kind = ItemKind.KeyItem,
            ItemId = 2000456u,
            TargetNpcId = 1000789u,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84010, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(84010, 0, step));

        // Tick 1: engine emits UseItem
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var useItem = Assert.IsType<EngineAction.UseItem>(tick1);
        Assert.Equal(ItemKind.KeyItem, useItem.Kind);
        Assert.Equal(2000456u, useItem.ItemId);
        Assert.Equal(new NpcId(1000789), useItem.TargetNpcId!.Value);
        Assert.Null(useItem.TargetPosition);

        // Simulate the RunToCompletion dispatch arm: call the adapter
        await harness.ItemUser.UseItem(
            ItemKind.KeyItem, 2000456u, new NpcId(1000789), null, CancellationToken.None);

        // Simulate the game effect: quest flag bit 3 on quest 84010 is now true
        harness.QuestState.SetQuestFlagBit(new QuestId(84010), 3, true);

        // Tick 2: Expect now satisfies â†’ step confirmed done; no more steps â†’ Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        Assert.Equal(1, harness.ItemUser.RecordedCalls.Count);
        var call = harness.ItemUser.RecordedCalls[0];
        Assert.Equal(ItemKind.KeyItem, call.Kind);
        Assert.Equal(2000456u, call.ItemId);
        Assert.Equal(new NpcId(1000789), call.TargetNpcId);
        Assert.Null(call.TargetPosition);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "UseItem Step Test Quest",
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
            Name = "UseItem Step Two-Step Test Quest",
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
