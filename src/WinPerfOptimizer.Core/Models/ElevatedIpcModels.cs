using System.Text.Json.Serialization;

namespace WinPerfOptimizer.Core.Models;

public sealed record RegistrySnapshotEntry(
    string Path,
    string Name,
    bool Existed,
    string? ValueKind,
    string? SerializedValue);

public sealed record RestoreSnapshot(
    Guid SnapshotId,
    DateTimeOffset CreatedAt,
    PresetProfile Profile,
    string OriginalPowerGuid,
    string PowerExportPath,
    bool HibernationWasOn,
    IReadOnlyList<RegistrySnapshotEntry> RegistryEntries);

public sealed record ApplyPayload(
    PresetProfile Preset,
    IReadOnlyList<TweakId> TweakIds,
    HibernationAction HibernationAction,
    bool ForceOnBattery,
    bool CreateRestorePoint);

public sealed record RollbackPayload(Guid SnapshotId);
public sealed record RestorePointPayload(RestoreReason Reason);

public sealed record ElevatedRequest(
    int SchemaVersion,
    Guid RequestId,
    WorkerMode Mode,
    ApplyPayload? Apply,
    RollbackPayload? Rollback,
    RestorePointPayload? Restore);

public sealed record ElevatedResult(
    Guid RequestId,
    bool Success,
    Guid? SnapshotId,
    IReadOnlyList<TweakId> AppliedIds,
    string ErrorCode,
    string Message);
