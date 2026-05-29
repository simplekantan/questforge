using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

/// <summary>
/// Mandatory gate: every Step subtype must survive a serialization round-trip.
/// A missing [JsonSerializable] registration causes silent failure at runtime —
/// catching it here prevents discovering it mid-validator-development.
/// </summary>
public class RoundTripTests
{
    private static T RoundTrip<T>(T value) where T : Step
    {
        var json = JsonSerializer.Serialize<Step>(value, QuestForgeJsonContext.QuestFileOptions);
        var result = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        return Assert.IsType<T>(result);
    }

    [Fact]
    public void TravelStep_RoundTrips()
    {
        var step = new TravelStep
        {
            Id = "go-somewhere",
            Destination = new TravelDestination(130, new Position3(10f, 0f, 20f)),
            Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
        };

        var result = RoundTrip(step);

        Assert.Equal("go-somewhere", result.Id);
        Assert.Equal(130, result.Destination.Zone);
        Assert.IsType<PredicateExpect>(result.Expect);
    }

    [Fact]
    public void TalkStep_RoundTrips()
    {
        var step = new TalkStep
        {
            Id = "talk-to-npc",
            Target = new NpcLocation(1000789, 129, new Position3(1f, 2f, 3f)),
            DialogueChoices = [new DialogueChoice("yesno", "Confirm?", "yes")],
            Expect = new PredicateExpect { Predicate = "questSequence(65) >= 1" }
        };

        var result = RoundTrip(step);

        Assert.Equal("talk-to-npc", result.Id);
        Assert.NotNull(result.Target);
        Assert.Single(result.DialogueChoices);
        Assert.Equal("yesno", result.DialogueChoices[0].Type);
    }

    [Fact]
    public void TalkStep_MultiTarget_RoundTrips()
    {
        var step = new TalkStep
        {
            Id = "talk-multi",
            Targets = [new NpcLocation(1, 130, new Position3(0, 0, 0)), new NpcLocation(2, 130, new Position3(5, 0, 5))],
            TargetOrder = "any",
            Expect = new AllExpect { All = ["questFlag(65, 1)", "questFlag(65, 2)"] }
        };

        var result = RoundTrip(step);

        Assert.Equal(2, result.Targets!.Length);
        Assert.Equal("any", result.TargetOrder);
        Assert.IsType<AllExpect>(result.Expect);
    }

