using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

internal static class DraftValidatorAssertions
{
    /// <summary>
    /// Asserts exactly one error with the given code, exactly zero warnings.
    /// Use when the contract is "this rule and only this rule".
    /// </summary>
    public static void AssertSingleError(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        string code)
    {
        var (errors, warnings) = result;
        Assert.True(errors.Count == 1,
            $"Expected exactly 1 error but got {errors.Count}: " +
            $"[{string.Join(", ", errors.Select(e => $"'{e.Code}': {e.Message}"))}]");
        Assert.Equal(code, errors[0].Code);
        Assert.True(warnings.Count == 0,
            $"Expected zero warnings but got {warnings.Count}: " +
            $"[{string.Join(", ", warnings.Select(w => $"'{w.Code}': {w.Message}"))}]");
    }

    /// <summary>
    /// Asserts exactly one warning with the given code, exactly zero errors.
    /// </summary>
    public static void AssertSingleWarning(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        string code)
    {
        var (errors, warnings) = result;
        Assert.True(warnings.Count == 1,
            $"Expected exactly 1 warning but got {warnings.Count}: " +
            $"[{string.Join(", ", warnings.Select(w => $"'{w.Code}': {w.Message}"))}]");
        Assert.Equal(code, warnings[0].Code);
        Assert.True(errors.Count == 0,
            $"Expected zero errors but got {errors.Count}: " +
            $"[{string.Join(", ", errors.Select(e => $"'{e.Code}': {e.Message}"))}]");
    }

    /// <summary>
    /// Asserts the result contains errors with exactly these codes (multi-set equality),
    /// and zero warnings. Use when a fixture deliberately triggers more than one rule.
    /// </summary>
    public static void AssertErrorCodes(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result,
        params string[] expectedCodes)
    {
        var actual = result.Errors.Select(e => e.Code).OrderBy(c => c).ToArray();
        var expected = expectedCodes.OrderBy(c => c).ToArray();
        Assert.Equal(expected, actual);
        Assert.True(result.Warnings.Count == 0,
            $"Expected zero warnings but got {result.Warnings.Count}: " +
            $"[{string.Join(", ", result.Warnings.Select(w => $"'{w.Code}'"))}]");
    }

    /// <summary>
    /// Asserts the result is completely clean — no errors, no warnings.
    /// Use for the happy-path baseline.
    /// </summary>
    public static void AssertClean(
        (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) result)
    {
        Assert.True(result.Errors.Count == 0 && result.Warnings.Count == 0,
            $"Expected clean result. Errors: [{string.Join(", ", result.Errors.Select(e => e.Code))}], " +
            $"Warnings: [{string.Join(", ", result.Warnings.Select(w => w.Code))}]");
    }
}
