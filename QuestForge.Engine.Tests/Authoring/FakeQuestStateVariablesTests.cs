using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for FakeQuestState.GetQuestVariables / SetQuestVariables behavior.
/// Issue #40 Group U — U6, U7, U8, U9.
/// All tests fail until Builder implements GetQuestVariables + SetQuestVariables on FakeQuestState.
/// </summary>
public sealed class FakeQuestStateVariablesTests
{
    private static readonly QuestId Q = new(2054);

    private static string FormatResult(Result<IReadOnlyList<byte>> result) =>
        result is Result<IReadOnlyList<byte>>.Success s
            ? $"Ok([{string.Join(",", s.Value.Select(b => $"0x{b:X2}"))}])"
            : $"Fail({((Result<IReadOnlyList<byte>>.Failure)result).Reason})";

    // =========================================================================
    // U6 — FakeQuestState with no scripted variables → Result.Ok, length 6, all zero
    // =========================================================================

    [Fact]
    public async Task GetQuestVariables_NoScriptedVariables_ReturnsOkAllZeroLength6()
    {
        /*
         * RED: Will fail until Builder implements GetQuestVariables on FakeQuestState.
         *
         * U6 — CONTRACT: Given a FakeQuestState with no scripted variables for quest Q,
         *                When GetQuestVariables(Q, ct),
         *                Then the result is Result.Ok with Count == 6 and every element == 0.
         *                (Mirrors the DalamudQuestState not-accepted fallback — all-zero length-6 list.)
         */

        // Arrange
        var fake = new FakeQuestState();

        // Act
        var result = await fake.GetQuestVariables(Q, CancellationToken.None);

        // Assert — must be success
        Assert.True(result.IsSuccess,
            $"Expected Result.Ok but got: {FormatResult(result)}");

        var vars = ((Result<IReadOnlyList<byte>>.Success)result).Value;

        Assert.Equal(6, vars.Count);
        Assert.All(vars, b => Assert.Equal(0, b));
    }

    // =========================================================================
    // U7 — After SetQuestVariables, GetQuestVariables returns the scripted bytes
    // =========================================================================

    [Fact]
    public async Task GetQuestVariables_AfterSetQuestVariables_ReturnsScriptedBytes()
    {
        /*
         * RED: Will fail until Builder implements SetQuestVariables + GetQuestVariables on FakeQuestState.
         *
         * U7 — CONTRACT: Given fake.SetQuestVariables(Q, 0x10, 0, 0, 5, 0, 0),
         *                When GetQuestVariables(Q, ct),
         *                Then the result is Result.Ok with [0x10, 0, 0, 5, 0, 0].
         */

        // Arrange
        var fake = new FakeQuestState();
        fake.SetQuestVariables(Q, 0x10, 0, 0, 5, 0, 0);

        // Act
        var result = await fake.GetQuestVariables(Q, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess,
            $"Expected Result.Ok but got: {FormatResult(result)}");

        var vars = ((Result<IReadOnlyList<byte>>.Success)result).Value;

        Assert.Equal(6, vars.Count);
        Assert.Equal(0x10, vars[0]);
        Assert.Equal(0,    vars[1]);
        Assert.Equal(0,    vars[2]);
        Assert.Equal(5,    vars[3]);
        Assert.Equal(0,    vars[4]);
        Assert.Equal(0,    vars[5]);
    }

    // =========================================================================
    // U8 — SetQuestVariables with wrong length throws ArgumentException
    // =========================================================================

    [Fact]
    public void SetQuestVariables_WrongLength_ThrowsArgumentException()
    {
        /*
         * RED: Will fail until Builder implements SetQuestVariables on FakeQuestState
         *      with the length-6 guard.
         *
         * U8 — CONTRACT: Given fake.SetQuestVariables(Q, 1, 2, 3) (only 3 bytes),
         *                Then an ArgumentException is thrown.
         *                (The setter enforces exactly 6 bytes — V0 through V5.)
         */

        // Arrange
        var fake = new FakeQuestState();

        // Act + Assert
        Assert.Throws<ArgumentException>(() => fake.SetQuestVariables(Q, 1, 2, 3));
    }

    [Fact]
    public void SetQuestVariables_EmptyArray_ThrowsArgumentException()
    {
        /*
         * RED: Will fail until Builder implements SetQuestVariables on FakeQuestState.
         *
         * Companion to U8 — also enforces the 6-byte contract when 0 bytes are passed.
         */

        // Arrange
        var fake = new FakeQuestState();

        // Act + Assert
        Assert.Throws<ArgumentException>(() => fake.SetQuestVariables(Q));
    }

    // =========================================================================
    // U9 — GetQuestVariables records the read in RecordedReads
    // =========================================================================

    [Fact]
    public async Task GetQuestVariables_RecordsReadWithCorrectMethod()
    {
        /*
         * RED: Will fail until Builder implements GetQuestVariables (which calls Record(nameof(GetQuestVariables))).
         *
         * U9 — CONTRACT: Given fake.GetQuestVariables(Q, ct),
         *                Then fake.RecordedReads contains a StateRead with Method == "GetQuestVariables".
         *                (Mirrors how GetQuestFlags, GetQuestSequence, etc. record their reads.)
         */

        // Arrange
        var fake = new FakeQuestState();

        // Act
        await fake.GetQuestVariables(Q, CancellationToken.None);

        // Assert
        Assert.Contains(fake.RecordedReads.Snapshot(),
            read => read.Method == "GetQuestVariables");
    }

    [Fact]
    public async Task GetQuestVariables_RecordedRead_MethodName_IsExact()
    {
        /*
         * RED: Will fail until Builder implements GetQuestVariables.
         *
         * Companion to U9 — verifies the recorded method name is exactly "GetQuestVariables"
         * (not "getQuestVariables", not "GetVariables", etc.).
         * Uses AssertSingle to confirm exactly one read is recorded for a fresh fake.
         */

        // Arrange
        var fake = new FakeQuestState();

        // Act
        await fake.GetQuestVariables(Q, CancellationToken.None);

        // Assert — exactly one read on a fresh fake, and it names the right method
        var reads = fake.RecordedReads.Snapshot();
        Assert.Single(reads);
        Assert.Equal("GetQuestVariables", reads[0].Method);
    }
}