    [Fact]
    public void InteractObjectStep_RoundTrips()
    {
        var step = new InteractObjectStep
        {
            Id = "ring-bell",
            Target = new InteractableTarget(2001500, 134, new Position3(81f, 7f, 32f)),
            Expect = new PredicateExpect { Predicate = "questFlag(65, 5)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("ring-bell", result.Id);
        Assert.Equal(2001500u, result.Target.InteractableId);
    }

    [Fact]
    public void PickupItemStep_RoundTrips()
    {
        var step = new PickupItemStep
        {
            Id = "pick-up-letter",
            Target = new InteractableTarget(2001234, 134, new Position3(81f, 7f, 32f)),
            Expect = new PredicateExpect { Predicate = "questFlag(65, 3)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("pick-up-letter", result.Id);
    }

    [Fact]
    public void AcceptStep_RoundTrips()
    {
        var step = new AcceptStep
        {
            Id = "accept-quest",
            Target = new NpcLocation(1000789, 128, new Position3(9f, 40f, 14f)),
            Expect = new PredicateExpect { Predicate = "isQuestAccepted(65657)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(1000789u, result.Target.NpcId);
    }

    [Fact]
    public void TurnInStep_RoundTrips()
    {
        var step = new TurnInStep
        {
            Id = "turn-in",
            Target = new NpcLocation(1000790, 129, new Position3(-10f, 40f, 14f)),
            Expect = new PredicateExpect { Predicate = "isQuestComplete(65657)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("turn-in", result.Id);
    }

    [Fact]
    public void CombatStep_RoundTrips()
    {
        // CombatTarget retired in part A. CombatStep now uses KillEnemyDataIds + Spawn + Location.
        var step = new CombatStep
        {
            Id = "fight-bandits",
            KillEnemyDataIds = [100u, 200u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66104, 0) >= 3" }
        };

        var result = RoundTrip(step);
        Assert.Equal("fight-bandits", result.Id);
        Assert.Equal(2, result.KillEnemyDataIds.Length);
        Assert.Equal(CombatSpawn.AutoOnEnterArea, result.Spawn);
    }

    [Fact]
    public void DutyStep_Regular_RoundTrips()
    {
        var step = new DutyStep
        {
            Id = "do-dungeon",
            Kind = "regular",
            DutyId = 56,
            EntryNpc = new NpcLocation(1014883, 419, new Position3(6f, -1f, 47f)),
            Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 5" }
        };

        var result = RoundTrip(step);
        Assert.Equal("regular", result.Kind);
        Assert.Equal(56u, result.DutyId);
    }

    [Fact]
    public void DutyStep_Spd_RoundTrips()
    {
        var step = new DutyStep
        {
            Id = "quest-battle",
            Kind = "spd",
            Trigger = new DutyTrigger("object", 134, new Position3(81f, 7f, 32f), InteractableId: 2001234),
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("spd", result.Kind);
        Assert.NotNull(result.Trigger);
    }

    [Fact]
    public void CutsceneStep_RoundTrips()
    {
        var step = new CutsceneStep
        {
            Id = "watch-cutscene",
            Skip = "ifAllowed",
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 4" }
        };

        var result = RoundTrip(step);
        Assert.Equal("ifAllowed", result.Skip);
    }

    /// <summary>
    /// B7 — Schema round-trip: discriminator assertion.
    /// Verifies that the JSON discriminator "type":"cutscene" is present in the serialized output.
    /// This locks the [JsonDerivedType(typeof(CutsceneStep), "cutscene")] registration.
    /// </summary>
    [Fact]
    public void CutsceneStep_SerializedJson_ContainsCutsceneDiscriminator()
    {
        /*
         * CONTRACT: Given a CutsceneStep with Id, Skip, and Expect populated,
         *           When serialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then the JSON contains the literal string "type":"cutscene"
         *                (discriminator present and correct).
         *
         * This test is expected to PASS immediately (schema registration already exists).
         * It is authored here to lock the behavior against future regressions.
         */

        // Arrange
        var step = new CutsceneStep
        {
            Id = "watch-intro",
            Skip = "never",
            Expect = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(
            step, QuestForgeJsonContext.QuestFileOptions);

        // Assert — discriminator must be present with the exact registered value.
        // QuestFileOptions uses WriteIndented=true, so the JSON contains spaces around
        // the colon: `"type": "cutscene"`. Strip whitespace before checking to be
        // robust against indentation changes while still asserting the correct token.
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"cutscene\"", compactJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SayChatMessageStep_RoundTrips()
    {
        var step = new SayChatMessageStep
        {
            Id = "shout-help",
            Channel = "say",
            Message = "TEXT_EXAMPLE_000_001",
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 2)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("say", result.Channel);
    }

    // UE10 — round-trip with TargetNpcId set and motion=true (default)
    // TODO: UseEmoteStep must be rewritten to { EmoteId, TargetNpcId: uint?, Motion: bool = true }
    //       The old Target: NpcLocation? field is removed (Decision UE3).
    [Fact]
    public void UseEmoteStep_MotionTrue_WithTarget_RoundTrips_UE10()
    {
        var step = new UseEmoteStep
        {
            Id = "salute",
            EmoteId = 7,
            TargetNpcId = 1000789u,  // TODO: new field — replaces Target: NpcLocation?
            Motion = true,            // TODO: new field
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(7u, result.EmoteId);
        Assert.Equal(1000789u, result.TargetNpcId);
        Assert.True(result.Motion);
    }

    // UE11 — round-trip with motion=false explicit and no target
    // TODO: UseEmoteStep.Motion must round-trip as false when set explicitly.
    [Fact]
    public void UseEmoteStep_MotionFalse_NoTarget_RoundTrips_UE11()
    {
        var step = new UseEmoteStep
        {
            Id = "yell",
            EmoteId = 17,
            TargetNpcId = null,  // TODO: new field
            Motion = false,       // TODO: new field — explicit false
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 3" }
        };

        var result = RoundTrip(step);
        Assert.Equal(17u, result.EmoteId);
        Assert.Null(result.TargetNpcId);
        Assert.False(result.Motion);
    }

    // UE12 — Motion defaults to true when field absent from JSON
    // Pins Decision UE2: authors omitting "motion" get motion=true (broadcast suppressed).
    // TODO: UseEmoteStep.Motion must default to true on deserialization when absent.
    [Fact]
    public void UseEmoteStep_MotionAbsentInJson_DefaultsToTrue_UE12()
    {
        const string json = """
            {
              "type": "use-emote",
              "id": "celebrate",
              "emoteId": 17,
              "expect": "questSequence(65657) >= 3"
            }
            """;

        var result = Assert.IsType<UseEmoteStep>(
            JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(17u, result.EmoteId);
        Assert.Null(result.TargetNpcId);
        Assert.True(result.Motion);
    }

    [Fact]
    public void UseItemStep_NoTarget_RoundTrips()
    {
        var step = new UseItemStep
        {
            Id = "use-bell",
            ItemId = 12345,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 4)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(12345u, result.ItemId);
        Assert.Null(result.Target);
    }

    [Fact]
    public void UseItemStep_WithTarget_RoundTrips()
    {
        var step = new UseItemStep
        {
            Id = "use-letter",
            ItemId = 12347,
            Target = new UseItemTarget { Kind = "npc", NpcId = 1000789, Zone = 130 },
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 3" }
        };

        var result = RoundTrip(step);
        Assert.Equal("npc", result.Target!.Kind);
    }

    [Fact]
    public void UseActionStep_RoundTrips()
    {
        var step = new UseActionStep
        {
            Id = "axe-the-rock",
            ActionType = ActionType.Action,
            ActionId = 31,
            TargetNpcId = 2001234u,
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(ActionType.Action, result.ActionType);
        Assert.Equal(31u, result.ActionId);
        Assert.Equal(2001234u, result.TargetNpcId);
    }

    [Fact]
    public void UseActionStep_NullTarget_RoundTrips()
    {
        var step = new UseActionStep
        {
            Id = "sprint-to-keep-up",
            ActionType = ActionType.GeneralAction,
            ActionId = 4,
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "playerHasBuff(50)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        Assert.DoesNotContain("\"targetNpcId\"", json, System.StringComparison.OrdinalIgnoreCase);

        var result = RoundTrip(step);
        Assert.Null(result.TargetNpcId);
    }

    [Fact]
    public void EquipGearForQuestStep_RoundTrips()
    {
        var step = new EquipGearForQuestStep
        {
            Id = "equip-armor",
            Items = [new GearItem("body", 12345), new GearItem("head", 12346)],
            Expect = new PredicateExpect { Predicate = "playerHasEquipped(item:12345, slot:body)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(2, result.Items.Length);
    }

    [Fact]
    public void EquipBestGearStep_RoundTrips()
    {
        var step = new EquipBestGearStep
        {
            Id = "gear-up",
            Constraints = new GearConstraints(50),
            Expect = new PredicateExpect { Predicate = "playerAverageItemLevel() >= 50" }
        };

        var result = RoundTrip(step);
        Assert.Equal(50, result.Constraints!.MinItemLevel);
    }

    [Fact]
    public void ChangeJobStep_RoundTrips()
    {
        var step = new ChangeJobStep
        {
            Id = "switch-job",
            Job = "Gladiator",
            Expect = new PredicateExpect { Predicate = "currentJob() == \"Gladiator\"" }
        };

        var result = RoundTrip(step);
        Assert.Equal("Gladiator", result.Job);
    }

    [Fact]
    public void MinigameStep_RoundTrips()
    {
        var step = new MinigameStep
        {
            Id = "sniper-practice",
            Kind = "sniping",
            Skip = "ifAllowed",
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 7)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("sniping", result.Kind);
    }

    [Fact]
    public void AwaitUserStep_RoundTrips()
    {
        var step = new AwaitUserStep
        {
            Id = "wait-for-user",
            Reason = "Please complete the jumping puzzle.",
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 8)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("Please complete the jumping puzzle.", result.Reason);
    }

    [Fact]
    public void BranchStep_RoundTrips()
    {
        var step = new BranchStep
        {
            Id = "fight-or-flee",
            Branches =
            [
                new BranchCase("playerLevel() >= 15", [new CutsceneStep { Id = "watch-fight", Expect = new PredicateExpect { Predicate = "questSequence(65) >= 4" } }]),
                new BranchCase("default",             [new AwaitUserStep  { Id = "await-help",  Reason = "Need help",    Expect = new PredicateExpect { Predicate = "questSequence(65) >= 4" } }])
            ],
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 4" }
        };

        var result = RoundTrip(step);
        Assert.Equal(2, result.Branches.Length);
        Assert.Equal("default", result.Branches[1].When);
        Assert.IsType<CutsceneStep>(result.Branches[0].Steps[0]);
        Assert.IsType<AwaitUserStep>(result.Branches[1].Steps[0]);
    }

    [Fact]
    public void FragmentStep_RoundTrips()
    {
        var step = new FragmentStep
        {
            Id = "travel-to-gridania",
            Ref = "travel/uldah-to-gridania",
            Expect = new PredicateExpect { Predicate = "playerZone() == 132" }
        };

        var result = RoundTrip(step);
        Assert.Equal("travel/uldah-to-gridania", result.Ref);
    }

    // =========================================================================
    // Phase 11B — AttunementStep schema tests (B1-B4 from PHASE_11B_PLAN.md §3.1)
    // =========================================================================

    [Fact]
    public void AttunementStep_HappyPath_RoundTrips()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep schema type.
         *
         * CONTRACT: Given a JSON document with type "attune", target.value 53, and a location,
         *           When deserialised through QuestForgeJsonContext,
         *           Then result is AttunementStep with Target == new AetheryteId(53),
         *                Location.NpcId == 2147491840, Location.Zone == 130.
         *
         * BUILDER GUIDANCE:
         *   - Add [JsonDerivedType(typeof(AttunementStep), "attune")] to Step.cs.
         *   - AttunementStep.Target is Schema.AetheryteId (the schema-side alias, not Adapters.Types).
         *   - AttunementStep.Location is NpcLocation? (optional).
         */

        // Arrange
        var json = """
            {
              "type": "attune",
              "id": "attune-thal",
              "target": { "value": 53 },
              "location": {
                "npcId": 2147491840,
                "zone": 130,
                "position": { "x": -15.5, "y": 4, "z": -7.2 }
              }
            }
            """;

        // Act
        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var attune = Assert.IsType<AttunementStep>(step);
        Assert.Equal(new AetheryteId(53), attune.Target);
        Assert.NotNull(attune.Location);
        Assert.Equal(2147491840u, attune.Location.NpcId);
        Assert.Equal(130, attune.Location.Zone);
    }

    [Fact]
    public void AttunementStep_LocationOmitted_IsNull()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep.
         *
         * CONTRACT: Given JSON with type "attune" and no location field,
         *           When deserialised, Then Location == null.
         *
         * BUILDER GUIDANCE: Location is NpcLocation? — STJ treats omitted fields as null.
         */

        // Arrange
        var json = """
            {
              "type": "attune",
              "id": "attune-limsa",
              "target": { "value": 8 }
            }
            """;

        // Act
        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var attune = Assert.IsType<AttunementStep>(step);
        Assert.Equal(new AetheryteId(8), attune.Target);
        Assert.Null(attune.Location);
    }

    [Fact]
    public void AttunementStep_SkipIfPreserved_RoundTrips()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep.
         *
         * CONTRACT: Given a step with skipIf predicate "isAttuned(53)",
         *           When round-tripped through STJ,
         *           Then SkipIf is a PredicateExpect whose Predicate == "isAttuned(53)".
         *
         * BUILDER GUIDANCE: Step.SkipIf is ExpectValue? — the existing ExpectValue converter
         *   handles deserialization. The predicate string is preserved verbatim.
         */

        // Arrange
        var step = new AttunementStep
        {
            Id = "attune-gridania",
            Target = new AetheryteId(2),
            SkipIf = new PredicateExpect { Predicate = "isAttuned(53)" }
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        var skipIf = Assert.IsType<PredicateExpect>(result.SkipIf);
        Assert.Equal("isAttuned(53)", skipIf.Predicate);
    }

    [Fact]
    public void AttunementStep_MissingTarget_DefaultsToZero()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep.
         *
         * CONTRACT: Given JSON missing the target field,
         *           When deserialised, Then Target == default (AetheryteId(0)).
         *           No exception is expected — structural validation is the validator's job.
         *
         * BUILDER GUIDANCE: Schema types use { get; init; } with no [JsonRequired].
         *   Missing fields default to the type's default value, not an exception.
         */

        // Arrange
        var json = """
            {
              "type": "attune",
              "id": "attune-missing-target"
            }
            """;

        // Act
        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        var attune = Assert.IsType<AttunementStep>(step);
        Assert.Equal(default(AetheryteId), attune.Target);
        Assert.Equal(0u, attune.Target.Value);
    }

    // =========================================================================
    // B9 — AcceptStep schema round-trip preserves "type" discriminator
    // =========================================================================

    /// <summary>
    /// B9 — AcceptStep serialized JSON contains the type discriminator literal "accept".
    /// The existing AcceptStep_RoundTrips test only verifies object field fidelity.
    /// This test additionally locks the [JsonDerivedType(typeof(AcceptStep), "accept")]
    /// registration by asserting the literal discriminator token is present in the JSON.
    /// Expected to PASS immediately (registration already exists). Authored to prevent regressions.
    /// </summary>
    [Fact]
    public void AcceptStep_JsonContainsTypeDiscriminator()
    {
        /*
         * CONTRACT: Given an AcceptStep instance with Target.NpcId == 1003987,
         *           When serialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then the JSON contains "type": "accept" (with space — WriteIndented=true),
         *           AND deserializing the JSON back produces a Step whose runtime type is AcceptStep,
         *           AND Target.NpcId is preserved.
         *
         * BUILDER GUIDANCE: No implementation needed — schema registration already exists.
         *   This test is a regression lock on [JsonDerivedType(typeof(AcceptStep), "accept")].
         */

        // Arrange
        var step = new AcceptStep
        {
            Id = "accept-coming-to-uldah",
            Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
            Expect = new PredicateExpect { Predicate = "isQuestAccepted(66130)" }
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);

        // Assert — discriminator token present (compact to be robust against indentation)
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"accept\"", compactJson, StringComparison.Ordinal);

        // Assert — round-trip fidelity: runtime type and NpcId preserved
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var acceptStep = Assert.IsType<AcceptStep>(deserialized);
        Assert.Equal(1003987u, acceptStep.Target.NpcId);
    }

    // =========================================================================
    // B10 — TurnInStep schema round-trip preserves discriminator and DialogueChoices
    // =========================================================================

    /// <summary>
    /// B10 — TurnInStep serialized JSON contains the type discriminator "turn-in" and
    /// preserves the DialogueChoices array (count + first element fields).
    /// The existing TurnInStep_RoundTrips test only verifies Id round-trip.
    /// This test additionally locks [JsonDerivedType(typeof(TurnInStep), "turn-in")]
    /// and verifies DialogueChoices survives the round-trip.
    /// Expected to PASS immediately (registration already exists). Authored to prevent regressions.
    /// </summary>
    [Fact]
    public void TurnInStep_JsonContainsTypeDiscriminatorAndDialogueChoices()
    {
        /*
         * CONTRACT: Given a TurnInStep with Target.NpcId == 1000327
         *               AND DialogueChoices containing one entry (Type="yesno", Prompt="Accept?", Answer="yes"),
         *           When serialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then the JSON contains "type": "turn-in" (with space — WriteIndented=true),
         *           AND deserializing the JSON back produces a Step whose runtime type is TurnInStep,
         *           AND Target.NpcId is preserved,
         *           AND DialogueChoices has count 1 and the first element's Type == "yesno".
         *
         * BUILDER GUIDANCE: No implementation needed — schema registration already exists.
         *   This test is a regression lock on [JsonDerivedType(typeof(TurnInStep), "turn-in")]
         *   and verifies the DialogueChoice record survives STJ serialization.
         */

        // Arrange
        var step = new TurnInStep
        {
            Id = "turn-in-coming-to-uldah",
            Target = new NpcLocation(NpcId: 1000327, Zone: 130, Position: new Position3(21.84f, 7f, -81.13f)),
            Expect = new PredicateExpect { Predicate = "isQuestComplete(66130)" },
            DialogueChoices = [new DialogueChoice("yesno", "Accept?", "yes")]
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);

        // Assert — discriminator token present (compact to be robust against indentation)
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"turn-in\"", compactJson, StringComparison.Ordinal);

        // Assert — round-trip fidelity: runtime type, NpcId, and DialogueChoices preserved
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var turnInStep = Assert.IsType<TurnInStep>(deserialized);
        Assert.Equal(1000327u, turnInStep.Target.NpcId);
        Assert.Single(turnInStep.DialogueChoices);
        Assert.Equal("yesno", turnInStep.DialogueChoices[0].Type);
    }

    [Fact]
    public void Step_WithRecovery_RoundTrips()
    {
        var step = new TravelStep
        {
            Id = "travel-with-recovery",
            Destination = new TravelDestination(130),
            Recover = new RecoverConfig
            {
                OnTimeout = new GotoRecoverAction { StepId = "prev-step" },
                OnObstacle = new UseReturnRecoverAction { ThenRetry = true }
            },
            Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
        };

        var result = RoundTrip(step);
        var onTimeout = Assert.IsType<GotoRecoverAction>(result.Recover!.OnTimeout);
        Assert.Equal("prev-step", onTimeout.StepId);
        var onObstacle = Assert.IsType<UseReturnRecoverAction>(result.Recover.OnObstacle);
        Assert.True(onObstacle.ThenRetry);
    }

    [Fact]
    public void Step_WithAllExpectAndAnyExpect_RoundTrips()
    {
        var step = new TalkStep
        {
            Id = "complex-expect",
            Target = new NpcLocation(1, 130, new Position3(0, 0, 0)),
            Expect = new AnyExpect { Any = ["questSequence(65) >= 3", "questFlag(65, 5)"] }
        };

        var result = RoundTrip(step);
        var any = Assert.IsType<AnyExpect>(result.Expect);
        Assert.Equal(2, any.Any.Length);
    }

    // =========================================================================
    // B8 — HandOverItemStep schema round-trip tests (RED PHASE)
    // All tests in this group will fail to compile until Builder implements
    // HandOverItemStep in QuestForge.Schema (Step.cs + QuestForgeJsonContext.cs).
    // =========================================================================

    /// <summary>
    /// B8 — HandOverItemStep round-trips through JSON with correct NpcId, Item, and Expect.
    /// </summary>
    [Fact]
    public void HandOverItemStep_RoundTrips()
    {
        /*
         * RED: Will fail to compile until Builder implements HandOverItemStep.
         *
         * CONTRACT: Given a HandOverItemStep with Target.NpcId==1003987, Items==[2002001],
         *             and Expect="isQuestComplete(12345)",
         *           When serialized as Step and deserialized back,
         *           Then result is HandOverItemStep with:
         *             Target.NpcId == 1003987u
         *             Items[0]     == 2002001u
         *             Expect is PredicateExpect with Predicate == "isQuestComplete(12345)"
         *
         * BUILDER GUIDANCE:
         *   1. Add HandOverItemStep class to Step.cs inheriting Step with:
         *        public NpcLocation Target { get; init; } = default!;
         *        public uint[] Items { get; init; } = []
         *   2. Add [JsonDerivedType(typeof(HandOverItemStep), "hand-over-item")] to Step.
         *   3. Add [JsonSerializable(typeof(HandOverItemStep))] to QuestForgeJsonContext.
         */

        // Arrange
        var step = new HandOverItemStep   // RED: type does not exist yet
        {
            Id     = "hand-over-letter",
            Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
            Items  = [2002001u],
            Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
        };

        // Act
        var result = RoundTrip(step);   // RED: RoundTrip<HandOverItemStep> won't resolve yet

        // Assert
        Assert.Equal(1003987u, result.Target.NpcId);
        Assert.Equal(2002001u, result.Items[0]);
        var expect = Assert.IsType<PredicateExpect>(result.Expect);
        Assert.Equal("isQuestComplete(12345)", expect.Predicate);
    }

    /// <summary>
    /// B8b — serialized JSON contains the "hand-over-item" type discriminator.
    /// </summary>
    [Fact]
    public void HandOverItemStep_SerializedJson_ContainsDiscriminator()
    {
        /*
         * RED: Will fail to compile until Builder implements HandOverItemStep.
         *
         * CONTRACT: Given a HandOverItemStep instance,
         *           When serialized via QuestForgeJsonContext.QuestFileOptions,
         *           Then the JSON contains the literal token "type":"hand-over-item"
         *                (compact, robust against indentation).
         */

        // Arrange
        var step = new HandOverItemStep   // RED: type does not exist yet
        {
            Id     = "hand-over-letter",
            Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
            Items  = [2002001u]
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(
            step, QuestForgeJsonContext.QuestFileOptions);

        // Assert — discriminator token present
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"hand-over-item\"", compactJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// B8c — missing "items" field in JSON deserializes to Items == [] (no exception).
    /// </summary>
    [Fact]
    public void HandOverItemStep_MissingItemsField_DefaultsToEmpty()
    {
        /*
         * RED: Will fail to compile until Builder implements HandOverItemStep.
         *
         * CONTRACT: Given JSON with type "hand-over-item" but no "items" field,
         *           When deserialized,
         *           Then Items == [] and no exception is thrown.
         *           Structural validation is the validator's job, not the deserializer's.
         *
         * BUILDER GUIDANCE: Use { get; init; } = [] with no [JsonRequired]. Missing fields
         *   default to an empty array.
         */

        // Arrange
        var json = """
            {
              "type": "hand-over-item",
              "id": "hand-over-missing-item",
              "target": {
                "npcId": 1003987,
                "zone": 182,
                "position": { "x": 35.56, "y": 4.0, "z": -151.18 }
              }
            }
            """;

        // Act
        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(
            json, QuestForgeJsonContext.QuestFileOptions);

        // Assert — no exception; Items defaults to empty array
        var handOver = Assert.IsType<HandOverItemStep>(step);   // RED: type does not exist yet
        Assert.Empty(handOver.Items);
    }

    /// <summary>
    /// B8d — StopDistance round-trips: 7.5f survives serialize/deserialize.
    /// </summary>
    [Fact]
    public void HandOverItemStep_StopDistance_RoundTrips()
    {
        /*
         * RED: Will fail to compile until Builder implements HandOverItemStep.
         *
         * CONTRACT: Given HandOverItemStep with StopDistance=7.5f,
         *           When round-tripped through JSON,
         *           Then result.StopDistance == 7.5f.
         *
         * BUILDER GUIDANCE: StopDistance is defined on the base Step class (already present).
         *   No additional work needed beyond registering HandOverItemStep. This test locks
         *   that the base-class property serializes correctly for the new subtype.
         */

        // Arrange
        var step = new HandOverItemStep   // RED: type does not exist yet
        {
            Id           = "hand-over-letter",
            Target       = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
            Items        = [2002001u],
            StopDistance = 7.5f
        };

        // Act
        var result = RoundTrip(step);

        // Assert
        Assert.Equal(7.5f, result.StopDistance);
    }

    // =========================================================================
    // Group E — PurchaseItemStep schema round-trip tests (RED PHASE)
    // All tests in this group will fail to compile until Builder implements
    // PurchaseItemStep + PurchaseCurrency in QuestForge.Schema (Step.cs +
    // QuestForgeJsonContext.cs). That compile failure IS the correct RED.
    // =========================================================================

    // Test constants: vendor NpcId 1001234, Zone 128, Position (10.5, 0, -20.0), ItemId 1601.

    /// <summary>E1 — PurchaseItemStep round-trips with the "purchase-item" discriminator.</summary>
    [Fact]
    public void PurchaseItemStep_RoundTrips_WithDiscriminator()
    {
        // CONTRACT: Given PurchaseItemStep with all fields set, serialized as Step,
        //           Then JSON contains "type":"purchase-item"; deserialized runtime type is
        //           PurchaseItemStep; ItemId, Quantity, and Target.NpcId are preserved.
        // RED: Fails to compile — PurchaseItemStep does not exist yet.

        var step = new PurchaseItemStep   // RED: type does not exist yet
        {
            Id       = "buy-thing",
            Target   = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId   = 1601,
            Quantity = 2,
            Currency = PurchaseCurrency.Gil   // RED: enum does not exist yet
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"purchase-item\"", compactJson, StringComparison.Ordinal);

        var result = RoundTrip(step);   // RED: RoundTrip<PurchaseItemStep> won't resolve yet

        Assert.Equal(1601u, result.ItemId);
        Assert.Equal(2, result.Quantity);
        Assert.Equal(1001234u, result.Target.NpcId);
    }

    /// <summary>E2 — Currency serializes camelCase and round-trips both members.</summary>
    [Theory]
    [InlineData(PurchaseCurrency.GcSeals, "gcSeals")]   // RED: enum does not exist yet
    [InlineData(PurchaseCurrency.Gil,     "gil")]        // RED: enum does not exist yet
    public void PurchaseItemStep_Currency_SerializesCamelCase(PurchaseCurrency currency, string expectedToken)
    {
        // CONTRACT: Given PurchaseItemStep with the given Currency value,
        //           When serialized, Then JSON contains "currency":"<expectedToken>";
        //           deserialization yields the same PurchaseCurrency member.
        // RED: Fails to compile — PurchaseCurrency does not exist yet.

        var step = new PurchaseItemStep   // RED: type does not exist yet
        {
            Id       = "buy-thing",
            Target   = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId   = 1601,
            Currency = currency
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains($"\"currency\":\"{expectedToken}\"", compactJson, StringComparison.Ordinal);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var purchase = Assert.IsType<PurchaseItemStep>(deserialized);   // RED: type does not exist yet
        Assert.Equal(currency, purchase.Currency);
    }

    /// <summary>E3 — Quantity defaults to 1 when omitted from JSON.</summary>
    [Fact]
    public void PurchaseItemStep_MissingQuantityField_DefaultsToOne()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no "quantity" field,
        //           When deserialized, Then Quantity == 1 (no exception).
        // RED: Fails to compile — PurchaseItemStep does not exist yet.

        var json = """
            {
              "type": "purchase-item",
              "id": "buy-thing-no-qty",
              "target": {
                "npcId": 1001234,
                "zone": 128,
                "position": { "x": 10.5, "y": 0, "z": -20.0 }
              },
              "itemId": 1601
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var purchase = Assert.IsType<PurchaseItemStep>(step);   // RED: type does not exist yet
        Assert.Equal(1, purchase.Quantity);
    }

    /// <summary>E3b — Currency defaults to Gil when omitted from JSON.</summary>
    [Fact]
    public void PurchaseItemStep_MissingCurrencyField_DefaultsToGil()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no "currency" field,
        //           When deserialized, Then Currency == PurchaseCurrency.Gil (no exception).
        // RED: Fails to compile — PurchaseItemStep / PurchaseCurrency do not exist yet.

        var json = """
            {
              "type": "purchase-item",
              "id": "buy-thing-no-currency",
              "target": {
                "npcId": 1001234,
                "zone": 128,
                "position": { "x": 10.5, "y": 0, "z": -20.0 }
              },
              "itemId": 1601
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var purchase = Assert.IsType<PurchaseItemStep>(step);   // RED: type does not exist yet
        Assert.Equal(PurchaseCurrency.Gil, purchase.Currency);  // RED: enum does not exist yet
    }

    // =========================================================================
    // Group G-E — GcCategory / GcRankTier schema round-trip tests (RED PHASE)
    // All three tests will fail to COMPILE until Builder adds GcCategory and
    // GcRankTier to PurchaseItemStep in Step.cs. That compile failure is the
    // intended RED — do NOT stub the production fields.
    // =========================================================================

    // Test constants: Vendor=1001234, Zone=128, Position=(10.5,0,-20.0), ItemId=1601.

    /// <summary>G-E1 — GcCategory/GcRankTier round-trip when set.</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_RoundTrip_WhenSet()
    {
        // CONTRACT: Given PurchaseItemStep with GcCategory=2, GcRankTier=1, Currency=GcSeals,
        //           When serialized via QuestForgeJsonContext.QuestFileOptions,
        //           Then JSON contains "gcCategory":2 AND "gcRankTier":1;
        //           deserialized step is PurchaseItemStep with GcCategory==2, GcRankTier==1,
        //           ItemId and Target.NpcId also preserved.
        // RED: Fails to compile — GcCategory/GcRankTier do not exist on PurchaseItemStep yet.

        var step = new PurchaseItemStep
        {
            Id       = "buy-gc",
            Target   = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId   = 1601,
            Quantity = 1,
            Currency = PurchaseCurrency.GcSeals,
            GcCategory = 2,   // RED: property does not exist yet
            GcRankTier = 1    // RED: property does not exist yet
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"gcCategory\":2", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"gcRankTier\":1", compactJson, StringComparison.Ordinal);

        var result = RoundTrip(step);

        Assert.Equal(2, result.GcCategory);   // RED: property does not exist yet
        Assert.Equal(1, result.GcRankTier);   // RED: property does not exist yet
        Assert.Equal(1601u, result.ItemId);
        Assert.Equal(1001234u, result.Target.NpcId);
    }

    /// <summary>G-E2 — omitted gcCategory/gcRankTier fields default to null.</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_MissingFields_DefaultToNull()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no gcCategory/gcRankTier fields,
        //           When deserialized, Then GcCategory == null && GcRankTier == null.
        // RED: Fails to compile — GcCategory/GcRankTier do not exist on PurchaseItemStep yet.

        var json = """
            {
              "type": "purchase-item",
              "id": "x",
              "target": {
                "npcId": 1001234,
                "zone": 128,
                "position": { "x": 10.5, "y": 0, "z": -20.0 }
              },
              "itemId": 1601,
              "quantity": 1,
              "currency": "gcSeals"
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var purchase = Assert.IsType<PurchaseItemStep>(step);
        Assert.Null(purchase.GcCategory);   // RED: property does not exist yet
        Assert.Null(purchase.GcRankTier);   // RED: property does not exist yet
    }

    /// <summary>G-E3 — null C# side round-trips (omitted or explicit null — match existing nullable-int convention).</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_NullCSharpSide_RoundTrips()
    {
        // CONTRACT: Given PurchaseItemStep with GcCategory=null, GcRankTier=null,
        //           When serialized then deserialized via QuestForgeJsonContext.QuestFileOptions,
        //           Then deserialized step has GcCategory==null AND GcRankTier==null.
        //           The exact JSON shape (omitted keys vs explicit nulls) is NOT asserted —
        //           match whichever convention the existing nullable-int handling uses.
        // RED: Fails to compile — GcCategory/GcRankTier do not exist on PurchaseItemStep yet.

        var step = new PurchaseItemStep
        {
            Id         = "buy-gc-no-nav",
            Target     = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId     = 1601,
            Quantity   = 1,
            Currency   = PurchaseCurrency.GcSeals,
            GcCategory = null,   // RED: property does not exist yet
            GcRankTier = null    // RED: property does not exist yet
        };

        var result = RoundTrip(step);

        Assert.Null(result.GcCategory);   // RED: property does not exist yet
        Assert.Null(result.GcRankTier);   // RED: property does not exist yet
    }

}