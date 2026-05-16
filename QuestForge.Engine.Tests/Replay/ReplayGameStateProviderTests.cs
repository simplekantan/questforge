using System.Text.Json;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for ReplayGameStateProvider — the observation-backed IGameStateProvider.
/// All tests will fail until Builder implements ReplayGameStateProvider in QuestForge.Adapters.Fakes.Replay.
///
/// Covers §8.3 of PHASE_7_PLAN.md — 3 scenarios.
/// </summary>
public sealed class ReplayGameStateProviderTests
{
    private static readonly DateTimeOffset _at = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // §8.3 Happy path — GetPlayerZone returns success with correct ZoneId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_ObservationWithZone182_ReturnsSuccessZoneId182()
    {
        /*
         * RED: Will fail until ReplayGameStateProvider is implemented.
         *
         * CONTRACT: Given a ReplayGameStateProvider over
         *   [ObservationEvent(method:"GetPlayerZone", value:{"value":182})],
         *   When GetPlayerZone(ct) is awaited,
         *   Then it returns Result<ZoneId>.Success(new ZoneId(182)).
         *   The observation cursor advances past entry 0.
         *
         * BUILDER GUIDANCE: call scanner.Next("GetPlayerZone", null),
         *   read obs.Value.GetProperty("value").GetUInt32() → wrap in ZoneId,
         *   return Result<ZoneId>.Success.
         */

        // Arrange
        var zoneValueJson = JsonDocument.Parse("{\"value\":182}").RootElement;
        var obs = MakeObservation("GetPlayerZone", null, zoneValueJson);
        var provider = new ReplayGameStateProvider(new[] { obs });

        // Act
        var result = await provider.GetPlayerZone(CancellationToken.None);

        // Assert
        var success = Assert.IsType<Result<ZoneId>.Success>(result);
        Assert.Equal(new ZoneId(182), success.Value);
    }

    // -------------------------------------------------------------------------
    // §8.3 Failure materialization — value has "failure" key → Result.Failure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_ObservationWithFailureValue_ReturnsFailureResult()
    {
        /*
         * RED: Will fail until ReplayGameStateProvider is implemented.
         *
         * CONTRACT: Given an observation with
         *   value: {"failure":"noLocalPlayer","detail":"ObjectTable[0] null"},
         *   When GetPlayerZone(ct) is awaited,
         *   Then it returns Result<ZoneId>.Failure with
         *   Reason == "noLocalPlayer" and Detail == "ObjectTable[0] null".
         *
         * BUILDER GUIDANCE: inside Materialize<T>(), check v.TryGetProperty("failure", out var f).
         *   If found → return Result.Fail<T>(f.GetString(), detail).
         *   This matches the recording proxy's Unwrap serialization of Failure results.
         */

        // Arrange
        var failureJson = JsonDocument.Parse(
            "{\"failure\":\"noLocalPlayer\",\"detail\":\"ObjectTable[0] null\"}").RootElement;
        var obs = MakeObservation("GetPlayerZone", null, failureJson);
        var provider = new ReplayGameStateProvider(new[] { obs });

        // Act
        var result = await provider.GetPlayerZone(CancellationToken.None);

        // Assert
        var failure = Assert.IsType<Result<ZoneId>.Failure>(result);
        Assert.Equal("noLocalPlayer", failure.Reason);
        Assert.Equal("ObjectTable[0] null", failure.Detail);
    }

    // -------------------------------------------------------------------------
    // §8.3 Starvation — no GetPlayerZone observation → exception propagates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlayerZone_NoMatchingObservation_PropagatesStarvationException()
    {
        /*
         * RED: Will fail until ReplayGameStateProvider is implemented.
         *
         * CONTRACT: Given a ReplayGameStateProvider whose observation list contains
         *   no "GetPlayerZone" entries (only a "GetPlayerPosition" entry),
         *   When GetPlayerZone(ct) is awaited,
         *   Then ReplayObservationStarvationException propagates (not caught by the provider).
         *
         * BUILDER GUIDANCE: the provider does NOT catch starvation exceptions — they
         *   propagate to the engine/test to signal a regression.
         */

        // Arrange
        var positionJson = JsonDocument.Parse("{\"x\":0.0,\"y\":0.0,\"z\":0.0}").RootElement;
        var obs = MakeObservation("GetPlayerPosition", null, positionJson);
        var provider = new ReplayGameStateProvider(new[] { obs });

        // Act + Assert
        await Assert.ThrowsAsync<ReplayObservationStarvationException>(
            () => provider.GetPlayerZone(CancellationToken.None));
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
