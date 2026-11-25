using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public sealed class RecommendationEngine : IRecommendationEngine
{
    public SubsystemScores Score(SystemRevisionSnapshot snapshot)
    {
        var driverScore = ScoreDrivers(snapshot.Drivers);
        var batteryScore = ScoreBattery(snapshot.Battery);
        var memoryScore = ScoreMemory(snapshot.Memory, snapshot.StartupEntries);
        var cpuScore = ScoreCpu(snapshot.Cpu);
        var storageScore = ScoreStorage(snapshot.Storage);

        var overall = (int)Math.Round(
            driverScore * 0.25 +
            memoryScore * 0.20 +
            cpuScore * 0.20 +
            storageScore * 0.20 +
            batteryScore * 0.15);

        return new SubsystemScores(driverScore, batteryScore, memoryScore, cpuScore, storageScore, Math.Clamp(overall, 0, 100));
    }

    public IReadOnlyList<Recommendation> Evaluate(SystemRevisionSnapshot snapshot)
    {
        var recs = new List<Recommendation>();

        if (snapshot.Drivers.Status is ProbeStatus.Ok or ProbeStatus.Partial && snapshot.Drivers.Data is { } drv)
        {
            if (drv.ErroredCount > 0)
            {
                recs.Add(new Recommendation(
                    "DRV-PNP-ERROR",
                    RecommendationSeverity.Critical,
                    RecommendationCategory.Driver,
                    $"{drv.ErroredCount} device(s) have PnP driver errors",
                    string.Join("; ", drv.FlaggedDevices.Where(d => d.ConfigManagerErrorCode != 0).Take(3).Select(d => $"{d.FriendlyName} (Code {d.ConfigManagerErrorCode})")),
                    "Restores hardware functionality and prevents BSOD/DPC hangs.",
                    RecommendationActionKind.StageDriverUpdate));
            }
            if (drv.UnsignedCount > 0)
            {
                recs.Add(new Recommendation(
                    "DRV-UNSIGNED",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Driver,
                    $"{drv.UnsignedCount} unsigned or untrusted driver(s) detected",
                    string.Join("; ", drv.FlaggedDevices.Where(d => !DriverGuardrails.IsTrustedSignature(d.Trust)).Take(3).Select(d => d.FriendlyName)),
                    "Improves kernel stability and anti-cheat compatibility.",
                    RecommendationActionKind.StageDriverUpdate));
            }
            if (drv.AvailableUpdates.Count > 0)
            {
                recs.Add(new Recommendation(
                    "DRV-UPDATES",
                    RecommendationSeverity.Advisory,
                    RecommendationCategory.Driver,
                    $"{drv.AvailableUpdates.Count} verified driver update(s) available",
                    string.Join(", ", drv.AvailableUpdates.Take(3).Select(u => u.Title)),
                    "Fixes known bugs, improves GPU/chipset efficiency and security.",
                    RecommendationActionKind.StageDriverUpdate,
                    LinkedCandidateId: drv.AvailableUpdates[0].CandidateId));
            }
        }

        if (snapshot.Battery.Data is { Presence: BatteryPresence.Present } bat && bat.Batteries.Count > 0)
        {
            var primary = bat.Batteries[0];
            if (primary.WearPercent >= 40)
            {
                recs.Add(new Recommendation(
                    "BAT-WEAR-HIGH",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Battery,
                    $"Battery wear is {primary.WearPercent:0.#}% ({primary.FullChargeCapacityMwh}/{primary.DesignCapacityMwh} mWh)",
                    $"Rating: {primary.Rating}" + (primary.CycleCount.HasValue ? $", Cycles: {primary.CycleCount}" : ""),
                    "Consider battery replacement or enabling OEM charge limit (80%) when plugged in.",
                    RecommendationActionKind.ManualBiosOrHardware));
            }
        }

        if (snapshot.Memory.Data is { } mem)
        {
            if (mem.CommitPressurePercent >= 80)
            {
                recs.Add(new Recommendation(
                    "MEM-COMMIT-HIGH",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Memory,
                    $"High memory commit pressure ({mem.CommitPressurePercent:0.#}%)",
                    $"{mem.CommitChargeGb:0.0} / {mem.CommitLimitGb:0.0} GB committed",
                    "Closing heavy background apps via Session Guard frees RAM and reduces paging.",
                    RecommendationActionKind.ApplyTweak,
                    LinkedTweak: TweakId.DisableEdgeBackground));
            }
            if (mem.PopulatedSlots == 1 && mem.TotalSlots >= 2)
            {
                recs.Add(new Recommendation(
                    "MEM-SINGLE-CHANNEL",
                    RecommendationSeverity.Advisory,
                    RecommendationCategory.Memory,
                    "RAM appears to be running in Single-Channel (1 populated slot)",
                    mem.ChannelTopologyNote,
                    "Adding a matched second DIMM can boost CPU/iGPU gaming FPS by 15-30%.",
                    RecommendationActionKind.ManualBiosOrHardware));
            }
            if (mem.XmpExpoHint == XmpExpoStatus.LikelyJedecBase)
            {
                recs.Add(new Recommendation(
                    "MEM-JEDEC-BASE",
                    RecommendationSeverity.Advisory,
                    RecommendationCategory.Memory,
                    "Memory may be running at base JEDEC speed (XMP/EXPO check)",
                    string.Join(", ", mem.Dimms.Select(d => $"{d.ConfiguredClockMt}/{d.JedecOrRatedSpeedMt} MT/s")),
                    "Enabling XMP/DOCP/EXPO in BIOS can improve 1% low FPS.",
                    RecommendationActionKind.ManualBiosOrHardware));
            }
        }

        if (snapshot.Cpu.Data is { } cpu)
        {
            if (cpu.ActiveThrottleReason != CpuThrottleReason.None)
            {
                recs.Add(new Recommendation(
                    "CPU-THROTTLED",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Cpu,
                    $"CPU throttling detected ({cpu.ActiveThrottleReason})",
                    $"Effective limit {cpu.EffectiveLimitMhz} MHz vs max {cpu.MaxObservedBoostMhz} MHz",
                    "Switch to Balanced/High Performance on AC or clean cooling vents.",
                    RecommendationActionKind.ApplyTweak,
                    LinkedTweak: TweakId.PowerBalanced));
            }
            if (cpu.SampledDpcTimePercent > 5.0)
            {
                recs.Add(new Recommendation(
                    "CPU-DPC-HIGH",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Cpu,
                    $"Elevated DPC latency ({cpu.SampledDpcTimePercent:0.1}%)",
                    string.Join("; ", cpu.DpcLatencyHints),
                    "Update network/audio/GPU drivers to eliminate audio crackle and micro-stutter.",
                    RecommendationActionKind.StageDriverUpdate));
            }
        }

        if (snapshot.Storage.Status == ProbeStatus.RequiresElevation)
        {
            recs.Add(new Recommendation(
                "STO-DEEP-SCAN",
                RecommendationSeverity.Info,
                RecommendationCategory.Storage,
                "SSD/NVMe SMART counters require Administrator elevation",
                snapshot.Storage.DiagnosticNote ?? "Access denied reading MSFT_StorageReliabilityCounter",
                "Run Elevated Deep Scan to inspect SSD wear %, temperature, and read/write errors.",
                RecommendationActionKind.RunElevatedDeepScan));
        }
        else if (snapshot.Storage.Data is { } sto)
        {
            foreach (var vol in sto.Volumes.Where(v => v.FreePercent < 15.0))
            {
                recs.Add(new Recommendation(
                    $"STO-LOW-SPACE-{vol.DriveLetter}",
                    RecommendationSeverity.Warning,
                    RecommendationCategory.Storage,
                    $"Low free space on {vol.DriveLetter} ({vol.FreePercent:0.#}% free)",
                    $"{vol.FreeGb:0.0} GB free of {vol.TotalGb:0.0} GB",
                    "Keep >15% free on SSDs for TRIM & wear-leveling; enable Storage Sense.",
                    RecommendationActionKind.ApplyTweak,
                    LinkedTweak: TweakId.EnableStorageSense));
            }
            foreach (var disk in sto.Disks.Where(d => (d.WearPercentUsed ?? 0) >= 80 || (d.ReadErrorsUncorrected ?? 0) > 0))
            {
                recs.Add(new Recommendation(
                    "STO-SMART-WARN",
                    RecommendationSeverity.Critical,
                    RecommendationCategory.Storage,
                    $"Storage health alert on {disk.FriendlyName}",
                    $"Wear={disk.WearPercentUsed?.ToString() ?? "?"}%, UncorrectedReadErrors={disk.ReadErrorsUncorrected ?? 0}",
                    "Back up critical data immediately and inspect NVMe/SSD health.",
                    RecommendationActionKind.ManualBiosOrHardware));
            }
        }

        if (snapshot.Gpu.Data is { HagsSupported: true, HagsEnabled: false })
        {
            recs.Add(new Recommendation(
                "GPU-HAGS-OFF",
                RecommendationSeverity.Advisory,
                RecommendationCategory.Power,
                "Hardware-Accelerated GPU Scheduling (HAGS) is currently Off",
                $"GPU: {snapshot.Gpu.Data.Name}",
                "Applying Gaming Profile enables HAGS to reduce render queue latency.",
                RecommendationActionKind.ApplyTweak,
                LinkedTweak: TweakId.EnableHags));
        }

        return recs;
    }

    private static int ScoreDrivers(ProbeResult<DriverAuditReport> p)
    {
        if (p.Data is not { } d) return 75;
        var s = 100 - (d.ErroredCount * 15) - (d.UnsignedCount * 10) - Math.Min(15, d.AvailableUpdates.Count * 3);
        return Math.Clamp(s, 0, 100);
    }

    private static int ScoreBattery(ProbeResult<BatteryHealthReport> p)
    {
        if (p.Data is not { Presence: BatteryPresence.Present } b || b.Batteries.Count == 0)
            return 100;
        var wear = b.Batteries[0].WearPercent;
        return Math.Clamp((int)Math.Round(100 - wear), 0, 100);
    }

    private static int ScoreMemory(ProbeResult<MemoryRevisionInfo> p, IReadOnlyList<StartupEntry> startups)
    {
        if (p.Data is not { } m) return 80;
        var s = 100;
        if (m.CommitPressurePercent >= 85) s -= 25;
        else if (m.CommitPressurePercent >= 70) s -= 10;
        if (m.PopulatedSlots == 1 && m.TotalSlots >= 2) s -= 10;
        var bloat = startups.Count(x => x.Category == StartupCategory.BloatUpdater);
        s -= Math.Min(15, bloat * 4);
        return Math.Clamp(s, 0, 100);
    }

    private static int ScoreCpu(ProbeResult<CpuRevisionInfo> p)
    {
        if (p.Data is not { } c) return 80;
        var s = 100;
        if (c.ActiveThrottleReason != CpuThrottleReason.None) s -= 20;
        if (c.SampledDpcTimePercent > 5.0) s -= 15;
        if (c.PackageTempCelsius is >= 90) s -= 20;
        return Math.Clamp(s, 0, 100);
    }

    private static int ScoreStorage(ProbeResult<StorageRevisionInfo> p)
    {
        if (p.Data is not { } st) return 80;
        var s = 100;
        if (st.Volumes.Any(v => v.FreePercent < 10)) s -= 25;
        else if (st.Volumes.Any(v => v.FreePercent < 15)) s -= 10;
        if (st.Disks.Any(d => (d.ReadErrorsUncorrected ?? 0) > 0)) s -= 35;
        if (st.Disks.Any(d => (d.WearPercentUsed ?? 0) >= 85)) s -= 20;
        return Math.Clamp(s, 0, 100);
    }
}
