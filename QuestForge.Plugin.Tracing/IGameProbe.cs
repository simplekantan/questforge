namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Abstracts game-state queries used by UIObserver.
/// Implement this over QuestManager / UIState / InventoryManager in production;
/// use FakeGameProbe in tests.
/// </summary>
public interface IGameProbe
{
    IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests();
    bool IsAetheryteUnlocked(uint rowId);
    IEnumerable<uint> GetAllAetheryteRowIds();
    IReadOnlyList<(uint ItemId, int Qty)> GetKeyItemSlots();
    (float X, float Y, float Z, int Zone)? GetPlayerPosition();
    (uint Sequence, uint FfxivActionType, uint ActionId,
     float TargetLocationX, float TargetLocationY, float TargetLocationZ)? GetLastActionEffect();
    ushort? GetPlayerEmoteId();

    /// <summary>
    /// Returns RaptureLogModule.LogModule.LogMessageCount — the full count of log entries
    /// (system + inbound + outbound). Iterate 0..count-1 with GetChatLogEntry for each.
    /// null when the module is unavailable.
    /// </summary>
    /// <remarks>
    /// We use LogMessageCount (the inherited LogModule base counter) rather than
    /// MsgSourceArrayLength because the source array only tracks INBOUND messages from
    /// other characters — the player's own outbound /say does not appear there. Verified
    /// in-game during slice 5 smoke (see SAY_CHAT_MESSAGE_INFERENCE_PLAN.md §F O1).
    /// </remarks>
    int? GetChatLogMessageCount();

    /// <summary>
    /// Returns the chat log entry at the given absolute log index (0..LogMessageCount-1),
    /// or null if out of range / read failed. Uses RaptureLogModule.GetLogMessageDetail
    /// which works for every log line including outbound player messages.
    /// </summary>
    ChatLogEntry? GetChatLogEntry(int index);

    /// <summary>
    /// Returns the local player's display name (e.g. "Gabriel Deleon"), or null when no
    /// character is loaded. Used to identify the player's own messages in the chat log,
    /// since LogInfo.SourceKind is uniformly 0 for all chat entries and ContentId is not
    /// populated for outbound messages.
    /// </summary>
    string? GetLocalPlayerName();
}

/// <summary>
/// Flattened chat log entry returned by IGameProbe.GetChatLogEntry.
/// LogKind is the LogInfo.LogKind bitfield value — for chat, this equals the chat channel ID
/// (10 = Say, 11 = Shout, etc.). SenderName is the display name as it appears in chat
/// ("Gabriel Deleon"); empty for system messages.
/// </summary>
public sealed record ChatLogEntry(
    int LogKind,
    string SenderName,
    string Message);
