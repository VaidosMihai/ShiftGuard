using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed partial class MemoryRevisionProbe : IMemoryProbe
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public async Task<ProbeResult<MemoryRevisionInfo>> ProbeMemoryAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var data = await Task.Run(() =>
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                GlobalMemoryStatusEx(ref mem);

                const double gb = 1024.0 * 1024 * 1024;
                var totalPhys = Math.Round(mem.ullTotalPhys / gb, 2);
                var availPhys = Math.Round(mem.ullAvailPhys / gb, 2);
                var commitLimit = Math.Round(mem.ullTotalPageFile / gb, 2);
                var commitCharge = Math.Round((mem.ullTotalPageFile - mem.ullAvailPageFile) / gb, 2);
                var commitPct = commitLimit > 0 ? Math.Round(100.0 * commitCharge / commitLimit, 1) : 0;

                var totalSlots = 0;
                try
                {
                    using var arr = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                    using var arrRes = arr.Get();
                    foreach (var obj in arrRes)
                    using (obj)
                    {
                        if (int.TryParse(obj["MemoryDevices"]?.ToString(), out var slots))
                            totalSlots += slots;
                    }
                }
                catch { /* VM or restricted SMBIOS */ }

                var dimms = new List<MemoryDimmInfo>();
                try
                {
                    using var pm = new ManagementObjectSearcher("SELECT DeviceLocator, BankLabel, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, ConfiguredVoltage, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
                    using var pmRes = pm.Get();
                    foreach (var obj in pmRes)
                    using (obj)
                    {
                        _ = ulong.TryParse(obj["Capacity"]?.ToString(), out var capBytes);
                        _ = int.TryParse(obj["SMBIOSMemoryType"]?.ToString(), out var smbiosType);
                        _ = int.TryParse(obj["Speed"]?.ToString(), out var speed);
                        _ = int.TryParse(obj["ConfiguredClockSpeed"]?.ToString(), out var cfgSpeed);
                        int? voltage = int.TryParse(obj["ConfiguredVoltage"]?.ToString(), out var v) && v > 0 ? v : null;

                        dimms.Add(new MemoryDimmInfo(
                            obj["DeviceLocator"]?.ToString()?.Trim() ?? "Unknown",
                            obj["BankLabel"]?.ToString()?.Trim() ?? "",
                            Math.Round(capBytes / gb, 1),
                            MapDdrType(smbiosType),
                            speed,
                            cfgSpeed > 0 ? cfgSpeed : speed,
                            voltage,
                            obj["Manufacturer"]?.ToString()?.Trim() ?? "Unknown",
                            obj["PartNumber"]?.ToString()?.Trim() ?? ""));
                    }
                }
                catch { /* VM or restricted SMBIOS */ }

                var pagefiles = new List<PagefileInfo>();
                try
                {
                    using var pf = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
                    using var pfRes = pf.Get();
                    foreach (var obj in pfRes)
                    using (obj)
                    {
                        _ = int.TryParse(obj["AllocatedBaseSize"]?.ToString(), out var alloc);
                        _ = int.TryParse(obj["CurrentUsage"]?.ToString(), out var cur);
                        _ = int.TryParse(obj["PeakUsage"]?.ToString(), out var peak);
                        pagefiles.Add(new PagefileInfo(obj["Name"]?.ToString() ?? "pagefile.sys", alloc, cur, peak));
                    }
                }
                catch { /* ignore */ }

                var populated = dimms.Count;
                var topo = populated switch
                {
                    0 => "SMBIOS DIMM layout unavailable (VM or restricted)",
                    1 => $"1/{Math.Max(totalSlots, 1)} slot populated (Single-Channel)",
                    _ when dimms.Select(d => d.CapacityGb).Distinct().Count() > 1 => $"{populated}/{Math.Max(totalSlots, populated)} slots populated (Asymmetric capacity)",
                    _ => $"{populated}/{Math.Max(totalSlots, populated)} slots populated (Multi-Channel capable)"
                };

                var xmpHint = InferXmpExpo(dimms);

                return new MemoryRevisionInfo(
                    totalPhys, availPhys, commitLimit, commitCharge, commitPct,
                    populated, totalSlots, topo, xmpHint, dimms, pagefiles);
            }, ct).ConfigureAwait(false);

            return new ProbeResult<MemoryRevisionInfo>(ProbeStatus.Ok, data, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<MemoryRevisionInfo>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
    }

    private static string MapDdrType(int smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => smbiosType > 0 ? $"Type-{smbiosType}" : "Unknown"
    };

    private static XmpExpoStatus InferXmpExpo(IReadOnlyList<MemoryDimmInfo> dimms)
    {
        if (dimms.Count == 0) return XmpExpoStatus.Unknown;
        var primary = dimms[0];
        if (primary.ConfiguredClockMt <= 0) return XmpExpoStatus.Unknown;
        if (primary.MemoryType == "DDR4")
            return primary.ConfiguredClockMt > 2666 ? XmpExpoStatus.LikelyActive : XmpExpoStatus.LikelyJedecBase;
        if (primary.MemoryType == "DDR5")
            return primary.ConfiguredClockMt > 4800 ? XmpExpoStatus.LikelyActive : XmpExpoStatus.LikelyJedecBase;
        return XmpExpoStatus.Unknown;
    }
}
