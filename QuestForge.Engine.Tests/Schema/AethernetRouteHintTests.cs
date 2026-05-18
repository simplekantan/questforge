using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Schema;

/// <summary>
/// RED PHASE: Tests for the new AethernetRouteHint record type (Issue #25, Component 1).
///
/// ALL tests will fail to compile until Builder adds:
///   public record AethernetRouteHint(uint? From, uint To);
/// to QuestForge.Schema/SharedValueTypes.cs.
/// </summary>
public sealed class AethernetRouteHintTests
{
    // =========================================================================
    // AH1 — Construction with To only (From defaults to null)
    // =========================================================================

    [Fact]
    public void AH1_ConstructWithToOnly_FromIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given AethernetRouteHint(From: null, To: 77),
         *           When From is read,
         *           Then From is null and To is 77.
         *
         * BUILDER GUIDANCE: Declare as:
         *   public record AethernetRouteHint(uint? From, uint To);
         * No defaults — both params required at construction.
         */

        // Arrange + Act
        // RED: AethernetRouteHint does not exist yet
        var hint = new AethernetRouteHint(From: null, To: 77u);

        // Assert
        Assert.Null(hint.From);
        Assert.Equal(77u, hint.To);
    }

    // =========================================================================
    // AH2 — Construction with both From and To
    // =========================================================================

    [Fact]
    public void AH2_ConstructWithFromAndTo_BothFieldsSet()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given AethernetRouteHint(From: 125, To: 77),
         *           When both fields are read,
         *           Then From == 125 and To == 77.
         *
         * BUILDER GUIDANCE: Standard positional record; no extra code needed.
         */

        // Arrange + Act
        // RED: AethernetRouteHint does not exist yet
        var hint = new AethernetRouteHint(From: 125u, To: 77u);

        // Assert
        Assert.Equal(125u, hint.From);
        Assert.Equal(77u, hint.To);
    }

    // =========================================================================
    // AH3 — Value equality: same From and To are equal
    // =========================================================================

    [Fact]
    public void AH3_Equality_SameValues_AreEqual()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given two AethernetRouteHint instances with identical From and To,
         *           When == is evaluated,
         *           Then they are equal (record structural equality).
         *
         * BUILDER GUIDANCE: Record types provide value equality automatically.
         */

        // RED: AethernetRouteHint does not exist yet
        var a = new AethernetRouteHint(From: 125u, To: 77u);
        var b = new AethernetRouteHint(From: 125u, To: 77u);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    // =========================================================================
    // AH4 — Value inequality: different To are not equal
    // =========================================================================

    [Fact]
    public void AH4_Inequality_DifferentTo_AreNotEqual()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given two AethernetRouteHint instances with the same From but different To,
         *           When != is evaluated,
         *           Then they are not equal.
         */

        // RED: AethernetRouteHint does not exist yet
        var a = new AethernetRouteHint(From: 125u, To: 77u);
        var b = new AethernetRouteHint(From: 125u, To: 33u);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    // =========================================================================
    // AH5 — With-expression: produces new instance with updated field
    // =========================================================================

    [Fact]
    public void AH5_WithExpression_UpdatesTo_OriginalUnchanged()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given original = AethernetRouteHint(From: 125, To: 77),
         *           When modified = original with { To = 33 },
         *           Then modified.To == 33 and original.To == 77 (immutable).
         *
         * BUILDER GUIDANCE: Record 'with' expressions work automatically.
         */

        // RED: AethernetRouteHint does not exist yet
        var original = new AethernetRouteHint(From: 125u, To: 77u);
        var modified = original with { To = 33u };

        Assert.Equal(77u, original.To);
        Assert.Equal(33u, modified.To);
        Assert.Equal(125u, modified.From);
    }

    // =========================================================================
    // AH6 — RouteHint.Aethernet accepts AethernetRouteHint? (type changed from uint[]?)
    // =========================================================================

    [Fact]
    public void AH6_RouteHint_Aethernet_AcceptsAethernetRouteHint()
    {
        /*
         * RED: Will fail to compile until Builder changes RouteHint.Aethernet
         * from uint[]? to AethernetRouteHint?.
         *
         * CONTRACT: Given new RouteHint(Aethernet: new AethernetRouteHint(null, 77u)),
         *           When RouteHint.Aethernet is read,
         *           Then it is a non-null AethernetRouteHint with To == 77.
         *
         * BUILDER GUIDANCE: In SharedValueTypes.cs, change:
         *   public record RouteHint(string? Aetheryte = null, uint[]? Aethernet = null);
         * to:
         *   public record RouteHint(string? Aetheryte = null, AethernetRouteHint? Aethernet = null);
         */

        // RED: AethernetRouteHint does not exist yet; RouteHint.Aethernet is still uint[]?
        var routeHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 77u));

        Assert.NotNull(routeHint.Aethernet);
        Assert.Equal(77u, routeHint.Aethernet!.To);
        Assert.Null(routeHint.Aethernet.From);
    }
}
