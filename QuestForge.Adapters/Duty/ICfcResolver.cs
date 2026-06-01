namespace QuestForge.Adapters.Duty;

/// <summary>
/// Resolves ContentFinderCondition IDs to territory type IDs.
/// Dalamud impl uses Lumina; fake impl uses a scripted dictionary.
/// </summary>
public interface ICfcResolver
{
    /// <summary>
    /// Returns the territory type for a CFC ID, or null if the CFC ID is unknown.
    /// </summary>
    uint? GetTerritoryType(uint contentFinderConditionId);
}
