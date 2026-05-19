using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine;

public abstract record EngineAction
{
    public sealed record Navigate(WorldPosition Destination, NavigationOptions Options) : EngineAction;
    public sealed record Interact(NpcId Target, Step? Origin = null) : EngineAction;
    public sealed record HandOver(NpcId Target, ItemId[] Items) : EngineAction;
    // SourcePosition: world position of the source aethernet shard the player navigates to
    // before the teleport fires. Null when no source position is known (immediate dispatch).
    public sealed record UseAethernet(AethernetId Destination, WorldPosition? SourcePosition = null) : EngineAction;
    public sealed record Wait(string Reason, Step? Origin = null) : EngineAction;
    public sealed record AwaitUser(string Reason) : EngineAction;
    public sealed record Done : EngineAction;
}