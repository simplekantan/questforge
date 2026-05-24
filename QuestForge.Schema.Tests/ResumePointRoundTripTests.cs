using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

/// <summary>
/// RED PHASE: Serialization round-trip tests for the new ResumePointFragmentId field on Step
/// and the OnResumeFail hook on RecoverConfig. Issue #39 — group A acceptance criteria (A1–A7).
///
/// ALL tests will fail to compile until Builder adds:
///   public string? ResumePointFragmentId { get; init; }
/// to the Step base class in QuestForge.Schema\Step.cs (immediately after RequiredZone),
/// AND:
///   public RecoverAction? OnResumeFail { get; init; }
/// to RecoverConfig in QuestForge.Schema\RecoverAction.cs.
///
/// Neither property carries [JsonIgnore] — both must be written even when null,
/// matching the serialization behavior of Zone and RequiredZone.
/// </summary>
public sealed class ResumePointRoundTripTests
{
    // Helper: serialize as base Step then deserialize back to concrete type T.
    private static T RoundTrip<T>(T value) where T : Step
    {
        var json = JsonSerializer.Serialize<Step>(value, QuestForgeJsonContext.QuestFileOptions);
        var result = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        return Assert.IsType<T>(result);
    }

    // =========================================================================
    // A1 — TravelStep with ResumePointFragmentId populated round-trips correctly
    // =========================================================================

    [Fact]
    public void A1_TravelStep_ResumePointFragmentIdPopulated_RoundTrips()
    {
        /*
         * AC A1 — Given a TravelStep with ResumePointFragmentId = "fragment-teleport-uldah",
         *          When serialized as Step via QuestFileOptions and deserialized back,
         *          Then result.ResumePointFragmentId == "fragment-teleport-uldah".
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "go-somewhere",
            Destination = new TravelDestination(128, new Position3(10f, 0f, 20f)),
            RequiredZone = "128",
            ResumePointFragmentId = "fragment-teleport-uldah"  // RED: property does not exist yet
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.Equal("fragment-teleport-uldah", result.ResumePointFragmentId);  // RED: property does not exist yet
    }

    // =========================================================================
    // A2 — null ResumePointFragmentId is written as "resumePointFragmentId":null (not omitted)
    // =========================================================================

    [Fact]
    public void A2_TravelStep_ResumePointFragmentIdNull_IsWrittenAsNullNotOmitted()
    {
        /*
         * AC A2 — Given a TravelStep with ResumePointFragmentId = null,
         *          When serialized via QuestFileOptions,
         *          Then the compacted JSON CONTAINS "resumePointFragmentId":null
         *               (the key is present and written as null — NOT omitted).
         *
         * This locks the "no [JsonIgnore], written-when-null" decision from §2.1 of the plan.
         * Exact camelCase key: "resumePointFragmentId" (STJ lowercases the R, preserves Point).
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "go-somewhere",
            Destination = new TravelDestination(128),
            ResumePointFragmentId = null  // RED: property does not exist yet
        };

        // Act
        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compact = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        // Assert — key must be present even when null (matches Zone / RequiredZone behaviour)
        Assert.Contains("\"resumePointFragmentId\":null", compact, StringComparison.Ordinal);
    }

    // =========================================================================
    // A3 — JSON omitting resumePointFragmentId entirely deserializes to null
    // =========================================================================

    [Fact]
    public void A3_JsonOmittingResumePointFragmentId_DeserializesToNull()
    {
        /*
         * AC A3 — Given JSON for a TravelStep that does NOT include "resumePointFragmentId",
         *          When deserialized,
         *          Then result.ResumePointFragmentId == null (missing key → null for nullable).
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         */

        // Arrange — no "resumePointFragmentId" key in the JSON
        var json = """
            {
              "type": "travel",
              "id": "step-without-resume-point",
              "destination": { "zone": 128 }
            }
            """;

        // Act
        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var travel = Assert.IsType<TravelStep>(step);
        Assert.Null(travel.ResumePointFragmentId);  // RED: property does not exist yet
    }

    // =========================================================================
    // A4 — requiredZone, resumePointFragmentId, and zone are three independent fields
    // =========================================================================

