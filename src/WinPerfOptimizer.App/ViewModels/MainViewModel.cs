using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IHardwareProbe _hardwareProbe;
    private readonly IPowerPlanService _powerPlan;
    private readonly IRegistryService _registry;
    private readonly IProcessInspector _processInspector;
    private readonly IBackupEngine _backupEngine;
    private readonly IElevatedExecutor _elevatedExecutor;

    private HardwareProfile? _lastHardware;
    private PresetProfile _lastPreviewProfile = PresetProfile.Daily;
    private string _lastPowerBefore = string.Empty;
    private string _lastPowerAfter = string.Empty;

    public MainViewModel(
        IHardwareProbe hardwareProbe,
        IPowerPlanService powerPlan,
        IRegistryService registry,
        IProcessInspector processInspector,
        IBackupEngine backupEngine,
        IElevatedExecutor elevatedExecutor)
    {
        _hardwareProbe = hardwareProbe;
        _powerPlan = powerPlan;
        _registry = registry;
        _processInspector = processInspector;
        _backupEngine = backupEngine;
        _elevatedExecutor = elevatedExecutor;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand), nameof(RunDryRunCommand), nameof(ApplyDailyCommand), nameof(ApplyGamingCommand), nameof(ResumeAppsCommand), nameof(RollbackCommand), nameof(ExportReportCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Ready. Run Audit or Dry-Run without Administrator rights.";
    [ObservableProperty] private string _hardwarePill = "Hardware: Pending Audit";
    [ObservableProperty] private string _powerPill = "Power: Unknown";
    [ObservableProperty] private string _uacPill = "UAC: Standard";
    [ObservableProperty] private string _backupPill = "Backup: None";
    [ObservableProperty] private bool _disableMouseAccel;
    [ObservableProperty] private bool _optimizeNetworkLatency;
    [ObservableProperty] private bool _includeCommsInGuard;
    [ObservableProperty] private HibernationAction _selectedHibernation = HibernationAction.Keep;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    private bool _hasSnapshot;

    private bool CanRunAction() => !IsBusy;
    private bool CanRollback() => !IsBusy && HasSnapshot;

    public ObservableCollection<DiffRow> DiffRows { get; } = [];
    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<HeavyProcessInfo> HeavyProcesses { get; } = [];
    public IReadOnlyList<HibernationAction> HibernationOptions { get; } = Enum.GetValues<HibernationAction>();

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task RunAuditAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Running read-only hardware and startup audit...";
        try
        {
            _lastHardware = await _hardwareProbe.ProbeAsync(ct);
            var activeScheme = await _powerPlan.GetActiveSchemeAsync(ct);
            _lastPowerBefore = activeScheme;

            HardwarePill = $"{_lastHardware.Model} · {_lastHardware.CpuVendor} · {_lastHardware.RamGb:0.#} GB RAM";
            PowerPill = _lastHardware.OnAcPower ? $"AC · {ExtractShortPlan(activeScheme)}" : $"Battery · {ExtractShortPlan(activeScheme)}";
            UacPill = _elevatedExecutor.IsCurrentProcessElevated() ? "UAC: Admin" : "UAC: Standard";

            var latest = _backupEngine.GetLatestSnapshotId();
            HasSnapshot = latest.HasValue;
            BackupPill = latest.HasValue ? $"Backup: {latest.Value.ToString("N")[..8]}" : "Backup: None";

            var (startupEntries, heavyProcesses) = await Task.Run(() => (
                _processInspector.GetStartupEntries(),
                _processInspector.GetHeavyProcesses(IncludeCommsInGuard)
            ), ct);

            StartupEntries.Clear();
            foreach (var s in startupEntries) StartupEntries.Add(s);

            HeavyProcesses.Clear();
            foreach (var p in heavyProcesses) HeavyProcesses.Add(p);

            StatusMessage = $"Audit complete: {StartupEntries.Count} startup items, {HeavyProcesses.Count} heavy background app(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Audit failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task RunDryRunAsync(PresetProfile profile, CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Generating {profile} Dry-Run preview...";
        try
        {
            _lastHardware ??= await _hardwareProbe.ProbeAsync(ct);
            _lastPowerBefore = await _powerPlan.GetActiveSchemeAsync(ct);
            var existingDx = _registry.ReadState(@"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings").SerializedValue;
            var edgeInstalled = _registry.KeyExists(@"HKLM:\SOFTWARE\Microsoft\EdgeUpdate");
            var chromeInstalled = _registry.KeyExists(@"HKLM:\SOFTWARE\Google\Chrome");

            var plan = profile == PresetProfile.Daily
                ? OptimizationPlanBuilder.BuildDaily(_lastHardware, edgeInstalled, chromeInstalled, SelectedHibernation)
                : OptimizationPlanBuilder.BuildGaming(_lastHardware, existingDx, DisableMouseAccel, OptimizeNetworkLatency, forceOnBattery: false);

            _lastPreviewProfile = profile;
            _lastPowerAfter = plan.TargetPowerGuid ?? "Unchanged";

            DiffRows.Clear();
            foreach (var rc in plan.RegistryChanges)
            {
                var cur = _registry.ReadState(rc.Path, rc.Name);
                DiffRows.Add(new DiffRow($"{rc.Path}\\{rc.Name}", cur.Existed ? cur.SerializedValue ?? "" : "Absent", rc.Value.ToString() ?? "", rc.Reason));
            }

            StatusMessage = $"Dry-Run ({profile}): {DiffRows.Count} planned registry changes (no changes applied).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Dry-Run failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public Task ApplyDailyAsync(CancellationToken ct) => ApplyPresetAsync(PresetProfile.Daily, ct);

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public Task ApplyGamingAsync(CancellationToken ct) => ApplyPresetAsync(PresetProfile.Gaming, ct);

    private async Task ApplyPresetAsync(PresetProfile preset, CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Requesting elevation to apply {preset} profile...";
        try
        {
            var tweaks = new List<TweakId>();
            if (preset == PresetProfile.Gaming)
            {
                if (DisableMouseAccel) tweaks.Add(TweakId.DisableMouseAcceleration);
                if (OptimizeNetworkLatency) tweaks.Add(TweakId.DisableNetworkThrottling);
            }

            var req = new ElevatedRequest(
                1, Guid.NewGuid(), WorkerMode.ApplyProfile,
                new ApplyPayload(preset, tweaks, SelectedHibernation, ForceOnBattery: false, CreateRestorePoint: true),
                null, null);

            var res = await _elevatedExecutor.ExecuteAsync(req, ct);
            if (res.Success && res.SnapshotId.HasValue)
            {
                HasSnapshot = true;
                BackupPill = $"Backup: {res.SnapshotId.Value.ToString("N")[..8]}";
            }
            StatusMessage = res.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task ResumeAppsAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var resumed = await _processInspector.ResumeSavedAppsAsync(ct);
            StatusMessage = resumed.Count > 0 ? $"Resumed: {string.Join(", ", resumed)}" : "No paused session apps saved.";
        }
        catch (Exception ex) { StatusMessage = $"Resume failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRollback))]
    public async Task RollbackAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var latest = _backupEngine.GetLatestSnapshotId();
            if (!latest.HasValue)
            {
                StatusMessage = "No backup snapshot found to roll back.";
                return;
            }
            var req = new ElevatedRequest(1, Guid.NewGuid(), WorkerMode.Rollback, null, new RollbackPayload(latest.Value), null);
            var res = await _elevatedExecutor.ExecuteAsync(req, ct);
            StatusMessage = res.Message;
        }
        catch (Exception ex) { StatusMessage = $"Rollback failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task ExportReportAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (DiffRows.Count == 0)
        {
            StatusMessage = "Run a Dry-Run preview first to populate the Before/After diff.";
            return;
        }
        IsBusy = true;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinPerfOptimizer-Reports");
            Directory.CreateDirectory(dir);
            var computer = _lastHardware is not null ? $"{_lastHardware.Manufacturer} {_lastHardware.Model}" : Environment.MachineName;
            var html = DiffReportFormatter.ToHtml(_lastPreviewProfile, computer, _lastPowerBefore, _lastPowerAfter, DiffRows.ToList());
            var md = DiffReportFormatter.ToMarkdown(_lastPreviewProfile, computer, _lastPowerBefore, _lastPowerAfter, DiffRows.ToList());
            var htmlPath = Path.Combine(dir, $"report-{_lastPreviewProfile.ToString().ToLowerInvariant()}.html");
            await File.WriteAllTextAsync(htmlPath, html, ct);
            await File.WriteAllTextAsync(Path.ChangeExtension(htmlPath, ".md"), md, ct);
            StatusMessage = $"Exported HTML & Markdown reports to {dir}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private static string ExtractShortPlan(string activeScheme)
    {
        var idx = activeScheme.IndexOf('(');
        if (idx >= 0)
        {
            var end = activeScheme.IndexOf(')', idx);
            if (end > idx) return activeScheme[(idx + 1)..end];
        }
        return activeScheme;
    }
}
