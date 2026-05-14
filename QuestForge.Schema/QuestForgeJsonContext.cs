using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Schema;

[JsonSerializable(typeof(QuestDefinition))]
[JsonSerializable(typeof(FragmentDefinition))]
// Step subtypes — all must be registered explicitly for source-gen + JsonPolymorphic
[JsonSerializable(typeof(TravelStep))]
[JsonSerializable(typeof(TalkStep))]
[JsonSerializable(typeof(InteractObjectStep))]
[JsonSerializable(typeof(PickupItemStep))]
[JsonSerializable(typeof(AcceptStep))]
[JsonSerializable(typeof(TurnInStep))]
[JsonSerializable(typeof(CombatStep))]
[JsonSerializable(typeof(DutyStep))]
[JsonSerializable(typeof(CutsceneStep))]
[JsonSerializable(typeof(SayChatMessageStep))]
[JsonSerializable(typeof(UseEmoteStep))]
[JsonSerializable(typeof(UseItemStep))]
[JsonSerializable(typeof(UseActionStep))]
[JsonSerializable(typeof(EquipGearForQuestStep))]
[JsonSerializable(typeof(EquipBestGearStep))]
[JsonSerializable(typeof(ChangeJobStep))]
[JsonSerializable(typeof(MinigameStep))]
[JsonSerializable(typeof(AwaitUserStep))]
[JsonSerializable(typeof(BranchStep))]
[JsonSerializable(typeof(FragmentStep))]
// RecoverAction subtypes
[JsonSerializable(typeof(RetryRecoverAction))]
[JsonSerializable(typeof(GotoRecoverAction))]
[JsonSerializable(typeof(UseReturnRecoverAction))]
[JsonSerializable(typeof(UseTeleportRecoverAction))]
[JsonSerializable(typeof(AwaitUserRecoverAction))]
[JsonSerializable(typeof(AbandonRecoverAction))]
// ExpectValue subtypes (converter handles polymorphism; register for completeness)
[JsonSerializable(typeof(ExpectValue))]
[JsonSerializable(typeof(PredicateExpect))]
[JsonSerializable(typeof(AllExpect))]
[JsonSerializable(typeof(AnyExpect))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
public partial class QuestForgeJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Pre-configured options for deserializing quest files.
    /// Uses UnsafeRelaxedJsonEscaping so predicates like ">=" round-trip without escaping.
    /// </summary>
    public static JsonSerializerOptions QuestFileOptions { get; } = new JsonSerializerOptions
    {
        TypeInfoResolver = Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}