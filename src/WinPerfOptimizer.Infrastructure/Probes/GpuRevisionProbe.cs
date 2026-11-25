using System.Diagnostics;
using System.Globalization;
using System.Management;
using Microsoft.Win32;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed class GpuRevisionProbe(IRegistryService registry) : IGpuRevisionProbe
{
    public async Task<ProbeResult<GpuRevisionInfo>> ProbeGpuAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var data = await Task.Run(() =>
            {
                var name = "Unknown GPU";
                double vramGb = 0;
                string? drvVer = null;
                DateOnly? drvDate = null;

                using var gpu = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate FROM Win32_VideoController");
                using var res = gpu.Get();
                foreach (var obj in res)
                using (obj)
                {
                    var n = obj["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (name == "Unknown GPU"
                        || n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        name = n;
                        drvVer = obj["DriverVersion"]?.ToString();
                        if (ManagementDateTimeConverterSafe(obj["DriverDate"]?.ToString()) is { } dt)
                            drvDate = dt;
                        if (ulong.TryParse(obj["AdapterRAM"]?.ToString(), out var ramBytes))
                            vramGb = Math.Round(ramBytes / (1024.0 * 1024 * 1024), 1);
                    }
                }

                var qwVram = Read64BitVramGb();
                if (qwVram > vramGb) vramGb = qwVram;

                var hagsState = registry.ReadState(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
                var hagsEnabled = hagsState.Existed && hagsState.SerializedValue == "2";
                var hagsSupported = Environment.OSVersion.Version.Build >= 19041
                    && (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

                return new GpuRevisionInfo(name, vramGb, drvVer, drvDate, hagsEnabled, hagsSupported);
            }, ct).ConfigureAwait(false);

            return new ProbeResult<GpuRevisionInfo>(ProbeStatus.Ok, data, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<GpuRevisionInfo>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
    }

    private static double Read64BitVramGb()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null) return 0;
            double maxGb = 0;
            foreach (var subName in baseKey.GetSubKeyNames())
            {
                if (!subName.All(char.IsDigit)) continue;
                using var sub = baseKey.OpenSubKey(subName);
                var val = sub?.GetValue("HardwareInformation.qwMemorySize");
                if (val is long l && l > 0)
                    maxGb = Math.Max(maxGb, Math.Round(l / (1024.0 * 1024 * 1024), 1));
                else if (val is byte[] bytes && bytes.Length >= 8)
                {
                    var qw = BitConverter.ToUInt64(bytes, 0);
                    maxGb = Math.Max(maxGb, Math.Round(qw / (1024.0 * 1024 * 1024), 1));
                }
            }
            return maxGb;
        }
        catch { return 0; }
    }

    private static DateOnly? ManagementDateTimeConverterSafe(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 8) return null;
        return DateOnly.TryParseExact(dmtf[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
