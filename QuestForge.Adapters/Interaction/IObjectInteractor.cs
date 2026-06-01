using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Interaction;

public interface IObjectInteractor
{
    Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct);
}
