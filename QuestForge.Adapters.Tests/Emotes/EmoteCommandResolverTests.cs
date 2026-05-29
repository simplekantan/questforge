using QuestForge.Adapters.Emotes; // TODO: EmoteCommandResolver lives here (QuestForge.Adapters, Dalamud-free)

namespace QuestForge.Adapters.Tests.Emotes;

/// <summary>
/// Pure-helper tests for EmoteCommandResolver.
/// Spec: docs/USE_EMOTE_STEP_PLAN.md â€” Decision UE8, scenarios UE16â€“UE18.
///
/// EmoteCommandResolver is Dalamud-free and lives in QuestForge.Adapters so these
/// tests can run without a game instance (net10.0 target, no Dalamud reference).
///
/// Builder tasks:
///         with static method: Result&lt;string&gt; Resolve(uint emoteId, bool motion, Func&lt;uint, string?&gt; commandLookup)
/// </summary>
public class EmoteCommandResolverTests
{
    // =========================================================================
    // UE16 â€” Resolve happy path: lookup returns "/cheer", motion=true â†’ "/cheer motion"
    // =========================================================================

    [Fact]
    public void Resolve_MotionTrue_AppendsMotionSuffix_UE16()
    {
        // Given: a lookup that maps emoteId 17 â†’ "/cheer"
        Func<uint, string?> lookup = id => id == 17u ? "/cheer" : null;

        // When:
        var result = EmoteCommandResolver.Resolve(emoteId: 17u, motion: true, commandLookup: lookup); // TODO: EmoteCommandResolver.Resolve

        // Then:
        Assert.True(result.IsSuccess,
            $"Expected success but got failure. IsSuccess={result.IsSuccess}");
        Assert.Equal("/cheer motion", result.ValueOrThrow);
    }

    // =========================================================================
    // UE17 â€” Resolve happy path: motion=false â†’ bare command, no suffix
    // =========================================================================

    [Fact]
    public void Resolve_MotionFalse_ReturnsBareCommand_UE17()
    {
        // Given: same lookup as UE16
        Func<uint, string?> lookup = id => id == 17u ? "/cheer" : null;

        // When:
        var result = EmoteCommandResolver.Resolve(emoteId: 17u, motion: false, commandLookup: lookup); // TODO: EmoteCommandResolver.Resolve

        // Then:
        Assert.True(result.IsSuccess,
            $"Expected success but got failure. IsSuccess={result.IsSuccess}");
        Assert.Equal("/cheer", result.ValueOrThrow);
    }

    // =========================================================================
    // UE18 â€” Resolve fails when lookup returns null â†’ emoteCommandNotFound
    // =========================================================================

    [Fact]
    public void Resolve_LookupReturnsNull_ReturnsEmoteCommandNotFound_UE18()
    {
        // Given: a lookup that returns null for every input
        Func<uint, string?> lookup = _ => null;

        // When:
        var result = EmoteCommandResolver.Resolve(emoteId: 9999u, motion: true, commandLookup: lookup); // TODO: EmoteCommandResolver.Resolve

        // Then:
        Assert.False(result.IsSuccess);
        // The failure's reason code must be "emoteCommandNotFound"
        var failure = Assert.IsType<QuestForge.Adapters.Types.Result<string>.Failure>(result);
        Assert.Equal("emoteCommandNotFound", failure.Reason);
        // The detail string must contain the emote id
        Assert.Contains("9999", failure.Detail ?? string.Empty);
    }

    // =========================================================================
    // UE18b (optional defensive) â€” Resolve fails when lookup returns a non-slash string
    // =========================================================================

    [Fact]
    public void Resolve_LookupReturnsMalformedCommand_ReturnsEmoteCommandMalformed_UE18b()
    {
        // Given: a lookup returning a string without a leading '/' (malformed Lumina row)
        Func<uint, string?> lookup = _ => "cheer"; // missing leading slash

        // When:
        var result = EmoteCommandResolver.Resolve(emoteId: 17u, motion: true, commandLookup: lookup); // TODO: EmoteCommandResolver.Resolve

        // Then:
        Assert.False(result.IsSuccess);
        var failure = Assert.IsType<QuestForge.Adapters.Types.Result<string>.Failure>(result);
        Assert.Equal("emoteCommandMalformed", failure.Reason);
    }
}
