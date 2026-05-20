namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Abstraction over Dalamud's IFramework.Update subscription.
/// Allows UIObserver to be tested without a real Dalamud IFramework.
/// </summary>
public interface IFrameworkDispatch
{
    void Subscribe(Action handler);
    void Unsubscribe(Action handler);
}
