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
    private readonly ISystemRevisionOrchestrator? _revisionOrchestrator;
    private readonly IRevisionStore? _revisionStore;
    private readonly IRevisionReportFormatter? _reportFormatter;

    private HardwareProfile? _lastHardware;
    private PresetProfile _lastPreviewProfile = PresetProfile.Daily;
    private string _lastPowerBefore = string.Empty;
    private string _lastPowerAfter = string.Empty;
    private SystemRevisionSnapshot? _baselineSnapshot;
    private SystemRevisionSnapshot? _latestSnapshot;

    public MainViewModel(
        IHardwareProbe hardwareProbe,
        IPowerPlanService powerPlan,
        IRegistryService registry,
        IProcessInspector processInspector,
        IBackupEngine backupEngine,
        IElevatedExecutor elevatedExecutor,
        ISystemRevisionOrchestrator? revisionOrchestrator = null,
        IRevisionStore? revisionStore = null,
        IRevisionReportFormatter? reportFormatter = null)
    {
        _hardwareProbe = hardwareProbe;
        _powerPlan = powerPlan;
        _registry = registry;
        _processInspector = processInspector;
        _backupEngine = backupEngine;
        _elevatedExecutor = elevatedExecutor;
        _revisionOrchestrator = revisionOrchestrator;
        _revisionStore = revisionStore;
        _reportFormatter = reportFormatter;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand), nameof(RunDryRunCommand), nameof(ApplyDailyCommand), nameof(ApplyGamingCommand), nameof(ResumeAppsCommand), nameof(RollbackCommand), nameof(ExportReportCommand), nameof(RunFullRevisionCommand), nameof(SaveBaselineCheckpointCommand), nameof(InstallSafeDriversCommand), nameof(ExportRevisionReportCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Ready. Run Full Revision or Audit without Administrator rights.";
    [ObservableProperty] private string _hardwarePill = "Hardware: Pending Audit";
    [ObservableProperty] private string _powerPill = "Power: Unknown";
    [ObservableProperty] private string _uacPill = "UAC: Standard";
    [ObservableProperty] private string _backupPill = "Backup: None";
    [ObservableProperty] private bool _disableMouseAccel;
    [ObservableProperty] private bool _optimizeNetworkLatency;
    [ObservableProperty] private bool _includeCommsInGuard;
    [ObservableProperty] private HibernationAction _selectedHibernation = HibernationAction.Keep;

    [ObservableProperty] private int _overallHealthScore;
    [ObservableProperty] private int _driverScore;
    [ObservableProperty] private int _batteryScore;
    [ObservableProperty] private int _memoryScore;
    [ObservableProperty] private int _cpuScore;
    [ObservableProperty] private int _storageScore;
    [ObservableProperty] private string _cpuSummary = "Pending technical revision...";
    [ObservableProperty] private string _memorySummary = "Pending technical revision...";
    [ObservableProperty] private string _batterySummary = "Pending technical revision...";
    [ObservableProperty] private string _storageSummary = "Pending technical revision...";
    [ObservableProperty] private string _gpuSummary = "Pending technical revision...";
    [ObservableProperty] private string _driversSummary = "Pending driver audit...";
    [ObservableProperty] private string _oemAdvisorHint = "OEM utility detection pending...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    private bool _hasSnapshot;

    private bool CanRunAction() => !IsBusy;
    private bool CanRollback() => !IsBusy && HasSnapshot;

    public ObservableCollection<DiffRow> DiffRows { get; } = [];
    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<HeavyProcessInfo> HeavyProcesses { get; } = [];
    public ObservableCollection<DriverDeviceInfo> FlaggedDrivers { get; } = [];
    public ObservableCollection<DriverUpdateCandidate> DriverUpdates { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<PhysicalDiskSmartInfo> StorageDisks { get; } = [];
    public ObservableCollection<MemoryDimmInfo> MemoryDimms { get; } = [];
    public ObservableCollection<MetricDelta> RevisionDeltas { get; } = [];
    public ObservableCollection<SystemRevisionSnapshot> SavedRevisions { get; } = [];
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
        catch (Exception ex) { StatusMessage = $"Audit failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task RunFullRevisionAsync(CancellationToken ct)
    {
        if (IsBusy || _revisionOrchestrator is null) return;
        IsBusy = true;
        StatusMessage = "Running full hardware, driver, battery, memory, CPU & storage revision...";
        try
        {
            var kind = _baselineSnapshot is null ? RevisionKind.AutoBaseline : RevisionKind.PostOptimization;
            var snap = await _revisionOrchestrator.CaptureRevisionAsync(
                new RevisionCaptureOptions($"{kind} {DateTime.Now:HH:mm}", kind, true, IncludeCommsInGuard),
                new Progress<string>(msg => StatusMessage = msg),
                ct);

            _latestSnapshot = snap;
            _lastHardware = snap.Hardware;
            _lastPowerBefore = snap.ActivePowerScheme;

            if (_baselineSnapshot is null && _revisionStore is not null)
            {
                _baselineSnapshot = await _revisionStore.LoadLatestByKindAsync(RevisionKind.ManualCheckpoint, ct)
                                    ?? await _revisionStore.LoadLatestByKindAsync(RevisionKind.AutoBaseline, ct);
            }

            if (_revisionStore is not null)
            {
                await _revisionStore.SaveRevisionAsync(snap, ct);
                var list = await _revisionStore.ListRevisionsAsync(ct);
                SavedRevisions.Clear();
                foreach (var r in list.Take(10)) SavedRevisions.Add(r);
            }

            _baselineSnapshot ??= snap;
            ApplySnapshotToUi(snap);

            var diff = RevisionDiffCalculator.Compute(_baselineSnapshot, snap);
            RevisionDeltas.Clear();
            foreach (var d in diff.TelemetryDeltas) RevisionDeltas.Add(d);

            StatusMessage = $"Full revision complete — Health Score: {snap.Scores.OverallScore}/100 ({snap.Recommendations.Count} finding(s)).";
        }
        catch (Exception ex) { StatusMessage = $"Revision failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task SaveBaselineCheckpointAsync(CancellationToken ct)
    {
        if (IsBusy || _revisionOrchestrator is null || _revisionStore is null) return;
        IsBusy = true;
        StatusMessage = "Saving manual Before baseline checkpoint...";
        try
        {
            var snap = await _revisionOrchestrator.CaptureRevisionAsync(
                new RevisionCaptureOptions($"Manual Baseline {DateTime.Now:yyyy-MM-dd HH:mm}", RevisionKind.ManualCheckpoint, true, IncludeCommsInGuard),
                null, ct);
            await _revisionStore.SaveRevisionAsync(snap, ct);
            _baselineSnapshot = snap;
            _latestSnapshot = snap;
            ApplySnapshotToUi(snap);
            StatusMessage = $"Saved Before checkpoint ({snap.RevisionId.ToString("N")[..8]}). Score: {snap.Scores.OverallScore}/100.";
        }
        catch (Exception ex) { StatusMessage = $"Checkpoint failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task InstallSafeDriversAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        var rebootPending = _latestSnapshot?.Drivers.Data?.SystemRebootPending ?? false;
        var safeCandidates = DriverUpdates.Where(c => DriverGuardrails.CanAutoInstall(c, rebootPending, out _)).ToList();
        if (safeCandidates.Count == 0)
        {
            StatusMessage = rebootPending
                ? "Reboot pending — restart Windows before installing driver updates."
                : "No auto-installable WHQL/winget driver updates queued.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Requesting elevation to install {safeCandidates.Count} verified driver update(s)...";
        try
        {
            var req = new ElevatedRequest(1, Guid.NewGuid(), WorkerMode.InstallDrivers, null, null, null,
                new DriverUpdatePayload(safeCandidates, rebootPending));
            var res = await _elevatedExecutor.ExecuteAsync(req, ct);
            StatusMessage = res.Message;
        }
        catch (Exception ex) { StatusMessage = $"Driver update failed: {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Dry-Run failed: {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Apply failed: {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    public async Task ExportRevisionReportAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (_baselineSnapshot is null || _latestSnapshot is null || _reportFormatter is null)
        {
            StatusMessage = "Run Full Technical Revision first to generate Before/After data.";
            return;
        }
        IsBusy = true;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinPerfOptimizer-Reports");
            Directory.CreateDirectory(dir);
            var diff = RevisionDiffCalculator.Compute(_baselineSnapshot, _latestSnapshot, _lastPreviewProfile, DiffRows.ToList());
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            await File.WriteAllTextAsync(Path.Combine(dir, $"revision-{stamp}.html"), _reportFormatter.ToHtml(diff), ct);
            await File.WriteAllTextAsync(Path.Combine(dir, $"revision-{stamp}.md"), _reportFormatter.ToMarkdown(diff), ct);
            await File.WriteAllTextAsync(Path.Combine(dir, $"revision-{stamp}.json"), _reportFormatter.ToJson(diff), ct);
            StatusMessage = $"Exported unified Before/After HTML, MD & JSON reports to {dir}";
        }
        catch (Exception ex) { StatusMessage = $"Revision export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ApplySnapshotToUi(SystemRevisionSnapshot snap)
    {
        HardwarePill = $"{snap.Hardware.Model} · {snap.Hardware.CpuVendor} · {snap.Hardware.RamGb:0.#} GB RAM";
        PowerPill = snap.Hardware.OnAcPower ? $"AC · {ExtractShortPlan(snap.ActivePowerScheme)}" : $"Battery · {ExtractShortPlan(snap.ActivePowerScheme)}";
        UacPill = snap.WasElevated ? "UAC: Admin" : "UAC: Standard";

        OverallHealthScore = snap.Scores.OverallScore;
        DriverScore = snap.Scores.DriverScore;
        BatteryScore = snap.Scores.BatteryScore;
        MemoryScore = snap.Scores.MemoryScore;
        CpuScore = snap.Scores.CpuScore;
        StorageScore = snap.Scores.StorageScore;

        CpuSummary = snap.Cpu.Data is { } c
            ? $"{c.Name} · {c.PhysicalCores}C/{c.LogicalProcessors}T · {c.CurrentClockMhz}/{c.MaxObservedBoostMhz} MHz · DPC {c.SampledDpcTimePercent:0.1}% · Throttle: {c.ActiveThrottleReason}"
            : snap.Cpu.DiagnosticNote ?? "CPU probe failed";

        MemorySummary = snap.Memory.Data is { } m
            ? $"{m.TotalPhysicalGb:0.0} GB ({m.AvailablePhysicalGb:0.0} GB free) · Commit {m.CommitPressurePercent:0.#}% · {m.ChannelTopologyNote} · XMP/EXPO: {m.XmpExpoHint}"
            : snap.Memory.DiagnosticNote ?? "Memory probe failed";

        BatterySummary = snap.Battery.Data is { Presence: BatteryPresence.Present } b && b.Batteries.Count > 0
            ? $"Wear {b.Batteries[0].WearPercent:0.#}% ({b.Batteries[0].FullChargeCapacityMwh}/{b.Batteries[0].DesignCapacityMwh} mWh) · Rating: {b.Batteries[0].Rating}" + (b.Batteries[0].CycleCount.HasValue ? $" · {b.Batteries[0].CycleCount} cycles" : "")
            : "No battery detected (Desktop / AC-only)";

        StorageSummary = snap.Storage.Data is { } s
            ? $"{s.Disks.Count} disk(s) · {string.Join(", ", s.Volumes.Select(v => $"{v.DriveLetter} {v.FreePercent:0.#}% free"))} ({snap.Storage.Status})"
            : snap.Storage.DiagnosticNote ?? "Storage probe failed";

        GpuSummary = snap.Gpu.Data is { } g
            ? $"{g.Name} ({g.VramGb:0.#} GB VRAM) · Driver {g.DriverVersion ?? "?"} · HAGS: {(g.HagsEnabled ? "On" : "Off")}"
            : "GPU probe unavailable";

        DriversSummary = snap.Drivers.Data is { } d
            ? $"{d.TotalDevices} PnP devices · {d.ErroredCount} errors · {d.UnsignedCount} unsigned · {d.AvailableUpdates.Count} update(s)"
            : snap.Drivers.DiagnosticNote ?? "Driver probe failed";

        OemAdvisorHint = $"Recommended OEM Utility: {WinPerfOptimizer.Infrastructure.Probes.OemToolAdvisor.DetectOfficialOemAdvisorHint(snap.Hardware.Manufacturer, snap.Hardware.GpuNames) ?? "Windows Update WHQL"}";

        FlaggedDrivers.Clear();
        if (snap.Drivers.Data is not null)
            foreach (var f in snap.Drivers.Data.FlaggedDevices) FlaggedDrivers.Add(f);

        DriverUpdates.Clear();
        if (snap.Drivers.Data is not null)
            foreach (var u in snap.Drivers.Data.AvailableUpdates) DriverUpdates.Add(u);

        Recommendations.Clear();
        foreach (var r in snap.Recommendations) Recommendations.Add(r);

        StorageDisks.Clear();
        if (snap.Storage.Data is not null)
            foreach (var disk in snap.Storage.Data.Disks) StorageDisks.Add(disk);

        MemoryDimms.Clear();
        if (snap.Memory.Data is not null)
            foreach (var dimm in snap.Memory.Data.Dimms) MemoryDimms.Add(dimm);

        StartupEntries.Clear();
        foreach (var st in snap.StartupEntries) StartupEntries.Add(st);

        HeavyProcesses.Clear();
        foreach (var hp in snap.HeavyProcesses) HeavyProcesses.Add(hp);
    }

    private static string ExtractShortPlan(string raw)
    {
        var start = raw.IndexOf('(');
        var end = raw.LastIndexOf(')');
        return start >= 0 && end > start ? raw[(start + 1)..end] : raw;
    }
}
