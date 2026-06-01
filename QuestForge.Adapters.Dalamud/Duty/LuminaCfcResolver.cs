using Lumina.Excel.Sheets;
using QuestForge.Adapters.Duty;

namespace QuestForge.Adapters.Dalamud.Duty;

/// <summary>
/// Resolves ContentFinderCondition row IDs to territory type IDs via Lumina.
/// The lookup is synchronous (Lumina sheet is in-memory).
/// </summary>
public sealed class LuminaCfcResolver : ICfcResolver
{
    private readonly PluginServices _svc;

    public LuminaCfcResolver(PluginServices svc)
    {
        _svc = svc;
    }

    public uint? GetTerritoryType(uint contentFinderConditionId)
    {
        var sheet = _svc.DataManager.GetExcelSheet<ContentFinderCondition>();
        var row = sheet.GetRowOrDefault(contentFinderConditionId);
        if (row is null) return null;
        var tt = row.Value.TerritoryType.RowId;
        return tt > 0 ? tt : null;
    }
}
