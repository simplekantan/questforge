using QuestForge.Adapters.Duty;

namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeCfcResolver : ICfcResolver
{
    private readonly Dictionary<uint, uint> _map = new();

    public void Register(uint cfcId, uint territoryType) => _map[cfcId] = territoryType;

    public uint? GetTerritoryType(uint contentFinderConditionId) =>
        _map.TryGetValue(contentFinderConditionId, out var tt) ? tt : null;
}
