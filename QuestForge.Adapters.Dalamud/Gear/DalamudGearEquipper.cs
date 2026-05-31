using FFXIVClientStructs.FFXIV.Client.Game;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudGearEquipper : IGearEquipper
{
    private readonly PluginServices _svc;

    private static readonly InventoryType[] SearchOrder =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public DalamudGearEquipper(PluginServices svc) => _svc = svc;

    public unsafe Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<EquipOutcome>>(
                Result.Fail<EquipOutcome>("inventoryManagerUnavailable", "InventoryManager.Instance() returned null"));

        var itemSheet = _svc.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var itemRow = itemSheet?.GetRowOrDefault(itemId);
        if (itemRow is null)
            return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.ItemNotFound));

        var equipSlotCategoryId = itemRow.Value.EquipSlotCategory.RowId;
        var targetSlot = EquipSlotResolver.GetTargetSlot(equipSlotCategoryId);
        if (targetSlot is null)
            return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

        var slot = targetSlot.Value;

        if (slot == 11) // right ring
        {
            var rightRingItem = im->GetInventorySlot(InventoryType.EquippedItems, 11);
            if (rightRingItem is not null && rightRingItem->ItemId == itemId)
                slot = 12; // fall back to left ring
        }

        InventoryType srcContainer = default;
        ushort srcSlot = 0;
        var found = false;

        foreach (var containerType in SearchOrder)
        {
            var container = im->GetInventoryContainer(containerType);
            if (container is null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = im->GetInventorySlot(containerType, i);
                if (item is null) continue;
                if (item->ItemId != itemId) continue;

                srcContainer = containerType;
                srcSlot = (ushort)i;
                found = true;
                break;
            }

            if (found) break;
        }

        if (!found)
            return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.ItemNotFound));

        im->MoveItemSlot(srcContainer, srcSlot, InventoryType.EquippedItems, (ushort)slot, true);

        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Equipped));
    }

    public unsafe Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("inventoryManagerUnavailable", "InventoryManager.Instance() returned null"));

        for (var i = 0; i < 14; i++)
        {
            var item = im->GetInventorySlot(InventoryType.EquippedItems, i);
            if (item is not null && item->ItemId == itemId)
                return Task.FromResult<Result<bool>>(Result.Ok(true));
        }

        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }
}
