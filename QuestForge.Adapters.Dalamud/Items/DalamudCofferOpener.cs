using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Items;

/// <summary>
/// Opens gear coffers from player inventory via ActionManager.UseAction(ActionType.Item).
/// GetCofferItemIds scans bags 1-4 and filters via CofferIdentifier.IsCoffer.
/// OpenCoffer calls UseAction(Item, itemId, 0xE0000000, 65535).
/// </summary>
public sealed class DalamudCofferOpener : ICofferOpener
{
    private readonly PluginServices _svc;

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public DalamudCofferOpener(PluginServices svc) => _svc = svc;

    public unsafe Task<Result<IReadOnlyList<uint>>> GetCofferItemIds(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<IReadOnlyList<uint>>>(
                Result.Fail<IReadOnlyList<uint>>("inventoryManagerUnavailable", "InventoryManager.Instance() returned null"));

        var itemSheet = _svc.DataManager.GetExcelSheet<Item>();
        if (itemSheet is null)
            return Task.FromResult<Result<IReadOnlyList<uint>>>(Result.Ok<IReadOnlyList<uint>>([]));

        var cofferIds = new List<uint>();

        foreach (var bagType in PlayerBags)
        {
            var container = im->GetInventoryContainer(bagType);
            if (container is null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0) continue;
                if (!itemSheet.TryGetRow(slot->ItemId, out var row)) continue;
                if (CofferIdentifier.IsCoffer(row.ItemAction.RowId, row.ItemUICategory.RowId))
                    cofferIds.Add(slot->ItemId);
            }
        }

        return Task.FromResult<Result<IReadOnlyList<uint>>>(Result.Ok<IReadOnlyList<uint>>(cofferIds));
    }

    public unsafe Task<Result<Unit>> OpenCoffer(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var am = ActionManager.Instance();
        if (am is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));

        // ActionType.Item, targetId = 0xE0000000 (self), extraParam = 65535 (unspecified slot)
        var ok = am->UseAction(ActionType.Item, itemId, 0xE000_0000, 65535);
        return Task.FromResult<Result<Unit>>(ok
            ? Result.Ok()
            : Result.Fail("UseAction returned false", $"itemId={itemId}"));
    }
}
