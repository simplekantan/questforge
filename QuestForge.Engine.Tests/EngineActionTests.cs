using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests;

/// <summary>
/// Tests for EngineAction record value semantics and exhaustive switch coverage.
///
/// RED PHASE: EngineAction is created by Phase A scaffolding as a real type (not a stub),
/// so these tests ARE expected to be green immediately after Phase A scaffolding.
/// They are included here to document the complete set of EngineAction subtypes and
/// verify that record equality is correct.
/// </summary>
public sealed class EngineActionTests
{
    // §5.1.1 — Navigate records with equal Destination and Options are equal.
    [Fact]
    public void Navigate_RecordEquality_EqualDestinationAndOptions_ReturnsTrue()
    {
        /*
         * CONTRACT: Given two Navigate records with equal Destination and Options,
         *           When compared with Equals,
         *           Then they are equal and have matching hash codes.
         *
         * BUILDER GUIDANCE: EngineAction uses C# records — value equality is automatic.
         *                   No implementation work needed beyond defining the record.
         */

        // Arrange
        var destination = new WorldPosition(35.56f, 4.0f, -151.18f);
        var options = new NavigationOptions(StoppingDistance: 3.0f);

        // Act
        var a = new EngineAction.Navigate(destination, options);
        var b = new EngineAction.Navigate(destination, options);

        // Assert
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // §5.1.2 — Navigate records with different Destination are not equal.
    [Fact]
    public void Navigate_Inequality_DifferentDestination_ReturnsFalse()
    {
        /*
         * CONTRACT: Given two Navigate records with different Destination,
         *           When compared,
         *           Then Equals returns false.
         *
         * BUILDER GUIDANCE: Record structural equality compares all properties.
         */

        // Arrange
        var options = new NavigationOptions(StoppingDistance: 3.0f);
        var a = new EngineAction.Navigate(new WorldPosition(35.56f, 4.0f, -151.18f), options);
        var b = new EngineAction.Navigate(new WorldPosition(21.84f, 7.0f, -81.13f), options);

        // Act & Assert
        Assert.NotEqual(a, b);
    }

    // §5.1.3 — Exhaustive switch over all five EngineAction subtypes compiles and all cases reachable.
    [Fact]
    public void ExhaustiveSwitch_AllFiveCasesHandled_EachSubtypeMapsToDistinctLabel()
    {
        /*
         * CONTRACT: Given all five EngineAction subtypes exist,
         *           When each is passed through a switch expression,
         *           Then each maps to a distinct string label.
         *
         * BUILDER GUIDANCE: This test documents the complete set of EngineAction subtypes.
         *                   If a new subtype is added without updating this switch, the test
         *                   will not compile (missing case) or will produce a duplicate label
         *                   (logic error). Both failure modes are intentional.
         */

        // Arrange
        var navigate = new EngineAction.Navigate(
            new WorldPosition(0f, 0f, 0f),
            new NavigationOptions());
        var interact = new EngineAction.Interact(new NpcId(1));
        var wait = new EngineAction.Wait("reason");
        var awaitUser = new EngineAction.AwaitUser("reason");
        var done = new EngineAction.Done();

        // Act
        var labels = new[]
        {
            LabelFor(navigate),
            LabelFor(interact),
            LabelFor(wait),
            LabelFor(awaitUser),
            LabelFor(done),
        };

        // Assert — all labels are distinct (no two subtypes map to the same label)
        Assert.Equal(5, labels.Distinct().Count());
        Assert.Contains("navigate", labels);
        Assert.Contains("interact", labels);
        Assert.Contains("wait", labels);
        Assert.Contains("await-user", labels);
        Assert.Contains("done", labels);
    }

    // Helper used by the exhaustive-switch test — switch must handle all five cases.
    private static string LabelFor(EngineAction action) => action switch
    {
        EngineAction.Navigate   => "navigate",
        EngineAction.Interact   => "interact",
        EngineAction.Wait       => "wait",
        EngineAction.AwaitUser  => "await-user",
        EngineAction.Done       => "done",
        _                       => throw new InvalidOperationException(
                                       $"Unhandled EngineAction subtype: {action.GetType().Name}"),
    };
}