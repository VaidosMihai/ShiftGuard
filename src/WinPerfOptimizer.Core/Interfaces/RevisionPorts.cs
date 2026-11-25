using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Interfaces;

public sealed record RevisionCaptureOptions(
    string Label,
    RevisionKind Kind,
    bool IncludeDriverUpdateDiscovery = true,
    bool IncludeCommsInProcessGuard = false,
    bool IncludeOptionalDeepSensors = false);

public interface ISystemRevisionOrchestrator
{
    Task<SystemRevisionSnapshot> CaptureRevisionAsync(
        RevisionCaptureOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

public interface IDriverProbe
{
    Task<ProbeResult<DriverAuditReport>> ScanDriversAsync(CancellationToken ct = default);
}

public interface IDriverUpdateDiscoveryService
{
    Task<IReadOnlyList<DriverUpdateCandidate>> FindUpdatesAsync(
        IReadOnlyList<DriverDeviceInfo> devices,
        CancellationToken ct = default);
}

public interface IDriverDeploymentService
{
    Task<DriverInstallOutcome> StageAndInstallAsync(
        DriverUpdateCandidate candidate,
        Guid snapshotId,
        CancellationToken ct = default);
}

public interface IBatteryProbe
{
    Task<ProbeResult<BatteryHealthReport>> ProbeBatteryAsync(CancellationToken ct = default);
}

public interface IMemoryProbe
{
    Task<ProbeResult<MemoryRevisionInfo>> ProbeMemoryAsync(CancellationToken ct = default);
}

public interface ICpuTelemetryProbe
{
    Task<ProbeResult<CpuRevisionInfo>> ProbeCpuAsync(CancellationToken ct = default);
}

public interface IStorageSmartProbe
{
    Task<ProbeResult<StorageRevisionInfo>> ProbeStorageAsync(CancellationToken ct = default);
}

public interface IGpuRevisionProbe
{
    Task<ProbeResult<GpuRevisionInfo>> ProbeGpuAsync(CancellationToken ct = default);
}

public interface IOptionalThermalSensorProvider
{
    bool IsAvailable { get; }
    Task<int?> TryReadPackageTempCelsiusAsync(CancellationToken ct = default);
}

public interface IRecommendationEngine
{
    SubsystemScores Score(SystemRevisionSnapshot snapshot);
    IReadOnlyList<Recommendation> Evaluate(SystemRevisionSnapshot snapshot);
}

public interface ISystemRestoreService
{
    Task<long> CreateRestorePointAsync(string description, CancellationToken ct = default);
}

public interface IRevisionStore
{
    Task SaveRevisionAsync(SystemRevisionSnapshot snapshot, CancellationToken ct = default);
    Task<SystemRevisionSnapshot?> LoadLatestByKindAsync(RevisionKind kind, CancellationToken ct = default);
    Task<SystemRevisionSnapshot?> LoadRevisionAsync(Guid revisionId, CancellationToken ct = default);
    Task<IReadOnlyList<SystemRevisionSnapshot>> ListRevisionsAsync(CancellationToken ct = default);
}

public interface IRevisionReportFormatter
{
    string ToHtml(RevisionDiffReport report);
    string ToMarkdown(RevisionDiffReport report);
    string ToJson(RevisionDiffReport report);
}
