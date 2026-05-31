using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Items;

public sealed class FakeCofferOpener : ICofferOpener
{
    public record OpenCofferCall(uint ItemId, DateTimeOffset At) : AdapterCall(At);

    public CallLog<OpenCofferCall> OpenCofferCalls { get; } = new();

    private IReadOnlyList<uint> _cofferItemIds = [];
    private (string Reason, string? Detail)? _nextGetFailure;
    private (string Reason, string? Detail)? _nextOpenFailure;

    public void SetCofferItemIds(uint[] ids)
    {
        lock (this) _cofferItemIds = ids.ToArray();
    }

    public void ScriptNextGetFailure(string reason, string? detail = null)
        => _nextGetFailure = (reason, detail);

    public void ScriptNextOpenFailure(string reason, string? detail = null)
        => _nextOpenFailure = (reason, detail);

    public void Reset()
    {
        OpenCofferCalls.Clear();
        lock (this) _cofferItemIds = [];
        _nextGetFailure = null;
        _nextOpenFailure = null;
    }

    public Task<Result<IReadOnlyList<uint>>> GetCofferItemIds(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_nextGetFailure is { } f)
        {
            _nextGetFailure = null;
            return Task.FromResult<Result<IReadOnlyList<uint>>>(Result.Fail<IReadOnlyList<uint>>(f.Reason, f.Detail));
        }

        IReadOnlyList<uint> ids;
        lock (this) ids = _cofferItemIds;
        return Task.FromResult<Result<IReadOnlyList<uint>>>(Result.Ok(ids));
    }

    public Task<Result<Unit>> OpenCoffer(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OpenCofferCalls.Add(new OpenCofferCall(itemId, DateTimeOffset.UtcNow));

        if (_nextOpenFailure is { } f)
        {
            _nextOpenFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }

        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
