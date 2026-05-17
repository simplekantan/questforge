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

        // Rule 7: LastNpcInteracted changed
        if (after.LastNpcInteracted != before.LastNpcInteracted)
        {
            return new InferenceResult(
                StepType: "talk",
                SuggestedStepId: "talk-step-1",
                SuggestedExpect: null,
                Confidence: Confidence.Low,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: null);
        }

        // Rule 8: Nothing matched
        return InferenceResult.Empty;
    }
}
