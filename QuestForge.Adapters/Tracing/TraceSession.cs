using System.Text.Json;

namespace QuestForge.Adapters.Tracing;

/// <summary>
/// Unified trace lifecycle manager. Governs when a trace file is open and when
/// writes are gated through, based on the current <see cref="TraceMode"/>.
///
/// Thread-safe: all public members acquire <c>_lock</c>.
/// </summary>
public sealed class TraceSession : ITraceWriter, IDisposable
{
    // ── dependencies ────────────────────────────────────────────────────────
    private TraceMode _mode;
    private readonly string _tracesDir;
    private readonly Func<string, ITraceWriter> _writerFactory;
    private readonly Action<Exception>? _onOpenError;

    // ── state ───────────────────────────────────────────────────────────────
    private readonly object _lock = new();
    private ITraceWriter? _writer;
    private string? _currentFilePath;
    private bool _isWriteGateOpen;
    private bool _disposed;

    // ── dedup cache: key=(method, argRawText), value=valueRawText ───────────
    private readonly Dictionary<(string, string), string> _dedup = new();

    // ── decision debounce: last DecisionEvent key written to the current file ─
    private (string RunId, string StepId, string ActionType)? _lastDecision;
    // ── run.start dedup: last RunStartEvent RunId written to the current file ─
    private string? _lastRunStart;

    // ────────────────────────────────────────────────────────────────────────
    // Construction
    // ────────────────────────────────────────────────────────────────────────

