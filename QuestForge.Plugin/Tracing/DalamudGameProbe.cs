using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using QuestForge.Plugin.Tracing;

namespace QuestForge.Plugin.Tracing;

public sealed unsafe class DalamudGameProbe : IGameProbe
{
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;

    public DalamudGameProbe(IDataManager dataManager, IObjectTable objectTable, IClientState clientState)
    {
        _dataManager  = dataManager;
        _objectTable  = objectTable;
        _clientState  = clientState;
    }

    public IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests()
    {
        var mgr = QuestManager.Instance();
        if (mgr == null) return [];
        var result = new List<(ushort, byte, byte, IReadOnlyList<byte>)>();
        foreach (ref var slot in mgr->NormalQuests)
        {
            if (slot.QuestId == 0) continue;
            var span = slot.Variables;
            var vars = new byte[6];
            for (var i = 0; i < 6; i++) vars[i] = span[i];
            result.Add((slot.QuestId, slot.Sequence, slot.Flags, vars));
        }
        return result;
    }

    public bool IsAetheryteUnlocked(uint rowId)
    {
        var uiState = UIState.Instance();
        return uiState != null && uiState->IsAetheryteUnlocked(rowId);
    }

    public IEnumerable<uint> GetAllAetheryteRowIds()
    {
        var sheet = _dataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null) return [];
        return sheet.Select(r => r.RowId).Where(id => id > 0);
    }

    public IReadOnlyList<(uint ItemId, int Qty)> GetKeyItemSlots()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return [];
        var container = mgr->GetInventoryContainer(InventoryType.KeyItems);
        if (container == null || !container->IsLoaded) return [];
        var result = new List<(uint, int)>();
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            result.Add((slot->ItemId, (int)slot->Quantity));
        }
        return result;
    }

    public (float X, float Y, float Z, int Zone)? GetPlayerPosition()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null) return null;
        var p = player.Position;
        return (p.X, p.Y, p.Z, (int)_clientState.TerritoryType);
    }
}
