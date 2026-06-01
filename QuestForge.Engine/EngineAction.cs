using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;

namespace QuestForge.Engine;

public abstract record EngineAction
{
    public sealed record Navigate(WorldPosition Destination, NavigationOptions Options) : EngineAction;
    public sealed record Interact(NpcId Target, Step? Origin = null) : EngineAction;
    public sealed record InteractObject(InteractableId Target, Step? Origin = null) : EngineAction;
    public sealed record HandOver(NpcId Target, ItemId[] Items) : EngineAction;
    public sealed record Purchase(NpcId Vendor, ItemId Item, int Quantity, PurchaseCurrency Currency, Step? Origin = null, int? GcCategory = null, int? GcRankTier = null) : EngineAction;
    // SourcePosition: world position of the source aethernet shard the player navigates to
    // before the teleport fires. Null when no source position is known (immediate dispatch).
    public sealed record UseAethernet(AethernetId Destination, WorldPosition? SourcePosition = null) : EngineAction;
    public sealed record Teleport(Adapters.Types.AetheryteId Destination, Step? Origin = null) : EngineAction;
    public sealed record Wait(string Reason, Step? Origin = null) : EngineAction;
    public sealed record AwaitUser(string Reason) : EngineAction;
    public sealed record Done : EngineAction;

    public sealed record UseAction(
        ActionType Type,
        uint ActionId,
        NpcId? TargetNpcId,
        Step? Origin = null) : EngineAction;

    /// <summary>
    /// Emitted while a CombatStep is the active step and expect is unmet, OR when global
    /// defense fires on any step type (Step=null when defense-driven, non-null when
    /// CombatStep cursor-driven).
    /// Target is the actor the CombatController selected this tick (null = nothing to attack).
    /// </summary>
    public sealed record Engage(CombatStep? Step, KillTarget? Target) : EngineAction;

    public sealed record UseEmote(
        uint EmoteId,
        Adapters.Types.NpcId? TargetNpcId,
        bool Motion,
        Step? Origin = null) : EngineAction;

    public sealed record SayChatMessage(
        string Message,
        Adapters.Types.NpcId? TargetNpcId,
        Step? Origin = null) : EngineAction;

    public sealed record UseItem(
        ItemKind Kind,
        uint ItemId,
        Adapters.Types.NpcId? TargetNpcId,
        Position3? TargetPosition,
        Step? Origin = null) : EngineAction;

    public sealed record EquipGear(uint ItemId, Step? Origin = null) : EngineAction;

    public sealed record EquipBestGear(Step? Origin = null) : EngineAction;

    public sealed record ChangeJob(JobId Job, Step? Origin = null) : EngineAction;

    public sealed record RegisterGearset(Step? Origin = null) : EngineAction;

    public sealed record OpenCoffer(uint ItemId, Step? Origin = null) : EngineAction;

    public sealed record EnterSinglePlayerDuty(Step? Origin = null) : EngineAction;
}