using Microsoft.Extensions.Logging;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public sealed class QuestScheduler : IQuestScheduler
{
    private readonly IQuestState _questState;
    private readonly IGameStateProvider _gameState;
    private readonly IQuestDataProvider _questData;
    private readonly ILogger<QuestScheduler> _logger;
    private readonly Func<string, CancellationToken, Task<bool>>? _evaluateSkipIf;

    // Snapshot-on-read via volatile; UpdateOptions affects next call only (spec §5).
    private volatile SchedulerOptions _options;
    private volatile SchedulerStatus _currentStatus = new SchedulerStatus.Idle();

    // Dedup: only log unavailability reasons that changed since last poll.
    private Dictionary<QuestId, string> _lastLoggedReasons = new();
    private Dictionary<QuestId, string> _currentPollReasons = new();

    // Returned when WhyUnavailable unexpectedly returns null for a manual-chain quest.
    private static readonly QuestUnlockReason _whyNullSyntheticReason = new(
        LevelTooLow: false, RequiredLevel: 0,
        PrerequisiteIncomplete: false, MissingPrereqs: [],
        WrongJob: false, RequiredJob: null,
        AlreadyCompleted: false,
        OtherReason: true,
        Detail: "WhyUnavailable returned null but quest is not available");

    public QuestScheduler(
        IQuestState questState,
        IGameStateProvider gameState,
        IQuestDataProvider questData,
        SchedulerOptions initialOptions,
        ILogger<QuestScheduler> logger,
        Func<string, CancellationToken, Task<bool>>? evaluateSkipIf = null)
    {
        ArgumentNullException.ThrowIfNull(questState);
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(questData);
        ArgumentNullException.ThrowIfNull(initialOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _questState = questState;
        _gameState = gameState;
        _questData = questData;
        _options = initialOptions;
        _logger = logger;
        _evaluateSkipIf = evaluateSkipIf;
    }

    public SchedulerStatus CurrentStatus => _currentStatus;

    public void UpdateOptions(SchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<Result<QuestId?>> NextQuestToRun(CancellationToken ct)
    {
        var options = _options;
        _currentStatus = new SchedulerStatus.SelectingNext();
        var visited = new HashSet<QuestId>();
        _currentPollReasons = new Dictionary<QuestId, string>();
        var knownQuests = _questData.EnumerateKnownQuests();

        // --- Rule 0: Tier 0 — Manual chain -------------------------------------------------------
        foreach (var q in options.ManualChain)
        {
            var completeResult = await _questState.IsQuestComplete(q, ct);
            if (completeResult is Result<bool>.Success { Value: true })
                continue;

            var availResult = await _questState.IsQuestAvailable(q, ct);
            if (availResult is Result<bool>.Success { Value: true })
            {
                _currentStatus = new SchedulerStatus.Running(q);
                return Result.Ok<QuestId?>(q);
            }

            var whyResult = await _questState.WhyUnavailable(q, ct);
            QuestUnlockReason? reason = whyResult is Result<QuestUnlockReason?>.Success ws
                ? ws.Value
                : null;

            if (reason is null)
            {
                _logger.LogWarning(
                    "WhyUnavailable returned null for manual-chain quest {QuestId} but it is not available. Treating as blocker.", q);
                reason = _whyNullSyntheticReason;
            }

            _currentStatus = new SchedulerStatus.AwaitingUser(q, reason);
            return Result.Ok<QuestId?>(null);
        }

        // --- Rule 1: Tier 1 — Active-job class quests --------------------------------------------
        var jobResult = await _gameState.GetCurrentJob(ct);
        bool hasJob = jobResult is Result<JobId>.Success;
        JobId currentJob = hasJob ? ((Result<JobId>.Success)jobResult).Value : default;

        if (hasJob)
        {
            var tier1 = knownQuests
                .Where(q => _questData.GetQuestTier(q) == 1
                         && (_questData.IsClassQuestForJob(q, currentJob)
                             || _questData.GetClassJobCategoryId(q) == 0))
                .OrderBy(q => _questData.GetJournalSortKey(q))
                .ThenBy(q => q.Value);

            foreach (var q in tier1)
            {
                var r = await TrySelectCandidate(q, ct, visited, isTier1: true);
                if (r is not null) return r;
            }
        }
        else
        {
            _logger.LogWarning("GetCurrentJob failed ({Result}); skipping Tier-1 evaluation.", jobResult);
        }

        // --- Rule 3: Tier 3 — Auto chain continuation --------------------------------------------
        var tier3 = await EvaluateTier(knownQuests, tier: 3, currentJob: default, requireJob: false, ct, visited);
        if (tier3 is not null) return tier3;

        // --- Rule 4: Tier 4 — Blue (feature unlock) quests (opt-in) ------------------------------
        // "blue-urgent" quests run at Tier 1 unconditionally. "blue" quests run here when opted in.
        // Blue quests have no class restriction — no job filter applied.
        if (options.EnableBlueQuests)
        {
            var tier4 = await EvaluateTier(knownQuests, tier: 4, currentJob: default, requireJob: false, ct, visited);
            if (tier4 is not null) return tier4;
        }

        // --- Rule 5: Tier 5 — Side quests (opt-in) -----------------------------------------------
        if (options.EnableSideQuests)
        {
            var tier5 = await EvaluateTier(knownQuests, tier: 5, currentJob: default, requireJob: false, ct, visited);
            if (tier5 is not null) return tier5;
        }

        // --- Rule 6: Nothing to run ---------------------------------------------------------------
        _currentStatus = new SchedulerStatus.Idle();
        _lastLoggedReasons = _currentPollReasons;
        return Result.Ok<QuestId?>(null);
    }

    private async Task<Result<QuestId?>?> EvaluateTier(
        IReadOnlyCollection<QuestId> knownQuests,
        int tier,
        JobId currentJob,
        bool requireJob,
        CancellationToken ct,
        HashSet<QuestId> visited)
    {
        var candidates = knownQuests
            .Where(q => _questData.GetQuestTier(q) == tier
                     && (!requireJob || _questData.IsClassQuestForJob(q, currentJob)))
            .OrderBy(q => _questData.GetJournalSortKey(q))
            .ThenBy(q => q.Value);

        foreach (var q in candidates)
        {
            var r = await TrySelectCandidate(q, ct, visited, isTier1: false);
            if (r is not null) return r;
        }
        return null;
    }

    private async Task<Result<QuestId?>?> TrySelectCandidate(
        QuestId q, CancellationToken ct, HashSet<QuestId> visited, bool isTier1)
    {
        var skipIf = _questData.GetSkipIf(q);
        if (skipIf is not null && _evaluateSkipIf is not null)
        {
            try
            {
                if (await _evaluateSkipIf(skipIf, ct))
                {
                    _logger.LogDebug("Quest {QuestId} skipped by skipIf predicate.", q);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Quest {QuestId} skipIf evaluation failed; treating as not-skipped.", q);
            }
        }

        var completeResult = await _questState.IsQuestComplete(q, ct);
        if (completeResult is Result<bool>.Success { Value: true })
            return null;

        var availResult = await _questState.IsQuestAvailable(q, ct);
        if (availResult is Result<bool>.Failure availFail)
        {
            _logger.LogWarning(
                "IsQuestAvailable failed for {QuestId}: {Reason} {Detail}; skipping.",
                q, availFail.Reason, availFail.Detail);
            return null;
        }

        if (availResult is Result<bool>.Success { Value: true })
        {
            _currentStatus = new SchedulerStatus.Running(q);
            return Result.Ok<QuestId?>(q);
        }

        var whyResult = await _questState.WhyUnavailable(q, ct);
        if (whyResult is not Result<QuestUnlockReason?>.Success ws)
        {
            _logger.LogWarning("WhyUnavailable failed for {QuestId}; skipping.", q);
            return null;
        }

        var reason = ws.Value;
        if (reason is null)
            return null;

        var reasonKey = $"{reason.LevelTooLow}|{reason.RequiredLevel}|{reason.WrongJob}|" +
            $"{reason.PrerequisiteIncomplete}|{string.Join(",", reason.MissingPrereqs.Select(p => p.Value))}|" +
            $"{reason.AlreadyCompleted}|{reason.OtherReason}|{reason.Detail}";
        _currentPollReasons[q] = reasonKey;
        if (!_lastLoggedReasons.TryGetValue(q, out var prev) || prev != reasonKey)
        {
            _logger.LogDebug(
                "Quest {QuestId} unavailable: LevelTooLow={LevelTooLow} (need {Level}) WrongJob={WrongJob} " +
                "PrereqIncomplete={PrereqIncomplete} (missing={Missing}) AlreadyCompleted={Completed} Other={Other} Detail={Detail}",
                q, reason.LevelTooLow, reason.RequiredLevel, reason.WrongJob,
                reason.PrerequisiteIncomplete,
                string.Join(",", reason.MissingPrereqs.Select(p => p.Value)),
                reason.AlreadyCompleted, reason.OtherReason, reason.Detail);
        }

        if (reason.PrerequisiteIncomplete)
        {
            var blocker = await ResolveBlocker(reason.MissingPrereqs, ct, visited);
            if (blocker is not null && _questData.EnumerateKnownQuests().Contains(blocker.Value))
            {
                _currentStatus = new SchedulerStatus.Running(blocker.Value);
                return Result.Ok<QuestId?>(blocker);
            }
            return null;
        }

        return null;
    }

    // Resolves the first available prerequisite that can unblock a locked candidate.
    // Takes the caller's already-fetched MissingPrereqs to avoid re-querying the entry blocker.
    // visited is shared per NextQuestToRun call — prevents re-exploring dead chains and data cycles.
    private async Task<QuestId?> ResolveBlocker(
        IReadOnlyList<QuestId> missingPrereqs, CancellationToken ct, HashSet<QuestId> visited)
    {
        foreach (var missing in missingPrereqs)
        {
            var skipIf = _questData.GetSkipIf(missing);
            if (skipIf is not null && _evaluateSkipIf is not null)
            {
                try { if (await _evaluateSkipIf(skipIf, ct)) continue; }
                catch { /* non-fatal — treat as not-skipped */ }
            }

            var completeResult = await _questState.IsQuestComplete(missing, ct);
            if (completeResult is Result<bool>.Success { Value: true })
                continue;

            var availResult = await _questState.IsQuestAvailable(missing, ct);
            if (availResult is Result<bool>.Success { Value: true })
                return missing;

            // Not available — descend recursively into its own prerequisites.
            if (!visited.Add(missing))
                continue; // Cycle guard: already explored this node.

            var subReasonResult = await _questState.WhyUnavailable(missing, ct);
            if (subReasonResult is not Result<QuestUnlockReason?>.Success ss)
                continue;
            if (ss.Value is not { PrerequisiteIncomplete: true } subReason)
                continue;

            var sub = await ResolveBlocker(subReason.MissingPrereqs, ct, visited);
            if (sub is not null)
                return sub;
        }
        return null;
    }
}
