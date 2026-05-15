using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class VnavmeshNavigator : INavigator
{
    private readonly PluginServices _svc;

    public VnavmeshNavigator(PluginServices svc) => _svc = svc;

    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> Stop(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
        => throw new NotImplementedException();
}