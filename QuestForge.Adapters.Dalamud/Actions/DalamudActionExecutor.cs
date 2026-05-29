using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Types;
using SchemaActionType = QuestForge.Schema.ActionType;

namespace QuestForge.Adapters.Dalamud.Actions;

public sealed class DalamudActionExecutor : IActionExecutor
{
    private readonly PluginServices _svc;

    public DalamudActionExecutor(PluginServices svc) => _svc = svc;

    public unsafe Task<Result<Unit>> UseAction(
        SchemaActionType type,
        uint actionId,
        NpcId? targetNpcId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var am = ActionManager.Instance();
        if (am is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));

        ulong targetGameObjectId = 0xE000_0000UL;

        if (targetNpcId is { } id)
        {
            IGameObject? found = null;
            foreach (var obj in _svc.ObjectTable)
            {
                if (obj is null || obj.BaseId != id.Value) continue;
                if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
                found = obj;
                break;
            }

            if (found is null)
                return Task.FromResult<Result<Unit>>(
                    Result.Fail("targetNotFound", ActionStatusInterpreter.FormatTargetNotFoundReason(id.Value)));

            _svc.TargetManager.Target = found;
            targetGameObjectId = found.GameObjectId;
        }

        am->UseAction(ActionExecutorLogic.ToFFXIVActionType(type), actionId, targetId: targetGameObjectId);
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public unsafe Task<Result<ActionStatus>> GetActionStatus(
        SchemaActionType type,
        uint actionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var am = ActionManager.Instance();
        if (am is null)
            return Task.FromResult<Result<ActionStatus>>(
                Result.Fail<ActionStatus>("actionManagerUnavailable", "ActionManager.Instance() returned null"));

        var clientStructsType = ActionExecutorLogic.ToFFXIVActionType(type);
        var statusCode = am->GetActionStatus(clientStructsType, actionId);
        var recastSeconds = am->GetRecastTime(clientStructsType, actionId);
        var elapsedSeconds = am->GetRecastTimeElapsed(clientStructsType, actionId);

        return Task.FromResult<Result<ActionStatus>>(
            Result.Ok(ActionStatusInterpreter.InterpretStatus(statusCode, recastSeconds, elapsedSeconds)));
    }
}
