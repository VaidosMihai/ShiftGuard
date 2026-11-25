namespace WinPerfOptimizer.Core.Models;

public enum WorkerMode { ApplyProfile, Rollback, CreateRestorePoint, InstallDrivers }
public enum PresetProfile { Daily, Gaming }
public enum HibernationAction { Keep, Reduced, Off }
public enum StartupCategory { Protected, GamingLauncher, BloatUpdater, Other }
public enum RestoreReason { PreApply }

public enum TweakId
{
    PowerBalanced,
    PowerHighPerformance,
    DisableGameDvr,
    EnableGameMode,
    DisableWindowsTips,
    EnableStorageSense,
    DeliveryOptimizationHttpOnly,
    DisableEdgeBackground,
    DisableChromeBackground,
    DirectXWindowedAndVrr,
    MmcssGamesPriority,
    EnableHags,
    DisableNetworkThrottling,
    DisableMouseAcceleration,
    HibernationReduced,
    HibernationOff
}

public sealed record HardwareProfile(
    string Manufacturer,
    string Model,
    string OsCaption,
    int OsBuild,
    string CpuName,
    string CpuVendor,
    double RamGb,
    string GpuNames,
    bool HagsEligible,
    bool HasBattery,
    bool OnAcPower,
    bool IsModernStandby,
    bool HasSsd,
    bool HiberfilExists,
    double HiberfilSizeGb,
    string ActiveOemTools);

public sealed record RegistryChange(
    string Path,
    string Name,
    object Value,
    string ValueKind,
    string Reason);

public sealed record PowerCfgCommand(IReadOnlyList<string> Arguments, string Description);

public sealed record OptimizationPlan(
    PresetProfile Profile,
    string? TargetPowerGuid,
    IReadOnlyList<PowerCfgCommand> PowerCommands,
    IReadOnlyList<RegistryChange> RegistryChanges,
    HibernationAction HibernationAction,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notices);

public sealed record StartupEntry(
    string Name,
    string Command,
    string Location,
    StartupCategory Category,
    bool IsProtected);

public sealed record HeavyProcessInfo(
    string ProcessName,
    int Pid,
    double MemoryMb,
    string? ExecutablePath);

public sealed record DiffRow(string Item, string Before, string After, string Reason);

public sealed record AuditReport(
    HardwareProfile Hardware,
    string ActivePowerScheme,
    string GameModeValue,
    string GameDvrValue,
    string HagsValue,
    string MouseSpeedValue,
    IReadOnlyList<StartupEntry> StartupEntries,
    IReadOnlyList<HeavyProcessInfo> HeavyProcesses);
