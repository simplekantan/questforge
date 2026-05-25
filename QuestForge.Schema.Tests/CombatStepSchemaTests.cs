using System.Text.Json;
using System.Text.Json.Nodes;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Schema.Tests;

/// <summary>
/// SC-* schema round-trip tests for the reshaped CombatStep.
/// All tests should PASS immediately once the schema types are in place.
/// If they fail, the schema implementation is incomplete.
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G6
/// </summary>
public sealed class CombatStepSchemaTests
{
    private static T RoundTrip<T>(T value) where T : Step
    {
        var json = JsonSerializer.Serialize<Step>(value, QuestForgeJsonContext.QuestFileOptions);
        var result = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);
        return Assert.IsType<T>(result);
    }

    // =========================================================================
    // SC-roundtrip — full CombatStep with all fields survives serialization
    // =========================================================================

    [Fact]
    public void SC_RoundTrip_FullCombatStep_AllFieldsSurvive()
    {
        /*
         * Serialize then deserialize a CombatStep with all fields set.
         * Assert KillEnemyDataIds, Spawn, Location, and Expect survive.
         * Assert the "type" discriminator is "combat".
         */
        var step = new CombatStep
        {
            Id = "defeat-enemies",
            KillEnemyDataIds = [100u, 200u],
            Spawn = CombatSpawn.OverworldEnemies,
            Location = new NpcLocation(9876u, 130, new Position3(50f, 0f, 75f)),
            Expect = new PredicateExpect { Predicate = "questVariable(66104, 0) >= 3" }
        };

        var result = RoundTrip(step);

        Assert.Equal("defeat-enemies", result.Id);
        Assert.Equal(2, result.KillEnemyDataIds.Length);
        Assert.Contains(100u, result.KillEnemyDataIds);
        Assert.Contains(200u, result.KillEnemyDataIds);
        Assert.Equal(CombatSpawn.OverworldEnemies, result.Spawn);
        Assert.NotNull(result.Location);
        Assert.Equal(9876u, result.Location!.NpcId);
        Assert.Equal(130, result.Location.Zone);
        Assert.Equal(50f, result.Location.Position.X);
        Assert.IsType<PredicateExpect>(result.Expect);
        Assert.Equal("questVariable(66104, 0) >= 3",
            ((PredicateExpect)result.Expect!).Predicate);

        // Verify the type discriminator
        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("combat", doc.RootElement.GetProperty("type").GetString());
    }

    // =========================================================================
    // SC-spawn-enum-camelcase — AutoOnEnterArea serializes as "autoOnEnterArea"
    // =========================================================================

    [Fact]
    public void SC_SpawnEnumCamelCase_AutoOnEnterArea_SerializesAsCamelCase()
    {
        /*
         * Assert Spawn=AutoOnEnterArea serializes as "autoOnEnterArea" (UseStringEnumConverter camelCase)
         * and round-trips back to AutoOnEnterArea.
         */
        var step = new CombatStep
        {
            Id = "auto-spawn-test",
            Spawn = CombatSpawn.AutoOnEnterArea
        };

        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var doc = JsonDocument.Parse(json);

        // The "spawn" field must be "autoOnEnterArea" (camelCase, not "AutoOnEnterArea")
        Assert.True(doc.RootElement.TryGetProperty("spawn", out var spawnEl),
            $"Expected 'spawn' property in JSON: {json}");
        Assert.Equal("autoOnEnterArea", spawnEl.GetString());

        // Round-trip preserves the enum value
        var result = RoundTrip(step);
        Assert.Equal(CombatSpawn.AutoOnEnterArea, result.Spawn);
    }

    [Fact]
    public void SC_SpawnEnumCamelCase_OverworldEnemies_SerializesAsCamelCase()
    {
        /*
         * Assert Spawn=OverworldEnemies serializes as "overworldEnemies".
         */
        var step = new CombatStep
        {
            Id = "overworld-spawn-test",
            Spawn = CombatSpawn.OverworldEnemies
        };

        var json = JsonSerializer.Serialize<Step>(step, QuestForgeJsonContext.QuestFileOptions);
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("spawn", out var spawnEl),
            $"Expected 'spawn' property in JSON: {json}");
        Assert.Equal("overworldEnemies", spawnEl.GetString());
    }

    // =========================================================================
    // SC-defaults — minimal JSON deserializes to correct defaults
    // =========================================================================

    [Fact]
    public void SC_Defaults_MinimalJson_HasCorrectDefaults()
    {
        /*
         * Deserialize { "type":"combat", "id":"x" } → KillEnemyDataIds=[], Spawn=AutoOnEnterArea, Location=null.
         */
        var minimalJson = """{"type":"combat","id":"x"}""";
        var step = JsonSerializer.Deserialize<Step>(minimalJson, QuestForgeJsonContext.QuestFileOptions);

        var combat = Assert.IsType<CombatStep>(step);
        Assert.Equal("x", combat.Id);
        Assert.NotNull(combat.KillEnemyDataIds);
        Assert.Empty(combat.KillEnemyDataIds);
        Assert.Equal(CombatSpawn.AutoOnEnterArea, combat.Spawn);
        Assert.Null(combat.Location);
        Assert.Null(combat.Expect);
    }

    // =========================================================================
    // SC-no-combattarget — old "target" field with CombatTarget shape is silently ignored
    // =========================================================================

    [Fact]
    public void SC_NoCombatTarget_OldTargetField_IsIgnoredNoException()
    {
        /*
         * Assert that a JSON with the old {"target":{"kind":"nearestHostile"}} shape
         * does NOT throw and does NOT populate any CombatStep property.
         * CombatTarget no longer exists — the old field is silently ignored by STJ.
         */
        var oldJson = """{"type":"combat","id":"old-step","target":{"kind":"nearestHostile","radius":10}}""";

        // Should not throw
        var step = JsonSerializer.Deserialize<Step>(oldJson, QuestForgeJsonContext.QuestFileOptions);

        var combat = Assert.IsType<CombatStep>(step);
        Assert.Equal("old-step", combat.Id);
        // Old "target" is gone — no mapping, silently ignored
        Assert.Empty(combat.KillEnemyDataIds);
        Assert.Equal(CombatSpawn.AutoOnEnterArea, combat.Spawn);
        Assert.Null(combat.Location);
    }

    // =========================================================================
    // SC-no-combattarget-type — CombatTarget type no longer exists in the schema assembly
    // =========================================================================

    [Fact]
    public void SC_NoCombatTargetType_TypeDoesNotExistInAssembly()
    {
        /*
         * Assert that the CombatTarget type is gone from QuestForge.Schema.
         * This is a compile-time guarantee (if the type existed, the deletion would fail),
         * but we add a runtime assertion to pin the deletion explicitly.
         */
        var assembly = typeof(CombatStep).Assembly;
        var combatTargetType = assembly.GetType("QuestForge.Schema.CombatTarget");

        Assert.Null(combatTargetType);
    }
}
