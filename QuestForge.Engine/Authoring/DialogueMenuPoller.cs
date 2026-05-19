namespace QuestForge.Engine.Authoring;

public sealed class DialogueMenuPoller
{
    private readonly SnapshotAggregator _aggregator;
    private bool _wasOpen;
    private int? _lastIdx;

    public DialogueMenuPoller(SnapshotAggregator aggregator) => _aggregator = aggregator;

    public void Tick(bool menuIsOpen, int? selectedIdx)
    {
        if (!menuIsOpen)
        {
            if (_wasOpen && _lastIdx.HasValue)
                _aggregator.OnDialogueOptionSelected(_lastIdx.Value);
            _wasOpen = false;
            _lastIdx = null;
            return;
        }

        if (!_wasOpen)
            _wasOpen = true;

        if (selectedIdx.HasValue)
            _lastIdx = selectedIdx.Value;
    }
}
