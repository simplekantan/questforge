using System.Text.Json;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Schema;

/// <summary>
/// RED PHASE: Tests for STJ serialization/deserialization of the new AethernetRouteHint
/// type within RouteHint (Issue #25, Component 2).
///
/// ALL tests will fail to compile until Builder:
///   1. Adds AethernetRouteHint record to SharedValueTypes.cs
///   2. Changes RouteHint.Aethernet from uint[]? to AethernetRouteHint?
///   3. Registers AethernetRouteHint in QuestForgeJsonContext
///
/// Tests 2.8 (old array form throws JsonException) will compile once the type exists
/// but needs no message check per spec.
/// </summary>
public sealed class RouteHintSerializationTests
{
    private static readonly JsonSerializerOptions Opts = QuestForgeJsonContext.QuestFileOptions;

    // =========================================================================
    // 2.1 — Serialize AethernetRouteHint with To only → {"to":77}
    // =========================================================================

    [Fact]
    public void SER_2_1_ToOnly_SerializesWithoutFrom()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint and
         * changes RouteHint.Aethernet to AethernetRouteHint?.
         *
         * CONTRACT: Given RouteHint(Aethernet: new AethernetRouteHint(null, 77)),
         *           When serialized with QuestFileOptions,
         *           Then the JSON contains "aethernet":{"to":77} and no "from" property.
         *
         * BUILDER GUIDANCE: STJ camelCase naming policy writes From→"from", To→"to".
         * Since From is null, it should be omitted (verify JsonIgnoreCondition or
         * that the source-gen context omits null optional properties by default).
         */

