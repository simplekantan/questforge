using System.Text.Json;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Schema;

/// <summary>
/// RED PHASE: Tests for NpcDialogueHint and the new RouteHint.NpcDialogue parameter.
///
/// ALL tests will fail to compile (or fail at runtime) until Builder adds:
///   - public record NpcDialogueHint(NpcLocation Target, DialogueChoice[]? DialogueChoices = null)
///     with DialogueChoices defaulting to [] when null
///   - RouteHint gains a third parameter: NpcDialogueHint? NpcDialogue = null
///     decorated with [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
///   - NpcDialogueHint registered in QuestForgeJsonContext
/// </summary>
public sealed class NpcDialogueHintTests
{
    private static readonly NpcLocation SampleLocation =
        new(NpcId: 1234u, Zone: 131, Position: new Position3(10.5f, 0f, 20.25f));

    // SC-1
    [Fact]
    public void SC1_DefaultConstruction_DialogueChoicesIsEmpty()
    {
        /*
         * RED: Will fail to compile until Builder adds NpcDialogueHint.
         *
         * CONTRACT: Given new NpcDialogueHint(loc) with no DialogueChoices argument,
         *           When DialogueChoices.Length is read,
         *           Then it equals 0 (empty array, never null).
         *
         * BUILDER GUIDANCE: Default the parameter to null and normalise to [] in the body:
         *   public record NpcDialogueHint(NpcLocation Target, DialogueChoice[]? DialogueChoices = null) {
         *       public DialogueChoice[] DialogueChoices { get; init; } = DialogueChoices ?? [];
         *   }
         */

        // Arrange + Act
        var hint = new NpcDialogueHint(SampleLocation);

        // Assert
        Assert.NotNull(hint.DialogueChoices);
        Assert.Empty(hint.DialogueChoices);
    }

    // SC-2
    [Fact]
    public void SC2_ConstructWithChoices_FieldsPreserved()
    {
        /*
         * RED: Will fail to compile until Builder adds NpcDialogueHint.
         *
         * CONTRACT: Given new NpcDialogueHint(loc, [new DialogueChoice("list", null, "2")]),
         *           When Target and DialogueChoices are read,
         *           Then Target == loc and DialogueChoices[0].Answer == "2".
         */

        // Arrange
        var choice = new DialogueChoice(Type: "list", Answer: "2");

        // Act
        var hint = new NpcDialogueHint(SampleLocation, [choice]);

        // Assert
        Assert.Equal(SampleLocation, hint.Target);
        Assert.Single(hint.DialogueChoices);
        Assert.Equal("2", hint.DialogueChoices[0].Answer);
        Assert.Equal("list", hint.DialogueChoices[0].Type);
    }

    // SC-3
    [Fact]
    public void SC3_DefaultRouteHint_NpcDialogueIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds NpcDialogue parameter to RouteHint.
         *
         * CONTRACT: Given new RouteHint() with no arguments,
         *           When NpcDialogue is read,
         *           Then NpcDialogue == null.
         *
         * BUILDER GUIDANCE: NpcDialogue must default to null just like Aetheryte and Aethernet.
         */

        // Arrange + Act
        var hint = new RouteHint();

