using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Interfaces;

public interface IHardwareProbe
{
    Task<HardwareProfile> ProbeAsync(CancellationToken ct = default);
}

public interface IPowerPlanService
{
    Task<(int ExitCode, string Output)> RunPowerCfgAsync(IReadOnlyList<string> args, CancellationToken ct = default);
    Task<string> GetActiveSchemeAsync(CancellationToken ct = default);
}

public interface IRegistryService
{
    RegistrySnapshotEntry ReadState(string path, string name);
    void WriteValue(string path, string name, object value, string valueKind);
    void DeleteValue(string path, string name);
    bool KeyExists(string path);
}

public interface IProcessInspector
{
    IReadOnlyList<StartupEntry> GetStartupEntries();
    IReadOnlyList<HeavyProcessInfo> GetHeavyProcesses(bool includeComms);
    Task<IReadOnlyList<HeavyProcessInfo>> CloseGracefullyAsync(IReadOnlyList<HeavyProcessInfo> targets, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResumeSavedAppsAsync(CancellationToken ct = default);
}

public interface IBackupEngine
{
    Task<RestoreSnapshot> CreateSnapshotAsync(PresetProfile profile, IReadOnlyList<RegistryChange> changes, CancellationToken ct = default);
    Task<IReadOnlyList<string>> RestoreSnapshotAsync(Guid snapshotId, CancellationToken ct = default);
    Guid? GetLatestSnapshotId();
}

public interface IElevatedExecutor
{
    bool IsCurrentProcessElevated();
    Task<ElevatedResult> ExecuteAsync(ElevatedRequest request, CancellationToken ct = default);
}
