namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Abstracts Dalamud UI addon state queries used by UIObserver.
/// Implement this over Dalamud's GameGui / AtkUnitBase in production;
/// use FakeAddonProbe in tests.
/// </summary>
public interface IAddonProbe
{
    bool IsAddonOpen(string addonName);
    int? GetSelectedItemIndex(string addonName);
    string? GetTelepotTownDestinationName(int idx);
    uint?   GetTelepotTownDestinationId(int idx);   // resolves name to Aetheryte sheet RowId
}
