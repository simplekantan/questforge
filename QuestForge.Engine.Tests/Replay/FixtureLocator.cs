using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Locates canonical trace fixtures from the questforge-data sibling repository.
/// Walks upward from a starting directory until a directory containing
/// questforge-data/&lt;relativePath&gt; is found.
///
/// Provides a testable overload (TryFindFixtureFromAncestor) that accepts an explicit
/// starting directory so unit tests can construct controlled temp layouts.
/// </summary>
internal static class FixtureLocator
{
    /// <summary>
    /// Walks upward from startDir looking for questforge-data/&lt;relativePath&gt;.
    /// Returns the absolute path to the fixture file if found, or null if not found.
    /// </summary>
    public static string? TryFindFixtureFromAncestor(string startDir, string relativePath)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "questforge-data", relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Walks upward from AppContext.BaseDirectory (the test assembly output folder).
    /// Returns the absolute path to the fixture file if found, or null if not found.
    /// </summary>
    public static string? TryFindQuestForgeDataFixture(string relativePath)
        => TryFindFixtureFromAncestor(AppContext.BaseDirectory, relativePath);

    /// <summary>
    /// Returns the fixture path, or skips the current test via Assert.Skip (xUnit v3)
    /// if questforge-data is not present at any ancestor of the test assembly.
    /// Skipped tests are not failures — they are expected when developers have only
    /// the questforge repo cloned locally.
    /// </summary>
    public static string LocateOrSkip(string relativePath)
    {
        var path = TryFindQuestForgeDataFixture(relativePath);
        if (path is null)
            Assert.Skip(
                $"questforge-data fixture not found: {relativePath}. " +
                $"Clone questforge-data next to questforge, or run the CI checkout step " +
                $"(actions/checkout with path: questforge-data, lfs: true).");
        return path!;
    }
}
