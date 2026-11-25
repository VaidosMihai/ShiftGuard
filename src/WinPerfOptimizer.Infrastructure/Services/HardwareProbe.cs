using System.Diagnostics;
using System.Management;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class HardwareProbe(IPowerPlanService powerPlan) : IHardwareProbe
{
    public async Task<HardwareProfile> ProbeAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var manufacturer = "Unknown";
            var model = "Unknown";
            var ramGb = 0.0;
            using (var cs = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            using (var csResults = cs.Get())
            {
                foreach (var obj in csResults)
                using (obj)
                {
                    manufacturer = obj["Manufacturer"]?.ToString() ?? manufacturer;
                    model = obj["Model"]?.ToString() ?? model;
                    if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out var bytes))
                        ramGb = Math.Round(bytes / (1024.0 * 1024 * 1024), 1);
                }
            }

            var osCaption = "Windows";
            var osBuild = Environment.OSVersion.Version.Build;
            using (var os = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
            using (var osResults = os.Get())
            {
                foreach (var obj in osResults)
                using (obj)
                {
                    osCaption = obj["Caption"]?.ToString() ?? osCaption;
                    if (int.TryParse(obj["BuildNumber"]?.ToString(), out var b)) osBuild = b;
                }
            }

            var cpuName = "Unknown";
            var cpuVendor = "Unknown";
            using (var cpu = new ManagementObjectSearcher("SELECT Name, Manufacturer FROM Win32_Processor"))
            using (var cpuResults = cpu.Get())
            {
                foreach (var obj in cpuResults)
                using (obj)
                {
                    cpuName = obj["Name"]?.ToString()?.Trim() ?? cpuName;
                    var mfg = obj["Manufacturer"]?.ToString() ?? "";
                    if (mfg.Contains("AMD", StringComparison.OrdinalIgnoreCase)) cpuVendor = "AMD";
                    else if (mfg.Contains("Intel", StringComparison.OrdinalIgnoreCase)) cpuVendor = "Intel";
                    break;
                }
            }

            var gpus = new List<string>();
            var hagsEligible = false;
            using (var gpu = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            using (var gpuResults = gpu.Get())
            {
                foreach (var obj in gpuResults)
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    gpus.Add(name);
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                        hagsEligible = osBuild >= 19041;
                }
            }

            var hasBattery = false;
            var onAc = true;
            using (var bat = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery"))
            using (var batResults = bat.Get())
            {
                foreach (var obj in batResults)
                using (obj)
                {
                    hasBattery = true;
                    if (ushort.TryParse(obj["BatteryStatus"]?.ToString(), out var st) && st == 1)
                        onAc = false;
                }
            }

            var (_, sleepOut) = await powerPlan.RunPowerCfgAsync(["/a"], ct).ConfigureAwait(false);
            var isModernStandby = sleepOut.Contains("S0 Low Power Idle", StringComparison.OrdinalIgnoreCase)
                               || sleepOut.Contains("Standby (S0", StringComparison.OrdinalIgnoreCase);

            var hasSsd = false;
            try
            {
                using var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType FROM MSFT_PhysicalDisk");
                using var diskResults = disks.Get();
                foreach (var d in diskResults)
                using (d)
                {
                    if (ushort.TryParse(d["MediaType"]?.ToString(), out var mt) && mt == 4) hasSsd = true;
                }
            }
            catch
            {
                hasSsd = true;
            }

            var oemList = new List<string>();
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    if (p.ProcessName.Contains("ArmouryCrate", StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.Contains("GHelper", StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.Contains("MyASUS", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!oemList.Contains(p.ProcessName)) oemList.Add(p.ProcessName);
                    }
                }
                catch { /* exited process */ }
                finally { p.Dispose(); }
            }

            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var hiberPath = Path.Combine(sysDrive, "hiberfil.sys");
            var hiberExists = File.Exists(hiberPath);
            var hiberSize = hiberExists ? Math.Round(new FileInfo(hiberPath).Length / (1024.0 * 1024 * 1024), 2) : 0;

            return new HardwareProfile(
                manufacturer, model, osCaption, osBuild, cpuName, cpuVendor, ramGb,
                string.Join("; ", gpus), hagsEligible, hasBattery, onAc, isModernStandby,
                hasSsd, hiberExists, hiberSize, string.Join(", ", oemList));
        }, ct);
    }
}
