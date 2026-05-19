using QuestForge.Adapters.Types;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests;

/// <summary>
/// RED PHASE: Tests for EngineAction.Interact and EngineAction.Wait gaining
/// an optional Step? Origin property.
///
/// ALL tests will fail to compile until Builder adds:
///   public sealed record Interact(NpcId Target, Step? Origin = null) : EngineAction;
///   public sealed record Wait(string Reason, Step? Origin = null) : EngineAction;
///
/// IMPORTANT: The existing EngineActionTests.ExhaustiveSwitch test uses
///   new EngineAction.Interact(new NpcId(1))
///   new EngineAction.Wait("reason")
/// Both still compile after adding the optional parameter — backward compatible.
/// </summary>
public sealed class EngineActionDialogueTests
{
    private static readonly NpcId SomeNpc = new(1002001u);

    private static TalkStep MakeTalkStep() => new()
    {
        Id = "step-talk",
        DialogueChoices = [new DialogueChoice(Type: "list", Answer: "2")]
    };

    // EA-1
    [Fact]
    public void EA_1_Interact_DefaultOrigin_IsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds Step? Origin = null to Interact.
         *
         * CONTRACT: Given new EngineAction.Interact(npcId) with no Origin argument,
         *           When Origin is read,
         *           Then Origin == null (backward compatible default).
         *
         * BUILDER GUIDANCE: Add optional parameter with default null:
         *   public sealed record Interact(NpcId Target, Step? Origin = null) : EngineAction;
         */

        // Arrange & Act
        var action = new EngineAction.Interact(SomeNpc);

        // Assert
        // RED: Origin property does not exist yet
        Assert.Null(action.Origin);
    }

    // EA-2
    [Fact]
    public void EA_2_Interact_WithOriginSet_ReturnsStep()
    {
        /*
         * RED: Will fail to compile until Builder adds Step? Origin to Interact.
         *
         * CONTRACT: Given new EngineAction.Interact(npcId, Origin: talkStep),
         *           When Origin is read,
         *           Then Origin == talkStep.
         *
         * BUILDER GUIDANCE: Named parameter syntax; record stores the value directly.
         */

        // Arrange
        var talkStep = MakeTalkStep();

        // Act
        // RED: Origin parameter does not exist yet
        var action = new EngineAction.Interact(SomeNpc, Origin: talkStep);

        // Assert
        Assert.Same(talkStep, action.Origin);
    }

    // EA-3
    [Fact]
    public void EA_3_Wait_DefaultOrigin_IsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds Step? Origin = null to Wait.
         *
         * CONTRACT: Given new EngineAction.Wait("reason") with no Origin argument,
         *           When Origin is read,
         *           Then Origin == null (backward compatible default).
         *
         * BUILDER GUIDANCE: Add optional parameter with default null:
         *   public sealed record Wait(string Reason, Step? Origin = null) : EngineAction;
         */

        // Arrange & Act
        var action = new EngineAction.Wait("waiting for npc");

        // Assert
        // RED: Origin property does not exist yet
        Assert.Null(action.Origin);
    }

    // EA-4
    [Fact]
    public void EA_4_Wait_WithOriginSet_ReturnsStep()
    {
        /*
         * RED: Will fail to compile until Builder adds Step? Origin to Wait.
         *
         * CONTRACT: Given new EngineAction.Wait("reason", Origin: talkStep),
         *           When Origin is read,
         *           Then Origin == talkStep.
         *
         * BUILDER GUIDANCE: Named parameter syntax; record stores the value directly.
         */

        // Arrange
        var talkStep = MakeTalkStep();

        // Act
        // RED: Origin parameter does not exist yet
        var action = new EngineAction.Wait("waiting for dialogue", Origin: talkStep);

        // Assert
        Assert.Same(talkStep, action.Origin);
    }
}
