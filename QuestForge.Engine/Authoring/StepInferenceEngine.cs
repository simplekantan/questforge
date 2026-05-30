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

        // Rule 2.1: Foreign quest accepted (mandatory sub-quest offered mid-sequence)
        if (after.ForeignQuestAccepted is { } foreignQuest
            && after.ForeignQuestAccepted != before.ForeignQuestAccepted)
        {
            var npcId = after.LastNpcInteracted ?? before.LastNpcInteracted;
            var suggestedId = npcId.HasValue
                ? $"accept-quest-{foreignQuest.Value}"
                : "accept-quest";
            return new InferenceResult(
                StepType: "accept",
                SuggestedStepId: suggestedId,
                SuggestedExpect: $"isQuestAccepted({foreignQuest.Value})",
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.QuestAccepted,
                Notes: $"Mandatory sub-quest {foreignQuest.Value} accepted. NPC: {npcId?.Value}");
        }

        // Rule 2.2: Target-correlated combat (span-scoped).
        // Fires when the span has at least one hostile target and one bumped nibble.
        // Must run BEFORE Rule 3 (sequence advance) so combat is not mis-emitted as a talk step.
        if (after.KillCorrelatedTargets is { Count: > 0 } targets
            && targets.Any(kv => kv.Value.DataIds.Count > 0))
        {
            var primary = targets
                .Where(kv => kv.Value.DataIds.Count > 0)
                .OrderByDescending(kv => kv.Value.FinalValue)
                .ThenBy(kv => kv.Key.VarIndex)
                .ThenBy(kv => kv.Key.Half)
                .First();

            var dataIds = primary.Value.DataIds.Distinct().OrderBy(x => x).ToArray();

            string fn = primary.Key.Half == NibbleHalf.Low ? "questVariableLow" : "questVariableHigh";
            string expect = $"{fn}({questIdValue}, {primary.Key.VarIndex}) >= {primary.Value.FinalValue}";

            bool multiTarget = dataIds.Length > 1;
            bool multiNibble = targets.Count(kv => kv.Value.DataIds.Count > 0) > 1;

            string notes = "Spawn defaulted to overworldEnemies; review. Location = player position at combat start.";
            if (multiTarget || multiNibble)
            {
                notes = $"Ambiguous record: span saw {dataIds.Length} hostile target(s) [{string.Join(",", dataIds)}] " +
                        $"and {targets.Count} nibble bump(s). One combat step per NPC type — re-record split. " + notes;
            }

            return new InferenceResult(
                StepType: "combat",
                SuggestedStepId: $"defeat-{dataIds[0]}",
                SuggestedExpect: expect,
                Confidence: multiTarget || multiNibble ? Confidence.Low : Confidence.Medium,
                InferredFrom: InferredFrom.Combat,
                Notes: notes);
        }

        // Rule 2.2b: Purchase detected (shop was open, item delta > 0, currency dropped)
        // Fires AFTER combat (Rule 2.2) and BEFORE key-item pickup (Rule 2.3) so that a
        // shop interaction is not mis-inferred as a talk or sequence step.
        if (after.PurchaseDetected is { } pd
            && pd.ShopWasOpen
            && pd.ItemDeltas.Count > 0
            && (pd.GilDropped > 0 || pd.SealsDropped > 0))
        {
            var primary = pd.ItemDeltas
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .First();

            string? notes = null;
            if (pd.ItemDeltas.Count > 1)
                notes = $"Multiple items increased while the shop was open [{string.Join(",", pd.ItemDeltas.Keys.OrderBy(x => x))}]. " +
                        $"Drafted purchase of item {primary.Key} (qty {primary.Value}); split if the others were separate purchases.";
            if (pd.GilDropped > 0 && pd.SealsDropped > 0)
                notes = (notes is null ? "" : notes + " ") +
                        $"Both gil ({pd.GilDropped}) and seals ({pd.SealsDropped}) dropped; confirm currency.";

            return new InferenceResult(
                StepType: "purchase-item",
                SuggestedStepId: $"buy-item-{primary.Key}",
                SuggestedExpect: $"playerHasItem({primary.Key},{primary.Value})",
                Confidence: (pd.ItemDeltas.Count > 1 || (pd.GilDropped > 0 && pd.SealsDropped > 0))
                    ? Confidence.Low : Confidence.Medium,
                InferredFrom: InferredFrom.Purchase,
                Notes: notes);
        }

        // Rule 2.3: Key item acquired
        if (after.KeyItemsAdded is { Count: > 0 } newItems)
        {
            var itemId = newItems[0];
            var expect = newItems.Count == 1
                ? $"playerHasItem({itemId})"
                : string.Join(" and ", newItems.Select(id => $"playerHasItem({id})"));
            return new InferenceResult(
                StepType: "pickup-item",
                SuggestedStepId: $"pickup-item-{itemId}",
                SuggestedExpect: expect,
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
            var expect = removedItems.Count == 1
                ? $"not(playerHasItem({itemId}))"
                : string.Join(" and ", removedItems.Select(id => $"not(playerHasItem({id}))"));
            return new InferenceResult(
                StepType: "hand-over-item",
                SuggestedStepId: $"hand-over-item-{itemId}",
                SuggestedExpect: expect,
                Confidence: Confidence.Medium,
                InferredFrom: InferredFrom.DialogueInteraction,
                Notes: removedItems.Count > 1
                    ? $"Multiple key items removed: {string.Join(", ", removedItems)}. Step ID uses first item ({itemId})."
                    : null);
        }

        // Rule 2.5: Aetheryte targeted in same zone → attune step
        // WHY: tracking LastAttuned only fires for newly-attuned crystals. Re-authoring a quest
        // where aetherytes are already attuned would never trigger. Using LastAethernetShardInteracted
        // fires whenever the author targets the crystal during recording, regardless of prior
        // attunement state. The LastNpcInteracted guard ensures the author is physically AT the
        // crystal (not just viewing it in an aethernet menu from another shard). Zone-change
        // cases are handled by Rule 4. AethernetDestinationSelected guard: if the author used
        // the TelepotTown menu this window, the shard change is an intra-zone aethernet hop
        // (Rule 2.7), not genuine attunement.
        if (after.LastAethernetShardInteracted.HasValue
            && after.LastAethernetShardInteracted != before.LastAethernetShardInteracted
            && after.Zone == before.Zone
            && after.LastNpcInteracted.HasValue
            && after.LastNpcInteracted.Value.Value == after.LastAethernetShardInteracted.Value.Value
            && after.AethernetTeleportCompleted is null)
        {
            var aetheryteId = after.LastAethernetShardInteracted.Value.Value;
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

        // Rule 3.5s — SayChatMessageSent
        // Fires when UIObserver.PollPlayerChatMessage detected that the player typed /say <message>
        // during this recording window (RaptureLogModule observed a new log entry matching
        // ChatLogEntryFilter.IsPlayerSayMessage).
        //
        // PRIORITY: above Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted). Chat input is
        // always intentional; emotes and actions can be incidental. Defensive ordering: chat > emote > action.
        // PRIORITY: above Rule 3 (QuestSequence advanced) — say-chat-message is more specific.
        // PRIORITY: below Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6.
        //
        // CONFIDENCE: High. EXPECT: null — author MUST write the postcondition (Decision SC7).
        if (after.SayChatMessageSent is { } saySignal)
        {
            var stepIdSuffix = saySignal.TargetBaseId is { } tid
                ? $"on-{tid}"
                : "broadcast";
            return new InferenceResult(
                StepType:        "say-chat-message",
                SuggestedStepId: $"say-chat-message-{stepIdSuffix}",
                SuggestedExpect: null,
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.SayChatMessageSent,
                Notes:           $"Author MUST write the Expect predicate (no universal say-chat-message postcondition). Message=\"{Truncate(saySignal.Message, 40)}\"");
        }

        // Rule 3.5i — ItemUsed
        // Fires when UIObserver.PollPlayerActionEffect detected that the player used an item
        // (ActionType.Item=2 or ActionType.EventItem=3) during this recording window.
        //
        // PRIORITY: above Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted).
        // Item use is more specific than emote/action when both fire in the same window.
        // PRIORITY: above Rule 3 (QuestSequence advanced) — use-item is more specific than
        // the catch-all sequence-advance which defaults to "talk".
        // PRIORITY: below Rule 2.4 (KeyItemsRemoved) — when a key item is used AND consumed,
        // the removal signal fires hand-over-item instead (natural disambiguation).
        // PRIORITY: below Rule 3.5s (SayChatMessageSent) — chat is always intentional.
        // CONFIDENCE: High — the player demonstrably used the item (ActionEffect received).
        // EXPECT: null — author MUST write the postcondition (no universal item postcondition).
        if (after.ItemUsed is { } itemSignal)
        {
            var stepIdSuffix = itemSignal.TargetBaseId is { } tid
                ? $"{itemSignal.ItemId}-on-{tid}"
                : $"{itemSignal.ItemId}";
            return new InferenceResult(
                StepType:        "use-item",
                SuggestedStepId: $"use-item-{stepIdSuffix}",
                SuggestedExpect: null,
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.ItemUsed,
                Notes:           $"Author MUST write the Expect predicate (no universal item postcondition). Kind={itemSignal.Kind}, ItemId={itemSignal.ItemId}");
        }

        // Rule 3.5g — EquipmentChanged
        // Fires when UIObserver.PollEquipmentChange detected that the player's equipment changed
        // (one or more slots in InventoryType.EquippedItems changed item IDs) during this recording
        // window.
        //
        // PRIORITY: above Rule 3.5e (EmoteCompleted) — equipment changes are more deliberate and
        // more specific than emotes. Below Rule 3.5i (ItemUsed) — when an item is used AND equipment
        // changes (e.g. the item IS the equipment), ItemUsed is the more specific signal.
        // PRIORITY: above Rule 3 (QuestSequence advanced) — equip-gear-for-quest is more specific.
        // CONFIDENCE: High — the equipment state measurably changed.
        // EXPECT: null — implicit postcondition (IsItemEquipped) handles self-confirmation.
        if (after.EquipmentChanged is { } equipSignal)
        {
            return new InferenceResult(
                StepType:        "equip-gear-for-quest",
                SuggestedStepId: $"equip-gear-{string.Join("-", equipSignal.NewItemIds.Take(3))}",
                SuggestedExpect: null,
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.EquipmentChanged,
                Notes:           $"Equipment changed: {equipSignal.NewItemIds.Count} item(s) equipped.");
        }

        // Rule 3.5e — EmoteCompleted
        // Fires when UIObserver.PollPlayerEmote detected that the player started an emote during
        // this recording window (LocalPlayer.EmoteController.EmoteId transitioned to a new non-zero id).
        //
        // PRIORITY: above Rule 3.5 (ActionCompleted). Emote and action signals are mutually exclusive
        // in practice (different game code paths), but if both fire in the same window the emote is
        // the more deliberate authoring intent.
        // PRIORITY: above Rule 3 (QuestSequence advanced) — use-emote is more specific than the
        // catch-all sequence-advance which defaults to "talk".
        // PRIORITY: below Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6.
        // CONFIDENCE: High. EXPECT: null — author MUST write the postcondition (Decision UE4).
        if (after.EmoteCompleted is { } emoteSignal)
        {
            var stepIdSuffix = emoteSignal.TargetBaseId is { } tid
                ? $"{emoteSignal.EmoteId}-on-{tid}"
                : $"{emoteSignal.EmoteId}";
            return new InferenceResult(
                StepType:        "use-emote",
                SuggestedStepId: $"use-emote-{stepIdSuffix}",
                SuggestedExpect: null,
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.EmoteCompleted,
                Notes:           $"Author MUST write the Expect predicate (no universal emote postcondition). EmoteId={emoteSignal.EmoteId}");
        }

        // Rule 3.5 — ActionCompleted
        // Fires when UIObserver.PollPlayerActionEffect detected that the player used a supported
        // game action (Action / GeneralAction / KeyItem) during this recording window.
        // PRIORITY: above Rule 3 (QuestSequence advanced) — use-action is more specific than
        // the catch-all sequence-advance which defaults to "talk". Above Rule 4.0 (TeleportCompleted)
        // because ActionCompleted is the more specific signal when both are present.
        // CONFIDENCE: High — the player demonstrably pressed the button.
        // EXPECT: null — author MUST write the postcondition (Decision UA4).
        if (after.ActionCompleted is { } actionSignal)
        {
            var stepIdSuffix = actionSignal.TargetBaseId is { } tid
                ? $"{actionSignal.ActionId}-on-{tid}"
                : $"{actionSignal.ActionId}";
            return new InferenceResult(
                StepType:        "use-action",
                SuggestedStepId: $"use-action-{stepIdSuffix}",
                SuggestedExpect: null,
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.ActionCompleted,
                Notes:           "Author MUST write the Expect predicate (no universal action postcondition).");
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

        // Rule 4.0: Teleport completed — cross-region teleport via the Teleport general action menu.
        // Fires before Rule 4 (aethernet/dialog/catch-all) because TeleportCompleted is the definitive
        // cross-region signal (specific AetheryteId + confirmed zone change). Even if both
        // TeleportCompleted and AethernetTeleportCompleted are somehow set, teleport wins.
        // Zone-change guard: if Zone unchanged (stale signal), fall through to Rule 9.
        if (after.TeleportCompleted is { } teleAetheryteId
            && after.Zone != before.Zone)
        {
            return new InferenceResult(
                StepType:        "teleport",
                SuggestedStepId: $"teleport-to-{teleAetheryteId.Value}",
                SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                Confidence:      Confidence.High,
                InferredFrom:    InferredFrom.TeleportCompleted,
                Notes:           null);
        }

        // Rule 4: Zone changed (no quest change from rules above)
        if (after.Zone != before.Zone)
        {
            // AethernetTeleportCompleted is the definitive aethernet signal — check FIRST.
            // WHY before NpcDialogue: interacting with a main aetheryte crystal can briefly open
            // a SelectString menu ("Teleport/Return to inn/Housing") that sets DialogueOptionSelected
            // to a spurious value. AethernetTeleportCompleted must always win over that noise.
            if (after.AethernetTeleportCompleted is { } hopInterZone)
            {
                var fromId = hopInterZone.From?.Value; // uint?
                var toId   = hopInterZone.To.Value;
                return new InferenceResult(
                    StepType: "travel",
                    SuggestedStepId: $"aethernet-to-zone-{after.Zone.Value}",
                    SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                    Confidence: Confidence.High,
                    InferredFrom: InferredFrom.ZoneChange,
                    Notes: fromId.HasValue
                        ? $"Aethernet: shard {fromId.Value} → shard {toId}"
                        : $"Aethernet to shard {toId}");
            }

            // NpcDialogue sub-case: zone changed via SelectIconString list choice or SelectYesno.
            // DialogueNpcSource is captured from live TargetManager when the dialog opens,
            // so no pre-targeting is required from the author.
            if ((after.DialogueOptionSelected.HasValue || after.SelectYesnoConfirmed) && after.DialogueNpcSource != null)
            {
                var npcId = after.DialogueNpcSource.NpcId;
                var choiceIdx = after.DialogueOptionSelected;
                return new InferenceResult(
                    StepType: "travel",
                    SuggestedStepId: $"npc-dialogue-to-zone-{after.Zone.Value}",
                    SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                    Confidence: Confidence.High,
                    InferredFrom: InferredFrom.ZoneChange,
                    Notes: after.SelectYesnoConfirmed
                        ? $"NpcDialogue: npc {npcId} → yesno → zone {after.Zone.Value}"
                        : $"NpcDialogue: npc {npcId} → choice {choiceIdx} → zone {after.Zone.Value}");
            }

            // Sub-case: aethernet teleport detected
            var sourceShard = before.LastAethernetShardInteracted;
            var isAethernet = sourceShard.HasValue
                && before.LastNpcInteracted.HasValue
                && before.LastNpcInteracted.Value.Value == sourceShard.Value.Value;

            if (isAethernet)
            {
                var sourceShardId = sourceShard!.Value.Value;

                // Prefer before.AethernetDestinationSelected (explicit menu selection) over
                // after.LastAethernetShardInteracted (post-arrival observation).
                // Only use after.LastAethernetShardInteracted as fallback when it differs from source
                // (same shard = ambiguous, cannot confirm destination).
                var hop = after.AethernetTeleportCompleted;
                var afterShard = after.LastAethernetShardInteracted;
                var afterShardDiffers = afterShard.HasValue && afterShard.Value.Value != sourceShardId;
                var destId = hop?.To.Value
                    ?? before.AethernetDestinationSelected?.Value
                    ?? (afterShardDiffers ? afterShard!.Value.Value : (uint?)null);
                var fromId = hop?.From?.Value ?? sourceShardId;

                return new InferenceResult(
                    StepType: "travel",
                    SuggestedStepId: $"aethernet-to-zone-{after.Zone.Value}",
                    SuggestedExpect: $"playerZone() == {after.Zone.Value}",
                    Confidence: destId.HasValue ? Confidence.High : Confidence.Medium,
                    InferredFrom: InferredFrom.ZoneChange,
                    Notes: destId.HasValue
                        ? $"Aethernet: shard {fromId} → shard {destId.Value}"
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

        // Rule 2.7: Intra-zone aethernet — fires when AethernetTeleportCompleted is set
        // (TelepotTown menu closed after a selection) AND zone did not change.
        // WHY placed here (before Rules 5/6/7): NpcInteracted changes naturally when the player
        // arrives near the destination shard; if Rule 7 ran first it would produce a false talk-step.
        // The user's insight: check NpcType=Aetheryte + position changed + event set as a combined signal.
        if (after.AethernetTeleportCompleted is { } hop2 && before.Zone == after.Zone)
        {
            var hop = hop2;
            return new InferenceResult(
                StepType: "travel",
                SuggestedStepId: $"aethernet-intra-zone-{hop.To.Value}",
                SuggestedExpect: null,
                Confidence: Confidence.High,
                InferredFrom: InferredFrom.ZoneChange,
                Notes: hop.From.HasValue
                    ? $"Aethernet: shard {hop.From.Value.Value} → shard {hop.To.Value}"
                    : $"Aethernet to shard {hop.To.Value} (departure shard not captured)");
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

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s.Substring(0, n) + "...";
}
