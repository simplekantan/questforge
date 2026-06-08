using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

/// <summary>
/// Mandatory gate: every Step subtype must survive a serialization round-trip.
/// A missing [JsonSerializable] registration causes silent failure at runtime â€”
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
            Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 5" }
        };

        var result = RoundTrip(step);
        Assert.Equal("regular", result.Kind);
    }

    [Fact]
    public void DutyStep_Spd_RoundTrips()
    {
        var step = new DutyStep
        {
            Id = "quest-battle",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var result = RoundTrip(step);
        Assert.Equal("spd", result.Kind);
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
    /// B7 â€” Schema round-trip: discriminator assertion.
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

        // Assert â€” discriminator must be present with the exact registered value.
        // QuestFileOptions uses WriteIndented=true, so the JSON contains spaces around
        // the colon: `"type": "cutscene"`. Strip whitespace before checking to be
        // robust against indentation changes while still asserting the correct token.
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"cutscene\"", compactJson,
            StringComparison.Ordinal);
    }

    // SC10 â€” JSON round-trip with TargetNpcId set (replaces legacy SayChatMessageStep_RoundTrips)
    //       The old Channel + Target: NpcLocation? fields are removed (Decision SC1).
    [Fact]
    public void SayChatMessageStep_WithTarget_RoundTrips_SC10()
    {
        var step = new SayChatMessageStep
        {
            Id = "say-the-magic-word",
            Message = "Open Sesame",
            TargetNpcId = 1000789u,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 4)" }
        };

        var result = RoundTrip(step);

        Assert.Equal("say-the-magic-word", result.Id);
        Assert.Equal("Open Sesame", result.Message);
        Assert.Equal(1000789u, result.TargetNpcId);
    }

    // SC11 â€” JSON round-trip with no target (TargetNpcId omitted from JSON â†’ null on deserialize)
    [Fact]
    public void SayChatMessageStep_WithoutTarget_RoundTrips_SC11()
    {
        var step = new SayChatMessageStep
        {
            Id = "shout-into-void",
            Message = "Hello",
            TargetNpcId = null,
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 3" }
        };

        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = Assert.IsType<SayChatMessageStep>(
            JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal("Hello", result.Message);
        Assert.Null(result.TargetNpcId);

        // Verify [JsonIgnore(WhenWritingNull)] is honored â€” "targetNpcId" must not appear in JSON
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.DoesNotContain("\"targetNpcId\"", compactJson, StringComparison.Ordinal);
    }

    // UE10 â€” round-trip with TargetNpcId set and motion=true (default)
    //       The old Target: NpcLocation? field is removed (Decision UE3).
    [Fact]
    public void UseEmoteStep_MotionTrue_WithTarget_RoundTrips_UE10()
    {
        var step = new UseEmoteStep
        {
            Id = "salute",
            EmoteId = 7,
            TargetNpcId = 1000789u,
            Motion = true,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(7u, result.EmoteId);
        Assert.Equal(1000789u, result.TargetNpcId);
        Assert.True(result.Motion);
    }

    // UE11 â€” round-trip with motion=false explicit and no target
    [Fact]
    public void UseEmoteStep_MotionFalse_NoTarget_RoundTrips_UE11()
    {
        var step = new UseEmoteStep
        {
            Id = "yell",
            EmoteId = 17,
            TargetNpcId = null,
            Motion = false,
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 3" }
        };

        var result = RoundTrip(step);
        Assert.Equal(17u, result.EmoteId);
        Assert.Null(result.TargetNpcId);
        Assert.False(result.Motion);
    }

    // UE12 â€” Motion defaults to true when field absent from JSON
    // Pins Decision UE2: authors omitting "motion" get motion=true (broadcast suppressed).
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

    // UI11 — JSON round-trip self-cast (no target fields written)
    // Replaces UseItemStep_NoTarget_RoundTrips (old shape referenced deleted UseItemTarget)
    [Fact]
    public void UseItemStep_SelfCast_RoundTrips_UI11()
    {
        var step = new UseItemStep
        {
            Id = "drink-elixir",
            Kind = ItemKind.InventoryItem,
            ItemId = 4554u,
            TargetNpcId = null,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(84011, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = Assert.IsType<UseItemStep>(
            System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(ItemKind.InventoryItem, result.Kind);
        Assert.Equal(4554u, result.ItemId);
        Assert.Null(result.TargetNpcId);
        Assert.Null(result.TargetPosition);
        // Pins [JsonIgnore(WhenWritingNull)] on both target fields
        Assert.DoesNotContain("targetNpcId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetPosition", json, StringComparison.OrdinalIgnoreCase);
    }

    // UI12 — JSON round-trip with NPC target
    // Replaces UseItemStep_WithTarget_RoundTrips (old shape referenced deleted UseItemTarget)
    [Fact]
    public void UseItemStep_NpcTarget_RoundTrips_UI12()
    {
        var step = new UseItemStep
        {
            Id = "ring-bell",
            Kind = ItemKind.KeyItem,
            ItemId = 2000456u,
            TargetNpcId = 1000789u,
            TargetPosition = null,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = Assert.IsType<UseItemStep>(
            System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(ItemKind.KeyItem, result.Kind);
        Assert.Equal(2000456u, result.ItemId);
        Assert.Equal(1000789u, result.TargetNpcId);
        Assert.Null(result.TargetPosition);
        // Pins [JsonStringEnumMemberName("keyItem")]
        Assert.Contains("\"kind\": \"keyItem\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetNpcId\": 1000789", json, StringComparison.Ordinal);
        Assert.DoesNotContain("targetPosition", json, StringComparison.OrdinalIgnoreCase);
    }

    // UI13 — JSON round-trip with AoE position target
    [Fact]
    public void UseItemStep_AoEPosition_RoundTrips_UI13()
    {
        var step = new UseItemStep
        {
            Id = "smoke-bomb-camp",
            Kind = ItemKind.KeyItem,
            ItemId = 2000123u,
            TargetNpcId = null,
            TargetPosition = new Position3(123.4f, 0f, -45.6f),
            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 4" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = Assert.IsType<UseItemStep>(
            System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(ItemKind.KeyItem, result.Kind);
        Assert.Equal(2000123u, result.ItemId);
        Assert.Null(result.TargetNpcId);
        Assert.NotNull(result.TargetPosition);
        Assert.Equal(123.4f, result.TargetPosition!.X, precision: 3);
        Assert.Equal(0f, result.TargetPosition!.Y, precision: 3);
        Assert.Equal(-45.6f, result.TargetPosition!.Z, precision: 3);
        Assert.Contains("targetPosition", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetNpcId", json, StringComparison.OrdinalIgnoreCase);
    }

    // UI19 — ItemKind enum round-trips as the configured camelCase strings
    // Pins [JsonStringEnumMemberName] choices (Decision UI2)
    [Fact]
    public void UseItemStep_ItemKind_RoundTripsAsCamelCaseStrings_UI19()
    {
        var keyItemStep = new UseItemStep
        {
            Id = "key-item-kind",
            Kind = ItemKind.KeyItem,
            ItemId = 1u,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 1)" }
        };
        var inventoryItemStep = new UseItemStep
        {
            Id = "inventory-item-kind",
            Kind = ItemKind.InventoryItem,
            ItemId = 1u,
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 2)" }
        };

        var keyJson = System.Text.Json.JsonSerializer.Serialize<Step>(keyItemStep, QuestForgeJsonContext.QuestFileOptions);
        var inventoryJson = System.Text.Json.JsonSerializer.Serialize<Step>(inventoryItemStep, QuestForgeJsonContext.QuestFileOptions);

        // Exact string values — NOT "KeyItem" or "Key_Item"
        Assert.Contains("\"kind\": \"keyItem\"", keyJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"inventoryItem\"", inventoryJson, StringComparison.Ordinal);

        // Round-trip preserves Kind value
        var keyResult = Assert.IsType<UseItemStep>(
            System.Text.Json.JsonSerializer.Deserialize<Step>(keyJson, QuestForgeJsonContext.QuestFileOptions));
        var inventoryResult = Assert.IsType<UseItemStep>(
            System.Text.Json.JsonSerializer.Deserialize<Step>(inventoryJson, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(ItemKind.KeyItem, keyResult.Kind);
        Assert.Equal(ItemKind.InventoryItem, inventoryResult.Kind);
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
            Expect = new PredicateExpect { Predicate = "questFlag(84011, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        Assert.DoesNotContain("\"targetNpcId\"", json, System.StringComparison.OrdinalIgnoreCase);

        var result = RoundTrip(step);
        Assert.Null(result.TargetNpcId);
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
    // Phase 11B â€” AttunementStep schema tests (B1-B4 from PHASE_11B_PLAN.md Â§3.1)
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
         * BUILDER GUIDANCE: Location is NpcLocation? â€” STJ treats omitted fields as null.
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
         * BUILDER GUIDANCE: Step.SkipIf is ExpectValue? â€” the existing ExpectValue converter
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
         *           No exception is expected â€” structural validation is the validator's job.
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
    // B9 â€” AcceptStep schema round-trip preserves "type" discriminator
    // =========================================================================

    /// <summary>
    /// B9 â€” AcceptStep serialized JSON contains the type discriminator literal "accept".
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
         *           Then the JSON contains "type": "accept" (with space â€” WriteIndented=true),
         *           AND deserializing the JSON back produces a Step whose runtime type is AcceptStep,
         *           AND Target.NpcId is preserved.
         *
         * BUILDER GUIDANCE: No implementation needed â€” schema registration already exists.
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

        // Assert â€” discriminator token present (compact to be robust against indentation)
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"accept\"", compactJson, StringComparison.Ordinal);

        // Assert â€” round-trip fidelity: runtime type and NpcId preserved
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var acceptStep = Assert.IsType<AcceptStep>(deserialized);
        Assert.Equal(1003987u, acceptStep.Target.NpcId);
    }

    // =========================================================================
    // B10 â€” TurnInStep schema round-trip preserves discriminator and DialogueChoices
    // =========================================================================

    /// <summary>
    /// B10 â€” TurnInStep serialized JSON contains the type discriminator "turn-in" and
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
         *           Then the JSON contains "type": "turn-in" (with space â€” WriteIndented=true),
         *           AND deserializing the JSON back produces a Step whose runtime type is TurnInStep,
         *           AND Target.NpcId is preserved,
         *           AND DialogueChoices has count 1 and the first element's Type == "yesno".
         *
         * BUILDER GUIDANCE: No implementation needed â€” schema registration already exists.
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

        // Assert â€” discriminator token present (compact to be robust against indentation)
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"turn-in\"", compactJson, StringComparison.Ordinal);

        // Assert â€” round-trip fidelity: runtime type, NpcId, and DialogueChoices preserved
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
    // B8 â€” HandOverItemStep schema round-trip tests (RED PHASE)
    // All tests in this group will fail to compile until Builder implements
    // HandOverItemStep in QuestForge.Schema (Step.cs + QuestForgeJsonContext.cs).
    // =========================================================================

    /// <summary>
    /// B8 â€” HandOverItemStep round-trips through JSON with correct NpcId, Item, and Expect.
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
    /// B8b â€” serialized JSON contains the "hand-over-item" type discriminator.
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

        // Assert â€” discriminator token present
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"hand-over-item\"", compactJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// B8c â€” missing "items" field in JSON deserializes to Items == [] (no exception).
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

        // Assert â€” no exception; Items defaults to empty array
        var handOver = Assert.IsType<HandOverItemStep>(step);   // RED: type does not exist yet
        Assert.Empty(handOver.Items);
    }

    /// <summary>
    /// B8d â€” StopDistance round-trips: 7.5f survives serialize/deserialize.
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
    // Group E â€” PurchaseItemStep schema round-trip tests (RED PHASE)
    // All tests in this group will fail to compile until Builder implements
    // PurchaseItemStep + PurchaseCurrency in QuestForge.Schema (Step.cs +
    // QuestForgeJsonContext.cs). That compile failure IS the correct RED.
    // =========================================================================

    // Test constants: vendor NpcId 1001234, Zone 128, Position (10.5, 0, -20.0), ItemId 1601.

    /// <summary>E1 â€” PurchaseItemStep round-trips with the "purchase-item" discriminator.</summary>
    [Fact]
    public void PurchaseItemStep_RoundTrips_WithDiscriminator()
    {
        // CONTRACT: Given PurchaseItemStep with all fields set, serialized as Step,
        //           Then JSON contains "type":"purchase-item"; deserialized runtime type is
        //           PurchaseItemStep; ItemId, Quantity, and Target.NpcId are preserved.

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

    /// <summary>E2 â€” Currency serializes camelCase and round-trips both members.</summary>
    [Theory]
    [InlineData(PurchaseCurrency.GcSeals, "gcSeals")]   // RED: enum does not exist yet
    [InlineData(PurchaseCurrency.Gil,     "gil")]        // RED: enum does not exist yet
    public void PurchaseItemStep_Currency_SerializesCamelCase(PurchaseCurrency currency, string expectedToken)
    {
        // CONTRACT: Given PurchaseItemStep with the given Currency value,
        //           When serialized, Then JSON contains "currency":"<expectedToken>";
        //           deserialization yields the same PurchaseCurrency member.

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

    /// <summary>E3 â€” Quantity defaults to 1 when omitted from JSON.</summary>
    [Fact]
    public void PurchaseItemStep_MissingQuantityField_DefaultsToOne()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no "quantity" field,
        //           When deserialized, Then Quantity == 1 (no exception).

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

    /// <summary>E3b â€” Currency defaults to Gil when omitted from JSON.</summary>
    [Fact]
    public void PurchaseItemStep_MissingCurrencyField_DefaultsToGil()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no "currency" field,
        //           When deserialized, Then Currency == PurchaseCurrency.Gil (no exception).

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
    // Group G-E â€” GcCategory / GcRankTier schema round-trip tests (RED PHASE)
    // All three tests will fail to COMPILE until Builder adds GcCategory and
    // GcRankTier to PurchaseItemStep in Step.cs. That compile failure is the
    // intended RED â€” do NOT stub the production fields.
    // =========================================================================

    // Test constants: Vendor=1001234, Zone=128, Position=(10.5,0,-20.0), ItemId=1601.

    /// <summary>G-E1 â€” GcCategory/GcRankTier round-trip when set.</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_RoundTrip_WhenSet()
    {
        // CONTRACT: Given PurchaseItemStep with GcCategory=2, GcRankTier=1, Currency=GcSeals,
        //           When serialized via QuestForgeJsonContext.QuestFileOptions,
        //           Then JSON contains "gcCategory":2 AND "gcRankTier":1;
        //           deserialized step is PurchaseItemStep with GcCategory==2, GcRankTier==1,
        //           ItemId and Target.NpcId also preserved.

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

    /// <summary>G-E2 â€” omitted gcCategory/gcRankTier fields default to null.</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_MissingFields_DefaultToNull()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no gcCategory/gcRankTier fields,
        //           When deserialized, Then GcCategory == null && GcRankTier == null.

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

    /// <summary>G-E3 â€” null C# side round-trips (omitted or explicit null â€” match existing nullable-int convention).</summary>
    [Fact]
    public void PurchaseItemStep_GcFields_NullCSharpSide_RoundTrips()
    {
        // CONTRACT: Given PurchaseItemStep with GcCategory=null, GcRankTier=null,
        //           When serialized then deserialized via QuestForgeJsonContext.QuestFileOptions,
        //           Then deserialized step has GcCategory==null AND GcRankTier==null.
        //           The exact JSON shape (omitted keys vs explicit nulls) is NOT asserted â€”
        //           match whichever convention the existing nullable-int handling uses.

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

    // =========================================================================
    // Group V -- VendorCategory schema round-trip tests (RED PHASE)
    // All three tests fail to COMPILE until Builder adds VendorCategory
    // to PurchaseItemStep in Step.cs. That compile failure is the intended RED.
    // =========================================================================

    /// <summary>V1 -- VendorCategory=2 round-trips when set.</summary>
    [Fact]
    public void PurchaseItemStep_VendorCategory_RoundTrip_WhenSet_V1()
    {
        // CONTRACT: Given PurchaseItemStep with VendorCategory=2, Currency=Gil,
        //           When serialized via QuestForgeJsonContext.QuestFileOptions,
        //           Then JSON contains "vendorCategory":2;
        //           deserialized step is PurchaseItemStep with VendorCategory==2,
        //           ItemId and Target.NpcId also preserved.

        var step = new PurchaseItemStep
        {
            Id             = "buy-cat-2",
            Target         = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId         = 1601,
            Quantity       = 1,
            Currency       = PurchaseCurrency.Gil,
            VendorCategory = 2   // RED: property does not exist yet
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"vendorCategory\":2", compactJson, StringComparison.Ordinal);

        var result = RoundTrip(step);

        Assert.Equal(2, result.VendorCategory);   // RED: property does not exist yet
        Assert.Equal(1601u, result.ItemId);
        Assert.Equal(1001234u, result.Target.NpcId);
    }

    /// <summary>V2 -- VendorCategory null omitted from JSON (WhenWritingNull).</summary>
    [Fact]
    public void PurchaseItemStep_VendorCategory_NullOmittedFromJson_V2()
    {
        // CONTRACT: Given PurchaseItemStep with VendorCategory=null,
        //           When serialized, Then compact JSON does NOT contain "vendorCategory";
        //           deserialized step has VendorCategory==null.

        var step = new PurchaseItemStep
        {
            Id             = "buy-no-cat",
            Target         = new NpcLocation(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f)),
            ItemId         = 1601,
            Quantity       = 1,
            Currency       = PurchaseCurrency.Gil,
            VendorCategory = null   // RED: property does not exist yet
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.DoesNotContain("\"vendorCategory\"", compactJson, StringComparison.Ordinal);

        var result = RoundTrip(step);

        Assert.Null(result.VendorCategory);   // RED: property does not exist yet
    }

    /// <summary>V3 -- Missing vendorCategory field in JSON defaults to null.</summary>
    [Fact]
    public void PurchaseItemStep_VendorCategory_MissingField_DefaultsToNull_V3()
    {
        // CONTRACT: Given JSON with type "purchase-item" and no vendorCategory field,
        //           When deserialized, Then VendorCategory == null (backward compat).

        var json = """
            {
              "type": "purchase-item",
              "id": "x",
              "target": {
                "npcId": 1001234,
                "zone": 128,
                "position": { "x": 10.5, "y": 0, "z": -20.0 }
              },
              "itemId": 1601
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var purchase = Assert.IsType<PurchaseItemStep>(step);
        Assert.Null(purchase.VendorCategory);   // RED: property does not exist yet
    }

    // =========================================================================
    // Issue #122 -- Gear step schema update round-trip tests (RED PHASE)
    // All tests reference NEW property names (ItemIds, JobId) and the removal
    // of GearConstraints. They will fail to compile until the builder updates
    // EquipGearForQuestStep, EquipBestGearStep, and ChangeJobStep in Step.cs.
    // =========================================================================

    // GS-RT1: EquipGearForQuestStep round-trips with uint[] ItemIds
    [Fact]
    public void EquipGearForQuestStep_ItemIds_RoundTrips_GS_RT1()
    {
        /*
         * RED: Will fail to compile -- EquipGearForQuestStep.ItemIds does not exist yet
         *      (current shape has GearItem[] Items).
         *
         * CONTRACT: Given EquipGearForQuestStep with ItemIds = [12345u, 12346u],
         *           When serialized as Step then deserialized,
         *           Then result is EquipGearForQuestStep with ItemIds.Length == 2,
         *                ItemIds[0] == 12345u, ItemIds[1] == 12346u.
         *
         * BUILDER GUIDANCE: Replace GearItem[] Items with uint[] ItemIds on EquipGearForQuestStep.
         */

        var step = new EquipGearForQuestStep
        {
            Id = "equip-quest-armor",
            ItemIds = [12345u, 12346u],  // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
        };

        var result = RoundTrip(step);

        Assert.Equal("equip-quest-armor", result.Id);
        Assert.Equal(2, result.ItemIds.Length);
        Assert.Equal(12345u, result.ItemIds[0]);
        Assert.Equal(12346u, result.ItemIds[1]);
    }

    // GS-RT2: EquipGearForQuestStep discriminator and camelCase in JSON
    [Fact]
    public void EquipGearForQuestStep_Discriminator_GS_RT2()
    {
        /*
         * RED: Will fail to compile -- ItemIds does not exist yet.
         *
         * CONTRACT: Given EquipGearForQuestStep with ItemIds = [12345u, 12346u],
         *           When serialized,
         *           Then compact JSON contains "type":"equip-gear-for-quest"
         *                and "itemIds":[12345,12346].
         */

        var step = new EquipGearForQuestStep
        {
            Id = "equip-quest-armor",
            ItemIds = [12345u, 12346u],  // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        Assert.Contains("\"type\":\"equip-gear-for-quest\"", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"itemIds\":[12345,12346]", compactJson, StringComparison.Ordinal);
    }

    // GS-RT3: EquipGearForQuestStep with missing itemIds field defaults to empty array
    [Fact]
    public void EquipGearForQuestStep_MissingItemIds_DefaultsToEmpty_GS_RT3()
    {
        /*
         * RED: Will fail to compile -- ItemIds does not exist yet.
         *
         * CONTRACT: Given JSON with type "equip-gear-for-quest" and no itemIds field,
         *           When deserialized,
         *           Then ItemIds is [] (empty array, no exception).
         */

        const string json = """
            {
              "type": "equip-gear-for-quest",
              "id": "x"
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var equipGear = Assert.IsType<EquipGearForQuestStep>(step);
        Assert.Empty(equipGear.ItemIds);  // RED: property does not exist yet
    }

    // GS-RT4: EquipBestGearStep round-trips (empty body, no Constraints property)
    [Fact]
    public void EquipBestGearStep_EmptyBody_RoundTrips_GS_RT4()
    {
        /*
         * RED: Will fail because test asserts the Constraints property is GONE.
         *      Current shape still has GearConstraints? Constraints.
         *      This test validates the new shape (no step-specific properties).
         *
         * CONTRACT: Given EquipBestGearStep with only Id set,
         *           When serialized as Step then deserialized,
         *           Then result is EquipBestGearStep with Id == "gear-up".
         *
         * BUILDER GUIDANCE: Remove GearConstraints? Constraints property from EquipBestGearStep.
         */

        var step = new EquipBestGearStep
        {
            Id = "gear-up"
        };

        var result = RoundTrip(step);

        Assert.Equal("gear-up", result.Id);
    }

    // GS-RT5: EquipBestGearStep discriminator, no "constraints" in JSON
    [Fact]
    public void EquipBestGearStep_Discriminator_NoConstraints_GS_RT5()
    {
        /*
         * CONTRACT: Given EquipBestGearStep with only Id set,
         *           When serialized,
         *           Then compact JSON contains "type":"equip-best-gear"
         *                and does NOT contain "constraints".
         *
         * BUILDER GUIDANCE: After removing GearConstraints? Constraints, the serialized
         *   output naturally omits it. This test locks that contract.
         */

        var step = new EquipBestGearStep
        {
            Id = "gear-up"
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        Assert.Contains("\"type\":\"equip-best-gear\"", compactJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"constraints\"", compactJson, StringComparison.OrdinalIgnoreCase);
    }

    // GS-RT6: ChangeJobStep round-trips with uint JobId
    [Fact]
    public void ChangeJobStep_JobId_RoundTrips_GS_RT6()
    {
        /*
         * RED: Will fail to compile -- ChangeJobStep.JobId does not exist yet
         *      (current shape has string Job).
         *
         * CONTRACT: Given ChangeJobStep with JobId = 19u,
         *           When serialized as Step then deserialized,
         *           Then result is ChangeJobStep with JobId == 19u.
         *
         * BUILDER GUIDANCE: Replace string Job with uint JobId on ChangeJobStep.
         */

        var step = new ChangeJobStep
        {
            Id = "switch-to-paladin",
            JobId = 19u,  // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "currentJob() == 19" }
        };

        var result = RoundTrip(step);

        Assert.Equal("switch-to-paladin", result.Id);
        Assert.Equal(19u, result.JobId);  // RED: property does not exist yet
    }

    // GS-RT7: ChangeJobStep discriminator and camelCase in JSON
    [Fact]
    public void ChangeJobStep_Discriminator_GS_RT7()
    {
        /*
         * RED: Will fail to compile -- JobId does not exist yet.
         *
         * CONTRACT: Given ChangeJobStep with JobId = 19u,
         *           When serialized,
         *           Then compact JSON contains "type":"change-job" and "jobId":19.
         *           And does NOT contain "job" as a string-valued property.
         */

        var step = new ChangeJobStep
        {
            Id = "switch-to-paladin",
            JobId = 19u,  // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "currentJob() == 19" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        Assert.Contains("\"type\":\"change-job\"", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"jobId\":19", compactJson, StringComparison.Ordinal);
    }

    // GS-RT8: ChangeJobStep with missing jobId defaults to 0
    [Fact]
    public void ChangeJobStep_MissingJobId_DefaultsToZero_GS_RT8()
    {
        /*
         * RED: Will fail to compile -- JobId does not exist yet.
         *
         * CONTRACT: Given JSON with type "change-job" and no jobId field,
         *           When deserialized,
         *           Then JobId == 0u (default, no exception -- validator catches this).
         */

        const string json = """
            {
              "type": "change-job",
              "id": "x"
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var changeJob = Assert.IsType<ChangeJobStep>(step);
        Assert.Equal(0u, changeJob.JobId);  // RED: property does not exist yet
    }

    // =========================================================================
    // RG-RT1 -- Round-trip serialization for RegisterGearsetStep
    // =========================================================================

    [Fact]
    public void RegisterGearsetStep_RoundTrips_RGRT1()
    {
        /*
         * RED: Will fail to compile -- RegisterGearsetStep does not exist yet.
         *
         * CONTRACT: Given RegisterGearsetStep { Id = "register-gearset",
         *                Expect = PredicateExpect { Predicate = "jobGearsetExists(32)" } },
         *           When serialized then deserialized,
         *           Then result is RegisterGearsetStep with Id "register-gearset"
         *                and Expect.Predicate "jobGearsetExists(32)".
         */

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset",
            Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        var result = RoundTrip(step);

        Assert.Equal("register-gearset", result.Id);
        Assert.IsType<PredicateExpect>(result.Expect);
        Assert.Equal("jobGearsetExists(32)", ((PredicateExpect)result.Expect!).Predicate);
    }

    // RG-RT2: RegisterGearsetStep minimal (no Expect) round-trips
    [Fact]
    public void RegisterGearsetStep_Minimal_RoundTrips_RGRT2()
    {
        /*
         * RED: Will fail to compile -- RegisterGearsetStep does not exist yet.
         *
         * CONTRACT: Given RegisterGearsetStep { Id = "rg-minimal" } with no Expect,
         *           When serialized then deserialized,
         *           Then result is RegisterGearsetStep with Id "rg-minimal"
         *                and Expect is null.
         */

        var step = new RegisterGearsetStep
        {
            Id = "rg-minimal"
        };

        var result = RoundTrip(step);

        Assert.Equal("rg-minimal", result.Id);
        Assert.Null(result.Expect);
    }

    // =========================================================================
    // IO-R1 -- InteractObjectStep flat shape round-trips (RED PHASE)
    // Will fail to compile until Builder flattens InteractObjectStep:
    // InteractableId (uint) and Position (Position3?) replace InteractableTarget Target.
    // =========================================================================

    /// <summary>
    /// IO-R1 -- InteractObjectStep with flat InteractableId, Position, Zone, and Expect round-trips.
    /// No "target" wrapper object in serialized JSON.
    /// </summary>
    [Fact]
    public void InteractObjectStep_FlatShape_RoundTrips_IOR1()
    {
        /*
         * RED: Will fail to compile -- InteractObjectStep.InteractableId does not exist yet
         *      (current shape has InteractableTarget Target).
         *
         * CONTRACT: Given InteractObjectStep with InteractableId=2001500, Position=(1,2,3),
         *               Zone="134", Expect="questFlag(65657, 5)",
         *           When serialized then deserialized,
         *           Then InteractableId == 2001500, Position.X == 1, Zone == "134",
         *                and JSON does NOT contain a "target" wrapper.
         *
         * BUILDER GUIDANCE: Flatten InteractObjectStep to use uint InteractableId
         *   and Position3? Position directly on the step. Delete InteractableTarget.
         */

        var step = new InteractObjectStep
        {
            Id = "ring-bell",
            InteractableId = 2001500u,                      // RED: property does not exist yet
            Position = new Position3(1f, 2f, 3f),           // RED: property does not exist yet
            Zone = "134",
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Equal(2001500u, result.InteractableId);
        Assert.NotNull(result.Position);
        Assert.Equal(1f, result.Position!.X, precision: 3);
        Assert.Equal("134", result.Zone);

        // Verify no "target" wrapper in JSON
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.DoesNotContain("\"target\":{", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"interactableId\":2001500", compactJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// IO-R2 -- PickupItemStep with flat shape round-trips.
    /// </summary>
    [Fact]
    public void PickupItemStep_FlatShape_RoundTrips_IOR2()
    {
        /*
         * RED: Will fail to compile -- PickupItemStep.InteractableId does not exist yet.
         *
         * CONTRACT: Given PickupItemStep with InteractableId=2001234, Position=(81.5,7.0,32.2),
         *               Zone="134", Expect="questFlag(65657, 3)",
         *           When serialized then deserialized,
         *           Then InteractableId == 2001234, Position.X == 81.5.
         */

        var step = new PickupItemStep
        {
            Id = "pick-up-letter",
            InteractableId = 2001234u,                      // RED: property does not exist yet
            Position = new Position3(81.5f, 7.0f, 32.2f),   // RED: property does not exist yet
            Zone = "134",
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 3)" }
        };

        var result = RoundTrip(step);

        Assert.Equal(2001234u, result.InteractableId);
        Assert.NotNull(result.Position);
        Assert.Equal(81.5f, result.Position!.X, precision: 3);
        Assert.Equal(7.0f, result.Position!.Y, precision: 3);
        Assert.Equal(32.2f, result.Position!.Z, precision: 3);
    }

    /// <summary>
    /// IO-R3 -- InteractObjectStep with null Position round-trips.
    /// Position key must be absent from JSON output ([JsonIgnore(WhenWritingNull)]).
    /// </summary>
    [Fact]
    public void InteractObjectStep_NullPosition_RoundTrips_IOR3()
    {
        /*
         * RED: Will fail to compile -- InteractObjectStep.InteractableId does not exist yet.
         *
         * CONTRACT: Given InteractObjectStep with InteractableId=2001500, Position=null,
         *           When serialized then deserialized,
         *           Then Position is null and "position" key is absent from JSON.
         *
         * BUILDER GUIDANCE: Apply [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         *   to Position property.
         */

        var step = new InteractObjectStep
        {
            Id = "interact-no-pos",
            InteractableId = 2001500u,                      // RED: property does not exist yet
            Position = null,                                // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Null(result.Position);

        // Verify "position" key absent from JSON
        Assert.DoesNotContain("\"position\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // R-S7 -- DutyStep(kind: "spd") with EntryTargetId + EntryPosition round-trips
    // Spec: docs/SPD_RETRY_PLAN.md scenario R-S7
    // RED: DutyStep.EntryTargetId and DutyStep.EntryPosition do not exist yet.
    // =========================================================================

    /// <summary>
    /// R-S7 -- DutyStep with all entry fields set round-trips through JSON.
    /// Verifies EntryTargetId and EntryPosition serialize and deserialize correctly.
    /// </summary>
    [Fact]
    public void DutyStep_Spd_WithEntryFields_RoundTrips_RS7()
    {
        var step = new DutyStep
        {
            Id = "spd-entry-rt",
            Kind = "spd",
            ContentFinderConditionId = 830,
            EntryTargetId = 1045123u,                              // RED: property does not exist
            EntryPosition = new Position3(100f, 20f, -50f),        // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70020) >= 3" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        // Round-trip fidelity
        Assert.Equal("spd", result.Kind);
        Assert.Equal(830u, result.ContentFinderConditionId);
        Assert.Equal(1045123u, result.EntryTargetId);              // RED: property does not exist
        Assert.NotNull(result.EntryPosition);                      // RED: property does not exist
        Assert.Equal(100f, result.EntryPosition!.X, precision: 3);
        Assert.Equal(20f, result.EntryPosition!.Y, precision: 3);
        Assert.Equal(-50f, result.EntryPosition!.Z, precision: 3);

        // Verify fields present in JSON
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"entryTargetId\":1045123", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"entryPosition\":", compactJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-S8 -- DutyStep without entry fields: fields absent from JSON.
    /// Verifies [JsonIgnore(WhenWritingNull)] is honored on both new nullable fields.
    /// </summary>
    [Fact]
    public void DutyStep_Spd_WithoutEntryFields_FieldsAbsentFromJson_RS8()
    {
        var step = new DutyStep
        {
            Id = "spd-no-entry-rt",
            Kind = "spd",
            ContentFinderConditionId = 830,
            // EntryTargetId and EntryPosition intentionally omitted (null)
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);

        // EntryTargetId and EntryPosition must be absent from JSON when null
        Assert.DoesNotContain("entryTargetId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entryPosition", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D19 -- DutyStep(kind: "duty") with ContentFinderConditionId round-trips
    // Spec: docs/DUTY_AUTODUTY_PLAN.md scenario D19
    // RED: DutyStep.ContentFinderConditionId does not exist yet.
    // =========================================================================

    /// <summary>
    /// D19 -- DutyStep(kind: "duty") with ContentFinderConditionId round-trips.
    /// Verifies the new field serializes and deserializes correctly.
    /// </summary>
    [Fact]
    public void DutyStep_Duty_WithCfcId_RoundTrips_D19()
    {
        var step = new DutyStep
        {
            Id = "enter-dungeon",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80001) >= 3" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Equal("duty", result.Kind);
        Assert.Equal(2u, result.ContentFinderConditionId);
        Assert.IsType<PredicateExpect>(result.Expect);

        // Verify the JSON contains the new field
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"contentFinderConditionId\":2", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"duty\"", compactJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// D19b -- DutyStep(kind: "spd") without ContentFinderConditionId: field absent from JSON.
    /// Verifies [JsonIgnore(WhenWritingNull)] is honored on the nullable field.
    /// </summary>
    [Fact]
    public void DutyStep_Spd_NoCfcId_FieldAbsentFromJson_D19b()
    {
        var step = new DutyStep
        {
            Id = "quest-battle",
            Kind = "spd",
            ContentFinderConditionId = null,
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);

        // ContentFinderConditionId must be absent from JSON when null
        Assert.DoesNotContain("contentFinderConditionId", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// D19c -- DutyStep minimal: kind "duty" + CFC ID only, no optional fields.
    /// </summary>
    [Fact]
    public void DutyStep_Duty_Minimal_RoundTrips_D19c()
    {
        const string json = """
            {
              "type": "duty",
              "id": "minimal-dungeon",
              "kind": "duty",
              "contentFinderConditionId": 42
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        var duty = Assert.IsType<DutyStep>(step);

        Assert.Equal("duty", duty.Kind);
        Assert.Equal(42u, duty.ContentFinderConditionId);
        Assert.Null(duty.Expect);
    }

    // =========================================================================
    // A14 -- UseActionStep with Location round-trips through JSON
    // Spec: docs/ACTION_LOCATION_PLAN.md -- GWT A14
    // =========================================================================

    [Fact]
    public void UseActionStep_WithLocation_RoundTrips_A14()
    {
        /*
         * CONTRACT: Given a UseActionStep with Location set (NpcId=2001234, Zone=130, Position=(100,5,200)),
         *           When serialized to JSON and deserialized back,
         *           Then all fields including Location are preserved.
         *           The JSON includes a "location" key with the nested object.
         */

        var step = new UseActionStep
        {
            Id = "action-with-location",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = 2001234u,
            Location = new NpcLocation(2001234u, 130, new Position3(100f, 5f, 200f)),
            Expect = new PredicateExpect { Predicate = "questFlag(83014, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Equal(ActionType.Action, result.ActionType);
        Assert.Equal(31u, result.ActionId);
        Assert.Equal(2001234u, result.TargetNpcId);
        Assert.NotNull(result.Location);
        Assert.Equal(2001234u, result.Location!.NpcId);
        Assert.Equal(130, result.Location.Zone);
        Assert.Equal(100f, result.Location.Position.X, precision: 1);
        Assert.Equal(5f, result.Location.Position.Y, precision: 1);
        Assert.Equal(200f, result.Location.Position.Z, precision: 1);

        // Verify "location" key present in JSON
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"location\":", compactJson, StringComparison.Ordinal);
    }

    // =========================================================================
    // A15 -- UseActionStep without Location round-trips (no "location" key in JSON)
    // Spec: docs/ACTION_LOCATION_PLAN.md -- GWT A15
    // =========================================================================

    [Fact]
    public void UseActionStep_WithoutLocation_NoLocationKeyInJson_A15()
    {
        /*
         * CONTRACT: Given a UseActionStep with Location = null,
         *           When serialized to JSON,
         *           Then the JSON does NOT contain a "location" key
         *           (due to [JsonIgnore(WhenWritingNull)]).
         */

        var step = new UseActionStep
        {
            Id = "action-no-location",
            ActionType = ActionType.Action,
            ActionId = 31u,
            TargetNpcId = 2001234u,
            Location = null,
            Expect = new PredicateExpect { Predicate = "questFlag(83015, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Equal(31u, result.ActionId);
        Assert.Null(result.Location);

        // "location" key must be absent from JSON
        Assert.DoesNotContain("\"location\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // A16 -- UseEmoteStep with Location round-trips through JSON
    // Spec: docs/ACTION_LOCATION_PLAN.md -- GWT A16
    // =========================================================================

    [Fact]
    public void UseEmoteStep_WithLocation_RoundTrips_A16()
    {
        /*
         * CONTRACT: Given a UseEmoteStep with Location set (NpcId=1000789, Zone=130, Position=(50,0,80)),
         *           When serialized to JSON and deserialized back,
         *           Then all fields including Location are preserved.
         */

        var step = new UseEmoteStep
        {
            Id = "emote-with-location",
            EmoteId = 17u,
            TargetNpcId = 1000789u,
            Motion = true,
            Location = new NpcLocation(1000789u, 130, new Position3(50f, 0f, 80f)),
            Expect = new PredicateExpect { Predicate = "questFlag(84016, 3)" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        Assert.Equal(17u, result.EmoteId);
        Assert.Equal(1000789u, result.TargetNpcId);
        Assert.True(result.Motion);
        Assert.NotNull(result.Location);
        Assert.Equal(1000789u, result.Location!.NpcId);
        Assert.Equal(130, result.Location.Zone);
        Assert.Equal(50f, result.Location.Position.X, precision: 1);
        Assert.Equal(0f, result.Location.Position.Y, precision: 1);
        Assert.Equal(80f, result.Location.Position.Z, precision: 1);

        // Verify "location" key present in JSON
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"location\":", compactJson, StringComparison.Ordinal);
    }

    // =========================================================================
    // A9 -- AethernetStep round-trip serialization
    // Spec: docs/AETHERNET_STEP_PLAN.md scenario A9
    // RED: AethernetStep does not exist yet in Step.cs.
    // =========================================================================

    /// <summary>
    /// A9 -- AethernetStep round-trips through JSON with correct From, To, FromPosition,
    /// and the "aethernet" type discriminator.
    /// RED: Will fail to compile until Builder adds AethernetStep to Step.cs,
    /// [JsonDerivedType(typeof(AethernetStep), "aethernet")] to Step,
    /// and [JsonSerializable(typeof(AethernetStep))] to QuestForgeJsonContext.
    /// </summary>
    [Fact]
    public void AethernetStep_RoundTrips_A9()
    {
        /*
         * CONTRACT: Given AethernetStep { Id="aethernet-to-guild", From=8, To=48,
         *                FromPosition=(-218.9, 16.0, 51.4) },
         *           When serialized as Step then deserialized,
         *           Then result is AethernetStep with From==8, To==48,
         *                FromPosition.X approximately -218.9.
         *           AND compact JSON contains "type":"aethernet", "from":8, "to":48,
         *                and "fromPosition".
         *
         * BUILDER GUIDANCE:
         *   1. Add AethernetStep class to Step.cs:
         *        public sealed class AethernetStep : Step
         *        {
         *            public uint From { get; init; }
         *            public uint To { get; init; }
         *            public Position3 FromPosition { get; init; } = default!;
         *        }
         *   2. Add [JsonDerivedType(typeof(AethernetStep), "aethernet")] to Step.
         *   3. Add [JsonSerializable(typeof(AethernetStep))] to QuestForgeJsonContext.
         */

        // Arrange
        var step = new AethernetStep
        {
            Id = "aethernet-to-guild",
            From = 8,
            To = 48,
            FromPosition = new Position3(-218.9f, 16.0f, 51.4f)
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        // Assert -- discriminator present
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"aethernet\"", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"from\":8", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"to\":48", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"fromPosition\":", compactJson, StringComparison.Ordinal);

        // Assert -- round-trip fidelity
        Assert.Equal("aethernet-to-guild", result.Id);
        Assert.Equal(8u, result.From);
        Assert.Equal(48u, result.To);
        Assert.Equal(-218.9f, result.FromPosition.X, precision: 1);
        Assert.Equal(16.0f, result.FromPosition.Y, precision: 1);
        Assert.Equal(51.4f, result.FromPosition.Z, precision: 1);
    }

    /// <summary>
    /// A9b -- AethernetStep with Expect round-trips: verifies base Step property preserved.
    /// </summary>
    [Fact]
    public void AethernetStep_WithExpect_RoundTrips_A9b()
    {
        var step = new AethernetStep
        {
            Id = "aethernet-with-expect",
            From = 8,
            To = 48,
            FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
            Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
        };

        var result = RoundTrip(step);

        Assert.Equal(8u, result.From);
        Assert.Equal(48u, result.To);
        var expect = Assert.IsType<PredicateExpect>(result.Expect);
        Assert.Equal("playerZone() == 128", expect.Predicate);
    }

    /// <summary>
    /// A9c -- AethernetStep missing fields in JSON: From/To default to 0, FromPosition to default.
    /// Structural validation is the validator's job, not the deserializer's.
    /// </summary>
    [Fact]
    public void AethernetStep_MissingFields_DefaultToZero_A9c()
    {
        const string json = """
            {
              "type": "aethernet",
              "id": "aethernet-minimal"
            }
            """;

        var step = System.Text.Json.JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var aethernet = Assert.IsType<AethernetStep>(step);
        Assert.Equal(0u, aethernet.From);
        Assert.Equal(0u, aethernet.To);
    }

    // =========================================================================
    // OC_RT1 -- OpenCoffersStep round-trip serialization
    // =========================================================================

    /// <summary>
    /// OC_RT1 -- OpenCoffersStep round-trips through JSON with correct Id, Expect, and discriminator.
    /// RED: Will fail to compile until Builder implements OpenCoffersStep in Step.cs
    /// with [JsonDerivedType(typeof(OpenCoffersStep), "open-coffers")] and
    /// [JsonSerializable(typeof(OpenCoffersStep))] in QuestForgeJsonContext.cs.
    /// </summary>
    [Fact]
    public void OpenCoffersStep_RoundTrips_OC_RT1()
    {
        /*
         * CONTRACT: Given OpenCoffersStep { Id = "open-coffers",
         *                Expect = PredicateExpect { Predicate = "not inventoryHasCoffers()" } },
         *           When serialized as Step then deserialized,
         *           Then result is OpenCoffersStep with Id "open-coffers"
         *                and Expect is PredicateExpect with Predicate "not inventoryHasCoffers()".
         *           AND the serialized JSON contains "type":"open-coffers" discriminator.
         *
         * BUILDER GUIDANCE:
         *   1. Add sealed class OpenCoffersStep : Step { } to Step.cs.
         *   2. Add [JsonDerivedType(typeof(OpenCoffersStep), "open-coffers")] to Step.
         *   3. Add [JsonSerializable(typeof(OpenCoffersStep))] to QuestForgeJsonContext.
         */

        // Arrange
        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var result = RoundTrip(step);

        // Assert -- discriminator present
        var compactJson = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("\"type\":\"open-coffers\"", compactJson, StringComparison.Ordinal);

        // Assert -- round-trip fidelity
        Assert.Equal("open-coffers", result.Id);
        var expect = Assert.IsType<PredicateExpect>(result.Expect);
        Assert.Equal("not inventoryHasCoffers()", expect.Predicate);
    }

}
