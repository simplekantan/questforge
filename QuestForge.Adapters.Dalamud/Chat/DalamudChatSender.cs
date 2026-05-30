using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
// Aliased: Slice 2 introduced QuestForge.Adapters.Chat (for IChatSender), which shadows
// the bare `Chat` type via namespace-vs-type lookup precedence. Without the alias, the
// `Chat.SendMessage(...)` call below resolves to the new sibling namespace and breaks
// the build. Mirrors the same fix in DalamudEmoteExecutor.cs.
using ECChat = ECommons.Automation.Chat;
using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Chat;

/// <summary>
/// Submits a literal /say chat message via ECommons.Automation.Chat.SendMessage.
/// Optionally writes TargetManager.Target before submitting so the player faces the NPC
/// while speaking.
///
/// Success means "Chat.SendMessage returned without throwing." It does NOT mean
/// "the NPC reacted to it" or "the quest script advanced." The engine verifies outcome
/// via the authored Expect predicate (per Slice 2 Decision SC5).
///
/// Failure cases:
///   - targetNotFound  — targetNpcId supplied but no matching object in ObjectTable
///   - chatSendFailed  — Chat.SendMessage threw (empty / too long / invalid chars / signature lost)
/// </summary>
public sealed class DalamudChatSender : IChatSender
{
    private readonly PluginServices _svc;

    public DalamudChatSender(PluginServices svc) => _svc = svc;

    public Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Acquire target if requested. Filter mirrors DalamudEmoteExecutor / DalamudActionExecutor /
        //    DalamudInteractor. EventNpc and BattleNpc cover the spoken-to NPC types; Aetheryte and
        //    EventObj are excluded (you don't /say at an aetheryte).
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
                    Result.Fail("targetNotFound", $"no object in scene with BaseId {id.Value}"));
            _svc.TargetManager.Target = found;
        }

        // 2. Build the command string. Slice 2 Decision SC2 / SC8: hard-wired /say, no resolver.
        var command = "/say " + message;

        // 3. Submit. ECommons docs (Chat.cs:65-67) document ArgumentException (empty /
        //    too long / invalid chars) and InvalidOperationException (signature not found).
        //    Catch broadly so adapter-shell exceptions surface as Result.Fail rather than
        //    crashing the dispatch loop.
        try
        {
            ECChat.SendMessage(command);
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<Unit>>(
                Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
        }

        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
