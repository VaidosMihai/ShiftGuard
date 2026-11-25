using System.Diagnostics;
using System.Management;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed class StorageSmartProbe : IStorageSmartProbe
{
    public async Task<ProbeResult<StorageRevisionInfo>> ProbeStorageAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (disks, volumes, smartDenied) = await Task.Run(ReadStorage, ct).ConfigureAwait(false);
            var status = smartDenied ? ProbeStatus.RequiresElevation : ProbeStatus.Ok;
            var note = smartDenied ? "MSFT_StorageReliabilityCounter requires Administrator elevation for full SMART wear/temperature counters." : null;
            return new ProbeResult<StorageRevisionInfo>(status, new StorageRevisionInfo(disks, volumes), sw.Elapsed, note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<StorageRevisionInfo>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
    }

    private static (List<PhysicalDiskSmartInfo> Disks, List<StorageVolumeSpaceInfo> Volumes, bool SmartDenied) ReadStorage()
    {
        var disks = new List<PhysicalDiskSmartInfo>();
        var volumes = new List<StorageVolumeSpaceInfo>();
        var smartDenied = false;

        var bitLockerOnOs = IsSystemDriveBitLockerOn();
        var reliabilityMap = new Dictionary<string, (int? Temp, int? TempMax, int? Wear, long? Hours, long? ReadErr, long? WriteErr, double? Latency)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var rel = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsUncorrected, WriteErrorsTotal, FlushLatencyMax FROM MSFT_StorageReliabilityCounter");
            using var relRes = rel.Get();
            foreach (var obj in relRes)
            using (obj)
            {
                var id = obj["DeviceId"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                reliabilityMap[id] = (
                    TryInt(obj["Temperature"]),
                    TryInt(obj["TemperatureMax"]),
                    TryInt(obj["Wear"]),
                    TryLong(obj["PowerOnHours"]),
                    TryLong(obj["ReadErrorsUncorrected"]),
                    TryLong(obj["WriteErrorsTotal"]),
                    TryDouble(obj["FlushLatencyMax"]));
            }
        }
        catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.AccessDenied)
        {
            smartDenied = true;
        }
        catch
        {
            smartDenied = true;
        }

        try
        {
            using var pd = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, FirmwareVersion, HealthStatus FROM MSFT_PhysicalDisk");
            using var pdRes = pd.Get();
            foreach (var obj in pdRes)
            using (obj)
            {
                var id = obj["DeviceId"]?.ToString() ?? "";
                var name = obj["FriendlyName"]?.ToString()?.Trim() ?? "Disk";
                var mt = TryInt(obj["MediaType"]);
                var bt = TryInt(obj["BusType"]);
                var sizeGb = ulong.TryParse(obj["Size"]?.ToString(), out var sz) ? Math.Round(sz / (1024.0 * 1024 * 1024), 1) : 0;
                var mediaLabel = (mt, bt) switch
                {
                    (4, 17) => "NVMe SSD",
                    (4, _) => "SSD",
                    (3, _) => "HDD",
                    _ => "Disk"
                };
                var busLabel = bt switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => $"Bus-{bt}" };
                reliabilityMap.TryGetValue(id, out var smart);

                disks.Add(new PhysicalDiskSmartInfo(
                    id, name, mediaLabel, busLabel, sizeGb,
                    obj["FirmwareVersion"]?.ToString()?.Trim() ?? "",
                    MapHealth(TryInt(obj["HealthStatus"])),
                    smart.Temp, smart.TempMax, smart.Wear, smart.Hours,
                    smart.ReadErr, null, smart.WriteErr, smart.Latency, bitLockerOnOs));
            }
        }
        catch { /* fallback below if storage namespace unavailable */ }

        foreach (var drv in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var total = Math.Round(drv.TotalSize / (1024.0 * 1024 * 1024), 1);
            var free = Math.Round(drv.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
            var pct = drv.TotalSize > 0 ? Math.Round(100.0 * drv.AvailableFreeSpace / drv.TotalSize, 1) : 0;
            volumes.Add(new StorageVolumeSpaceInfo(drv.Name.TrimEnd('\\'), drv.DriveFormat, total, free, pct));
        }

        return (disks, volumes, smartDenied);
    }

    private static bool IsSystemDriveBitLockerOn()
    {
        try
        {
            var sys = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            using var bl = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{sys}'");
            using var res = bl.Get();
            foreach (var obj in res)
            using (obj)
            {
                return TryInt(obj["ProtectionStatus"]) == 1;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static string MapHealth(int? code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };

    private static int? TryInt(object? v) => int.TryParse(v?.ToString(), out var x) ? x : null;
    private static long? TryLong(object? v) => long.TryParse(v?.ToString(), out var x) ? x : null;
    private static double? TryDouble(object? v) => double.TryParse(v?.ToString(), out var x) ? x : null;
}
