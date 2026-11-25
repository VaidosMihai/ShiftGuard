namespace WinPerfOptimizer.Core.Models;

public enum ProbeStatus { Ok, Partial, RequiresElevation, NotApplicable, TimedOut, Failed }

public sealed record ProbeResult<T>(
    ProbeStatus Status,
    T? Data,
    TimeSpan Duration,
    string? DiagnosticNote = null);

[Flags]
public enum DriverIssueFlags
{
    None = 0,
    ConfigManagerError = 1 << 0,
    MissingDriver = 1 << 1,
    UnsignedOrUntrusted = 1 << 2,
    OutdatedAvailable = 1 << 3,
    RebootPending = 1 << 4
}

public enum DriverSignatureTrust { MicrosoftInbox, WhqlSigned, ThirdPartyTrusted, TestOrSelfSigned, Unsigned, Unknown }
public enum DriverUpdateSource { WindowsUpdateCatalog, WingetOemCompanion, OemVendorUtility }
public enum DriverRiskTier { LowUserland, MediumPeripheral, HighDisplayNetwork, CriticalBootOrFirmware }

public sealed record DriverDeviceInfo(
    string DeviceInstanceId,
    string FriendlyName,
    string DeviceClass,
    int ConfigManagerErrorCode,
    string? InfName,
    string? DriverVersion,
    DateOnly? DriverDate,
    string? ProviderName,
    string? Signer,
    DriverSignatureTrust Trust,
    IReadOnlyList<string> HardwareIds,
    DriverIssueFlags Issues);

public sealed record DriverUpdateCandidate(
    string CandidateId,
    string DeviceInstanceId,
    string Title,
    string? CurrentVersion,
    string TargetVersion,
    DriverUpdateSource Source,
    DriverRiskTier RiskTier,
    bool RequiresReboot,
    bool IsAutoApplyAllowed,
    string? BlockReason);

public sealed record DriverAuditReport(
    int TotalDevices,
    int ErroredCount,
    int MissingCount,
    int UnsignedCount,
    IReadOnlyList<DriverDeviceInfo> FlaggedDevices,
    IReadOnlyList<DriverUpdateCandidate> AvailableUpdates,
    bool SystemRebootPending);

public sealed record DriverInstallOutcome(
    string CandidateId,
    string Title,
    bool Success,
    bool Reverted,
    bool RebootRequired,
    string Message);

public enum BatteryPresence { NotPresent, Present, ReportUnavailable }
public enum BatteryWearRating { Healthy, Fair, Degraded, ReplaceRecommended, Unknown }

public sealed record BatteryCapacityPoint(DateOnly PeriodStart, int FullChargeMwh, int DesignMwh);

public sealed record BatteryUnitHealth(
    string Id,
    string Manufacturer,
    string Chemistry,
    int DesignCapacityMwh,
    int FullChargeCapacityMwh,
    double WearPercent,
    int? CycleCount,
    double? CurrentChargePercent,
    double? LiveChargeRateMw,
    BatteryWearRating Rating,
    IReadOnlyList<BatteryCapacityPoint> History);

public sealed record BatteryHealthReport(
    BatteryPresence Presence,
    IReadOnlyList<BatteryUnitHealth> Batteries,
    bool ConnectedStandbySupported);

public enum XmpExpoStatus { LikelyActive, LikelyJedecBase, MixedOrCustom, Unknown }

public sealed record MemoryDimmInfo(
    string DeviceLocator,
    string BankLabel,
    double CapacityGb,
    string MemoryType,
    int JedecOrRatedSpeedMt,
    int ConfiguredClockMt,
    int? ConfiguredVoltageMv,
    string Manufacturer,
    string PartNumber);

public sealed record PagefileInfo(string Path, int AllocatedMb, int CurrentUsageMb, int PeakUsageMb);

public sealed record MemoryRevisionInfo(
    double TotalPhysicalGb,
    double AvailablePhysicalGb,
    double CommitLimitGb,
    double CommitChargeGb,
    double CommitPressurePercent,
    int PopulatedSlots,
    int TotalSlots,
    string ChannelTopologyNote,
    XmpExpoStatus XmpExpoHint,
    IReadOnlyList<MemoryDimmInfo> Dimms,
    IReadOnlyList<PagefileInfo> Pagefiles);

