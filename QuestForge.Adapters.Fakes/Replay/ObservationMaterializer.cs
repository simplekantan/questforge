using System.Text.Json;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

internal static class ObservationMaterializer
{
    // Converts a recorded observation value into Result<T>.
    // {"failure":"...","detail":"..."} → Result.Fail<T>; anything else → deserialize as T.
    internal static Result<T> Materialize<T>(JsonElement? value)
    {
        if (value is null) throw new InvalidDataException("Observation value is null");
        var v = value.Value;

        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("failure", out var f))
        {
            var detail = v.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return Result.Fail<T>(f.GetString() ?? "unknown", detail);
        }

        var payload = JsonSerializer.Deserialize<T>(v, ReplayJsonOptions.Default);
        return new Result<T>.Success(payload!);
    }
}
