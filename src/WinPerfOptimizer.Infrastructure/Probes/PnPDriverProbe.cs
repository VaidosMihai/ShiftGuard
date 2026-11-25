using System.Diagnostics;
using System.Globalization;
using System.Management;
using Microsoft.Win32;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed class PnPDriverProbe(IDriverUpdateDiscoveryService? discovery = null) : IDriverProbe
{
    public async Task<ProbeResult<DriverAuditReport>> ScanDriversAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (allDevices, flagged, errored, missing, unsigned, rebootPending) = await Task.Run(ScanPnp, ct).ConfigureAwait(false);
            IReadOnlyList<DriverUpdateCandidate> updates = [];
            if (discovery is not null)
            {
                try { updates = await discovery.FindUpdatesAsync(allDevices, ct).ConfigureAwait(false); }
                catch { updates = []; }
            }

            var report = new DriverAuditReport(allDevices.Count, errored, missing, unsigned, flagged, updates, rebootPending);
            return new ProbeResult<DriverAuditReport>(ProbeStatus.Ok, report, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<DriverAuditReport>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
    }

    private static (List<DriverDeviceInfo> All, List<DriverDeviceInfo> Flagged, int Errored, int Missing, int Unsigned, bool RebootPending) ScanPnp()
    {
        var errorMap = new Dictionary<string, (int Code, string Name, string Class)>(StringComparer.OrdinalIgnoreCase);
        using (var pnp = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity"))
        using (var res = pnp.Get())
        {
            foreach (var obj in res)
            using (obj)
            {
                var id = obj["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                _ = int.TryParse(obj["ConfigManagerErrorCode"]?.ToString(), out var code);
                errorMap[id] = (code, obj["Name"]?.ToString() ?? id, obj["PNPClass"]?.ToString() ?? "Unknown");
            }
        }

        var all = new List<DriverDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var drv = new ManagementObjectSearcher("SELECT DeviceID, DeviceName, DeviceClass, InfName, DriverVersion, DriverDate, Manufacturer, Signer, IsSigned FROM Win32_PnPSignedDriver"))
        using (var res = drv.Get())
        {
            foreach (var obj in res)
            using (obj)
            {
                var id = obj["DeviceID"]?.ToString();
                var name = obj["DeviceName"]?.ToString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                seen.Add(id);
                errorMap.TryGetValue(id, out var pnpInfo);
                var code = pnpInfo.Code;
                var devClass = obj["DeviceClass"]?.ToString() ?? pnpInfo.Class ?? "Unknown";
                var inf = obj["InfName"]?.ToString();
                var ver = obj["DriverVersion"]?.ToString();
                var signer = obj["Signer"]?.ToString();
                var isSigned = obj["IsSigned"] as bool? ?? false;

                var trust = !isSigned
                    ? DriverSignatureTrust.Unsigned
                    : (signer?.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase) == true
                        ? DriverSignatureTrust.MicrosoftInbox
                        : DriverSignatureTrust.WhqlSigned);

                var flags = DriverIssueFlags.None;
                if (code != 0) flags |= DriverIssueFlags.ConfigManagerError;
                if (code == 28 || string.IsNullOrWhiteSpace(inf)) flags |= DriverIssueFlags.MissingDriver;
                if (!isSigned) flags |= DriverIssueFlags.UnsignedOrUntrusted;

                all.Add(new DriverDeviceInfo(
                    id, name, devClass, code, inf, ver, ParseDate(obj["DriverDate"]?.ToString()),
                    obj["Manufacturer"]?.ToString(), signer, trust, [], flags));
            }
        }

        foreach (var kv in errorMap.Where(e => e.Value.Code != 0 && !seen.Contains(e.Key)))
        {
            all.Add(new DriverDeviceInfo(
                kv.Key, kv.Value.Name, kv.Value.Class, kv.Value.Code,
                null, null, null, null, null, DriverSignatureTrust.Unknown, [],
                DriverIssueFlags.ConfigManagerError | DriverIssueFlags.MissingDriver));
        }

        var flagged = all.Where(d => d.Issues != DriverIssueFlags.None).ToList();
        var errored = all.Count(d => d.ConfigManagerErrorCode != 0);
        var missing = all.Count(d => d.Issues.HasFlag(DriverIssueFlags.MissingDriver));
        var unsigned = all.Count(d => d.Issues.HasFlag(DriverIssueFlags.UnsignedOrUntrusted));

        return (all, flagged, errored, missing, unsigned, IsRebootPending());
    }

    private static DateOnly? ParseDate(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 8) return null;
        return DateOnly.TryParseExact(dmtf[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static bool IsRebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null) return true;
        }
        catch { /* ignore */ }
        return false;
    }
}
