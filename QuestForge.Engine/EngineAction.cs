using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Engine;

public abstract record EngineAction
{
    public sealed record Navigate(WorldPosition Destination, NavigationOptions Options) : EngineAction;
    public sealed record Interact(NpcId Target) : EngineAction;
    public sealed record HandOver(NpcId Target, ItemId Item) : EngineAction;
    public sealed record Wait(string Reason) : EngineAction;
    public sealed record AwaitUser(string Reason) : EngineAction;
    public sealed record Done : EngineAction;
}