        // Assert
        Assert.Null(hint.NpcDialogue);
    }

    // SC-4
    [Fact]
    public void SC4_RouteHintWithNpcDialogue_OtherFieldsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds NpcDialogue parameter to RouteHint.
         *
         * CONTRACT: Given new RouteHint(NpcDialogue: hint),
         *           When Aetheryte, Aethernet, and NpcDialogue are read,
         *           Then Aetheryte == null, Aethernet == null, NpcDialogue is not null.
         */

        // Arrange
        var npcHint = new NpcDialogueHint(SampleLocation);

        // Act
        var routeHint = new RouteHint(NpcDialogue: npcHint);

        // Assert
        Assert.Null(routeHint.Aetheryte);
        Assert.Null(routeHint.Aethernet);
        Assert.NotNull(routeHint.NpcDialogue);
        Assert.Equal(SampleLocation, routeHint.NpcDialogue!.Target);
    }

    // SC-5
    [Fact]
    public void SC5_RoundTrip_Serialization_PreservesAllNpcDialogueHintFields()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogueHint to QuestForgeJsonContext
         *      and RouteHint.NpcDialogue parameter.
         *
         * CONTRACT: Given a TravelStep with RouteHint.NpcDialogue fully populated
         *           (NpcId=1234, Zone=131, Position.X=10.5f, Z=20.25f, DialogueChoices[0].Answer=="2"),
         *           When serialized then deserialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then all NpcDialogueHint fields are preserved exactly.
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "lift-attendant",
            Destination = new TravelDestination(Zone: 200, Position: new Position3(0f, 0f, 0f)),
            RouteHint = new RouteHint(
                NpcDialogue: new NpcDialogueHint(
                    new NpcLocation(NpcId: 1234u, Zone: 131, Position: new Position3(10.5f, 0f, 20.25f)),
                    [new DialogueChoice(Type: "list", Answer: "2")]))
        };

        // Act
        var json = JsonSerializer.Serialize(step, QuestForgeJsonContext.QuestFileOptions);
        var deserialized = JsonSerializer.Deserialize<TravelStep>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.RouteHint);
        Assert.NotNull(deserialized.RouteHint!.NpcDialogue);
        var npc = deserialized.RouteHint!.NpcDialogue!;
        Assert.Equal(1234u, npc.Target.NpcId);
        Assert.Equal(131, npc.Target.Zone);
        Assert.Equal(10.5f, npc.Target.Position.X, precision: 3);
        Assert.Equal(20.25f, npc.Target.Position.Z, precision: 3);
        Assert.Single(npc.DialogueChoices);
        Assert.Equal("2", npc.DialogueChoices[0].Answer);
    }

    // SC-6
    [Fact]
    public void SC6_AethernetOnlyTravelStep_JsonDoesNotContainNpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds [JsonIgnore(WhenWritingNull)] to NpcDialogue.
         *
         * CONTRACT: Given a TravelStep with Aethernet only (NpcDialogue == null),
         *           When serialized to JSON,
         *           Then the JSON string does NOT contain "npcDialogue".
         *
         * BUILDER GUIDANCE: Decorate NpcDialogue with
         *   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "aethernet-step",
            Destination = new TravelDestination(Zone: 131, Position: new Position3(0f, 0f, 0f)),
            RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 33u))
        };

        // Act
        var json = JsonSerializer.Serialize(step, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        Assert.DoesNotContain("npcDialogue", json, StringComparison.OrdinalIgnoreCase);
    }

    // SC-7
    [Fact]
    public void SC7_NpcDialogueTravelStep_JsonContainsNpcDialogueAndDialogueChoices()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogueHint and registers it.
         *
         * CONTRACT: Given a TravelStep with RouteHint.NpcDialogue set (including DialogueChoices),
         *           When serialized to JSON,
         *           Then the JSON DOES contain "npcDialogue" and "dialogueChoices".
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "lift-to-zone",
            Destination = new TravelDestination(Zone: 200, Position: new Position3(0f, 0f, 0f)),
            RouteHint = new RouteHint(
                NpcDialogue: new NpcDialogueHint(
                    SampleLocation,
                    [new DialogueChoice(Type: "list", Answer: "2")]))
        };

        // Act
        var json = JsonSerializer.Serialize(step, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        Assert.Contains("npcDialogue", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dialogueChoices", json, StringComparison.OrdinalIgnoreCase);
    }

    // SC-8
    [Fact]
    public void SC8_TravelStep_DestinationZone_And_NpcDialogueTargetZone_AreIndependent()
    {
        /*
         * RED: Will fail to compile until Builder adds NpcDialogue to RouteHint.
         *
         * CONTRACT: Given a TravelStep with Destination.Zone=200 and
         *           RouteHint.NpcDialogue.Target.Zone=131,
         *           When both zones are read,
         *           Then Destination.Zone==200 and NpcDialogue.Target.Zone==131 (independent fields).
         *
         * BUILDER GUIDANCE: These are completely separate fields. Target.Zone is the SOURCE zone
         * (where the NPC is); Destination.Zone is the DESTINATION zone (where the player lands).
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "lift",
            Destination = new TravelDestination(Zone: 200),
            RouteHint = new RouteHint(
                NpcDialogue: new NpcDialogueHint(
                    Target: new NpcLocation(NpcId: 7777u, Zone: 131, Position: new Position3(0f, 0f, 0f))))
        };

        // Assert
        Assert.Equal(200, step.Destination.Zone);
        Assert.Equal(131, step.RouteHint!.NpcDialogue!.Target.Zone);
        Assert.NotEqual(step.Destination.Zone, step.RouteHint.NpcDialogue.Target.Zone);
    }
}
