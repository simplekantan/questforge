using System.Text.Json;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for ReplayQuestState — the observation-backed IQuestState.
/// All tests will fail until Builder implements ReplayQuestState in QuestForge.Adapters.Fakes.Replay.
///
/// Covers §8.4 of PHASE_7_PLAN.md — 2 scenarios.
/// </summary>
public sealed class ReplayQuestStateTests
{
    private static readonly DateTimeOffset _at = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // §8.4 Happy path — GetQuestSequence with matching argument returns sequence value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_MatchingQuestId_ReturnsSuccessWithSequenceValue()
    {
        /*
         * RED: Will fail until ReplayQuestState is implemented.
         *
         * CONTRACT: Given a ReplayQuestState over
         *   [ObservationEvent(method:"GetQuestSequence", argument:{"value":66130}, value:0)],
         *   When GetQuestSequence(new QuestId(66130), ct) is awaited,
         *   Then it returns Result<int>.Success(0).
         *   The argument JSON {"value":66130} matches the requested QuestId(66130);
         *   the integer value 0 is deserialized from the observation.
         *
         * BUILDER GUIDANCE: serialize the QuestId argument via ReplayJsonOptions.Default
         *   (camelCase, WriteIndented=false) → {"value":66130}.
         *   Compare to obs.Argument.GetRawText().
         *   Deserialize obs.Value as int via ReplayJsonOptions.Default.
         */

        // Arrange — argument as the recording proxy would serialize QuestId(66130)
        var argumentJson = JsonDocument.Parse("{\"value\":66130}").RootElement;
        var valueJson = JsonDocument.Parse("0").RootElement;
        var obs = MakeObservation("GetQuestSequence", argumentJson, valueJson);
        var provider = new ReplayQuestState(new[] { obs });

        // Act
        var result = await provider.GetQuestSequence(new QuestId(66130), CancellationToken.None);

        // Assert
        var success = Assert.IsType<Result<int>.Success>(result);
        Assert.Equal(0, success.Value);
    }

    // -------------------------------------------------------------------------
    // §8.4 Argument mismatch — different QuestId → starvation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetQuestSequence_WrongQuestId_ThrowsStarvationException()
    {
        /*
         * RED: Will fail until ReplayQuestState is implemented.
         *
         * CONTRACT: Given the same provider with an observation for QuestId 66130,
         *   When GetQuestSequence(new QuestId(66104), ct) is awaited (different quest),
         *   Then ReplayObservationStarvationException is thrown because the only
         *   observation has a different argument ({value:66130} vs {value:66104}).
         *
         * BUILDER GUIDANCE: argument JSON text comparison is the discriminator.
         *   {"value":66104} != {"value":66130} → no match → starvation.
         */

        // Arrange
        var argumentJson = JsonDocument.Parse("{\"value\":66130}").RootElement;
        var valueJson = JsonDocument.Parse("0").RootElement;
        var obs = MakeObservation("GetQuestSequence", argumentJson, valueJson);
        var provider = new ReplayQuestState(new[] { obs });

        // Act + Assert
        await Assert.ThrowsAsync<ReplayObservationStarvationException>(
            () => provider.GetQuestSequence(new QuestId(66104), CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static ObservationEvent MakeObservation(
        string method,
        JsonElement? argument,
        JsonElement? value) =>
        new ObservationEvent(
            RunId: "test-run",
            Method: method,
            Argument: argument,
            Value: value,
            At: _at);
}
