using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

/// <summary>
/// RED PHASE: Serialization round-trip tests for the new RequiredZone field on Step.
/// Issue #38 — group A acceptance criteria (A1–A5).
///
/// ALL tests will fail to compile until Builder adds:
///   public string? RequiredZone { get; init; }
/// to the Step base class in QuestForge.Schema\Step.cs (immediately after the Zone property).
/// No [JsonIgnore] attribute — must be written even when null, matching Step.Zone behaviour.
/// </summary>
public sealed class RequiredZoneRoundTripTests
{
    // Helper matches the pattern from RoundTripTests.cs
    private static T RoundTrip<T>(T value) where T : Step
    {
        var json = JsonSerializer.Serialize<Step>(value, QuestForgeJsonContext.QuestFileOptions);
        var result = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        return Assert.IsType<T>(result);
    }

    // =========================================================================
    // A1 — TravelStep with RequiredZone = "129" round-trips correctly
    // =========================================================================

    [Fact]
    public void A1_TravelStep_RequiredZonePopulated_RoundTrips()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given a TravelStep with RequiredZone = "129",
         *           When serialized as Step and deserialized back,
         *           Then result.RequiredZone == "129".
         */

        // Arrange
        var step = new TravelStep
        {
            Id          = "go-somewhere",
            Destination = new TravelDestination(130, new Position3(10f, 0f, 20f)),
            Zone        = "130",
            RequiredZone = "129",  // RED: property does not exist yet
            Expect      = new PredicateExpect { Predicate = "playerZone() == 130" }
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.Equal("129", result.RequiredZone);  // RED: property does not exist yet
    }

    // =========================================================================
    // A2 — null RequiredZone is written as "requiredZone": null (never omitted)
    // =========================================================================

    [Fact]
    public void A2_TravelStep_RequiredZoneNull_IsWrittenAsNullNotOmitted()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given a TravelStep with RequiredZone = null (unset),
         *           When serialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then the compacted JSON CONTAINS the key "requiredZone":null
         *                (key is present and written as null — NOT omitted).
         *
         * This locks the "no [JsonIgnore], written-when-null" decision from §2.2 of the plan.
         * Step.Zone behaves identically; RequiredZone must match.
         */

        // Arrange
        var step = new TravelStep
        {
            Id          = "go-somewhere",
            Destination = new TravelDestination(130),
            RequiredZone = null  // RED: property does not exist yet
        };

        // Act
        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compact = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        // Assert — key must be present even when null (matches Step.Zone behaviour)
        Assert.Contains("\"requiredZone\":null", compact, StringComparison.Ordinal);
    }

    // =========================================================================
    // A3 — JSON with requiredZone populated and no zone key deserializes independently
    // =========================================================================

    [Fact]
    public void A3_JsonWithRequiredZoneOnly_DeserializesIndependentlyFromZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given JSON for a TravelStep with "requiredZone": "128" and no "zone" key,
         *           When deserialized,
         *           Then result.RequiredZone == "128" AND result.Zone == null.
         *
         * The two zone fields are independent; deserializing one does not affect the other.
         */

        // Arrange
        var json = """
            {
              "type": "travel",
              "id": "step-with-required-zone",
              "destination": { "zone": 128 },
              "requiredZone": "128"
            }
            """;

        // Act
        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var travel = Assert.IsType<TravelStep>(step);
        Assert.Equal("128", travel.RequiredZone);  // RED: property does not exist yet
        Assert.Null(travel.Zone);                  // Zone key was absent — must remain null
    }

    // =========================================================================
    // A4 — JSON omitting requiredZone entirely deserializes to null
    // =========================================================================

    [Fact]
    public void A4_JsonOmittingRequiredZone_DeserializesToNull()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given JSON for a TravelStep that does NOT include a "requiredZone" key,
         *           When deserialized,
         *           Then result.RequiredZone == null (missing key → null for nullable property).
         */

        // Arrange
        var json = """
            {
              "type": "travel",
              "id": "step-without-required-zone",
              "destination": { "zone": 130 }
            }
            """;

        // Act
        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var travel = Assert.IsType<TravelStep>(step);
        Assert.Null(travel.RequiredZone);  // RED: property does not exist yet
    }

    // =========================================================================
    // A5 — Non-travel subtype (AcceptStep) round-trips RequiredZone (base-class coverage)
    // =========================================================================

    [Fact]
    public void A5_AcceptStep_RequiredZonePopulated_RoundTrips()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given a non-travel step (AcceptStep) with RequiredZone = "128",
         *           When round-tripped through JSON,
         *           Then result.RequiredZone == "128".
         *
         * This locks that the base-class property serializes for every subtype,
         * not just TravelStep.
         */

        // Arrange
        var step = new AcceptStep
        {
            Id          = "accept-quest",
            Target      = new NpcLocation(1000789, 128, new Position3(9f, 40f, 14f)),
            RequiredZone = "128",  // RED: property does not exist yet
            Expect      = new PredicateExpect { Predicate = "isQuestAccepted(65657)" }
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.Equal("128", result.RequiredZone);  // RED: property does not exist yet
    }
}