    [Fact]
    public void A4_RequiredZoneAndResumePointFragmentId_AreIndependentFields()
    {
        /*
         * AC A4 — Given JSON with "requiredZone":"128", "resumePointFragmentId":"fragment-x",
         *          and no "zone" key,
         *          When deserialized,
         *          Then RequiredZone == "128", ResumePointFragmentId == "fragment-x", Zone == null.
         *
         * The three zone-related fields are independent; deserializing one does not affect others.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         */

        // Arrange
        var json = """
            {
              "type": "travel",
              "id": "step-with-both",
              "destination": { "zone": 128 },
              "requiredZone": "128",
              "resumePointFragmentId": "fragment-x"
            }
            """;

        // Act
        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var travel = Assert.IsType<TravelStep>(step);
        Assert.Equal("128", travel.RequiredZone);
        Assert.Equal("fragment-x", travel.ResumePointFragmentId);  // RED: property does not exist yet
        Assert.Null(travel.Zone);  // "zone" key was absent → null
    }

    // =========================================================================
    // A5 — Non-travel subtype (AcceptStep) round-trips ResumePointFragmentId
    // =========================================================================

    [Fact]
    public void A5_AcceptStep_ResumePointFragmentIdPopulated_RoundTrips()
    {
        /*
         * AC A5 — Given an AcceptStep with ResumePointFragmentId = "fragment-x",
         *          When round-tripped through JSON,
         *          Then result.ResumePointFragmentId == "fragment-x".
         *
         * Proves the base-class property serializes for every subtype, not just TravelStep.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         */

        // Arrange
        var step = new AcceptStep
        {
            Id = "accept-quest",
            Target = new NpcLocation(1000789, 128, new Position3(9f, 40f, 14f)),
            RequiredZone = "128",
            ResumePointFragmentId = "fragment-x"  // RED: property does not exist yet
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.Equal("fragment-x", result.ResumePointFragmentId);  // RED: property does not exist yet
    }

    // =========================================================================
    // A6 — OnResumeFail round-trips as AwaitUserRecoverAction with reason preserved
    // =========================================================================

    [Fact]
    public void A6_OnResumeFail_AwaitUserRecoverAction_RoundTrips()
    {
        /*
         * AC A6 — Given a TravelStep whose Recover has OnResumeFail = new AwaitUserRecoverAction { Reason = "stuck" },
         *          When the step is round-tripped,
         *          Then result.Recover!.OnResumeFail is an AwaitUserRecoverAction with Reason == "stuck".
         *
         * RED: Will fail to compile until Builder adds OnResumeFail to RecoverConfig.
         */

        // Arrange
        var step = new TravelStep
        {
            Id = "step-with-on-resume-fail",
            Destination = new TravelDestination(128, new Position3(10f, 0f, 10f)),
            Recover = new RecoverConfig
            {
                OnResumeFail = new AwaitUserRecoverAction { Reason = "stuck" }  // RED: property does not exist yet
            }
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.NotNull(result.Recover);
        var onResumeFail = result.Recover!.OnResumeFail;  // RED: property does not exist yet
        Assert.NotNull(onResumeFail);
        var awaitUser = Assert.IsType<AwaitUserRecoverAction>(onResumeFail);
        Assert.Equal("stuck", awaitUser.Reason);
    }

    // =========================================================================
    // A7 — OnResumeFail null is written as "onResumeFail":null (not omitted)
    // =========================================================================

    [Fact]
    public void A7_OnResumeFail_Null_IsWrittenAsNullNotOmitted()
    {
        /*
         * AC A7 — Given a RecoverConfig with OnResumeFail = null (but other hooks set, so
         *          the Recover object is present and serialized),
         *          When the step is serialized,
         *          Then the compacted JSON contains "onResumeFail":null (key is present, not omitted).
         *
         * Matches sibling hooks (OnTimeout, OnObstacle, etc.) which all emit null when unset.
         *
         * RED: Will fail to compile until Builder adds OnResumeFail to RecoverConfig.
         */

        // Arrange — OnResumeFail = null; OnTimeout set so the Recover object is emitted
        var step = new TravelStep
        {
            Id = "step-with-partial-recover",
            Destination = new TravelDestination(128),
            Recover = new RecoverConfig
            {
                OnTimeout = new RetryRecoverAction { MaxAttempts = 3 },
                OnResumeFail = null  // RED: property does not exist yet
            }
        };

        // Act
        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compact = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        // Assert — onResumeFail key must be present even when null
        Assert.Contains("\"onResumeFail\":null", compact, StringComparison.Ordinal);
    }
}
