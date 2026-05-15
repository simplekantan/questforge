namespace QuestForge.Adapters.Types;

public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public abstract record Result<T>
{
    public sealed record Success(T Value) : Result<T>;
    public sealed record Failure(string Reason, string? Detail = null) : Result<T>;

    public bool IsSuccess => this is Success;

    // Safe accessor — returns default when Failure.
    public T? ValueOrDefault => this is Success s ? s.Value : default;

    // Throwing accessor — use only after confirming IsSuccess.
    // Named ValueOrThrow (not Value) to avoid shadowing Success.Value.
    public T ValueOrThrow => this is Success s
        ? s.Value
        : throw new InvalidOperationException($"Result is Failure: {((Failure)this).Reason}");
}

// Static helpers — the non-generic Result abstract record is replaced by Result<Unit>.
// Void-returning adapter methods return Task<Result<Unit>>.
public static class Result
{
    public static Result<T>.Success Ok<T>(T value) => new(value);
    public static Result<T>.Failure Fail<T>(string reason, string? detail = null) => new(reason, detail);
    public static Result<Unit>.Success Ok() => new(Unit.Value);
    public static Result<Unit>.Failure Fail(string reason, string? detail = null) => new(reason, detail);
}