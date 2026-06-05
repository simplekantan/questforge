using System.Text.Json;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Authoring;

/// <summary>
/// File-backed implementation of IFragmentDraftStorage.
/// Drafts are written to {draftsRoot}/fragments/{fragmentId}.draft.json
/// where fragmentId slashes become directory separators.
/// Backups rotate: .bak.001 (most recent) through .bak.005; older backups are deleted.
/// </summary>
public sealed class FileFragmentDraftStorage : IFragmentDraftStorage
{
    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _draftsRoot;
    private readonly IPluginLog _log;

    public FileFragmentDraftStorage(string draftsRoot, IPluginLog log)
    {
        _draftsRoot = Path.Combine(draftsRoot, "fragments");
        _log = log;
    }

    public async Task<Result<bool>> Save(string fragmentId, FragmentDraft draft, CancellationToken ct)
    {
        try
        {
            var dto = ToDto(draft);
            var json = JsonSerializer.Serialize(dto, DraftJsonOptions);
            var path = DraftPath(fragmentId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json, ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return Result.Fail<bool>("saveFailed", ex.Message);
        }
    }

    public async Task<Result<FragmentDraft?>> Load(string fragmentId, CancellationToken ct)
    {
        var path = DraftPath(fragmentId);
        if (!File.Exists(path))
            return Result.Ok<FragmentDraft?>(null);

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var dto = JsonSerializer.Deserialize<FragmentDraftFileDto>(json, DraftJsonOptions);
            if (dto is null)
                return Result.Ok<FragmentDraft?>(null);

            var draft = FromDto(dto, fragmentId);
            return Result.Ok<FragmentDraft?>(draft);
        }
        catch (Exception ex)
        {
            return Result.Fail<FragmentDraft?>("loadFailed", ex.Message);
        }
    }

    public Task<Result<IReadOnlyList<string>>> ListDrafts(CancellationToken ct)
    {
        try
        {
            var result = new List<string>();
            if (!Directory.Exists(_draftsRoot))
                return Task.FromResult<Result<IReadOnlyList<string>>>(
                    Result.Ok<IReadOnlyList<string>>(result));

            foreach (var file in Directory.EnumerateFiles(_draftsRoot, "*.draft.json", SearchOption.AllDirectories))
            {
                // Convert from absolute path back to fragmentId:
                // path = {_draftsRoot}/{fragmentId}.draft.json (with OS separators)
                var relative = Path.GetRelativePath(_draftsRoot, file);
                // Strip .draft.json suffix
                var withoutExt = relative[..^".draft.json".Length];
                // Normalize to forward slashes
                var fragmentId = withoutExt.Replace('\\', '/');
                result.Add(fragmentId);
            }

            return Task.FromResult<Result<IReadOnlyList<string>>>(
                Result.Ok<IReadOnlyList<string>>(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<IReadOnlyList<string>>>(
                Result.Fail<IReadOnlyList<string>>("listFailed", ex.Message));
        }
    }

    public Task<Result<bool>> Delete(string fragmentId, CancellationToken ct)
    {
        try
        {
            var path = DraftPath(fragmentId);
            if (File.Exists(path))
                File.Delete(path);

            for (var i = 1; i <= 5; i++)
            {
                var bakPath = BackupPath(fragmentId, i);
                if (File.Exists(bakPath))
                    File.Delete(bakPath);
            }

            return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("deleteFailed", ex.Message));
        }
    }

    public Task<Result<bool>> CreateBackup(string fragmentId, CancellationToken ct)
    {
        try
        {
            var path = DraftPath(fragmentId);
            if (!File.Exists(path))
                return Task.FromResult<Result<bool>>(Result.Ok(false));

            var bak5 = BackupPath(fragmentId, 5);
            if (File.Exists(bak5))
                File.Delete(bak5);

            for (var i = 4; i >= 1; i--)
            {
                var src = BackupPath(fragmentId, i);
                var dst = BackupPath(fragmentId, i + 1);
                if (File.Exists(src))
                    File.Move(src, dst);
            }

            File.Copy(path, BackupPath(fragmentId, 1));
            return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("backupFailed", ex.Message));
        }
    }

