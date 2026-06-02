using System.Text.Json;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Recording;

/// <summary>
/// Recording proxy for ICombat.
/// Records SetTarget / ClearTarget / StartRotation / StopRotation as non-deduped
/// ObservationEvents (debug-only — replay does not consume them; FakeCombat is used instead).
/// Delegates reads (IsRotationModuleAvailable / GetRotationModule) through
/// unrecorded — those produce no side-effects visible in the trace.
///
/// No dedup: combat acts are an ordered command log, not a value-change observation stream.
///
/// Wired by EngineHost.BeginRun alongside RecordingGameStateProvider / RecordingQuestState
/// (mirrors EngineHost.cs:165-169). Not wired during replay (FakeCombat is used instead).
///
/// STUB — Builder implements all method bodies; RC-* tests in QuestForge.Adapters.Tests (G-RC).
/// </summary>
public sealed class RecordingCombat : ICombat
{
    private readonly ICombat _inner;
    private readonly ITraceWriter _trace;
    private readonly Func<string?> _runIdAccessor;
    private readonly TimeProvider _clock;
    private readonly bool _skipIfNoRunId;

    /// <summary>
    /// Wraps an existing ICombat and records combat acts to the given trace writer.
    /// Constructor mirrors RecordingGameStateProvider(inner, trace, runIdAccessor, clock?, skipIfNoRunId).
    /// </summary>
    public RecordingCombat(
        ICombat inner,
        ITraceWriter trace,
        Func<string?> runIdAccessor,
        TimeProvider? clock = null,
        bool skipIfNoRunId = false)
    {
        _inner          = inner          ?? throw new ArgumentNullException(nameof(inner));
        _trace          = trace          ?? throw new ArgumentNullException(nameof(trace));
        _runIdAccessor  = runIdAccessor  ?? throw new ArgumentNullException(nameof(runIdAccessor));
        _clock          = clock          ?? TimeProvider.System;
        _skipIfNoRunId  = skipIfNoRunId;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private void RecordAct(string method, object? argument)
    {
        try
        {
            var runId = _runIdAccessor();
            if (_skipIfNoRunId && runId is null or "") return;
            var argEl = argument is null ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(argument, _jsonOpts);
            _trace.Write(new ObservationEvent
            {
                RunId = runId ?? "",
                Data = new ObservationEvent.ObservationData
                {
                    Method   = method,
                    Argument = argEl,
                    Value    = null
                }
            });
        }
        catch
        {
            // Write failure must not propagate to the caller
        }
    }

    // ---- Recorded methods (side-effect acts → non-deduped ObservationEvent) ----

    public async Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct)
    {
        var r = await _inner.SetTarget(target, ct);
        RecordAct("SetTarget", new { actorId = target.Value });
        return r;
    }

    public async Task<Result<Unit>> ClearTarget(CancellationToken ct)
    {
        var r = await _inner.ClearTarget(ct);
        RecordAct("ClearTarget", null);
        return r;
    }

    public async Task<Result<Unit>> StartRotation(CancellationToken ct)
    {
        var r = await _inner.StartRotation(ct);
        RecordAct("StartRotation", null);
        return r;
    }

    public async Task<Result<Unit>> StopRotation(CancellationToken ct)
    {
        var r = await _inner.StopRotation(ct);
        RecordAct("StopRotation", null);
        return r;
    }

    // ---- Unrecorded delegate-through methods (reads) ----

    public Task<Result<bool>> IsRotationModuleAvailable(CancellationToken ct)
        => _inner.IsRotationModuleAvailable(ct);

    public Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct)
        => _inner.GetRotationModule(ct);
}
