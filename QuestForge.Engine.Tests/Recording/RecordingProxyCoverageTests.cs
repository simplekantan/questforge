using System.Reflection;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.State;
using Xunit;

namespace QuestForge.Engine.Tests.Recording;

/// <summary>
/// Reflection-based guard: verifies that every method declared on IGameStateProvider
/// and IQuestState is explicitly implemented (not inherited) by the recording proxies.
///
/// Purpose: when a new method is added to either interface in Phase 6+, this test
/// fails immediately, prompting the developer to add the corresponding proxy method
/// and ObservationEvent emission rather than silently falling through to a default.
/// </summary>
public sealed class RecordingProxyCoverageTests
{
    [Fact]
    public void RecordingGameStateProvider_ImplementsAllIGameStateProviderMethods()
    {
        var interfaceMethods = typeof(IGameStateProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        var proxyMethods = typeof(RecordingGameStateProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        var missing = interfaceMethods.Except(proxyMethods).ToList();
        Assert.True(missing.Count == 0,
            $"RecordingGameStateProvider does not explicitly implement these IGameStateProvider methods: " +
            $"{string.Join(", ", missing)}. " +
            $"Add each method and emit an ObservationEvent.");
    }

    [Fact]
    public void RecordingQuestState_ImplementsAllIQuestStateMethods()
    {
        var interfaceMethods = typeof(IQuestState)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        var proxyMethods = typeof(RecordingQuestState)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        var missing = interfaceMethods.Except(proxyMethods).ToList();
        Assert.True(missing.Count == 0,
            $"RecordingQuestState does not explicitly implement these IQuestState methods: " +
            $"{string.Join(", ", missing)}. " +
            $"Add each method and emit an ObservationEvent.");
    }
}