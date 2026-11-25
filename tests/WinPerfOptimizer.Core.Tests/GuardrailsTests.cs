using FluentAssertions;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;
using Xunit;

namespace WinPerfOptimizer.Core.Tests;

public class GuardrailsTests
{
    [Theory]
    [InlineData(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"HKLM:/SOFTWARE/Policies/Microsoft/Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"HKLM:\SYSTEM\CurrentControlSet\\Services\WinDefend", "Start")]
    [InlineData(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\\System", "EnableLUA")]
    [InlineData(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles")]
    [InlineData(@"HKLM:\SYSTEM\ControlSet001\Services\WinDefend", "Start")]
    [InlineData(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")]
    [InlineData(@"Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode")]
    [InlineData(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", "Evil")]
    public void AssertSafeRegistryTarget_BlocksProtectedOrNonCanonicalTargets(string path, string name)
    {
        var act = () => Guardrails.AssertSafeRegistryTarget(path, name);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("wuauserv")]
    [InlineData("mpssvc")]
    [InlineData("AudioSrv")]
    public void AssertSafeServiceTarget_BlocksCoreServices(string service)
    {
        var act = () => Guardrails.AssertSafeServiceTarget(service);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void AssertSafeDirectoryPath_BlocksEscapeFromAllowedRoot()
    {
        var act = () => Guardrails.AssertSafeDirectoryPath(@"C:\Windows\System32", @"C:\PerfOptimizerBackup");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ValidateElevatedRequest_RejectsMismatchedModeAndPayload()
    {
        var bad = new ElevatedRequest(1, Guid.NewGuid(), WorkerMode.ApplyProfile, null, new RollbackPayload(Guid.NewGuid()), null);
        var act = () => Guardrails.ValidateElevatedRequest(bad);
        act.Should().Throw<InvalidOperationException>();
    }
}
