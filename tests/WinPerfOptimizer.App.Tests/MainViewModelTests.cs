using FluentAssertions;
using NSubstitute;
using WinPerfOptimizer.App.ViewModels;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using Xunit;

namespace WinPerfOptimizer.App.Tests;

public class MainViewModelTests
{
    private readonly IHardwareProbe _hw = Substitute.For<IHardwareProbe>();
    private readonly IPowerPlanService _power = Substitute.For<IPowerPlanService>();
    private readonly IRegistryService _reg = Substitute.For<IRegistryService>();
    private readonly IProcessInspector _proc = Substitute.For<IProcessInspector>();
    private readonly IBackupEngine _backup = Substitute.For<IBackupEngine>();
    private readonly IElevatedExecutor _elevated = Substitute.For<IElevatedExecutor>();

    private MainViewModel CreateVm()
    {
        _hw.ProbeAsync(Arg.Any<CancellationToken>()).Returns(new HardwareProfile(
            "ASUSTeK", "FA506IU", "Windows 11 Pro", 26200, "Ryzen 7 4800H", "AMD",
            24, "GTX 1660 Ti", true, true, true, true, true, false, 0, "ArmouryCrate"));
        _power.GetActiveSchemeAsync(Arg.Any<CancellationToken>()).Returns("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e (Balanced)");
        _reg.ReadState(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => new RegistrySnapshotEntry(ci.ArgAt<string>(0), ci.ArgAt<string>(1), false, null, null));
        _proc.GetStartupEntries().Returns([new StartupEntry("Steam", "steam.exe", "HKCU", StartupCategory.GamingLauncher, false)]);
        _proc.GetHeavyProcesses(Arg.Any<bool>()).Returns([]);
        return new MainViewModel(_hw, _power, _reg, _proc, _backup, _elevated);
    }

    [Fact]
    public async Task RunAudit_PopulatesPillsAndStartupEntries()
    {
        var vm = CreateVm();
        await vm.RunAuditCommand.ExecuteAsync(null);
        vm.HardwarePill.Should().Contain("FA506IU");
        vm.PowerPill.Should().Contain("Balanced");
        vm.StartupEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunDryRun_Gaming_PopulatesDiffRowsWithoutCallingElevatedExecutor()
    {
        var vm = CreateVm();
        await vm.RunDryRunCommand.ExecuteAsync(PresetProfile.Gaming);
        vm.DiffRows.Should().NotBeEmpty();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ElevatedRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyGaming_CallsElevatedExecutorWithApplyProfile()
    {
        var vm = CreateVm();
        vm.RollbackCommand.CanExecute(null).Should().BeFalse();
        var snapId = Guid.NewGuid();
        _elevated.ExecuteAsync(Arg.Any<ElevatedRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ElevatedResult(ci.ArgAt<ElevatedRequest>(0).RequestId, true, snapId, [], "OK", "Applied"));

        await vm.ApplyGamingCommand.ExecuteAsync(null);
        vm.HasSnapshot.Should().BeTrue();
        vm.RollbackCommand.CanExecute(null).Should().BeTrue();
        vm.BackupPill.Should().Contain(snapId.ToString("N")[..8]);
    }

    [Fact]
    public async Task RunFullRevision_PopulatesHealthScoresAndDeltas()
    {
        var orchestrator = Substitute.For<ISystemRevisionOrchestrator>();
        var hw = new HardwareProfile("ASUS", "FA506IU", "Windows 11", 26200, "Ryzen 7", "AMD", 16, "GTX 1660 Ti", true, true, true, true, true, false, 0, "");
        var snap = new SystemRevisionSnapshot(
            Guid.NewGuid(), "Baseline", RevisionKind.AutoBaseline, DateTimeOffset.UtcNow, 1000, false,
            hw, "Balanced",
            new ProbeResult<DriverAuditReport>(ProbeStatus.Ok, new DriverAuditReport(50, 0, 0, 0, [], [], false), TimeSpan.Zero),
            new ProbeResult<BatteryHealthReport>(ProbeStatus.NotApplicable, new BatteryHealthReport(BatteryPresence.NotPresent, [], false), TimeSpan.Zero),
            new ProbeResult<MemoryRevisionInfo>(ProbeStatus.Ok, new MemoryRevisionInfo(16, 8, 24, 10, 41, 2, 2, "Dual", XmpExpoStatus.LikelyActive, [], []), TimeSpan.Zero),
            new ProbeResult<CpuRevisionInfo>(ProbeStatus.Ok, new CpuRevisionInfo("Ryzen 7", "AMD", 8, 16, 8, 0, 2900, 2900, 4200, 4200, null, CpuThrottleReason.None, true, 100, 1.0, 0.5, []), TimeSpan.Zero),
            new ProbeResult<StorageRevisionInfo>(ProbeStatus.Ok, new StorageRevisionInfo([], []), TimeSpan.Zero),
            new ProbeResult<GpuRevisionInfo>(ProbeStatus.Ok, new GpuRevisionInfo("GTX 1660 Ti", 6, "566.0", null, true, true), TimeSpan.Zero),
            [], [], new SubsystemScores(100, 100, 95, 100, 100, 99), []);

        orchestrator.CaptureRevisionAsync(Arg.Any<RevisionCaptureOptions>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(snap);

        var vm = new MainViewModel(_hw, _power, _reg, _proc, _backup, _elevated, orchestrator);
        await vm.RunFullRevisionCommand.ExecuteAsync(null);

        vm.OverallHealthScore.Should().Be(99);
        vm.RevisionDeltas.Should().NotBeEmpty();
        vm.StatusMessage.Should().Contain("99/100");
    }
}
