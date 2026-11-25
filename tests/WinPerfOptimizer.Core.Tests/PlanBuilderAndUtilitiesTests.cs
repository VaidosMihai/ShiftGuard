using FluentAssertions;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;
using Xunit;

namespace WinPerfOptimizer.Core.Tests;

public class PlanBuilderAndUtilitiesTests
{
    private static HardwareProfile LaptopAc(bool hags = true) => new(
        "ASUSTeK", "FA506IU", "Windows 11 Pro", 26200,
        "Ryzen 7 4800H", "AMD", 24, "NVIDIA GeForce GTX 1660 Ti",
        hags, true, true, true, true, false, 0, "ArmouryCrate");

    [Fact]
    public void BuildDaily_IncludesBatteryDcCommandsAndHttpDeliveryOpt()
    {
        var plan = OptimizationPlanBuilder.BuildDaily(LaptopAc(), edgeInstalled: true, chromeInstalled: true);
        plan.TargetPowerGuid.Should().Be(OptimizationPlanBuilder.BalancedPlanGuid);
        plan.PowerCommands.Should().Contain(c => c.Arguments[0] == "/setdcvalueindex");
        plan.RegistryChanges.Should().Contain(c => c.Name == "DODownloadMode" && (int)c.Value == 0);
        plan.RegistryChanges.Should().Contain(c => c.Name == "StartupBoostEnabled" && (int)c.Value == 0);
    }

    [Fact]
    public void BuildGaming_SkipsHighPerformanceOnBatteryUnlessForced()
    {
        var onBattery = LaptopAc() with { OnAcPower = false };
        var plan = OptimizationPlanBuilder.BuildGaming(onBattery, null, false, false, forceOnBattery: false);
        plan.TargetPowerGuid.Should().BeNull();
        plan.Warnings.Should().HaveCount(1);

        var forced = OptimizationPlanBuilder.BuildGaming(onBattery, null, false, false, forceOnBattery: true);
        forced.TargetPowerGuid.Should().Be(OptimizationPlanBuilder.HighPerformancePlanGuid);
    }

    [Fact]
    public void DirectXTokenMerger_PreservesExistingTokens()
    {
        var merged = DirectXTokenMerger.Merge("AutoHDREnable=1;", new Dictionary<string, string>
        {
            ["SwapEffectUpgradeEnable"] = "1",
            ["VRROptimizeEnable"] = "1"
        });
        merged.Should().Contain("AutoHDREnable=1;");
        merged.Should().Contain("SwapEffectUpgradeEnable=1;");
        merged.Should().Contain("VRROptimizeEnable=1;");
    }

    [Fact]
    public void StartupClassifier_And_Encoder_ProtectCriticalEntries()
    {
        StartupClassifier.Classify("Riot Vanguard", @"C:\Program Files\Riot Vanguard\vgc.exe").Should().Be(StartupCategory.Protected);
        StartupClassifier.Classify("Steam", @"C:\Steam\steam.exe").Should().Be(StartupCategory.GamingLauncher);
        StartupClassifier.Classify("utweb", @"C:\Users\u\utweb.exe").Should().Be(StartupCategory.BloatUpdater);

        var bytes = StartupApprovedEncoder.Encode(new byte[] { 2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, disable: true);
        bytes[0].Should().Be(3);
        bytes[1].Should().Be(1);
    }

    [Fact]
    public void DiffReportFormatter_EscapesHtmlInReport()
    {
        var html = DiffReportFormatter.ToHtml(PresetProfile.Gaming, "PC <script>alert(1)</script>", "Balanced", "High", [new DiffRow("GameMode", "Absent", "1", "Test")]);
        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }
}
