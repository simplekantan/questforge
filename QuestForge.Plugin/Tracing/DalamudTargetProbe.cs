using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using QuestForge.Plugin.Tracing;

namespace QuestForge.Plugin.Tracing;

public sealed class DalamudTargetProbe : ITargetProbe
{
    private readonly ITargetManager _targetManager;
    private readonly IClientState _clientState;

    public DalamudTargetProbe(ITargetManager targetManager, IClientState clientState)
    {
        _targetManager = targetManager;
        _clientState   = clientState;
    }

    public (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcTarget()
        => AsInteractableNpc(_targetManager.Target);

    public (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcPreviousTarget()
        => AsInteractableNpc(_targetManager.PreviousTarget);

    public uint? GetAetheryteTarget()
    {
        var t = _targetManager.Target;
        return t?.ObjectKind == ObjectKind.Aetheryte ? t.BaseId : null;
    }

    private (uint, float, float, float, int)? AsInteractableNpc(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        if (obj is null) return null;
        if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) return null;
        var p = obj.Position;
        return (obj.BaseId, p.X, p.Y, p.Z, (int)_clientState.TerritoryType);
    }
}