    /// <param name="mode">Controls when files open and writes are admitted.</param>
    /// <param name="tracesDir">Directory under which trace files are created.</param>
    /// <param name="writerFactory">
    ///   Optional factory: given a full file path, returns an open <see cref="ITraceWriter"/>.
    ///   Defaults to <c>TraceWriter.OpenFile(path)</c> when omitted.
    ///   The factory is only invoked inside a lock; it must not itself call back into
    ///   this session.
    /// </param>
    public TraceSession(
        TraceMode mode,
        string tracesDir,
        Func<string, ITraceWriter>? writerFactory = null,
        Action<Exception>? onOpenError = null)
    {
        _mode = mode;
        _tracesDir = tracesDir;
        _writerFactory = writerFactory ?? DefaultFileFactory;
        _onOpenError   = onOpenError;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Public properties
    // ────────────────────────────────────────────────────────────────────────

    public TraceMode Mode { get { lock (_lock) return _mode; } }

    public bool IsFileOpen
    {
        get { lock (_lock) return _writer is not null; }
    }

    public bool IsWriteGateOpen
    {
        get { lock (_lock) return _isWriteGateOpen; }
    }

    public string? CurrentFilePath
    {
        get { lock (_lock) return _currentFilePath; }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Live mode switching
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches the trace mode at runtime without requiring a plugin reload.
    /// <list type="bullet">
    ///   <item>Any → Off: closes the file immediately.</item>
    ///   <item>Any → Always: closes any open file and opens a new session file now.</item>
    ///   <item>Any → Authoring/Recording: closes an Always file; the per-session file
    ///         opens on the next <see cref="OnEnterAuthorMode"/> / <see cref="OnOpenRecordModal"/>.
    ///         An active authoring session is not disrupted — tracing starts next session.</item>
    /// </list>
    /// </summary>
    public void ChangeMode(TraceMode newMode)
    {
        lock (_lock)
        {
            if (_disposed || newMode == _mode) return;
            _mode = newMode;

            switch (newMode)
            {
                case TraceMode.Off:
                    CloseFileUnderLock();
                    break;

                case TraceMode.Always:
                    CloseFileUnderLock();
                    var fileName = $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
                    OpenFileUnderLock(fileName, clearDedup: true);
                    break;

                case TraceMode.Authoring:
                case TraceMode.Recording:
                case TraceMode.QuestRun:
                    // Close the always-open file if present; per-session files open later.
                    CloseFileUnderLock();
                    break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Write
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="evt"/> to the inner writer, subject to gate / file-open checks.
    /// <see cref="RunEndEvent"/> bypasses the gate check but NOT the file-open check.
    /// All other event types are dropped when the gate is closed.
    /// Exceptions from the inner writer are swallowed.
    /// </summary>
    public void Write(TraceEvent evt)
    {
        lock (_lock)
        {
            // File must be open for any write.
            if (_writer is null) return;

            // RunEndEvent bypasses the gate; all others require it open.
            if (evt is not RunEndEvent && !_isWriteGateOpen) return;

            if (evt is DecisionEvent d)
            {
                var key = (d.RunId, d.StepId ?? "", d.ActionType);
                if (_lastDecision == key) return;
                _lastDecision = key;
            }
            else if (evt is RunStartEvent rs)
            {
                if (_lastRunStart == rs.RunId) return;
                _lastRunStart = rs.RunId;
            }

            try
            {
                _writer.Write(evt);
            }
            catch
            {
                // Swallow inner-writer failures; session stays usable.
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // WriteObservation
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deduplicating wrapper around <see cref="Write"/>.
    /// The dedup key is <c>(method, argRawText)</c>; suppression is based on
    /// whether the serialised <paramref name="value"/> has changed since the last emission
    /// for that key. A null or undefined <paramref name="argument"/> is treated as JSON null.
    /// </summary>
    public void WriteObservation(
        string method,
        JsonElement? argument,
        JsonElement value,
        string runId,
        DateTimeOffset at)
    {
        lock (_lock)
        {
            var argRaw   = argument is { ValueKind: not JsonValueKind.Undefined } a
                           ? a.GetRawText()
                           : "null";
            var valueRaw = value.GetRawText();
            var key      = (method, argRaw);

            if (_dedup.TryGetValue(key, out var lastValue) && lastValue == valueRaw)
                return; // dedup hit — suppress

            _dedup[key] = valueRaw;

            // Already inside _lock — use WriteUnderLock to avoid re-entering.
            WriteUnderLock(new ObservationEvent(
                RunId:    runId,
                Method:   method,
                Argument: argument is { ValueKind: not JsonValueKind.Undefined } ? argument : null,
                Value:    value,
                At:       at));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Lifecycle — Off mode: all no-ops
    // Always mode: OnPluginStart opens, OnPluginStop closes
    // Authoring mode: OnEnterAuthorMode opens, OnExitAuthoring closes
    // Recording mode: OnOpenRecordModal(first) opens+clears dedup,
    //                 subsequent OnOpenRecordModal reopens gate only,
    //                 OnConfirmRecordStep closes gate,
    //                 OnExitAuthoring closes file
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Called when the plugin is loaded / enabled.</summary>
    public void OnPluginStart()
    {
        if (_mode != TraceMode.Always) return;

        lock (_lock)
        {
            var fileName = $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
            OpenFileUnderLock(fileName, clearDedup: true);
        }
    }

    /// <summary>Called when the plugin is unloaded / disabled.</summary>
    public void OnPluginStop()
    {
        if (_mode != TraceMode.Always) return;

        lock (_lock)
        {
            CloseFileUnderLock();
        }
    }

    /// <summary>Called when the user enters Author/Recording mode for a given quest.</summary>
    public void OnEnterAuthorMode(uint questId)
    {
        lock (_lock)
        {
            switch (_mode)
            {
                case TraceMode.Authoring:
                    // Open a new file immediately; gate goes open.
                    var fileName = $"{questId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
                    OpenFileUnderLock(fileName, clearDedup: true);
                    break;

                case TraceMode.Recording:
                    // No file yet — waits for first OnOpenRecordModal.
                    // Gate stays closed.
                    break;

                case TraceMode.QuestRun:
                    // QuestRun has its own lifecycle via OnQuestRunStart/OnQuestRunEnd.
                    break;

                // Always / Off: no-op
            }
        }
    }

    /// <summary>
    /// Called when the "Record step" modal opens.
    /// Recording mode: first call opens a file; subsequent calls just reopen the gate.
    /// Authoring mode: no-op.
    /// </summary>
    public void OnOpenRecordModal(uint questId)
    {
        if (_mode != TraceMode.Recording) return;

        lock (_lock)
        {
            if (_writer is null)
            {
                // First call — open file and clear dedup.
                var fileName = $"{questId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
                OpenFileUnderLock(fileName, clearDedup: true);
            }
            else
            {
                // Subsequent call — just reopen the gate; do NOT clear dedup.
                _isWriteGateOpen = true;
            }
        }
    }

    /// <summary>
    /// Called when the user confirms the recorded step.
    /// Closes the write gate; file stays open (more steps may follow).
    /// </summary>
    public void OnConfirmRecordStep()
    {
        if (_mode != TraceMode.Recording) return;

        lock (_lock)
        {
            _isWriteGateOpen = false;
        }
    }

    /// <summary>
    /// Called when the user exits Author/Recording mode.
    /// Closes and disposes the file in both Authoring and Recording modes.
    /// QuestRun has its own lifecycle — OnExitAuthoring is a no-op for it.
    /// </summary>
    public void OnExitAuthoring()
    {
        lock (_lock)
        {
            switch (_mode)
            {
                case TraceMode.Authoring:
                case TraceMode.Recording:
                    CloseFileUnderLock();
                    break;
                // Always / Off / QuestRun: no-op
            }
        }
    }

    /// <summary>
    /// Called when a quest run begins in <see cref="TraceMode.QuestRun"/> mode.
    /// Opens a new trace file named <c>{questId}-run-{timestamp}.jsonl</c> and opens the gate.
    /// No-op for all other modes.
    /// </summary>
    public void OnQuestRunStart(uint questId)
    {
        if (_mode != TraceMode.QuestRun) return;

        lock (_lock)
        {
            var fileName = $"{questId}-run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
            OpenFileUnderLock(fileName, clearDedup: true);
        }
    }

    /// <summary>
    /// Called when a quest run ends in <see cref="TraceMode.QuestRun"/> mode.
    /// Closes the gate and the trace file.
    /// No-op for all other modes.
    /// </summary>
    public void OnQuestRunEnd()
    {
        if (_mode != TraceMode.QuestRun) return;

        lock (_lock)
        {
            CloseFileUnderLock();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseFileUnderLock();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Default file factory (used when no factory is injected)
    // ────────────────────────────────────────────────────────────────────────

    private static ITraceWriter DefaultFileFactory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        return TraceWriter.OpenFile(path);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Private helpers (all called under _lock)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a new trace file. If the factory throws, the file is left closed.
    /// Idempotent in failure: does not close an already-open file on error.
    /// </summary>
    private void OpenFileUnderLock(string fileName, bool clearDedup)
    {
        // Do not open a second file if one is already open.
        if (_writer is not null) return;

        var fullPath = Path.Combine(_tracesDir, fileName);

        try
        {
            var writer = _writerFactory(fullPath);
            _writer          = writer;
            _currentFilePath = fullPath;
            _isWriteGateOpen = true;

            if (clearDedup)
            {
                _dedup.Clear();
                _lastDecision = null;
                _lastRunStart = null;
            }
        }
        catch (Exception ex)
        {
            // Factory threw — leave file closed and surface through the error callback.
            _onOpenError?.Invoke(ex);
        }
    }

    /// <summary>Closes the current file and resets state.</summary>
    private void CloseFileUnderLock()
    {
        if (_writer is null) return;

        if (_writer is IDisposable d)
            d.Dispose();

        _writer          = null;
        _currentFilePath = null;
        _isWriteGateOpen = false;
    }

    /// <summary>
    /// Write path that assumes the caller already holds <c>_lock</c>.
    /// Used internally by <see cref="WriteObservation"/> to avoid re-entering the lock.
    /// </summary>
    private void WriteUnderLock(TraceEvent evt)
    {
        if (_writer is null) return;
        if (evt is not RunEndEvent && !_isWriteGateOpen) return;

        try
        {
            _writer.Write(evt);
        }
        catch
        {
            // Swallow inner-writer failures.
        }
    }

}
