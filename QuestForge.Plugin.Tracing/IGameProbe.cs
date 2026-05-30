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
    (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect();
    ushort? GetPlayerEmoteId();

    /// <summary>
    /// Returns RaptureLogModule.MsgSourceArrayLength, the count of valid entries in the
    /// chat log circular buffer. null when the module is unavailable.
    /// </summary>
    int? GetChatLogMessageCount();

    /// <summary>
    /// Returns the chat log entry at the given index, or null if out of range / read failed.
    /// </summary>
    ChatLogEntry? GetChatLogEntry(int index);

    /// <summary>
    /// Returns InfoModule.LocalContentId. Returns 0 when unavailable (early init / character select).
    /// </summary>
    ulong GetLocalContentId();
}

/// <summary>
/// Flattened chat log entry returned by IGameProbe.GetChatLogEntry. Integer fields are raw wire
/// values (LogInfo.SourceKind as int, LogMessageSource.ChatType as int) so ChatLogEntryFilter
/// (Dalamud-free) can consume them directly.
/// </summary>
public sealed record ChatLogEntry(
    int SourceKind,
    int ChatType,
    ulong ContentId,
    string Message);
