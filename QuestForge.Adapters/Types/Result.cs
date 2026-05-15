namespace QuestForge.Adapters.Types;

public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public abstract record Result<T>
{
    // Inner types match the spec: Result<T>.Success and Result<T>.Failure.
    // Success uses a distinct primary-constructor parameter name to avoid clashing
    // with the abstract base's Value property; the new keyword resolves the
    // remaining hide-warning since TreatWarningsAsErrors is on.
    public sealed record Success(T Value) : Result<T>
    {
        // CS0108: hides inherited Result<T>.Value — intentional; 'new' silences it.
        public new T Value { get; init; } = Value;
    }

    public sealed record Failure(string Reason, string? Detail = null) : Result<T>;

    public bool IsSuccess => this is Success;
    public T? ValueOrDefault => this is Success s ? s.Value : default;

    // Throws InvalidOperationException on Failure; use after checking IsSuccess.
    public T Value => this is Success s
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