        // RED: AethernetRouteHint does not exist yet
        var hint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 77u));
        var json = JsonSerializer.Serialize(hint, Opts);
        var compact = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        Assert.Contains("\"to\":77", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("\"from\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 2.2 — Serialize AethernetRouteHint with From and To → {"from":125,"to":77}
    // =========================================================================

    [Fact]
    public void SER_2_2_FromAndTo_SerializesBothFields()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given RouteHint(Aethernet: new AethernetRouteHint(125, 77)),
         *           When serialized with QuestFileOptions,
         *           Then the JSON contains both "from":125 and "to":77.
         */

        // RED: AethernetRouteHint does not exist yet
        var hint = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 77u));
        var json = JsonSerializer.Serialize(hint, Opts);
        var compact = json.Replace(" ", "").Replace("\r", "").Replace("\n", "");

        Assert.Contains("\"from\":125", compact, StringComparison.Ordinal);
        Assert.Contains("\"to\":77", compact, StringComparison.Ordinal);
    }

    // =========================================================================
    // 2.3 — Deserialize {"aethernet":{"to":77}} → AethernetRouteHint(null, 77)
    // =========================================================================

    [Fact]
    public void SER_2_3_ToOnly_DeserializesWithFromNull()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint and
         * registers it in QuestForgeJsonContext.
         *
         * CONTRACT: Given JSON {"aethernet":{"to":77}},
         *           When deserialized into RouteHint,
         *           Then Aethernet.To == 77 and Aethernet.From is null.
         */

        const string json = """{"aethernet":{"to":77}}""";
        // RED: RouteHint.Aethernet type mismatch — currently uint[]? not AethernetRouteHint?
        var hint = JsonSerializer.Deserialize<RouteHint>(json, Opts);

        Assert.NotNull(hint);
        Assert.NotNull(hint!.Aethernet);
        Assert.Equal(77u, hint.Aethernet!.To);
        Assert.Null(hint.Aethernet.From);
    }

    // =========================================================================
    // 2.4 — Deserialize {"aethernet":{"from":125,"to":77}} → AethernetRouteHint(125,77)
    // =========================================================================

    [Fact]
    public void SER_2_4_FromAndTo_DeserializesBothFields()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint and registers it.
         *
         * CONTRACT: Given JSON {"aethernet":{"from":125,"to":77}},
         *           When deserialized into RouteHint,
         *           Then Aethernet.From == 125 and Aethernet.To == 77.
         */

        const string json = """{"aethernet":{"from":125,"to":77}}""";
        // RED: RouteHint.Aethernet type mismatch
        var hint = JsonSerializer.Deserialize<RouteHint>(json, Opts);

        Assert.NotNull(hint);
        Assert.NotNull(hint!.Aethernet);
        Assert.Equal(125u, hint.Aethernet!.From);
        Assert.Equal(77u, hint.Aethernet.To);
    }

    // =========================================================================
    // 2.5 — Null aethernet field deserializes to Aethernet == null
    // =========================================================================

    [Fact]
    public void SER_2_5_NullAethernet_DeserializesToNull()
    {
        /*
         * RED: Will fail until RouteHint.Aethernet is changed to AethernetRouteHint?.
         *
         * CONTRACT: Given JSON {"aetheryte":"Limsa Lominsa Lower Decks"},
         *           When deserialized into RouteHint,
         *           Then Aethernet is null.
         */

        const string json = """{"aetheryte":"Limsa Lominsa Lower Decks"}""";
        var hint = JsonSerializer.Deserialize<RouteHint>(json, Opts);

        Assert.NotNull(hint);
        Assert.Null(hint!.Aethernet);
    }

    // =========================================================================
    // 2.6 — Round-trip: serialize then deserialize produces equal value
    // =========================================================================

    [Fact]
    public void SER_2_6_RoundTrip_ProducesEqualValue()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint and registers it.
         *
         * CONTRACT: Given a RouteHint with non-null Aethernet,
         *           When serialized then deserialized,
         *           Then the deserialized value equals the original.
         */

        // RED: AethernetRouteHint does not exist yet
        var original = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 77u));
        var json = JsonSerializer.Serialize(original, Opts);
        var restored = JsonSerializer.Deserialize<RouteHint>(json, Opts);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Aethernet);
        Assert.Equal(original.Aethernet!.From, restored.Aethernet!.From);
        Assert.Equal(original.Aethernet.To, restored.Aethernet.To);
    }

    // =========================================================================
    // 2.7 — RouteHint without aethernet still serializes correctly
    // =========================================================================

    [Fact]
    public void SER_2_7_NoAethernet_RouteHintSerializesCleanly()
    {
        /*
         * CONTRACT: Given RouteHint(Aetheryte: "Limsa", Aethernet: null),
         *           When serialized,
         *           Then JSON does not contain "aethernet".
         */

        var hint = new RouteHint(Aetheryte: "Limsa Lominsa Lower Decks", Aethernet: null);
        var json = JsonSerializer.Serialize(hint, Opts);

        Assert.DoesNotContain("aethernet", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aetheryte", json, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 2.8 — Old array form [77] throws JsonException (breaking change)
    // =========================================================================

    [Fact]
    public void SER_2_8_OldArrayForm_ThrowsJsonException()
    {
        /*
         * RED: Currently parses successfully as uint[]?. After the type change to
         * AethernetRouteHint?, an array input [77] is not a valid object → JsonException.
         * Per spec: just assert JsonException thrown, no message check.
         *
         * CONTRACT: Given JSON {"aethernet":[77]},
         *           When deserialized into RouteHint with QuestFileOptions,
         *           Then JsonException is thrown (old array form is a breaking change).
         *
         * BUILDER GUIDANCE: The type change from uint[]? to AethernetRouteHint? makes
         * array inputs structurally invalid. No additional code needed — STJ will
         * reject the array when it expects an object.
         */

        const string json = """{"aethernet":[77]}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RouteHint>(json, Opts));
    }

    // =========================================================================
    // 2.9 — AethernetRouteHint is registered in QuestForgeJsonContext
    // =========================================================================

    [Fact]
    public void SER_2_9_AethernetRouteHint_IsRegisteredInJsonContext()
    {
        /*
         * RED: Will fail until Builder adds [JsonSerializable(typeof(AethernetRouteHint))]
         * to QuestForgeJsonContext.
         *
         * CONTRACT: Given QuestForgeJsonContext.Default,
         *           When GetTypeInfo(typeof(AethernetRouteHint)) is called,
         *           Then it returns a non-null type info (type is registered in source-gen context).
         *
         * BUILDER GUIDANCE: Add to QuestForgeJsonContext.cs:
         *   [JsonSerializable(typeof(AethernetRouteHint))]
         */

        // RED: AethernetRouteHint does not exist yet
        var typeInfo = QuestForgeJsonContext.Default.GetTypeInfo(typeof(AethernetRouteHint));

        Assert.NotNull(typeInfo);
    }
}
