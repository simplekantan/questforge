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
}
