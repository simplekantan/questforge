using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

/// <summary>
/// Integration-level tests that exercise the full serialization path rather than
/// individual types in isolation. Catches gaps the unit round-trip tests miss.
/// </summary>
public class IntegrationTests
{
    // -------------------------------------------------------------------------
    // Full QuestDefinition round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void QuestDefinition_FullRoundTrip_PreservesAllStructure()
    {
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 65657,
            Name = "Close to Home",
            Expansion = "arr",
            Category = "msq",
            Enabled = true,
            SupportStatus = new SupportStatus
            {
                Implementation = "complete",
                KnownIssues = [],
                MinigameSkippable = null
            },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements
            {
                MinLevel = 1,
                Prereqs = [new PrerequisiteRef(65656, "complete")]
            },
            AcceptFrom = new NpcLocation(1000789, 128, new Position3(9.5f, 40f, 14.2f)),
            Chain = new Chain
            {
                Previous = [65656],
                Next = [new ChainNext("default", 65658)]
            },
            Contributors = ["@alice"],
            Notes = "First quest in the chain.",
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "travel-to-questgiver",
                            Destination = new TravelDestination(128, new Position3(9.5f, 40f, 14.2f)),
                            RouteHint = new RouteHint("Ul'dah - Steps of Nald"),
                            Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
                        },
                        new TalkStep
                        {
                            Id = "talk-to-baderon",
                            Target = new NpcLocation(1000790, 129, new Position3(-10.2f, 40f, 14.5f)),
                            DialogueChoices = [new DialogueChoice("yesno", "Are you ready?", "yes")],
                            Recover = new RecoverConfig
                            {
                                OnTimeout = new GotoRecoverAction { StepId = "travel-to-questgiver" }
                            },
                            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 1" }
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 1,
                    SkipIf = "questFlag(65657, 5)",
                    Steps =
                    [
                        new BranchStep
                        {
                            Id = "fight-or-talk",
                            Branches =
                            [
                                new BranchCase(
                                    "playerLevel() >= 15",
                                    [new CombatStep { Id = "fight-enemy", Target = new CombatTarget("nearestHostile", 20f), Expect = new PredicateExpect { Predicate = "not playerInCombat()" } }]
                                ),
                                new BranchCase(
                                    "default",
                                    [new AwaitUserStep { Id = "wait-user", Reason = "Please defeat the enemy.", Expect = new PredicateExpect { Predicate = "questFlag(65657, 3)" } }]
                                )
                            ],
                            Expect = new PredicateExpect { Predicate = "questSequence(65657) >= 2" }
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 255,
                    Steps =
                    [
                        new TurnInStep
                        {
                            Id = "turn-in",
                            Target = new NpcLocation(1000790, 129, new Position3(-10.2f, 40f, 14.5f)),
                            Expect = new PredicateExpect { Predicate = "isQuestComplete(65657)" }
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(quest, QuestForgeJsonContext.QuestFileOptions);
        var result = JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);

        Assert.NotNull(result);

        // Top-level metadata
        Assert.Equal("1.0.0", result.SchemaVersion);
        Assert.Equal(65657u, result.Id);
        Assert.Equal("Close to Home", result.Name);
        Assert.Equal("arr", result.Expansion);
        Assert.Equal("msq", result.Category);
        Assert.True(result.Enabled);
        Assert.Equal("complete", result.SupportStatus!.Implementation);
        Assert.Equal("7.4", result.LastVerifiedPatch);
        Assert.Equal(1, result.Requirements!.MinLevel);
        Assert.Single(result.Requirements.Prereqs);
        Assert.Equal(65656u, result.Requirements.Prereqs[0].QuestId);
        Assert.Equal("complete", result.Requirements.Prereqs[0].State);
        Assert.Equal(1000789u, result.AcceptFrom!.NpcId);
        Assert.Equal(128, result.AcceptFrom.Zone);
        Assert.Single(result.Chain!.Previous);
        Assert.Equal(65656u, result.Chain.Previous[0]);
        Assert.Single(result.Chain.Next);
        Assert.Equal("default", result.Chain.Next[0].When);
        Assert.Equal(65658u, result.Chain.Next[0].QuestId);
        Assert.Equal(["@alice"], result.Contributors ?? []);
        Assert.Equal("First quest in the chain.", result.Notes);

        // Sequences
        Assert.Equal(3, result.Sequences.Length);
        Assert.Equal(0, result.Sequences[0].Sequence);
        Assert.Equal(1, result.Sequences[1].Sequence);
        Assert.Equal(255, result.Sequences[2].Sequence);

        // Sequence 0 — step types and nested recovery
        Assert.Null(result.Sequences[0].SkipIf);
        var travel = Assert.IsType<TravelStep>(result.Sequences[0].Steps[0]);
        Assert.Equal("travel-to-questgiver", travel.Id);
        Assert.Equal(128, travel.Destination.Zone);
        Assert.Equal("Ul'dah - Steps of Nald", travel.RouteHint!.Aetheryte);
        var talkPredict = Assert.IsType<PredicateExpect>(travel.Expect);
        Assert.Equal("playerZone() == 128", talkPredict.Predicate);

        var talk = Assert.IsType<TalkStep>(result.Sequences[0].Steps[1]);
        Assert.Equal("talk-to-baderon", talk.Id);
        Assert.Single(talk.DialogueChoices);
        Assert.Equal("yesno", talk.DialogueChoices[0].Type);
        var onTimeout = Assert.IsType<GotoRecoverAction>(talk.Recover!.OnTimeout);
        Assert.Equal("travel-to-questgiver", onTimeout.StepId);

        // Sequence 1 — skipIf and nested branch with nested steps
        Assert.Equal("questFlag(65657, 5)", result.Sequences[1].SkipIf);
        var branch = Assert.IsType<BranchStep>(result.Sequences[1].Steps[0]);
        Assert.Equal(2, branch.Branches.Length);
        Assert.Equal("playerLevel() >= 15", branch.Branches[0].When);
        Assert.IsType<CombatStep>(branch.Branches[0].Steps[0]);
        Assert.Equal("default", branch.Branches[1].When);
        Assert.IsType<AwaitUserStep>(branch.Branches[1].Steps[0]);

        // Sequence 255 — turn-in
        var turnIn = Assert.IsType<TurnInStep>(result.Sequences[2].Steps[0]);
        Assert.Equal("turn-in", turnIn.Id);
        var turnInExpect = Assert.IsType<PredicateExpect>(turnIn.Expect);
        Assert.Equal("isQuestComplete(65657)", turnInExpect.Predicate);
    }

    // -------------------------------------------------------------------------
    // FragmentStep with Params round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void FragmentStep_WithParams_RoundTrips()
    {
        // Params uses Dictionary<string, JsonElement> — a trickier type than most.
        // This test verifies params survive serialization with their original JSON shape.
        const string originalJson = """
            {
              "type": "fragment",
              "id": "travel-to-gridania",
              "ref": "travel/uldah-to-gridania",
              "params": {
                "finalPosition": { "x": 81.5, "y": 7.0, "z": 32.2 },
                "label": "Destination",
                "count": 3
              },
              "expect": "playerZone() == 132"
            }
            """;

        var step = JsonSerializer.Deserialize<Step>(originalJson, QuestForgeJsonContext.QuestFileOptions);
        var fragment = Assert.IsType<FragmentStep>(step);

        Assert.Equal("travel-to-gridania", fragment.Id);
        Assert.Equal("travel/uldah-to-gridania", fragment.Ref);
        Assert.NotNull(fragment.Params);
        Assert.Equal(3, fragment.Params!.Count);

        // Position object param
        Assert.True(fragment.Params.ContainsKey("finalPosition"));
        var pos = fragment.Params["finalPosition"];
        Assert.Equal(JsonValueKind.Object, pos.ValueKind);
        Assert.Equal(81.5, pos.GetProperty("x").GetDouble(), precision: 1);

        // String param
        Assert.Equal("Destination", fragment.Params["label"].GetString());

        // Number param
        Assert.Equal(3, fragment.Params["count"].GetInt32());

        // Verify Params survive a full serialize → deserialize cycle
        var json = JsonSerializer.Serialize<Step>(fragment, QuestForgeJsonContext.QuestFileOptions);
        var roundTripped = Assert.IsType<FragmentStep>(
            JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions));

        Assert.Equal(3, roundTripped.Params!.Count);
        Assert.Equal("Destination", roundTripped.Params["label"].GetString());
        Assert.Equal(3, roundTripped.Params["count"].GetInt32());
    }
}