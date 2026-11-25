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
}
