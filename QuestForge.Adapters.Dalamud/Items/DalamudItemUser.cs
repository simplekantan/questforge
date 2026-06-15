using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Items;

/// <summary>
/// Submits an item use via ActionManager.UseAction (self/NPC target) or
/// ActionManager.UseActionLocation (AoE ground target).
///
/// Success means "ActionManager returned true." It does NOT mean "the item
/// effect resolved." The engine verifies outcome via the authored Expect predicate.
///
/// Failure cases:
///   - actionManagerUnavailable — ActionManager.Instance() returned null
///   - targetNotFound           — targetNpcId supplied but no matching object in ObjectTable
///   - UseItem returned false   — the native call returned false (item not usable, on cooldown, etc.)
/// </summary>
public sealed class DalamudItemUser : IItemUser
{
    private readonly PluginServices _svc;

    public DalamudItemUser(PluginServices svc) => _svc = svc;

    public unsafe Task<Result<Unit>> UseItem(
        ItemKind kind,
        uint itemId,
        NpcId? targetNpcId,
        Position3? targetPosition,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var am = ActionManager.Instance();
        if (am is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));

        var nativeType = ItemActionTypeMapper.ToNative(kind);
        var mode = ItemUseInterpreter.ResolveTargetMode(targetNpcId, targetPosition);

        switch (mode)
        {
            case ItemTargetMode.Direct:
            {
                if (kind == ItemKind.InventoryItem)
                {
                    var result = AgentInventoryContext.Instance()->UseItem(itemId);
                    return Task.FromResult<Result<Unit>>(result >= 0
                        ? Result.Ok()
                        : Result.Fail("UseItem returned false", $"kind={kind} id={itemId} mode={mode} result={result}"));
                }
                var ok = am->UseAction(nativeType, itemId);
                return Task.FromResult<Result<Unit>>(ok
                    ? Result.Ok()
                    : Result.Fail("UseItem returned false", $"kind={kind} id={itemId} mode={mode}"));
            }

            case ItemTargetMode.Npc:
            {
                var id = targetNpcId!.Value;
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
                var ok = am->UseAction(nativeType, itemId, targetId: found.GameObjectId);
                return Task.FromResult<Result<Unit>>(ok
                    ? Result.Ok()
                    : Result.Fail("UseItem returned false", $"kind={kind} id={itemId} mode={mode}"));
            }

            case ItemTargetMode.Ground:
            {
                var pos = targetPosition!;
                var location = new Vector3(pos.X, pos.Y, pos.Z);
                var ok = am->UseActionLocation(nativeType, itemId, location: &location);
                return Task.FromResult<Result<Unit>>(ok
                    ? Result.Ok()
                    : Result.Fail("UseItem returned false", $"kind={kind} id={itemId} mode={mode}"));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled ItemTargetMode");
        }
    }
}
