using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Emotes;

/// <summary>
/// Issues a single emote text command via ECommons.Automation.Chat.SendMessage.
/// Lumina Emote sheet is preloaded at construction; lookup is O(1) per call.
///
/// Success means "Chat.SendMessage returned without throwing." It does NOT mean
/// "the emote animation played" or "the NPC reacted to it." The engine verifies
/// outcome via the authored Expect predicate (per engine plan Decision UE4).
///
/// Failure cases:
///   - emoteCommandNotFound  — emoteId has no Lumina row, or row has no text command
///   - emoteCommandMalformed — Lumina text command doesn't start with '/' (defensive)
///   - targetNotFound        — targetNpcId supplied but no matching object in ObjectTable
///   - chatSendFailed        — Chat.SendMessage threw (invalid characters, signature lost, etc.)
/// </summary>
public sealed class DalamudEmoteExecutor : IEmoteExecutor
{
    private readonly PluginServices _svc;
    private readonly IReadOnlyDictionary<uint, string> _emoteCommands;

    public DalamudEmoteExecutor(PluginServices svc)
    {
        _svc = svc;
        _emoteCommands = LoadEmoteCommands(svc.DataManager);
    }

    public Task<Result<Unit>> UseEmote(
        uint emoteId,
        NpcId? targetNpcId,
        bool motion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Resolve text command via the pure helper. Bails fast on bad emoteId
        //    BEFORE touching game state (don't clobber TargetManager.Target on bad input).
        var commandResult = EmoteCommandResolver.Resolve(
            emoteId, motion,
            id => _emoteCommands.TryGetValue(id, out var c) ? c : null);

        if (commandResult is not Result<string>.Success { Value: var command })
        {
            var failure = (Result<string>.Failure)commandResult;
            return Task.FromResult<Result<Unit>>(Result.Fail(failure.Reason, failure.Detail));
        }

        // 2. Acquire target if requested. Filter mirrors DalamudActionExecutor.UseAction.
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

        // 3. Submit. ECommons docs (Chat.cs:65-67) document ArgumentException
        //    (empty / too long / invalid chars) and InvalidOperationException
        //    (signature not found). Catch broadly so adapter-shell exceptions
        //    surface as Result.Fail rather than crashing the dispatch loop.
        try
        {
            Chat.SendMessage(command);
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<Unit>>(
                Result.Fail("chatSendFailed", $"Chat.SendMessage threw: {ex.Message}"));
        }

        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    private static IReadOnlyDictionary<uint, string> LoadEmoteCommands(IDataManager dataManager)
    {
        var dict = new Dictionary<uint, string>();
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet is null) return dict; // defensive — should never happen post-init
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (!row.TextCommand.IsValid) continue;
            var cmd = row.TextCommand.Value.Command.ExtractText();
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('/')) continue;
            dict[row.RowId] = cmd;
        }
        return dict;
    }
}