public enum CpuThrottleReason { None, PowerLimit, Thermal, WindowsPowerPlanCap, Unknown }

public sealed record CpuRevisionInfo(
    string Name,
    string Vendor,
    int PhysicalCores,
    int LogicalProcessors,
    int PerformanceCores,
    int EfficiencyCores,
    int BaseClockMhz,
    int CurrentClockMhz,
    int MaxObservedBoostMhz,
    int EffectiveLimitMhz,
    int? PackageTempCelsius,
    CpuThrottleReason ActiveThrottleReason,
    bool VirtualizationEnabled,
    double SampledProcessorPerfPercent,
    double SampledDpcTimePercent,
    double SampledInterruptTimePercent,
    IReadOnlyList<string> DpcLatencyHints);

public sealed record PhysicalDiskSmartInfo(
    string DeviceId,
    string FriendlyName,
    string MediaType,
    string BusType,
    double SizeGb,
    string FirmwareVersion,
    string HealthStatus,
    int? TemperatureCelsius,
    int? TemperatureMaxCelsius,
    int? WearPercentUsed,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? ReadErrorsCorrected,
    long? WriteErrorsTotal,
    double? MaxLatencyMs,
    bool IsBitLockerProtected);

public sealed record StorageVolumeSpaceInfo(string DriveLetter, string FileSystem, double TotalGb, double FreeGb, double FreePercent);

public sealed record StorageRevisionInfo(
    IReadOnlyList<PhysicalDiskSmartInfo> Disks,
    IReadOnlyList<StorageVolumeSpaceInfo> Volumes);

public sealed record GpuRevisionInfo(
    string Name,
    double VramGb,
    string? DriverVersion,
    DateOnly? DriverDate,
    bool HagsEnabled,
    bool HagsSupported);

public sealed record TelemetrySample(
    DateTimeOffset TimestampUtc,
    double CpuClockMhz,
    double CpuLoadPercent,
    double DpcTimePercent,
    double CommitChargeGb,
    int? CpuTempCelsius,
    double? BatteryRateMw);

public enum RecommendationSeverity { Critical, Warning, Advisory, Info }
public enum RecommendationCategory { Driver, Battery, Memory, Cpu, Storage, Power, Startup }
public enum RecommendationActionKind { ApplyTweak, StageDriverUpdate, RunElevatedDeepScan, OpenOemTool, ManualBiosOrHardware }

public sealed record Recommendation(
    string RuleId,
    RecommendationSeverity Severity,
    RecommendationCategory Category,
    string Title,
    string Evidence,
    string ExpectedImpact,
    RecommendationActionKind ActionKind,
    TweakId? LinkedTweak = null,
    string? LinkedCandidateId = null);

public sealed record SubsystemScores(
    int DriverScore,
    int BatteryScore,
    int MemoryScore,
    int CpuScore,
    int StorageScore,
    int OverallScore);

public enum RevisionKind { AutoBaseline, ManualCheckpoint, PostOptimization, PostReboot }

public sealed record SystemRevisionSnapshot(
    Guid RevisionId,
    string Label,
    RevisionKind Kind,
    DateTimeOffset CapturedAtUtc,
    long SystemUptimeMs,
    bool WasElevated,
    HardwareProfile Hardware,
    string ActivePowerScheme,
    ProbeResult<DriverAuditReport> Drivers,
    ProbeResult<BatteryHealthReport> Battery,
    ProbeResult<MemoryRevisionInfo> Memory,
    ProbeResult<CpuRevisionInfo> Cpu,
    ProbeResult<StorageRevisionInfo> Storage,
    ProbeResult<GpuRevisionInfo> Gpu,
    IReadOnlyList<StartupEntry> StartupEntries,
    IReadOnlyList<HeavyProcessInfo> HeavyProcesses,
    SubsystemScores Scores,
    IReadOnlyList<Recommendation> Recommendations);

public sealed record MetricDelta(string Category, string Metric, string Before, string After, string DeltaAssessment);

public sealed record RevisionDiffReport(
    PresetProfile? AppliedProfile,
    SystemRevisionSnapshot Before,
    SystemRevisionSnapshot After,
    IReadOnlyList<DiffRow> ConfigChanges,
    IReadOnlyList<MetricDelta> TelemetryDeltas);
