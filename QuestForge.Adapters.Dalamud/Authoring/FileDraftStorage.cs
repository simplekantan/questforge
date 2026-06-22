using System.Text.Json;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Authoring;

/// <summary>
/// File-backed implementation of IDraftStorage.
/// Drafts are written to {draftsRoot}/{questId}.draft.json.
/// Backups rotate: .bak.001 (most recent) through .bak.005; older backups are deleted.
/// </summary>
public sealed class FileDraftStorage : IDraftStorage
{
    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _draftsRoot;
    private readonly IPluginLog _log;

    public FileDraftStorage(string draftsRoot, IPluginLog log)
    {
        _draftsRoot = draftsRoot;
        _log = log;
    }

    public async Task<Result<bool>> Save(QuestId quest, QuestDraft draft, CancellationToken ct)
    {
        try
        {
            var dto = ToDto(draft);
            var json = JsonSerializer.Serialize(dto, DraftJsonOptions);
            var path = DraftPath(quest);
            Directory.CreateDirectory(_draftsRoot); // safe no-op if already exists
            await File.WriteAllTextAsync(path, json, ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return Result.Fail<bool>("saveFailed", ex.Message);
        }
    }

    public async Task<Result<QuestDraft?>> Load(QuestId quest, CancellationToken ct)
    {
        var path = DraftPath(quest);
        if (!File.Exists(path))
            return Result.Ok<QuestDraft?>(null);

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var dto = JsonSerializer.Deserialize<DraftFileDto>(json, DraftJsonOptions);
            if (dto is null)
                return Result.Ok<QuestDraft?>(null);

            var draft = FromDto(dto, quest);
            return Result.Ok<QuestDraft?>(draft);
        }
        catch (Exception ex)
        {
            return Result.Fail<QuestDraft?>("loadFailed", ex.Message);
        }
    }

    public Task<Result<IReadOnlyList<QuestId>>> ListDrafts(CancellationToken ct)
    {
        try
        {
            var result = new List<QuestId>();
            if (!Directory.Exists(_draftsRoot))
                return Task.FromResult<Result<IReadOnlyList<QuestId>>>(
                    Result.Ok<IReadOnlyList<QuestId>>(result));

            foreach (var file in Directory.EnumerateFiles(_draftsRoot, "*.draft.json"))
            {
                var name = Path.GetFileName(file);
                // name is "{questId}.draft.json"
                var withoutExt = name[..^".draft.json".Length];
                if (uint.TryParse(withoutExt, out var id))
                    result.Add(new QuestId(id));
            }

            return Task.FromResult<Result<IReadOnlyList<QuestId>>>(
                Result.Ok<IReadOnlyList<QuestId>>(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<IReadOnlyList<QuestId>>>(
                Result.Fail<IReadOnlyList<QuestId>>("listFailed", ex.Message));
        }
    }

    public Task<Result<bool>> Delete(QuestId quest, CancellationToken ct)
    {
        try
        {
            var path = DraftPath(quest);
            if (File.Exists(path))
                File.Delete(path);

            // Delete all backups
            for (var i = 1; i <= 5; i++)
            {
                var bakPath = BackupPath(quest, i);
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

    public Task<Result<bool>> CreateBackup(QuestId quest, CancellationToken ct)
    {
        try
        {
            var path = DraftPath(quest);
            if (!File.Exists(path))
                return Task.FromResult<Result<bool>>(Result.Ok(false));

            // Rotate existing backups: .bak.004 → .bak.005, .bak.003 → .bak.004, ... .bak.001 → .bak.002
            // Delete .bak.005 first if it exists
            var bak5 = BackupPath(quest, 5);
            if (File.Exists(bak5))
                File.Delete(bak5);

            for (var i = 4; i >= 1; i--)
            {
                var src = BackupPath(quest, i);
                var dst = BackupPath(quest, i + 1);
                if (File.Exists(src))
                    File.Move(src, dst);
            }

            // Copy current draft to .bak.001
            File.Copy(path, BackupPath(quest, 1));
            return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("backupFailed", ex.Message));
        }
    }

    private string DraftPath(QuestId quest)
        => Path.Combine(_draftsRoot, $"{quest.Value}.draft.json");

    private string BackupPath(QuestId quest, int n)
        => Path.Combine(_draftsRoot, $"{quest.Value}.draft.json.bak.{n:000}");

    // --- DTO conversion ---

    private static DraftFileDto ToDto(QuestDraft draft)
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

        var epilogueDtos = draft.EpilogueSteps
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

        return new DraftFileDto(
            DraftSchemaVersion: "1.0.0",
            QuestId: draft.QuestId.Value,
            QuestName: draft.QuestName,
            Category: draft.Category,
            Expansion: draft.Expansion,
            LastVerifiedPatch: draft.LastVerifiedPatch,
            CreatedAt: draft.CreatedAt,
            LastModifiedAt: draft.LastModifiedAt,
            Steps: stepDtos,
            EpilogueSteps: epilogueDtos.Length > 0 ? epilogueDtos : null);
    }

    private QuestDraft FromDto(DraftFileDto dto, QuestId questId)
    {
        var draft = new QuestDraft(questId, dto.CreatedAt)
        {
            QuestName = dto.QuestName,
            Category = dto.Category,
            Expansion = dto.Expansion,
            LastVerifiedPatch = dto.LastVerifiedPatch
        };

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
                    _log.Warning($"FileDraftStorage: failed to deserialize step '{stepDto.StepId}' Raw JSON — skipping. {ex.Message}");
                    continue;
                }
            }

            if (!Enum.TryParse<InferredFrom>(stepDto.InferredFrom, out var inferredFrom))
                inferredFrom = InferredFrom.None;

            var before = FromSnapshotDto(stepDto.ObservedBefore, questId);
            var after = FromSnapshotDto(stepDto.ObservedAfter, questId);

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
                _log.Warning($"FileDraftStorage: duplicate stepId '{stepDto.StepId}' — skipping. {ex.Message}");
            }
        }

