using QuestForge.Engine.Travel;
using Xunit;

namespace QuestForge.Engine.Tests.Travel;

/// <summary>
/// Tests AetheryteZoneMap forward and reverse lookup. The static map is populated from Lumina
/// at plugin startup, but tests use Populate() to inject deterministic state.
/// Every test re-installs the test-corpus baseline (1000→130, 8→129) so TeleportStepTests etc.
/// remain green regardless of test execution order.
/// </summary>
public sealed class AetheryteZoneMapTests : IDisposable
{
    // Mirrors the static-default map in AetheryteZoneMap.cs.
    private static readonly Dictionary<uint, uint> TestCorpusDefaults = new()
    {
        { 1000u, 130u },
        { 8u,    129u },
    };

    public void Dispose() => AetheryteZoneMap.Populate(TestCorpusDefaults);

    private static void PopulateWithCorpus(IEnumerable<KeyValuePair<uint, uint>> extra)
    {
        var combined = new Dictionary<uint, uint>(TestCorpusDefaults);
        foreach (var kv in extra) combined[kv.Key] = kv.Value;
        AetheryteZoneMap.Populate(combined);
    }

    [Fact]
    public void TryGetZone_KnownAetheryte_ReturnsZone()
    {
        PopulateWithCorpus(new Dictionary<uint, uint>
        {
            { 2u, 132u }, // New Gridania
            { 9u, 130u }, // Ul'dah Steps of Nald (collides with test-corpus 1000→130)
        });

        Assert.True(AetheryteZoneMap.TryGetZone(2u, out var zone));
        Assert.Equal(132u, zone);
    }

    [Fact]
    public void TryGetZone_UnknownAetheryte_ReturnsFalse()
    {
        PopulateWithCorpus(Array.Empty<KeyValuePair<uint, uint>>());

        Assert.False(AetheryteZoneMap.TryGetZone(424242u, out _));
    }

    [Fact]
    public void TryGetAetheryteByZone_KnownZone_ReturnsAetheryte()
    {
        PopulateWithCorpus(new Dictionary<uint, uint> { { 2u, 132u } });

        Assert.True(AetheryteZoneMap.TryGetAetheryteByZone(132u, out var aid));
        Assert.Equal(2u, aid);
    }

    [Fact]
    public void TryGetAetheryteByZone_UnknownZone_ReturnsFalse()
    {
        PopulateWithCorpus(Array.Empty<KeyValuePair<uint, uint>>());

        Assert.False(AetheryteZoneMap.TryGetAetheryteByZone(99999u, out _));
    }

    [Fact]
    public void Populate_RebuildsReverseMap()
    {
        PopulateWithCorpus(new Dictionary<uint, uint> { { 50u, 200u } });
        Assert.True(AetheryteZoneMap.TryGetAetheryteByZone(200u, out var first));
        Assert.Equal(50u, first);

        PopulateWithCorpus(new Dictionary<uint, uint> { { 99u, 200u } });
        Assert.True(AetheryteZoneMap.TryGetAetheryteByZone(200u, out var second));
        Assert.Equal(99u, second);
    }
}
