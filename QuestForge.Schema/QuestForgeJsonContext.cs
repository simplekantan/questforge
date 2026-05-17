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
[JsonSerializable(typeof(AttunementStep))]
[JsonSerializable(typeof(HandOverItemStep))]
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
    /// Pre-configured options for reading and writing quest and fragment files.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so that predicate
    /// operators like <c>&gt;=</c>, <c>&lt;=</c>, <c>==</c> are written as-is rather than
    /// as <c>>=</c> etc. Both forms are valid JSON and deserialize identically, but
    /// the unescaped form is required for human-readable quest files that authors edit
    /// by hand. "Unsafe" here means unsafe for HTML embedding (XSS risk) — it is the
    /// correct choice for JSON data files that are never embedded in HTML.
    ///
    /// Any tool that writes quest or fragment files MUST use these options (or equivalent
    /// encoder settings). A file written with the default encoder is functionally correct
    /// but will have escaped operators, making predicates unreadable.
    /// </remarks>
    public static JsonSerializerOptions QuestFileOptions { get; } = new JsonSerializerOptions
    {
        TypeInfoResolver = Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}