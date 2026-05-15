using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Interaction;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeDialogueResolverTests
{
    [Fact]
    public async Task RegisterText_ResolveText_ReturnsOkWithText()
    {
        var resolver = new FakeDialogueResolver();
        resolver.RegisterText("ref", GameLanguage.English, "Hello");
        var result = await resolver.ResolveText("ref", CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("Hello", result.ValueOrThrow);
    }

    [Fact]
    public async Task UnregisteredReference_ResolveText_ReturnsFailure()
    {
        var resolver = new FakeDialogueResolver();
        var result = await resolver.ResolveText("unknown-ref", CancellationToken.None);
        Assert.False(result.IsSuccess);
        var failure = Assert.IsType<global::QuestForge.Adapters.Types.Result<string>.Failure>(result);
        Assert.Equal("sheet-reference-not-found", failure.Reason);
    }

    [Fact]
    public async Task Reset_ClearsRecordedResolutions_ButPreservesRegisteredTexts()
    {
        var resolver = new FakeDialogueResolver();
        resolver.RegisterText("ref", GameLanguage.English, "Hello");
        await resolver.ResolveText("ref", CancellationToken.None); // record 1

        resolver.Reset();

        Assert.Equal(0, resolver.RecordedResolutions.Count);
        // Registered text survives
        var result = await resolver.ResolveText("ref", CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SetActiveLanguage_Japanese_UsesJapaneseRegistration()
    {
        var resolver = new FakeDialogueResolver();
        resolver.RegisterText("greeting", GameLanguage.English, "Hello");
        resolver.RegisterText("greeting", GameLanguage.Japanese, "Konnichiwa");
        resolver.SetActiveLanguage(GameLanguage.Japanese);

        var result = await resolver.ResolveText("greeting", CancellationToken.None);
        Assert.Equal("Konnichiwa", result.ValueOrThrow);
    }

    [Fact]
    public async Task RecordedResolutions_IncrementsOnEachResolveCall()
    {
        var resolver = new FakeDialogueResolver();
        resolver.RegisterText("ref", GameLanguage.English, "Hello");

        await resolver.ResolveText("ref", CancellationToken.None);
        await resolver.ResolveText("ref", CancellationToken.None);
        await resolver.ResolveText("ref", CancellationToken.None);

        Assert.Equal(3, resolver.RecordedResolutions.Count);
    }
}