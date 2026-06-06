using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Tests for StepFactory building Location from enriched ActionCompletedSignal /
/// EmoteCompletedSignal. Spec: docs/ACTION_LOCATION_PLAN.md -- GWT A10-A13.
///
/// RED: All tests will fail until Builder updates StepFactory.Build to call
/// BuildLocationFromSignal for "use-action" and "use-emote" cases.
/// The signal position fields (TargetX/Y/Z/TargetZone) on ActionCompletedSignal
/// and EmoteCompletedSignal exist (stub added), but StepFactory does not use them yet.
/// </summary>
public sealed class StepFactoryLocationTests
{
    // =========================================================================
    // A10 -- StepFactory builds UseActionStep with Location from enriched signal
    // =========================================================================

    [Fact]
    public void StepFactory_UseAction_WithPositionSignal_BuildsLocation_A10()
    {
        /*
         * RED: Will fail until Builder adds BuildLocationFromSignal to StepFactory.
         *
         * CONTRACT: Given a GameStateSnapshot where ActionCompleted has full position data
         *               (TargetX=100, TargetY=5, TargetZ=200, TargetZone=130, TargetBaseId=2001234),
         *           When StepFactory.Build("use-action", ...) is called,
         *           Then the UseActionStep has Location != null,
         *                Location.NpcId == 2001234,
         *                Location.Zone == 130,
         *                Location.Position == (100, 5, 200).
         */

        var after = MakeSnapshot(actionCompleted: new ActionCompletedSignal(
            ActionType.Action, 31u, 2001234u,
            TargetX: 100f, TargetY: 5f, TargetZ: 200f, TargetZone: 130));

        var step = StepFactory.Build("use-action", "a10", "questFlag(83010, 3)", after);

        var useAction = Assert.IsType<UseActionStep>(step);
        Assert.Equal(2001234u, useAction.TargetNpcId);
        Assert.NotNull(useAction.Location);
        Assert.Equal(2001234u, useAction.Location!.NpcId);
        Assert.Equal(130, useAction.Location.Zone);
        Assert.Equal(100f, useAction.Location.Position.X, precision: 1);
        Assert.Equal(5f, useAction.Location.Position.Y, precision: 1);
        Assert.Equal(200f, useAction.Location.Position.Z, precision: 1);
    }

    // =========================================================================
    // A11 -- StepFactory builds UseEmoteStep with Location from enriched signal
    // =========================================================================

    [Fact]
    public void StepFactory_UseEmote_WithPositionSignal_BuildsLocation_A11()
    {
        /*
         * RED: Will fail until Builder adds BuildLocationFromSignal to StepFactory.
         *
         * CONTRACT: Given a GameStateSnapshot where EmoteCompleted has full position data,
         *           When StepFactory.Build("use-emote", ...) is called,
         *           Then the UseEmoteStep has Location != null with correct NpcId, Zone, Position.
         */

        var after = MakeSnapshot(emoteCompleted: new EmoteCompletedSignal(
            17u, 1000789u,
            TargetX: 50f, TargetY: 0f, TargetZ: 80f, TargetZone: 130));

        var step = StepFactory.Build("use-emote", "a11", "questFlag(84011, 3)", after);

        var useEmote = Assert.IsType<UseEmoteStep>(step);
        Assert.Equal(1000789u, useEmote.TargetNpcId);
        Assert.NotNull(useEmote.Location);
        Assert.Equal(1000789u, useEmote.Location!.NpcId);
        Assert.Equal(130, useEmote.Location.Zone);
        Assert.Equal(50f, useEmote.Location.Position.X, precision: 1);
        Assert.Equal(0f, useEmote.Location.Position.Y, precision: 1);
        Assert.Equal(80f, useEmote.Location.Position.Z, precision: 1);
    }

    // =========================================================================
    // A12 -- StepFactory builds UseActionStep without Location when no target
    // =========================================================================

    [Fact]
    public void StepFactory_UseAction_SelfCast_NoTarget_LocationNull_A12()
    {
        /*
         * CONTRACT: Given a GameStateSnapshot where ActionCompleted has no target
         *               (TargetBaseId = null, all position fields null),
         *           When StepFactory.Build("use-action", ...) is called,
         *           Then UseActionStep.Location == null and TargetNpcId == null.
         */

        var after = MakeSnapshot(actionCompleted: new ActionCompletedSignal(
            ActionType.Action, 31u, null)); // self-cast

        var step = StepFactory.Build("use-action", "a12", "questFlag(83012, 3)", after);

        var useAction = Assert.IsType<UseActionStep>(step);
        Assert.Null(useAction.TargetNpcId);
        Assert.Null(useAction.Location);
    }

    // =========================================================================
    // A13 -- StepFactory builds UseActionStep without Location when target but no position
    // =========================================================================

    [Fact]
    public void StepFactory_UseAction_TargetButNoPosition_LocationNull_A13()
    {
        /*
         * RED: Will fail until Builder adds BuildLocationFromSignal to StepFactory.
         *
         * CONTRACT: Given a GameStateSnapshot where ActionCompleted has TargetBaseId=2001234
         *               but no position fields (all defaulted to null),
         *           When StepFactory.Build("use-action", ...) is called,
         *           Then UseActionStep.TargetNpcId == 2001234 but Location == null.
         */

        var after = MakeSnapshot(actionCompleted: new ActionCompletedSignal(
            ActionType.Action, 31u, 2001234u));
        // position fields are null (defaulted)

        var step = StepFactory.Build("use-action", "a13", "questFlag(83013, 3)", after);

        var useAction = Assert.IsType<UseActionStep>(step);
        Assert.Equal(2001234u, useAction.TargetNpcId);
        Assert.Null(useAction.Location);
    }

    // =========================================================================
    // Helper: build a minimal GameStateSnapshot with optional signal overrides
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ActionCompletedSignal? actionCompleted = null,
        EmoteCompletedSignal? emoteCompleted = null)
    {
        return new GameStateSnapshot(
            CapturedAt: DateTimeOffset.UtcNow,
            Zone: new ZoneId(130),
            Position: new WorldPosition(50f, 0f, 50f),
            ActiveQuest: new QuestId(81001),
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: true,
            QuestCompleted: false,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            ActionCompleted = actionCompleted,
            EmoteCompleted = emoteCompleted
        };
    }
}
