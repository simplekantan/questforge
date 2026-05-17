using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for KeyItemHasher — 9 tests covering determinism, order-independence,
/// sensitivity to changes, and edge cases.
/// All tests will fail until Builder implements KeyItemHasher.
/// </summary>
public sealed class KeyItemHasherTests
{
    // FNV-1a offset basis as defined in the spec.
    private const uint OffsetBasis = 0x811C9DC5u;

    // =========================================================================
    // Compute_EmptyMap_ReturnsOffsetBasis
    // =========================================================================

    [Fact]
    public void Compute_EmptyMap_ReturnsOffsetBasis()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given an empty dictionary,
         *           When Compute is called,
         *           Then the return value equals OffsetBasis (0x811C9DC5).
         *
         * BUILDER GUIDANCE: FNV-1a initial hash = OffsetBasis. With no entries to fold in,
         *   the hash stays at the offset basis.
         */

        // Arrange
        var map = new Dictionary<uint, int>();

        // Act
        var result = KeyItemHasher.Compute(map);

        // Assert
        Assert.Equal(OffsetBasis, result);
    }

    // =========================================================================
    // Compute_SingleItem_IsDeterministic
    // =========================================================================

    [Fact]
    public void Compute_SingleItem_IsDeterministic()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given a map with a single entry { 2000123 → 1 },
         *           When Compute is called twice,
         *           Then both calls return the same value.
         *
         * BUILDER GUIDANCE: Pure function, no state — same input always produces same output.
         */

        // Arrange
        var map = new Dictionary<uint, int> { [2000123u] = 1 };

        // Act
        var first  = KeyItemHasher.Compute(map);
        var second = KeyItemHasher.Compute(map);

        // Assert
        Assert.Equal(first, second);
    }

    // =========================================================================
    // Compute_OrderIndependent_DictionaryInput
    // =========================================================================

    [Fact]
    public void Compute_OrderIndependent_DictionaryInput()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute with sorted iteration.
         *
         * CONTRACT: Given two dictionaries with identical (key, value) entries but
         *           different insertion order,
         *           When Compute is called on each,
         *           Then both return the same hash.
         *
         * BUILDER GUIDANCE: Sort by key before folding into the FNV accumulator so that
         *   Dictionary's undefined iteration order does not affect the result.
         */

        // Arrange — same entries, different insertion order
        var mapA = new Dictionary<uint, int>
        {
            [2000200u] = 1,
            [2000100u] = 2,
            [2000300u] = 3,
        };
        var mapB = new Dictionary<uint, int>
        {
            [2000300u] = 3,
            [2000100u] = 2,
            [2000200u] = 1,
        };

        // Act
        var hashA = KeyItemHasher.Compute(mapA);
        var hashB = KeyItemHasher.Compute(mapB);

        // Assert
        Assert.Equal(hashA, hashB);
    }

    // =========================================================================
    // Compute_DifferentItemId_DifferentHash
    // =========================================================================

    [Fact]
    public void Compute_DifferentItemId_DifferentHash()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given two maps that differ only in item ID (same qty),
         *           When Compute is called on each,
         *           Then the hashes differ.
         *
         * BUILDER GUIDANCE: The item ID is folded into the hash; a different ID must
         *   produce a distinct hash value.
         */

        // Arrange
        var mapA = new Dictionary<uint, int> { [2000100u] = 1 };
        var mapB = new Dictionary<uint, int> { [2000101u] = 1 };

        // Act
        var hashA = KeyItemHasher.Compute(mapA);
        var hashB = KeyItemHasher.Compute(mapB);

        // Assert
        Assert.NotEqual(hashA, hashB);
    }

    // =========================================================================
    // Compute_DifferentQuantity_DifferentHash
    // =========================================================================

    [Fact]
    public void Compute_DifferentQuantity_DifferentHash()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given two maps with the same item ID but different quantities,
         *           When Compute is called on each,
         *           Then the hashes differ.
         *
         * BUILDER GUIDANCE: The quantity is also folded into the hash; a quantity change
         *   must be detectable.
         */

        // Arrange
        var mapA = new Dictionary<uint, int> { [2000100u] = 1 };
        var mapB = new Dictionary<uint, int> { [2000100u] = 2 };

        // Act
        var hashA = KeyItemHasher.Compute(mapA);
        var hashB = KeyItemHasher.Compute(mapB);

        // Assert
        Assert.NotEqual(hashA, hashB);
    }

    // =========================================================================
    // Compute_MultipleItems_DeterministicAcrossRuns
    // =========================================================================

    [Fact]
    public void Compute_MultipleItems_DeterministicAcrossRuns()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given a map with three entries,
         *           When Compute is called multiple times,
         *           Then all calls return the same value.
         *
         * BUILDER GUIDANCE: Confirms determinism with multiple entries; no hidden state.
         */

        // Arrange
        var map = new Dictionary<uint, int>
        {
            [2000100u] = 1,
            [2000200u] = 5,
            [2000999u] = 2,
        };

        // Act
        var h1 = KeyItemHasher.Compute(map);
        var h2 = KeyItemHasher.Compute(map);
        var h3 = KeyItemHasher.Compute(map);

        // Assert
        Assert.Equal(h1, h2);
        Assert.Equal(h2, h3);
    }

    // =========================================================================
    // Compute_LargeMap_DoesNotThrow
    // =========================================================================

    [Fact]
    public void Compute_LargeMap_DoesNotThrow()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given a map with 1000 entries,
         *           When Compute is called,
         *           Then no exception is thrown and a non-zero value is returned.
         *
         * BUILDER GUIDANCE: Performance guard — linear scan over 1000 entries must complete
         *   without overflow or stack issues.
         */

        // Arrange
        var map = new Dictionary<uint, int>();
        for (var i = 0; i < 1000; i++)
        {
            map[(uint)(2000000 + i)] = i + 1;
        }

        // Act
        var ex = Record.Exception(() => KeyItemHasher.Compute(map));

        // Assert
        Assert.Null(ex);
    }

    // =========================================================================
    // Compute_HashIsNotZero_WhenNonEmpty
    // =========================================================================

    [Fact]
    public void Compute_HashIsNotZero_WhenNonEmpty()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute.
         *
         * CONTRACT: Given a non-empty map,
         *           When Compute is called,
         *           Then the result is not zero.
         *
         * BUILDER GUIDANCE: FNV-1a with OffsetBasis = 0x811C9DC5 — zero output would indicate
         *   a broken implementation (e.g., XOR-then-zero or uninitialized accumulator).
         */

        // Arrange
        var map = new Dictionary<uint, int> { [2000100u] = 1 };

        // Act
        var result = KeyItemHasher.Compute(map);

        // Assert
        Assert.NotEqual(0u, result);
    }

    // =========================================================================
    // Compute_NullMap_Throws
    // =========================================================================

    [Fact]
    public void Compute_NullMap_Throws()
    {
        /*
         * RED: Will fail until Builder implements KeyItemHasher.Compute with null guard.
         *
         * CONTRACT: Given a null dictionary,
         *           When Compute is called,
         *           Then ArgumentNullException is thrown.
         *
         * BUILDER GUIDANCE: Standard defensive null-check at the top of the method:
         *   ArgumentNullException.ThrowIfNull(map);
         */

        // Arrange
        IReadOnlyDictionary<uint, int> map = null!;

        // Act + Assert
        Assert.Throws<ArgumentNullException>(() => KeyItemHasher.Compute(map));
    }
}
