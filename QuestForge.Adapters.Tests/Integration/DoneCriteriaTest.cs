using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Integration;

public class DoneCriteriaTest
{
    [Fact]
    public async Task DoneCriteria_NavigationRoundTrip()
    {
        var state = new FakeGameStateProvider();
        state.SetZone(new ZoneId(132));
        state.SetPosition(new WorldPosition(10, 0, 20));
        var nav = new FakeNavigator(state);
        var result = await nav.NavigateTo(new WorldPosition(50, 0, 50), new NavigationOptions(), CancellationToken.None);
        Assert.Equal(NavigationOutcome.Arrived, result.ValueOrThrow);
        Assert.Equal(1, nav.RecordedNavigationRequests.Count);
    }

    [Fact]
    public async Task DoneCriteria_StateReaderRoundTrip()
    {
        var state = new FakeGameStateProvider();
        state.SetZone(new ZoneId(132));
        state.SetPosition(new WorldPosition(10, 0, 20));
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(new WorldPosition(10, 0, 20), pos.ValueOrThrow);
        var zone = await state.GetPlayerZone(CancellationToken.None);
        Assert.Equal(new ZoneId(132), zone.ValueOrThrow);
    }
}