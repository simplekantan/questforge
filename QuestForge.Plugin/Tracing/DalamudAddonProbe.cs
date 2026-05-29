using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using QuestForge.Plugin.Tracing;
using System.Runtime.InteropServices;

namespace QuestForge.Plugin.Tracing;

public sealed unsafe class DalamudAddonProbe : IAddonProbe
{
    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;

    // Built once on first TelepotTown open; maps AethernetName display string → Aetheryte sheet RowId.
    private Dictionary<string, uint>? _aethernetNameToId;

    public DalamudAddonProbe(IGameGui gameGui, IDataManager dataManager)
    {
        _gameGui     = gameGui;
        _dataManager = dataManager;
    }

    public bool IsAddonOpen(string addonName)
    {
        var ptr = _gameGui.GetAddonByName(addonName);
        return !ptr.IsNull && ptr.IsReady;
    }

    public int? GetSelectedItemIndex(string addonName)
    {
        var ptr = _gameGui.GetAddonByName(addonName);
        if (ptr.IsNull || !ptr.IsReady) return null;
        var addon = (AtkUnitBase*)ptr.Address;
        // Walk node list for AtkComponentList (node type 1010 or 1022) and read SelectedItemIndex at offset 0x134
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null) continue;
            if ((ushort)node->Type != 1010 && (ushort)node->Type != 1022) continue;
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component == null) continue;
            var raw = *(int*)((nint)compNode->Component + 0x134);
            if (raw >= 0) return raw;
            break;
        }
        return null;
    }

    public string? GetTelepotTownDestinationName(int idx)
    {
        var ptr = _gameGui.GetAddonByName("TelepotTown");
        if (ptr.IsNull || !ptr.IsReady) return null;
        var addon = (AtkUnitBase*)ptr.Address;
        const int NamesBase = 262;
        if (addon->AtkValuesCount <= NamesBase + idx) return null;
        var nameVal = addon->AtkValues[NamesBase + idx];
        if (nameVal.Type != AtkValueType.String || nameVal.String.Value == null) return null;
        return Marshal.PtrToStringUTF8((nint)nameVal.String.Value);
    }

    public uint? GetTelepotTownDestinationId(int idx)
    {
        var name = GetTelepotTownDestinationName(idx);
        if (string.IsNullOrEmpty(name)) return null;
        return GetAethernetNameMap().TryGetValue(name, out var rowId) ? rowId : null;
    }

    private Dictionary<string, uint> GetAethernetNameMap()
    {
        if (_aethernetNameToId != null) return _aethernetNameToId;
        _aethernetNameToId = new Dictionary<string, uint>(StringComparer.Ordinal);
        var sheet = _dataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null) return _aethernetNameToId;
        foreach (var row in sheet)
        {
            // Include both sub-shards (IsAetheryte=false) AND master aetheryte crystals
            if (row.AethernetGroup == 0) continue;
            var name = row.AethernetName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrEmpty(name))
                _aethernetNameToId.TryAdd(name, row.RowId);
        }
        return _aethernetNameToId;
    }
}
