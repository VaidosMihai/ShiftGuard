using System.Diagnostics;
using System.Management;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed class PowerCfgBatteryProbe(IPowerPlanService powerPlan) : IBatteryProbe
{
    public async Task<ProbeResult<BatteryHealthReport>> ProbeBatteryAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPerfOptimizer", "Temp");
        Guardrails.AssertSafeDirectoryPath(tempDir);
        Directory.CreateDirectory(tempDir);
        var xmlPath = Path.Combine(tempDir, $"bat-{Guid.NewGuid():N}.xml");

        try
        {
            var (livePct, liveRate) = await Task.Run(ReadLiveBatteryMetrics, ct).ConfigureAwait(false);
            var (exitCode, _) = await powerPlan.RunPowerCfgAsync(["/batteryreport", "/xml", "/output", xmlPath], ct).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(xmlPath))
            {
                return new ProbeResult<BatteryHealthReport>(
                    livePct.HasValue ? ProbeStatus.Partial : ProbeStatus.NotApplicable,
                    new BatteryHealthReport(livePct.HasValue ? BatteryPresence.ReportUnavailable : BatteryPresence.NotPresent, [], false),
                    sw.Elapsed,
                    $"powercfg /batteryreport exited with {exitCode}.");
            }

            var xml = await File.ReadAllTextAsync(xmlPath, ct).ConfigureAwait(false);
            var report = BatteryReportXmlParser.Parse(xml, livePct, liveRate);
            return new ProbeResult<BatteryHealthReport>(
                report.Presence == BatteryPresence.NotPresent ? ProbeStatus.NotApplicable : ProbeStatus.Ok,
                report,
                sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<BatteryHealthReport>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static (double? ChargePercent, double? RateMw) ReadLiveBatteryMetrics()
    {
        double? pct = null;
        double? rate = null;
        try
        {
            using var bat = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery");
            using var res = bat.Get();
            foreach (var obj in res)
            using (obj)
            {
                if (double.TryParse(obj["EstimatedChargeRemaining"]?.ToString(), out var p))
                    pct = p;
                break;
            }
        }
        catch { /* ignore */ }

        try
        {
            using var wmi = new ManagementObjectSearcher(@"root\WMI", "SELECT Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus");
            using var res = wmi.Get();
            foreach (var obj in res)
            using (obj)
            {
                var charging = obj["Charging"] as bool? == true;
                var discharging = obj["Discharging"] as bool? == true;
                if (charging && double.TryParse(obj["ChargeRate"]?.ToString(), out var cr))
                    rate = cr;
                else if (discharging && double.TryParse(obj["DischargeRate"]?.ToString(), out var dr))
                    rate = -dr;
                break;
            }
        }
        catch { /* optional ACPI counter */ }

        return (pct, rate);
    }
}
