using System.Text.Json;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Shared JSON serializer options for replay infrastructure.
/// Must match RecordingGameStateProvider._jsonOpts exactly so that
/// argument JSON produced at replay time is byte-equal to argument JSON
/// captured at recording time. JsonElement.GetRawText() equality is the
/// comparison key in ObservationScanner.
/// </summary>
internal static class ReplayJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
