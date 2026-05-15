using System.Text;
using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Engine.Tracing;

public sealed class TraceWriter : ITraceWriter, IDisposable
{
    private const int MaxEventBytes = 4096;
    private readonly Stream _stream;
    private readonly object _writeLock = new();
    // _leaveStreamOpen: true if we should not close the stream on dispose (leaveOpen was true)
    private readonly bool _leaveStreamOpen;
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>Construct over an arbitrary stream. Used by tests with MemoryStream.</summary>
    public TraceWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveStreamOpen = leaveOpen;
        // StreamWriter is created lazily on first write to avoid throwing on construction
        // over non-writable streams. Errors are propagated naturally from Write.
    }

    /// <summary>Open an append-mode file. Used by the plugin layer in Phase 6.</summary>
    public static TraceWriter OpenFile(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new TraceWriter(stream, leaveOpen: false);
    }

    public void Write(TraceEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(evt, TraceEventJsonContext.Default.TraceEvent);
        if (json.Length > MaxEventBytes)
            throw new InvalidOperationException(
                $"Trace event exceeds {MaxEventBytes}-byte cap: {json.Length} bytes for type '{evt.Type}'");
        lock (_writeLock)
        {
            // Lazily create the StreamWriter inside the lock on first Write.
            // This ensures construction over a non-writable stream doesn't throw at construction time.
            _writer ??= new StreamWriter(
                _stream,
                new UTF8Encoding(false),
                bufferSize: 4096,
                leaveOpen: _leaveStreamOpen)
            {
                NewLine = "\n"
            };
            _writer.Write(json);
            _writer.Write('\n');
            _writer.Flush();
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer?.Dispose();
        // If we own the stream and the writer didn't close it (leaveOpen was true for writer),
        // close it now. If leaveOpen is false for the writer, it already closed it.
        if (!_leaveStreamOpen && _writer is null)
            _stream.Dispose(); // writer was never created, dispose stream directly
    }
}