using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Probes;

public sealed partial class CpuRevisionProbe(IOptionalThermalSensorProvider thermalProvider) : ICpuTelemetryProbe
{
    private const int ProcessorInformationLevel = 11;
    private const uint PdhFmtDouble = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [LibraryImport("powrprof.dll")]
    private static partial int CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, int inputBufferLength,
        IntPtr outputBuffer, int outputBufferLength);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr hQuery);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr hQuery);

    public async Task<ProbeResult<CpuRevisionInfo>> ProbeCpuAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (name, vendor, cores, logical, baseMhz, virt) = await Task.Run(ReadWmiCpu, ct).ConfigureAwait(false);
            var (curMhz, maxMhz, limitMhz) = ReadPowerInfo(logical);
            var (perfPct, dpcPct, intPct) = await SamplePdhAsync(ct).ConfigureAwait(false);
            var temp = await thermalProvider.TryReadPackageTempCelsiusAsync(ct).ConfigureAwait(false);

            var throttle = CpuThrottleReason.None;
            if (maxMhz > 0 && limitMhz > 0 && limitMhz < (int)(maxMhz * 0.90))
                throttle = CpuThrottleReason.WindowsPowerPlanCap;
            else if (temp is >= 92)
                throttle = CpuThrottleReason.Thermal;

            var hints = new List<string>();
            if (dpcPct > 5.0)
                hints.Add("High DPC time (>5%) — inspect network, audio, or GPU drivers.");
            if (throttle != CpuThrottleReason.None)
                hints.Add($"Effective CPU limit {limitMhz} MHz is below max {maxMhz} MHz.");

            var info = new CpuRevisionInfo(
                name, vendor, cores, logical, cores, 0,
                baseMhz, curMhz > 0 ? curMhz : baseMhz, maxMhz > 0 ? maxMhz : baseMhz, limitMhz > 0 ? limitMhz : baseMhz,
                temp, throttle, virt, perfPct, dpcPct, intPct, hints);

            return new ProbeResult<CpuRevisionInfo>(ProbeStatus.Ok, info, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult<CpuRevisionInfo>(ProbeStatus.Failed, null, sw.Elapsed, ex.Message);
        }
    }

    private static (string Name, string Vendor, int Cores, int Logical, int BaseMhz, bool Virt) ReadWmiCpu()
    {
        var name = "Unknown";
        var vendor = "Unknown";
        var cores = Environment.ProcessorCount;
        var logical = Environment.ProcessorCount;
        var baseMhz = 0;
        var virt = false;

        using var cpu = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, VirtualizationFirmwareEnabled FROM Win32_Processor");
        using var res = cpu.Get();
        foreach (var obj in res)
        using (obj)
        {
            name = obj["Name"]?.ToString()?.Trim() ?? name;
            var mfg = obj["Manufacturer"]?.ToString() ?? "";
            if (mfg.Contains("AMD", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
            else if (mfg.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
            _ = int.TryParse(obj["NumberOfCores"]?.ToString(), out cores);
            _ = int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out logical);
            _ = int.TryParse(obj["MaxClockSpeed"]?.ToString(), out baseMhz);
            virt = obj["VirtualizationFirmwareEnabled"] as bool? == true;
            break;
        }
        return (name, vendor, cores, logical, baseMhz, virt);
    }

    private static (int CurMhz, int MaxMhz, int LimitMhz) ReadPowerInfo(int logicalCount)
    {
        var count = Math.Max(1, logicalCount);
        var size = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        var buf = Marshal.AllocHGlobal(size * count);
        try
        {
            var status = CallNtPowerInformation(ProcessorInformationLevel, IntPtr.Zero, 0, buf, size * count);
            if (status != 0) return (0, 0, 0);

            var curSum = 0;
            var maxMax = 0;
            var limitMax = 0;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(IntPtr.Add(buf, i * size));
                curSum += (int)item.CurrentMhz;
                if (item.MaxMhz > maxMax) maxMax = (int)item.MaxMhz;
                if (item.MhzLimit > limitMax) limitMax = (int)item.MhzLimit;
            }
            return (curSum / count, maxMax, limitMax);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static async Task<(double PerfPct, double DpcPct, double IntPct)> SamplePdhAsync(CancellationToken ct)
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
            return (100, 0, 0);

        try
        {
            _ = PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out var cPerf);
            _ = PdhAddEnglishCounterW(query, @"\Processor(_Total)\% DPC Time", IntPtr.Zero, out var cDpc);
            _ = PdhAddEnglishCounterW(query, @"\Processor(_Total)\% Interrupt Time", IntPtr.Zero, out var cInt);

            _ = PdhCollectQueryData(query);
            await Task.Delay(450, ct).ConfigureAwait(false);
            if (PdhCollectQueryData(query) != 0)
                return (100, 0, 0);

            var perf = PdhGetFormattedCounterValue(cPerf, PdhFmtDouble, out _, out var vPerf) == 0 ? Math.Round(vPerf.doubleValue, 1) : 100.0;
            var dpc = PdhGetFormattedCounterValue(cDpc, PdhFmtDouble, out _, out var vDpc) == 0 ? Math.Round(vDpc.doubleValue, 2) : 0.0;
            var intr = PdhGetFormattedCounterValue(cInt, PdhFmtDouble, out _, out var vInt) == 0 ? Math.Round(vInt.doubleValue, 2) : 0.0;
            return (perf, dpc, intr);
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }
}
