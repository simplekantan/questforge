using System.Numerics;

namespace QuestForge.Engine.Authoring;

public sealed class StepInferenceEngine
{
    /// <summary>
    /// Compare two snapshots and produce a suggested DraftStep template.
    /// The 'after' snapshot must have CapturedAt >= 'before'.
    /// </summary>
    public InferenceResult Infer(GameStateSnapshot before, GameStateSnapshot after)
    {
        // Quest ID for predicates: use before.ActiveQuest ?? after.ActiveQuest
        var questId = before.ActiveQuest ?? after.ActiveQuest;
        var questIdValue = questId?.Value ?? 0u;

        // Rule 1: QuestCompleted
        if (after.QuestCompleted && !before.QuestCompleted)
        {
            return new InferenceResult(
                StepType: "turn-in",
                SuggestedStepId: "turn-in-quest",
                SuggestedExpect: $"isQuestComplete({questIdValue})",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.QuestCompleted,
                Notes: null);
        }

        // Rule 2: QuestAccepted
        if (after.QuestAccepted && !before.QuestAccepted)
        {
            return new InferenceResult(
                StepType: "accept",
                SuggestedStepId: "accept-quest",
                SuggestedExpect: $"isQuestAccepted({questIdValue})",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.QuestAccepted,
                Notes: null);
        }

        // Rule 2.3: Key item acquired
        if (after.KeyItemsAdded is { Count: > 0 } newItems)
        {
            var itemId = newItems[0];
            return new InferenceResult(
                StepType: "pickup-item",
                SuggestedStepId: $"pickup-item-{itemId}",
                SuggestedExpect: null,
                Confidence: Confidence.Medium,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: newItems.Count > 1
                    ? $"Multiple key items acquired: {string.Join(", ", newItems)}. Step ID uses first item ({itemId})."
                    : null);
        }

        // Rule 2.4: Key item handed over (removed from KeyItems inventory)
        if (after.KeyItemsRemoved is { Count: > 0 } removedItems)
        {
            var itemId = removedItems[0];
            return new InferenceResult(
                StepType: "hand-over-item",
                SuggestedStepId: $"hand-over-item-{itemId}",
                SuggestedExpect: null,
                Confidence: Confidence.Medium,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: removedItems.Count > 1
                    ? $"Multiple key items removed: {string.Join(", ", removedItems)}. Step ID uses first item ({itemId})."
                    : null);
        }

        // Rule 2.5: Attunement changed (no higher-priority signal)
        if (after.LastAttuned != before.LastAttuned && after.LastAttuned.HasValue)
        {
            var aetheryteId = after.LastAttuned.Value.Value;
            return new InferenceResult(
                StepType: "attune",
                SuggestedStepId: $"attune-aetheryte-{aetheryteId}",
                SuggestedExpect: $"isAttuned({aetheryteId})",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.AttunementChange,
                Notes: null);
        }

        // Rule 2.6: Inventory hash diff (authoring mode — full map available)
        if (after.InventoryHash != before.InventoryHash
            && before.InventoryHash != 0u
            && after.InventoryHash != 0u
            && before.KeyItems is not null
            && after.KeyItems is not null)
        {
            // Diff before vs after KeyItems
            var gained = new List<(uint ItemId, int Qty)>();
            var lost = new List<(uint ItemId, int Qty)>();

            foreach (var kvp in after.KeyItems)
            {
                if (before.KeyItems.TryGetValue(kvp.Key, out var prevQty))
                {
                    if (kvp.Value > prevQty)
                        gained.Add((kvp.Key, kvp.Value - prevQty));
                }
                else
                {
                    gained.Add((kvp.Key, kvp.Value));
                }
            }

            foreach (var kvp in before.KeyItems)
            {
                if (after.KeyItems.TryGetValue(kvp.Key, out var newQty))
                {
                    if (newQty < kvp.Value)
                        lost.Add((kvp.Key, kvp.Value - newQty));
                }
                else
                {
                    lost.Add((kvp.Key, kvp.Value));
                }
            }

            if (gained.Count == 0 && lost.Count == 0)
            {
                // hash collision or caller error: hashes differed but maps are equal — fall through
            }
            else
            {
                string stepType;
                string stepId;
                string expect;
                string? notes = null;

                if (gained.Count > 0 && lost.Count == 0)
                {
                    stepType = "pickup-item";
                    stepId   = $"pickup-item-{gained[0].ItemId}";
                    expect   = $"playerHasItem({gained[0].ItemId})";
                }
                else if (lost.Count > 0 && gained.Count == 0)
                {
                    stepType = "hand-over-item";
                    stepId   = $"hand-over-item-{lost[0].ItemId}";
                    expect   = $"not(playerHasItem({lost[0].ItemId}))";
                }
                else
                {
                    stepType = "talk";
                    stepId   = $"exchange-{lost[0].ItemId}-for-{gained[0].ItemId}";
                    expect   = string.Join(" and ",
                        gained.Select(g => $"playerHasItem({g.ItemId})")
                        .Concat(lost.Select(l => $"not(playerHasItem({l.ItemId}))")));
                }

                if (gained.Count + lost.Count > 1)
                    notes = $"Inventory changed: gained {gained.Count} item(s) [{string.Join(",", gained.Select(g => g.ItemId))}], lost {lost.Count} item(s) [{string.Join(",", lost.Select(l => l.ItemId))}].";

                return new InferenceResult(stepType, stepId, expect, Confidence.Medium, InferredFrom.InventoryChange, notes);
            }
        }

        // Rule 3: QuestSequence advanced
        if (after.QuestSequence > before.QuestSequence)
        {
            var npcId = after.LastNpcInteracted ?? before.LastNpcInteracted;
            var suggestedStepId = npcId.HasValue
                ? $"talk-to-npc-{npcId.Value.Value}"
                : "talk-step-1";

            string? notes = null;
            if (after.Zone != before.Zone)
            {
                notes = $"Zone also changed from {before.Zone.Value} to {after.Zone.Value}. Consider inserting a separate travel step before this one.";
            }

            return new InferenceResult(
                StepType: "talk",
                SuggestedStepId: suggestedStepId,
                SuggestedExpect: $"questSequence({questIdValue}) >= {after.QuestSequence}",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.QuestSequenceChange,
                Notes: notes);
        }

        // Rule 4: Zone changed (no quest change from rules above)
        if (after.Zone != before.Zone)
        {
            // Sub-case: aethernet teleport detected
            var sourceShard = before.LastAethernetShardInteracted;
            var isAethernet = sourceShard.HasValue
                && before.LastNpcInteracted.HasValue
                && before.LastNpcInteracted.Value.Value == sourceShard.Value.Value;

            if (isAethernet)
            {
                var sourceShardId = sourceShard!.Value.Value;
                var destShard = after.LastAethernetShardInteracted;
                var destDiffers = destShard.HasValue && destShard.Value.Value != sourceShardId;

                return new InferenceResult(
                    StepType: "travel",
                    SuggestedStepId: $"aethernet-to-zone-{after.Zone.Value}",
                    SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                    Confidence: destDiffers ? Confidence.High : Confidence.Medium,
                    InferredFrom: InferredFrom.ZoneChange,
                    Notes: destDiffers
                        ? $"Aethernet: shard {sourceShardId} → shard {destShard!.Value.Value}"
                        : $"Aethernet from shard {sourceShardId} detected. Target the destination shard in zone {after.Zone.Value} after arrival to capture its ID.");
            }

            // Catch-all: regular zone change
            return new InferenceResult(
                StepType: "travel",
                SuggestedStepId: $"travel-to-zone-{after.Zone.Value}",
                SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.ZoneChange,
                Notes: null);
        }

        // Rule 5: QuestFlags changed, sequence unchanged
        if (after.QuestFlags != before.QuestFlags && after.QuestSequence == before.QuestSequence)
        {
            var xorFlags = before.QuestFlags ^ after.QuestFlags;
            var lowestBit = BitOperations.TrailingZeroCount(xorFlags);

            string? notes = null;
            var flippedBits = new List<int>();
            var temp = xorFlags;
            while (temp != 0)
            {
                flippedBits.Add(BitOperations.TrailingZeroCount(temp));
                temp &= temp - 1; // WHY: n & (n-1) clears the lowest set bit — standard bit iteration trick
            }

            if (flippedBits.Count > 1)
            {
                notes = $"Multiple flag bits changed: bits {string.Join(", ", flippedBits)}. Suggested predicate uses bit {lowestBit} (lowest).";
            }

            return new InferenceResult(
                StepType: "talk",
                SuggestedStepId: "talk-step-1",
                SuggestedExpect: $"questFlag({questIdValue}, {lowestBit})",
                Confidence: Confidence.Medium,
                InferredFrom: InferredFrom.QuestFlagChange,
                Notes: notes);
        }

        // Rule 6: LastDialogueAnswer changed
        if (after.LastDialogueAnswer != before.LastDialogueAnswer)
        {
            return new InferenceResult(
                StepType: "talk",
                SuggestedStepId: "talk-step-1",
                SuggestedExpect: $"questSequence({questIdValue}) >= {after.QuestSequence}",
                Confidence: Confidence.Medium,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: null);
        }

        // Rule 7: LastNpcInteracted changed (only fires when after has a non-null NPC —
        // the aggregator never clears LastNpcInteracted, so null in after means no NPC was
        // targeted in that window, not that the NPC "went away").
        if (after.LastNpcInteracted.HasValue && after.LastNpcInteracted != before.LastNpcInteracted)
        {
            return new InferenceResult(
                StepType: "talk",
                SuggestedStepId: "talk-step-1",
                SuggestedExpect: null,
                Confidence: Confidence.Low,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: null);
        }

        // Rule 8: Player moved significantly within the same zone → intra-zone travel
        if (before.Zone == after.Zone && before.Zone.Value > 0)
        {
            var distMoved = before.Position.DistanceTo(after.Position);
            if (distMoved > 5f)
            {
                return new InferenceResult(
                    StepType: "travel",
                    SuggestedStepId: $"travel-to-{after.Position.X:F0}-{after.Position.Z:F0}",
                    SuggestedExpect: null,
                    Confidence: Confidence.Low,
                    InferredFrom: InferredFrom.MovementChange,
                    Notes: $"Player moved {distMoved:F1} units within zone {after.Zone.Value}.");
            }
        }

        // Rule 9: Nothing matched
        return InferenceResult.Empty;
    }
}
