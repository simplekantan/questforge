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
        var step = new CombatStep
        {
            Id = "fight-bandits",
            Target = new CombatTarget("nearestHostile", 20f),
            Expect = new PredicateExpect { Predicate = "not playerInCombat()" }
        };

        var result = RoundTrip(step);
        Assert.Equal("nearestHostile", result.Target.Kind);
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

    [Fact]
    public void UseEmoteStep_RoundTrips()
    {
        var step = new UseEmoteStep
        {
            Id = "salute",
            EmoteId = 7,
            Target = new NpcLocation(1000789, 132, new Position3(0f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(7u, result.EmoteId);
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
            Id = "heavy-swing-rocks",
            ActionId = 31,
            Target = new ActionTarget { Kind = "object", InteractableId = 2001234, Zone = 134 },
            RepeatUntilExpect = true,
            Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
        };

        var result = RoundTrip(step);
        Assert.Equal(31u, result.ActionId);
        Assert.True(result.RepeatUntilExpect);
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
}