    private string DraftPath(string fragmentId)
    {
        // fragmentId slashes become directory separators
        var relativePath = fragmentId.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_draftsRoot, $"{relativePath}.draft.json");
    }

    private string BackupPath(string fragmentId, int n)
    {
        var relativePath = fragmentId.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_draftsRoot, $"{relativePath}.draft.json.bak.{n:000}");
    }

    // --- DTO conversion ---

    private static FragmentDraftFileDto ToDto(FragmentDraft draft)
    {
        var stepDtos = draft.Steps
            .Select(s => new DraftStepFileDto(
                StepId: s.StepId,
                StepType: s.StepType,
                SequenceNumber: s.SequenceNumber,
                InferredFrom: s.InferredFrom.ToString(),
                ObservedBefore: ToSnapshotDto(s.ObservedBefore),
                ObservedAfter: ToSnapshotDto(s.ObservedAfter),
                SuggestedExpect: s.SuggestedExpect,
                Notes: s.Notes,
                RawJson: s.Raw is not null
                    ? JsonSerializer.Serialize(s.Raw, QuestForgeJsonContext.QuestFileOptions)
                    : null))
            .ToArray();

        return new FragmentDraftFileDto(
            DraftSchemaVersion: "1.0.0",
            FragmentId: draft.FragmentId,
            CreatedAt: draft.CreatedAt,
            LastModifiedAt: draft.LastModifiedAt,
            Steps: stepDtos);
    }

    private FragmentDraft FromDto(FragmentDraftFileDto dto, string fragmentId)
    {
        var draft = new FragmentDraft(fragmentId, dto.CreatedAt);

        foreach (var stepDto in dto.Steps)
        {
            Step? raw = null;
            if (stepDto.RawJson is not null)
            {
                try
                {
                    raw = JsonSerializer.Deserialize<Step>(stepDto.RawJson, QuestForgeJsonContext.QuestFileOptions);
                }
                catch (Exception ex)
                {
                    _log.Warning($"FileFragmentDraftStorage: failed to deserialize step '{stepDto.StepId}' Raw JSON — skipping. {ex.Message}");
                    continue;
                }
            }

            if (!Enum.TryParse<InferredFrom>(stepDto.InferredFrom, out var inferredFrom))
                inferredFrom = InferredFrom.None;

            var before = FromSnapshotDto(stepDto.ObservedBefore);
            var after = FromSnapshotDto(stepDto.ObservedAfter);

            var draftStep = new DraftStep(
                StepId: stepDto.StepId,
                StepType: stepDto.StepType,
                SequenceNumber: stepDto.SequenceNumber,
                InferredFrom: inferredFrom,
                ObservedBefore: before,
                ObservedAfter: after,
                SuggestedExpect: stepDto.SuggestedExpect,
                Notes: stepDto.Notes,
                Raw: raw);

            try { draft.AddStep(draftStep, dto.LastModifiedAt); }
            catch (InvalidOperationException ex)
            {
                _log.Warning($"FileFragmentDraftStorage: duplicate stepId '{stepDto.StepId}' — skipping. {ex.Message}");
            }
        }

        return draft;
    }

    private static SnapshotFileDto ToSnapshotDto(GameStateSnapshot s) => new(
        CapturedAtTicks: s.CapturedAt.UtcTicks,
        Zone: s.Zone.Value,
        PosX: s.Position.X,
        PosY: s.Position.Y,
        PosZ: s.Position.Z,
        ActiveQuest: s.ActiveQuest?.Value,
        QuestSequence: s.QuestSequence,
        QuestFlags: s.QuestFlags,
        QuestAccepted: s.QuestAccepted,
        QuestCompleted: s.QuestCompleted,
        LastNpcId: s.LastNpcInteracted?.Value,
        LastNpcPosX: s.LastNpcPosition?.X,
        LastNpcPosY: s.LastNpcPosition?.Y,
        LastNpcPosZ: s.LastNpcPosition?.Z,
        LastDialoguePrompt: s.LastDialoguePrompt,
        LastDialogueAnswer: s.LastDialogueAnswer,
        InventoryHash: s.InventoryHash);

    private static GameStateSnapshot FromSnapshotDto(SnapshotFileDto s)
    {
        WorldPosition? lastNpcPos = s.LastNpcPosX.HasValue && s.LastNpcPosY.HasValue && s.LastNpcPosZ.HasValue
            ? new WorldPosition(s.LastNpcPosX.Value, s.LastNpcPosY.Value, s.LastNpcPosZ.Value)
            : null;

        return new GameStateSnapshot(
            CapturedAt: new DateTimeOffset(s.CapturedAtTicks, TimeSpan.Zero),
            Zone: new ZoneId(s.Zone),
            Position: new WorldPosition(s.PosX, s.PosY, s.PosZ),
            ActiveQuest: s.ActiveQuest.HasValue ? new QuestId(s.ActiveQuest.Value) : (QuestId?)null,
            QuestSequence: s.QuestSequence,
            QuestFlags: s.QuestFlags,
            QuestAccepted: s.QuestAccepted,
            QuestCompleted: s.QuestCompleted,
            LastNpcInteracted: s.LastNpcId.HasValue ? new NpcId(s.LastNpcId.Value) : (NpcId?)null,
            LastNpcPosition: lastNpcPos,
            LastDialoguePrompt: s.LastDialoguePrompt,
            LastDialogueAnswer: s.LastDialogueAnswer,
            InventoryHash: s.InventoryHash,
            LastAttuned: null);
    }
}

// ---------------------------------------------------------------------------
// Internal DTO for fragment draft file format
// ---------------------------------------------------------------------------

internal sealed record FragmentDraftFileDto(
    string DraftSchemaVersion,
    string FragmentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    DraftStepFileDto[] Steps);
