namespace QuestForge.Engine.Rewards;

using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

public static class RewardPrioritizer
{
    public static readonly IReadOnlyList<RewardPriority> DefaultPriorityOrder = new[]
    {
        RewardPriority.BiggestUpgrade,
        RewardPriority.HighestGilValue,
        RewardPriority.GearCoffer,
        RewardPriority.GilSack,
        RewardPriority.EquippableGear,
        RewardPriority.UnequippableGear,
        RewardPriority.AnythingElse
    };

    public static async Task<int?> SelectBestReward(
        IReadOnlyList<QuestReward> rewards,
        IReadOnlyList<RewardPriority> priorityOrder,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct)
    {
        if (rewards.Count == 0) return null;

        foreach (var tier in priorityOrder)
        {
            var winner = await EvaluateTier(tier, rewards, currentJob, getEquippedIlvlForSlot, ct);
            if (winner is not null)
                return winner;
        }

        return null;
    }

    public static async Task<int?> ApplyOverride(
        IReadOnlyList<QuestReward> rewards,
        RewardOverride rewardOverride,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct)
    {
        var strategy = rewardOverride.Strategy;

        if (string.Equals(strategy, "specificItem", StringComparison.OrdinalIgnoreCase))
        {
            if (rewardOverride.ItemId is null) return null;
            var itemId = rewardOverride.ItemId.Value;
            var match = rewards.FirstOrDefault(r => r.Item.Value == itemId);
            return match is not null ? match.Index : null;
        }

        if (!Enum.TryParse<RewardPriority>(strategy, ignoreCase: true, out var tier))
            return null;

        return await EvaluateTier(tier, rewards, currentJob, getEquippedIlvlForSlot, ct);
    }

    private static async Task<int?> EvaluateTier(
        RewardPriority tier,
        IReadOnlyList<QuestReward> rewards,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct)
    {
        switch (tier)
        {
            case RewardPriority.BiggestUpgrade:
                return await EvaluateBiggestUpgrade(rewards, currentJob, getEquippedIlvlForSlot, ct);

            case RewardPriority.HighestGilValue:
            {
                var best = rewards
                    .Select(r => (r.Index, Value: r.VendorPrice * r.Quantity))
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Index)
                    .FirstOrDefault();
                return rewards.Count > 0 ? best.Index : null;
            }

            case RewardPriority.GearCoffer:
            {
                var match = rewards
                    .OrderBy(r => r.Index)
                    .FirstOrDefault(r => CofferIdentifier.IsCoffer(r.ItemActionId, r.ItemUiCategoryId));
                return match?.Index;
            }

            case RewardPriority.GilSack:
            {
                var match = rewards
                    .OrderBy(r => r.Index)
                    .FirstOrDefault(r => r.EquipSlotCategory == 0 && r.VendorPrice > 0 && !r.IsUntradable);
                return match?.Index;
            }

            case RewardPriority.EquippableGear:
            {
                var match = rewards
                    .OrderBy(r => r.Index)
                    .FirstOrDefault(r => r.EquipSlotCategory != 0 && IsEquippableByJob(r, currentJob));
                return match?.Index;
            }

            case RewardPriority.UnequippableGear:
            {
                var match = rewards
                    .OrderBy(r => r.Index)
                    .FirstOrDefault(r => r.EquipSlotCategory != 0 && !IsEquippableByJob(r, currentJob));
                return match?.Index;
            }

            case RewardPriority.AnythingElse:
                return rewards.MinBy(r => r.Index)?.Index;

            default:
                return null;
        }
    }

    private static async Task<int?> EvaluateBiggestUpgrade(
        IReadOnlyList<QuestReward> rewards,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct)
    {
        int? bestIndex = null;
        int bestDelta = 0;

        foreach (var reward in rewards.OrderBy(r => r.Index))
        {
            if (reward.EquipSlotCategory == 0) continue;
            if (!IsEquippableByJob(reward, currentJob)) continue;

            var slot = EquipSlotResolver.GetTargetSlot(reward.EquipSlotCategory);
            if (slot is null) continue;

            var ilvlResult = await getEquippedIlvlForSlot(slot.Value);
            if (ilvlResult is not Result<int>.Success { Value: var equippedIlvl }) continue;

            var delta = reward.ItemLevel - equippedIlvl;
            if (delta <= 0) continue;

            if (delta > bestDelta || (delta == bestDelta && (bestIndex is null || reward.Index < bestIndex)))
            {
                bestDelta = delta;
                bestIndex = reward.Index;
            }
        }

        return bestIndex;
    }

    private static bool IsEquippableByJob(QuestReward reward, JobId currentJob)
        => reward.RestrictedJobs.Count == 0 || reward.RestrictedJobs.Contains(currentJob);
}
