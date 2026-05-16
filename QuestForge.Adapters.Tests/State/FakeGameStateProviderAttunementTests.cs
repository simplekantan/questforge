using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Adapters.Tests.State;

/// <summary>
/// Verification tests for <see cref="FakeGameStateProvider"/> attunement behaviour.
/// These are fixture tests of the fake itself — they guarantee that downstream engine
/// tests can rely on the fake's contract for Phase 11B.
///
/// The fake is already fully implemented (SetAetheryteAttuned / IsAetheryteAttuned).
/// These tests pass immediately once the fake compiles (which it already does).
/// They are included here so the test suite documents the contract explicitly.
///
/// B1-B4 are mandatory; B5 (GetAttunedAetherytes consistency) is also included.
///
/// Spec: PHASE_11B_PLAN.md §3.4
/// </summary>
public sealed class FakeGameStateProviderAttunementTests
{
    // =========================================================================
    // B1 — default is false for any unset aetheryte
    // =========================================================================

    [Fact]
    public async Task IsAetheryteAttuned_Default_ReturnsFalse()
    {
        /*
         * CONTRACT: Given a fresh FakeGameStateProvider with no SetAetheryteAttuned calls,
         *           When IsAetheryteAttuned(new AetheryteId(99)) is awaited,
         *           Then the result is Result.Ok(false).
         *
         * BUILDER GUIDANCE: The fake uses a HashSet<AetheryteId> initialised empty.
         *   Contains returns false for any ID not explicitly added.
         */

        // Arrange
        var fake = new FakeGameStateProvider();

        // Act
        var result = await fake.IsAetheryteAttuned(new AetheryteId(99), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, "IsAetheryteAttuned should succeed");
        Assert.False(result.ValueOrThrow, "No aetheryte has been attuned — should be false");
    }

    // =========================================================================
    // B2 — SetAetheryteAttuned(true) round-trips to true
    // =========================================================================

    [Fact]
    public async Task IsAetheryteAttuned_AfterSetTrue_ReturnsTrue()
    {
        /*
         * CONTRACT: Given fake.SetAetheryteAttuned(new AetheryteId(42), true),
         *           When IsAetheryteAttuned(new AetheryteId(42)) is awaited,
         *           Then the result is Result.Ok(true).
         *
         * BUILDER GUIDANCE: SetAetheryteAttuned(id, true) adds id to the HashSet.
         *   IsAetheryteAttuned returns Contains(id).
         */

        // Arrange
        var fake = new FakeGameStateProvider();
        fake.SetAetheryteAttuned(new AetheryteId(42), true);

        // Act
        var result = await fake.IsAetheryteAttuned(new AetheryteId(42), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, "IsAetheryteAttuned should succeed");
        Assert.True(result.ValueOrThrow, "AetheryteId(42) was set attuned — should be true");
    }

    // =========================================================================
    // B3 — SetAetheryteAttuned(false) after true reverts to false
    // =========================================================================

    [Fact]
    public async Task IsAetheryteAttuned_AfterSetTrueThenFalse_ReturnsFalse()
    {
        /*
         * CONTRACT: Given SetAetheryteAttuned(42, true) then SetAetheryteAttuned(42, false),
         *           When IsAetheryteAttuned(42) is read,
         *           Then result is Result.Ok(false).
         *
         * BUILDER GUIDANCE: SetAetheryteAttuned(id, false) removes id from the HashSet.
         */

        // Arrange
        var fake = new FakeGameStateProvider();
        fake.SetAetheryteAttuned(new AetheryteId(42), true);
        fake.SetAetheryteAttuned(new AetheryteId(42), false);

        // Act
        var result = await fake.IsAetheryteAttuned(new AetheryteId(42), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, "IsAetheryteAttuned should succeed");
        Assert.False(result.ValueOrThrow, "AetheryteId(42) was reset to false — should be false");
    }

    // =========================================================================
    // B4 — IDs are independent; setting one does not affect another
    // =========================================================================

    [Fact]
    public async Task IsAetheryteAttuned_OtherIdUnaffected()
    {
        /*
         * CONTRACT: Given SetAetheryteAttuned(42, true),
         *           When reading IsAetheryteAttuned(43),
         *           Then result is Result.Ok(false).
         *
         * BUILDER GUIDANCE: Each AetheryteId is a separate HashSet entry.
         *   AetheryteId is a readonly record struct, so equality is by Value.
         */

        // Arrange
        var fake = new FakeGameStateProvider();
        fake.SetAetheryteAttuned(new AetheryteId(42), true);

        // Act
        var result = await fake.IsAetheryteAttuned(new AetheryteId(43), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, "IsAetheryteAttuned should succeed");
        Assert.False(result.ValueOrThrow, "AetheryteId(43) was not set — should be false");
    }

    // =========================================================================
    // B5 — GetAttunedAetherytes returns all set IDs (optional)
    // =========================================================================

    [Fact]
    public async Task GetAttunedAetherytes_AfterSettingTwo_ReturnsBothIds()
    {
        /*
         * CONTRACT: Given SetAetheryteAttuned(1, true) and SetAetheryteAttuned(2, true),
         *           When GetAttunedAetherytes is awaited,
         *           Then the result contains both AetheryteId(1) and AetheryteId(2).
         *           Order is undefined — assert as a set.
         *
         * BUILDER GUIDANCE: GetAttunedAetherytes returns _attunedAetherytes.ToArray().
         */

        // Arrange
        var fake = new FakeGameStateProvider();
        fake.SetAetheryteAttuned(new AetheryteId(1), true);
        fake.SetAetheryteAttuned(new AetheryteId(2), true);

        // Act
        var result = await fake.GetAttunedAetherytes(CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, "GetAttunedAetherytes should succeed");
        var list = result.ValueOrThrow;
        Assert.Contains(new AetheryteId(1), list);
        Assert.Contains(new AetheryteId(2), list);
        Assert.Equal(2, list.Count);
    }
}
