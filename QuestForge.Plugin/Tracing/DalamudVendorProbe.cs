using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Dalamud-backed <see cref="IVendorProbe"/> for authoring-mode purchase detection.
/// Reports shop-open state, current gil and GC seal balances, and per-poll inventory
/// count deltas for regular-bag items. No correlation logic lives here — SnapshotAggregator
/// derives purchase events from the observations forwarded by UIObserver.
/// </summary>
public sealed class DalamudVendorProbe : IVendorProbe
{
    private readonly IGameGui _gameGui;
    private readonly Dictionary<uint, int> _priorCounts = new();
    private bool _priorInitialized;

    public DalamudVendorProbe(IGameGui gameGui)
    {
        _gameGui = gameGui;
    }

    // VERIFY IN-GAME: addon names "Shop" (gil vendors) and "GrandCompanyExchange" (GC
    // quartermaster) were confirmed present during Slice C implementation; these strings
    // are already used identically in DalamudVendor.cs.
    public bool IsShopOpen()
    {
        var shop = _gameGui.GetAddonByName("Shop");
        if (!shop.IsNull && shop.IsReady) return true;

        var gcExchange = _gameGui.GetAddonByName("GrandCompanyExchange");
        return !gcExchange.IsNull && gcExchange.IsReady;
    }

    public unsafe long GetGil()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 0L;
        return (long)mgr->GetGil();
    }

    public unsafe int GetGrandCompanySeals()
    {
        var ps = PlayerState.Instance();
        if (ps == null) return 0;

        var gc = ps->GrandCompany;
        if (gc == 0) return 0;

        // GrandCompany byte: 1=Maelstrom→seal item 20, 2=Twin Adder→21, 3=Immortal Flames→22.
        // VERIFY IN-GAME: these seal item ids match the ones confirmed in DalamudGameStateProvider.
        uint sealItemId = gc switch
        {
            1 => 20u,
            2 => 21u,
            3 => 22u,
            _ => 0u
        };

        if (sealItemId == 0) return 0;

        var mgr = InventoryManager.Instance();
        if (mgr == null) return 0;

        return mgr->GetInventoryItemCount(sealItemId, isHq: false, checkEquipped: false, checkArmory: false);
    }

    // G6: implement radio-Selected reads. Per D14.4 the implementation walks the
    // AtkComponentRadioButton group on the GrandCompanyExchange addon (node ids 44+i for
    // category, 37+i for tier — see DalamudVendor.cs §G3) and returns the index of the
    // button whose Selected flag is true, or null on any traversal failure.
    // VERIFY IN-GAME: the exact AtkComponentRadioButton node path.
    // Returning null (fail-quiet) is safe — the aggregator treats null as "no opinion",
    // the modal falls back to blank inputs (D14.4), the rest of the pipeline works unchanged.
    public int? GetActiveGcCategory() => null;
    public int? GetActiveGcRankTier() => null;

    public unsafe IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts()
    {
        // VERIFY IN-GAME: Inventory1-4 cover the four regular inventory bags. Purchases land
        // in regular bags; reagent bag, equipped, and armory slots are excluded intentionally.
        var mgr = InventoryManager.Instance();
        if (mgr == null) return [];

        var inventoryTypes = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        var current = new Dictionary<uint, int>();
        foreach (var inventoryType in inventoryTypes)
        {
            var container = mgr->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                current[slot->ItemId] = current.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }

        if (!_priorInitialized)
        {
            foreach (var kvp in current)
                _priorCounts[kvp.Key] = kvp.Value;
            _priorInitialized = true;
            return [];
        }

        var changed = new List<(uint, int)>();
        var allIds = new HashSet<uint>(current.Keys);
        allIds.UnionWith(_priorCounts.Keys);

        foreach (var id in allIds)
        {
            var delta = current.GetValueOrDefault(id) - _priorCounts.GetValueOrDefault(id);
            if (delta != 0)
                changed.Add((id, delta));
        }

        _priorCounts.Clear();
        foreach (var kvp in current)
            _priorCounts[kvp.Key] = kvp.Value;

        return changed;
    }
}
