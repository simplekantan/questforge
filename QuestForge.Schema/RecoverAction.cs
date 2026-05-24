using System.Text.Json.Serialization;

namespace QuestForge.Schema;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(RetryRecoverAction),       "retry")]
[JsonDerivedType(typeof(GotoRecoverAction),        "goto")]
[JsonDerivedType(typeof(UseReturnRecoverAction),   "useReturn")]
[JsonDerivedType(typeof(UseTeleportRecoverAction), "useTeleport")]
[JsonDerivedType(typeof(AwaitUserRecoverAction),   "awaitUser")]
[JsonDerivedType(typeof(AbandonRecoverAction),     "abandon")]
public abstract class RecoverAction { }

public class RetryRecoverAction : RecoverAction
{
    public int? MaxAttempts { get; init; }
    public string? Backoff { get; init; }
}

public class GotoRecoverAction : RecoverAction
{
    public string StepId { get; init; } = default!;
}

public class UseReturnRecoverAction : RecoverAction
{
    public bool ThenRetry { get; init; }
}

public class UseTeleportRecoverAction : RecoverAction
{
    public uint AetheryteId { get; init; }
    public bool ThenRetry { get; init; }
}

public class AwaitUserRecoverAction : RecoverAction
{
    public string Reason { get; init; } = default!;
}

public class AbandonRecoverAction : RecoverAction { }

public class RecoverConfig
{
    public RecoverAction? OnTimeout { get; init; }
    public RecoverAction? OnObstacle { get; init; }
    public RecoverAction? OnAdapterError { get; init; }
    public RecoverAction? OnPostconditionFailed { get; init; }
    public RecoverAction? OnPlayerDefeated { get; init; }
    public RecoverAction? OnResumeFail { get; init; }
}