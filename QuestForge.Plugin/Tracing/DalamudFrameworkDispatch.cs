using Dalamud.Plugin.Services;
using QuestForge.Plugin.Tracing;

namespace QuestForge.Plugin.Tracing;

public sealed class DalamudFrameworkDispatch : IFrameworkDispatch
{
    private readonly IFramework _framework;
    // Dalamud's Update is Action<IFramework>; our interface expects Action
    private readonly Dictionary<Action, IFramework.OnUpdateDelegate> _map = new();

    public DalamudFrameworkDispatch(IFramework framework) => _framework = framework;

    public void Subscribe(Action handler)
    {
        IFramework.OnUpdateDelegate wrapper = _ => handler();
        _map[handler] = wrapper;
        _framework.Update += wrapper;
    }

    public void Unsubscribe(Action handler)
    {
        if (_map.TryGetValue(handler, out var wrapper))
        {
            _framework.Update -= wrapper;
            _map.Remove(handler);
        }
    }
}