        if (dto.EpilogueSteps is not null)
        {
            foreach (var stepDto in dto.EpilogueSteps)
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
                        _log.Warning($"FileDraftStorage: failed to deserialize epilogue step '{stepDto.StepId}' Raw JSON — skipping. {ex.Message}");
                        continue;
                    }
                }

                if (!Enum.TryParse<InferredFrom>(stepDto.InferredFrom, out var inferredFrom))
                    inferredFrom = InferredFrom.None;

                var before = FromSnapshotDto(stepDto.ObservedBefore, questId);
                var after = FromSnapshotDto(stepDto.ObservedAfter, questId);

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

                try { draft.AddEpilogueStep(draftStep, dto.LastModifiedAt); }
                catch (InvalidOperationException ex)
                {
                    _log.Warning($"FileDraftStorage: duplicate epilogue stepId '{stepDto.StepId}' — skipping. {ex.Message}");
                }
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
        InventoryHash: s.InventoryHash);   // LastAttuned not persisted in SnapshotFileDto (Phase 11B)

    private static GameStateSnapshot FromSnapshotDto(SnapshotFileDto s, QuestId questId)
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
            LastAttuned: null);   // Phase 11B: not persisted in SnapshotFileDto yet
    }
}

// ---------------------------------------------------------------------------
// Internal DTOs for draft file format
// ---------------------------------------------------------------------------

internal sealed record DraftFileDto(
    string DraftSchemaVersion,
    uint QuestId,
    string? QuestName,
    string Category,
    string Expansion,
    string? LastVerifiedPatch,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    DraftStepFileDto[] Steps,
    DraftStepFileDto[]? EpilogueSteps = null);

internal sealed record DraftStepFileDto(
    string StepId,
    string StepType,
    int SequenceNumber,
    string InferredFrom,
    SnapshotFileDto ObservedBefore,
    SnapshotFileDto ObservedAfter,
    string? SuggestedExpect,
    string? Notes,
    string? RawJson);

internal sealed record SnapshotFileDto(
    long CapturedAtTicks,
    uint Zone,
    float PosX, float PosY, float PosZ,
    uint? ActiveQuest,
    int QuestSequence,
    uint QuestFlags,
    bool QuestAccepted,
    bool QuestCompleted,
    uint? LastNpcId,
    float? LastNpcPosX, float? LastNpcPosY, float? LastNpcPosZ,
    string? LastDialoguePrompt,
    string? LastDialogueAnswer,
    uint InventoryHash);
