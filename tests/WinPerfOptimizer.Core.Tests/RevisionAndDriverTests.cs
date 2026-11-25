using FluentAssertions;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;
using Xunit;

namespace WinPerfOptimizer.Core.Tests;

public class RevisionAndDriverTests
{
    [Fact]
    public void BatteryReportXmlParser_ParsesDesignFullCycleAndWear()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport>
              <SystemInformation>
                <ConnectedStandby>1</ConnectedStandby>
              </SystemInformation>
              <Batteries>
                <Battery>
                  <Id>BAT0</Id>
                  <Manufacturer>ASUSTeK</Manufacturer>
                  <Chemistry>LION</Chemistry>
                  <DesignCapacity>48000</DesignCapacity>
                  <FullChargeCapacity>36000</FullChargeCapacity>
                  <CycleCount>142</CycleCount>
                </Battery>
              </Batteries>
              <History>
                <HistoryEntry LocalDate="2026-10-01" FullChargeCapacity="37000" DesignCapacity="48000" />
                <HistoryEntry LocalDate="2026-11-01" FullChargeCapacity="36000" DesignCapacity="48000" />
              </History>
            </BatteryReport>
            """;

        var report = BatteryReportXmlParser.Parse(xml, liveChargePercent: 85, liveRateMw: -12000);
        report.Presence.Should().Be(BatteryPresence.Present);
        report.ConnectedStandbySupported.Should().BeTrue();
        report.Batteries.Should().HaveCount(1);
        report.Batteries[0].WearPercent.Should().Be(25.0);
        report.Batteries[0].Rating.Should().Be(BatteryWearRating.Fair);
        report.Batteries[0].CycleCount.Should().Be(142);
        report.Batteries[0].History.Should().HaveCount(2);
    }

    [Fact]
    public void BatteryReportXmlParser_DesktopWithoutBatteries_ReturnsNotPresent()
    {
        const string xml = "<BatteryReport><Batteries /></BatteryReport>";
        var report = BatteryReportXmlParser.Parse(xml);
        report.Presence.Should().Be(BatteryPresence.NotPresent);
        report.Batteries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Firmware", true)]
    [InlineData("SecurityDevices", true)]
    [InlineData("DiskDrive", true)]
    [InlineData("Display", false)]
    [InlineData("Net", false)]
    public void DriverGuardrails_ClassifiesHighRiskDeviceClasses(string deviceClass, bool expectedHighRisk)
    {
        DriverGuardrails.IsHighRiskDeviceClass(deviceClass).Should().Be(expectedHighRisk);
    }

    [Fact]
    public void DriverGuardrails_BlocksRebootPendingAndUnlistedWinget()
    {
        var firmwareCand = new DriverUpdateCandidate(
            "fw-1", "ACPI\\UEFI", "System Firmware", "1.0", "2.0",
            DriverUpdateSource.WindowsUpdateCatalog, DriverRiskTier.CriticalBootOrFirmware,
            true, false, "Firmware");
        DriverGuardrails.CanAutoInstall(firmwareCand, systemRebootPending: false, out _).Should().BeFalse();

        var unlistedWinget = new DriverUpdateCandidate(
            "DriverBooster.Pro", "", "Driver Booster", "1.0", "9.0",
            DriverUpdateSource.WingetOemCompanion, DriverRiskTier.LowUserland,
            false, true, null);
        DriverGuardrails.CanAutoInstall(unlistedWinget, systemRebootPending: false, out _).Should().BeFalse();

        var validWua = new DriverUpdateCandidate(
            "wua-123", "PCI\\VEN_10DE", "NVIDIA Display", "555.0", "566.0",
            DriverUpdateSource.WindowsUpdateCatalog, DriverRiskTier.HighDisplayNetwork,
            false, true, null);
        DriverGuardrails.CanAutoInstall(validWua, systemRebootPending: true, out _).Should().BeFalse();
        DriverGuardrails.CanAutoInstall(validWua, systemRebootPending: false, out _).Should().BeTrue();
    }

    [Fact]
    public void RecommendationEngine_And_DiffCalculator_ProduceBeforeAfterDelta()
    {
        var hw = new HardwareProfile("ASUS", "FA506IU", "Windows 11", 26200, "Ryzen 7", "AMD", 16, "GTX 1660 Ti", true, true, true, true, true, false, 0, "");
        var before = new SystemRevisionSnapshot(
            Guid.NewGuid(), "Before", RevisionKind.AutoBaseline, DateTimeOffset.UtcNow.AddMinutes(-5), 10000, false,
            hw, "Balanced",
            new ProbeResult<DriverAuditReport>(ProbeStatus.Ok, new DriverAuditReport(100, 1, 0, 0, [
                new DriverDeviceInfo("PCI\\1", "Realtek Net", "Net", 10, "rt640.inf", "1.0", null, "Realtek", "Microsoft", DriverSignatureTrust.WhqlSigned, [], DriverIssueFlags.ConfigManagerError)
            ], [], false), TimeSpan.FromMilliseconds(50)),
            new ProbeResult<BatteryHealthReport>(ProbeStatus.Ok, new BatteryHealthReport(BatteryPresence.NotPresent, [], false), TimeSpan.FromMilliseconds(10)),
            new ProbeResult<MemoryRevisionInfo>(ProbeStatus.Ok, new MemoryRevisionInfo(16, 8, 24, 10, 41, 2, 2, "Dual", XmpExpoStatus.LikelyActive, [], []), TimeSpan.FromMilliseconds(10)),
            new ProbeResult<CpuRevisionInfo>(ProbeStatus.Ok, new CpuRevisionInfo("Ryzen 7", "AMD", 8, 16, 8, 0, 2900, 2900, 4200, 4200, null, CpuThrottleReason.None, true, 100, 1.0, 0.5, []), TimeSpan.FromMilliseconds(10)),
            new ProbeResult<StorageRevisionInfo>(ProbeStatus.RequiresElevation, null, TimeSpan.FromMilliseconds(5), "Admin needed"),
            new ProbeResult<GpuRevisionInfo>(ProbeStatus.Ok, new GpuRevisionInfo("GTX 1660 Ti", 6, "566.0", null, false, true), TimeSpan.FromMilliseconds(10)),
            [], [], new SubsystemScores(0, 0, 0, 0, 0, 0), []);

        var engine = new RecommendationEngine();
        var beforeScores = engine.Score(before);
        var beforeRecs = engine.Evaluate(before);
        before = before with { Scores = beforeScores, Recommendations = beforeRecs };

        beforeScores.BatteryScore.Should().Be(100);
        beforeRecs.Should().Contain(r => r.RuleId == "DRV-PNP-ERROR");
        beforeRecs.Should().Contain(r => r.RuleId == "STO-DEEP-SCAN");
        beforeRecs.Should().Contain(r => r.RuleId == "GPU-HAGS-OFF");

        var after = before with
        {
            RevisionId = Guid.NewGuid(),
            Kind = RevisionKind.PostOptimization,
            Drivers = new ProbeResult<DriverAuditReport>(ProbeStatus.Ok, new DriverAuditReport(100, 0, 0, 0, [], [], false), TimeSpan.FromMilliseconds(40)),
            Gpu = new ProbeResult<GpuRevisionInfo>(ProbeStatus.Ok, new GpuRevisionInfo("GTX 1660 Ti", 6, "566.0", null, true, true), TimeSpan.FromMilliseconds(10))
        };
        after = after with { Scores = engine.Score(after), Recommendations = engine.Evaluate(after) };

        var diff = RevisionDiffCalculator.Compute(before, after, PresetProfile.Gaming);
        diff.TelemetryDeltas.Should().Contain(d => d.Metric == "Overall Score");
        var html = new RevisionReportFormatter().ToHtml(diff);
        html.Should().Contain("ShiftGuard Technical Revision");
        var json = new RevisionReportFormatter().ToJson(diff);
        json.Should().Contain("\"schemaVersion\": 2");
    }
}
