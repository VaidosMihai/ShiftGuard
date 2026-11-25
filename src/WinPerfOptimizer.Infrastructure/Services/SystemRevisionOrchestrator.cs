using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class SystemRevisionOrchestrator(
    IHardwareProbe hardwareProbe,
    IPowerPlanService powerPlan,
    IProcessInspector processInspector,
    IDriverProbe driverProbe,
    IBatteryProbe batteryProbe,
    IMemoryProbe memoryProbe,
    ICpuTelemetryProbe cpuProbe,
    IStorageSmartProbe storageProbe,
    IGpuRevisionProbe gpuProbe,
    IRecommendationEngine recommendationEngine,
    IElevatedExecutor elevatedExecutor) : ISystemRevisionOrchestrator
{
    public async Task<SystemRevisionSnapshot> CaptureRevisionAsync(
        RevisionCaptureOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Probing hardware, drivers, battery, memory, CPU, storage, and GPU...");

        var hwTask = hardwareProbe.ProbeAsync(ct);
        var powerTask = powerPlan.GetActiveSchemeAsync(ct);
        var drvTask = driverProbe.ScanDriversAsync(ct);
        var batTask = batteryProbe.ProbeBatteryAsync(ct);
        var memTask = memoryProbe.ProbeMemoryAsync(ct);
        var cpuTask = cpuProbe.ProbeCpuAsync(ct);
        var stoTask = storageProbe.ProbeStorageAsync(ct);
        var gpuTask = gpuProbe.ProbeGpuAsync(ct);
        var procTask = Task.Run(() => (
            Startups: processInspector.GetStartupEntries(),
            Heavy: processInspector.GetHeavyProcesses(options.IncludeCommsInProcessGuard)
        ), ct);

        await Task.WhenAll(hwTask, powerTask, drvTask, batTask, memTask, cpuTask, stoTask, gpuTask, procTask).ConfigureAwait(false);

        var (startups, heavy) = await procTask.ConfigureAwait(false);
        var draft = new SystemRevisionSnapshot(
            Guid.NewGuid(),
            options.Label,
            options.Kind,
            DateTimeOffset.UtcNow,
            Environment.TickCount64,
            elevatedExecutor.IsCurrentProcessElevated(),
            await hwTask.ConfigureAwait(false),
            await powerTask.ConfigureAwait(false),
            await drvTask.ConfigureAwait(false),
            await batTask.ConfigureAwait(false),
            await memTask.ConfigureAwait(false),
            await cpuTask.ConfigureAwait(false),
            await stoTask.ConfigureAwait(false),
            await gpuTask.ConfigureAwait(false),
            startups,
            heavy,
            new SubsystemScores(0, 0, 0, 0, 0, 0),
            []);

        var scores = recommendationEngine.Score(draft);
        var recs = recommendationEngine.Evaluate(draft with { Scores = scores });
        return draft with { Scores = scores, Recommendations = recs };
    }
}
