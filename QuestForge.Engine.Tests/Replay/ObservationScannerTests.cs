using System.Text.Json;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for ObservationScanner.Next — the cursor-based observation matcher.
/// All tests will fail until Builder implements ObservationScanner in QuestForge.Adapters.Fakes.Replay.
///
/// Covers §8.2 of PHASE_7_PLAN.md — 4 scenarios.
/// </summary>
public sealed class ObservationScannerTests
{
    private static readonly DateTimeOffset _at = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // §8.2 Happy path — first observation matches immediately
    // -------------------------------------------------------------------------

    [Fact]
    public void Next_FirstObservationMatches_ReturnsThatObservationAndAdvancesCursor()
    {
        /*
         * RED: Will fail until ObservationScanner is implemented.
         *
         * CONTRACT: Given a scanner over
         *   [obs(GetPlayerZone, null, value:{value:182}),
         *    obs(GetQuestSequence, {value:66130}, value:0)],
         *   cursor at 0,
         *   When Next("GetPlayerZone", null) is called,
         *   Then it returns the first observation (Method == "GetPlayerZone")
         *   and the cursor advances to 1 (the next call will start scanning from index 1).
         *
         * BUILDER GUIDANCE: linear forward scan from _cursor; on match advance _cursor = i + 1.
         */

        // Arrange
        var obs1 = MakeObservation("GetPlayerZone", null, JsonDocument.Parse("{\"value\":182}").RootElement);
        var obs2 = MakeObservation("GetQuestSequence",
            JsonDocument.Parse("{\"value\":66130}").RootElement,
            JsonDocument.Parse("0").RootElement);

        var scanner = new ObservationScanner(new[] { obs1, obs2 });

        // Act
        var result = scanner.Next("GetPlayerZone", null);

        // Assert — correct observation returned
        Assert.Equal("GetPlayerZone", result.Data.Method);
        Assert.Null(result.Data.Argument);

        // Assert — cursor advanced: calling Next for the second method should now find obs2
        var result2 = scanner.Next("GetQuestSequence", new QuestId(66130));
        Assert.Equal("GetQuestSequence", result2.Data.Method);
    }

    // -------------------------------------------------------------------------
    // §8.2 Happy path — skip-forward match (second observation matches, first is silently consumed)
    // -------------------------------------------------------------------------

    [Fact]
    public void Next_SkipForwardMatch_ReturnsSecondObservationAndAdvancesCursor()
    {
        /*
         * RED: Will fail until ObservationScanner is implemented.
         *
         * CONTRACT: Given the same two-entry scanner,
         *   When Next("GetQuestSequence", new QuestId(66130)) is called WITHOUT
         *   first consuming GetPlayerZone,
         *   Then the scanner scans forward, finds obs2, returns it, and advances
         *   the cursor to 2. obs1 (GetPlayerZone) is silently skipped — it is no
         *   longer reachable. This is the Phase 7 loose-scan behaviour.
         *
         * BUILDER GUIDANCE: the scanner does NOT enforce strict ordering in Phase 7.
         *   It scans from _cursor to end, returns the first match it finds.
         */

        // Arrange
        var obs1 = MakeObservation("GetPlayerZone", null, JsonDocument.Parse("{\"value\":182}").RootElement);
        var obs2 = MakeObservation("GetQuestSequence",
            JsonDocument.Parse("{\"value\":66130}").RootElement,
            JsonDocument.Parse("0").RootElement);

        var scanner = new ObservationScanner(new[] { obs1, obs2 });

        // Act — skip obs1, jump directly to obs2
        var result = scanner.Next("GetQuestSequence", new QuestId(66130));

        // Assert
        Assert.Equal("GetQuestSequence", result.Data.Method);

        // Assert — cursor is now exhausted; asking for GetPlayerZone starvates
        Assert.Throws<ReplayObservationStarvationException>(
            () => scanner.Next("GetPlayerZone", null));
    }

    // -------------------------------------------------------------------------
    // §8.2 Edge — argument mismatch means no match → starvation
    // -------------------------------------------------------------------------

    [Fact]
    public void Next_ArgumentMismatch_ThrowsStarvationException()
    {
        /*
         * RED: Will fail until ObservationScanner is implemented.
         *
         * CONTRACT: Given a scanner with
         *   [obs(GetQuestSequence, {value:66130}, value:0)],
         *   When Next("GetQuestSequence", new QuestId(66104)) is called
         *   (the QuestId argument differs — 66104 vs 66130),
         *   Then no match is found and ReplayObservationStarvationException is thrown.
         *   The exception message names the method and the argument that was requested.
         *
         * BUILDER GUIDANCE: argument equality is JsonElement.GetRawText() comparison.
         *   QuestId(66104) serializes to {"value":66104}, QuestId(66130) to {"value":66130}.
         *   These differ → no match → starvation.
         */

        // Arrange
        var obs = MakeObservation("GetQuestSequence",
            JsonDocument.Parse("{\"value\":66130}").RootElement,
            JsonDocument.Parse("0").RootElement);

        var scanner = new ObservationScanner(new[] { obs });

        // Act + Assert
        var ex = Assert.Throws<ReplayObservationStarvationException>(
            () => scanner.Next("GetQuestSequence", new QuestId(66104)));

        Assert.Contains("GetQuestSequence", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // §8.2 Error — cursor exhausted → starvation exception
    // -------------------------------------------------------------------------

    [Fact]
    public void Next_CursorExhausted_ThrowsStarvationException()
    {
        /*
         * RED: Will fail until ObservationScanner is implemented.
         *
         * CONTRACT: Given a scanner with no remaining observations that match the request,
         *   When Next("GetPlayerZone", null) is called,
         *   Then ReplayObservationStarvationException is thrown.
         *   The exception message names the method and the cursor position.
         *
         * BUILDER GUIDANCE: after scanning i from _cursor to observations.Count without
         *   finding a match, throw ReplayObservationStarvationException naming the method.
         */

        // Arrange — scanner has one observation for a different method
        var obs = MakeObservation("GetPlayerPosition", null,
            JsonDocument.Parse("{\"x\":0.0,\"y\":0.0,\"z\":0.0}").RootElement);
        var scanner = new ObservationScanner(new[] { obs });

        // Consume the only observation so cursor is exhausted
        scanner.Next("GetPlayerPosition", null);

        // Act + Assert — now there are no more observations at all
        var ex = Assert.Throws<ReplayObservationStarvationException>(
            () => scanner.Next("GetPlayerZone", null));

        Assert.Contains("GetPlayerZone", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static ObservationEvent MakeObservation(
        string method,
        JsonElement? argument,
        JsonElement? value) =>
        new ObservationEvent
        {
            RunId = "test-run",
            Data = new ObservationEvent.ObservationData { Method = method, Argument = argument, Value = value }
        };
}
