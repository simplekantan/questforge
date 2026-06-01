using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class DalamudObjectInteractor : IObjectInteractor
{
    private readonly DalamudInteractor _interactor;

    public DalamudObjectInteractor(DalamudInteractor interactor)
        => _interactor = interactor;

    public async Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct)
    {
        var result = await _interactor.InteractWithObject(target, ct);
        return result switch
        {
            Result<InteractOutcome>.Success => Result.Ok(),
            Result<InteractOutcome>.Failure f => Result.Fail(f.Reason, f.Detail),
            _ => Result.Fail("unexpected", "Unexpected result type")
        };
    }
}
