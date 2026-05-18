using System.Text.Json;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Integration;

public sealed class StopDistanceDeserializationTest
{
    [Fact]
    public void AttunementStep_StopDistance_Integer_DeserializesCorrectly()
    {
        const string json = """
            {
              "type": "attune",
              "target": { "value": 9 },
              "location": { "npcId": 9, "zone": 130, "position": { "x": 1.0, "y": 0.0, "z": 1.0 } },
              "id": "test",
              "zone": "130",
              "expect": null,
              "skipIf": null,
              "stopDistance": 6,
              "recover": null,
              "retry": null,
              "preconditions": null,
              "notes": null
            }
            """;

        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        Assert.NotNull(step);
        Assert.IsType<AttunementStep>(step);
        Assert.Equal(6.0f, step.StopDistance);
    }

    [Fact]
    public void AttunementStep_StopDistance_Float_DeserializesCorrectly()
    {
        const string json = """
            {
              "type": "attune",
              "target": { "value": 9 },
              "location": { "npcId": 9, "zone": 130, "position": { "x": 1.0, "y": 0.0, "z": 1.0 } },
              "id": "test",
              "zone": "130",
              "expect": null,
              "skipIf": null,
              "stopDistance": 6.0,
              "recover": null,
              "retry": null,
              "preconditions": null,
              "notes": null
            }
            """;

        var step = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        Assert.NotNull(step);
        Assert.IsType<AttunementStep>(step);
        Assert.Equal(6.0f, step.StopDistance);
    }

    [Fact]
    public void QuestDefinition_AttunementStep_StopDistance_DeserializesCorrectly()
    {
        const string json = """
            {
              "schemaVersion": "1.0.0",
              "id": 66104,
              "name": "Test",
              "expansion": "arr",
              "category": "msq",
              "enabled": true,
              "supportStatus": { "implementation": "partial", "knownIssues": [], "minigameSkippable": null },
              "lastVerifiedPatch": "unknown",
              "requirements": { "minLevel": 1, "maxLevel": null, "requiredJob": null, "requiredStartingClass": null, "prereqs": [] },
              "acceptFrom": { "npcId": 1, "zone": 130, "position": { "x": 0, "y": 0, "z": 0 } },
              "chain": null, "rewardOverride": null, "contributors": null, "notes": null,
              "sequences": [
                {
                  "sequence": 1,
                  "skipIf": null,
                  "steps": [
                    {
                      "type": "attune",
                      "target": { "value": 9 },
                      "location": { "npcId": 9, "zone": 130, "position": { "x": 1.0, "y": 0.0, "z": 1.0 } },
                      "id": "attune-9",
                      "zone": "130",
                      "expect": null,
                      "skipIf": null,
                      "stopDistance": 6,
                      "recover": null,
                      "retry": null,
                      "preconditions": null,
                      "notes": null
                    }
                  ]
                }
              ]
            }
            """;

        var def = JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);

        Assert.NotNull(def);
        var step = Assert.Single(def.Sequences[0].Steps);
        var attune = Assert.IsType<AttunementStep>(step);
        Assert.Equal(6.0f, attune.StopDistance);
    }
}
