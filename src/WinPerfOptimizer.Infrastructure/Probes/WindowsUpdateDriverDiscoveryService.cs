using System.Diagnostics;
using System.Management;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed class WindowsUpdateDriverDiscoveryService : IDriverUpdateDiscoveryService
{
    public async Task<IReadOnlyList<DriverUpdateCandidate>> FindUpdatesAsync(
        IReadOnlyList<DriverDeviceInfo> devices,
        CancellationToken ct = default)
    {
        var results = new List<DriverUpdateCandidate>();

        await Task.Run(() =>
        {
            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType is null) return;
                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = true;
                dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                int count = searchResult.Updates.Count;

                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic upd = searchResult.Updates.Item(i);
                    string id = upd.Identity?.UpdateID ?? Guid.NewGuid().ToString("N");
                    string title = upd.Title ?? "Windows Driver Update";
                    string? devClass = null;
                    try { devClass = upd.DriverClass; } catch { /* dynamic COM prop optional */ }
                    string targetVer = "WHQL";
                    try { targetVer = upd.DriverVerDate?.ToString("yyyy-MM-dd") ?? "WHQL"; } catch { }
                    bool reboot = false;
                    try { reboot = upd.RebootRequired; } catch { }

                    var tier = DriverGuardrails.ClassifyRiskTier(devClass);
                    var allow = tier != DriverRiskTier.CriticalBootOrFirmware;
                    results.Add(new DriverUpdateCandidate(
                        id,
                        "",
                        title,
                        null,
                        targetVer,
                        DriverUpdateSource.WindowsUpdateCatalog,
                        tier,
                        reboot,
                        allow,
                        allow ? null : "High-risk Firmware/System/Boot class blocked from auto-install."));
                }
            }
            catch
            {
                /* WUA offline or disabled — non-fatal */
            }
        }, ct).ConfigureAwait(false);

        return results;
    }
}

public sealed class OptionalThermalSensorProvider : IOptionalThermalSensorProvider
{
    public bool IsAvailable { get; private set; } = true;

    public async Task<int?> TryReadPackageTempCelsiusAsync(CancellationToken ct = default)
    {
        return await Task.Run<int?>(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                using var res = searcher.Get();
                foreach (var obj in res)
                using (obj)
                {
                    if (double.TryParse(obj["CurrentTemperature"]?.ToString(), out var tenthsKelvin) && tenthsKelvin > 2732)
                    {
                        IsAvailable = true;
                        return (int)Math.Round((tenthsKelvin / 10.0) - 273.15);
                    }
                }
            }
            catch
            {
                IsAvailable = false;
            }
            return null;
        }, ct).ConfigureAwait(false);
    }
}

public static class OemToolAdvisor
{
    public static string? DetectOfficialOemAdvisorHint(string manufacturer, string gpuNames)
    {
        if (manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
            return "Dell Command | Update (dcu-cli / Dell SupportAssist)";
        if (manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase))
            return "Lenovo Vantage / System Update";
        if (manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase))
            return "HP Support Assistant / HP Image Assistant";
        if (manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            return "MyASUS / G-Helper";
        if (gpuNames.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA App (Driver WHQL)";
        if (gpuNames.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuNames.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD Software: Adrenalin Edition";
        return null;
    }
}
