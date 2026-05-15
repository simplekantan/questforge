using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeNavigatorTests
{
    private static FakeNavigator CreateNavigator(out FakeGameStateProvider state)
    {
        state = new FakeGameStateProvider();
        return new FakeNavigator(state);
    }

    [Fact]
    public async Task Default_NavigateTo_ReturnsArrived_RecordsOneRequest_UpdatesPosition()
    {
        var nav = CreateNavigator(out var state);
        var dest = new WorldPosition(50f, 0f, 50f);

        var result = await nav.NavigateTo(dest, new NavigationOptions(), CancellationToken.None);

        Assert.Equal(NavigationOutcome.Arrived, result.ValueOrThrow);
        Assert.Equal(1, nav.RecordedNavigationRequests.Count);
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(dest, pos.ValueOrThrow);
    }

    [Fact]
    public async Task FailNextWith_NavigateTo_ReturnsFailure_RecordsRequest_PositionUnchanged()
    {
        var nav = CreateNavigator(out var state);
        var initial = new WorldPosition(0f, 0f, 0f);
        state.SetPosition(initial);
        nav.FailNextWith("navmesh-not-ready");

        var result = await nav.NavigateTo(new WorldPosition(100f, 0f, 100f), new NavigationOptions(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        var failure = Assert.IsType<global::QuestForge.Adapters.Types.Result<NavigationOutcome>.Failure>(result);
        Assert.Equal("navmesh-not-ready", failure.Reason);
        Assert.Equal(1, nav.RecordedNavigationRequests.Count);
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(initial, pos.ValueOrThrow);
    }

    [Fact]
    public async Task ScriptNextResult_Interrupted_NavigateTo_ReturnsInterrupted_PositionUnchanged()
    {
        var nav = CreateNavigator(out var state);
        var initial = new WorldPosition(1f, 2f, 3f);
        state.SetPosition(initial);
        nav.ScriptNextResult(NavigationOutcome.Interrupted);

        var result = await nav.NavigateTo(new WorldPosition(99f, 0f, 99f), new NavigationOptions(), CancellationToken.None);

        Assert.Equal(NavigationOutcome.Interrupted, result.ValueOrThrow);
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(initial, pos.ValueOrThrow);
    }

    [Fact]
    public async Task ScriptNextResult_IsOneShot_SecondCallReturnsArrived()
    {
        var nav = CreateNavigator(out _);
        nav.ScriptNextResult(NavigationOutcome.Interrupted);

        // First call consumes the one-shot
        await nav.NavigateTo(new WorldPosition(1f, 0f, 1f), new NavigationOptions(), CancellationToken.None);

        // Second call returns default (Arrived)
        var result = await nav.NavigateTo(new WorldPosition(2f, 0f, 2f), new NavigationOptions(), CancellationToken.None);
        Assert.Equal(NavigationOutcome.Arrived, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetIsNavigating_True_IsNavigating_ReturnsTrue()
    {
        var nav = CreateNavigator(out _);
        nav.SetIsNavigating(true);
        var result = await nav.IsNavigating(CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_NoRequestsRecorded()
    {
        var nav = CreateNavigator(out _);
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => nav.NavigateTo(new WorldPosition(0, 0, 0), new NavigationOptions(), ct));
        Assert.Equal(0, nav.RecordedNavigationRequests.Count);
    }

    [Fact]
    public async Task Stop_RecordedInStops_IsNavigatingReturnsFalse()
    {
        var nav = CreateNavigator(out _);
        nav.SetIsNavigating(true);

        await nav.Stop(CancellationToken.None);

        Assert.Equal(1, nav.RecordedStops.Count);
        var navigating = await nav.IsNavigating(CancellationToken.None);
        Assert.False(navigating.ValueOrThrow);
    }

    [Fact]
    public async Task GetNavmeshInfo_ReturnsReady()
    {
        var nav = CreateNavigator(out _);
        var result = await nav.GetNavmeshInfo(new ZoneId(1), CancellationToken.None);
        Assert.Equal(NavmeshStatus.Ready, result.ValueOrThrow.Status);
    }
}