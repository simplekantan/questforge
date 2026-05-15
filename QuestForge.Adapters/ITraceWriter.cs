namespace QuestForge.Adapters;

/// <summary>Append-only event sink. Phase 5 refines Write's parameter to a typed event hierarchy.</summary>
public interface ITraceWriter
{
    void Write(object evt);
}