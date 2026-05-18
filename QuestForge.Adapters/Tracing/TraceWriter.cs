using System.Text;
using System.Text.Json;

namespace QuestForge.Adapters.Tracing;

public sealed class TraceWriter : ITraceWriter, IDisposable
{
    private const int MaxEventBytes = 4096;
    private readonly Stream _stream;
    private readonly object _writeLock = new();
    private readonly bool _leaveStreamOpen;
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>Construct over an arbitrary stream. Used by tests with MemoryStream.</summary>
    public TraceWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveStreamOpen = leaveOpen;
    }

    /// <summary>Open an append-mode file.</summary>
    public static TraceWriter OpenFile(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new TraceWriter(stream, leaveOpen: false);
    }

    public void Write(TraceEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(evt, TraceEventJsonContext.Default.TraceEvent);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxEventBytes)
            throw new InvalidOperationException(
                $"Trace event exceeds {MaxEventBytes}-byte cap: {byteCount} bytes for type '{evt.Type}'");
        lock (_writeLock)
        {
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
        if (!_leaveStreamOpen && _writer is null)
            _stream.Dispose();
    }
}
