using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class ElevatedWorkerHost(
    IHardwareProbe hardwareProbe,
    IPowerPlanService powerPlan,
    IRegistryService registry,
    IBackupEngine backupEngine) : IElevatedExecutor
{
    private static readonly JsonSerializerOptions StrictJsonOpts = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public bool IsCurrentProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<ElevatedResult> ExecuteAsync(ElevatedRequest request, CancellationToken ct = default)
    {
        Guardrails.ValidateElevatedRequest(request);
        if (IsCurrentProcessElevated())
            return await ExecuteInProcessAsync(request, ct).ConfigureAwait(false);

        var tempDir = Path.Combine(Path.GetTempPath(), "WinPerfOptimizer");
        Directory.CreateDirectory(tempDir);
        var reqPath = Path.Combine(tempDir, $"{request.RequestId:N}.json");
        var resPath = $"{reqPath}.result.json";

        await File.WriteAllTextAsync(reqPath, JsonSerializer.Serialize(request, StrictJsonOpts), ct).ConfigureAwait(false);

        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--elevated-worker --request \"{reqPath}\"",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("UAC elevation was canceled or failed.");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (!File.Exists(resPath))
            return new ElevatedResult(request.RequestId, false, null, [], "NO_RESULT", $"Elevated worker exited with code {proc.ExitCode}.");

        var result = JsonSerializer.Deserialize<ElevatedResult>(await File.ReadAllTextAsync(resPath, ct).ConfigureAwait(false), StrictJsonOpts);
        return result ?? new ElevatedResult(request.RequestId, false, null, [], "INVALID_RESULT", "Invalid worker response.");
    }

    public async Task<int> RunHeadlessCliAsync(string requestPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "WinPerfOptimizer");
        var safeReq = Guardrails.AssertSafeDirectoryPath(requestPath, tempRoot);
        if (!safeReq.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new FileInfo(safeReq).Length > 65536)
            throw new InvalidOperationException("Invalid elevated request file.");

        var req = JsonSerializer.Deserialize<ElevatedRequest>(await File.ReadAllTextAsync(safeReq).ConfigureAwait(false), StrictJsonOpts)
            ?? throw new InvalidOperationException("Null elevated request.");
        Guardrails.ValidateElevatedRequest(req);

        var safeRes = Guardrails.AssertSafeDirectoryPath($"{safeReq}.result.json", tempRoot);
        var result = await ExecuteInProcessAsync(req, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(safeRes, JsonSerializer.Serialize(result, StrictJsonOpts)).ConfigureAwait(false);
        return result.Success ? 0 : 1;
    }

    private async Task<ElevatedResult> ExecuteInProcessAsync(ElevatedRequest request, CancellationToken ct)
    {
        if (request.Mode == WorkerMode.Rollback && request.Rollback is not null)
        {
            var restored = await backupEngine.RestoreSnapshotAsync(request.Rollback.SnapshotId, ct).ConfigureAwait(false);
            return new ElevatedResult(request.RequestId, true, request.Rollback.SnapshotId, [], "OK", $"Rolled back {restored.Count} item(s).");
        }

        if (request.Mode == WorkerMode.ApplyProfile && request.Apply is not null)
        {
            var hw = await hardwareProbe.ProbeAsync(ct).ConfigureAwait(false);
            var existingDx = registry.ReadState(@"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings").SerializedValue;
            var edgeInstalled = registry.KeyExists(@"HKLM:\SOFTWARE\Microsoft\EdgeUpdate");
            var chromeInstalled = registry.KeyExists(@"HKLM:\SOFTWARE\Google\Chrome");

            var plan = request.Apply.Preset == PresetProfile.Daily
                ? OptimizationPlanBuilder.BuildDaily(hw, edgeInstalled, chromeInstalled, request.Apply.HibernationAction)
                : OptimizationPlanBuilder.BuildGaming(
                    hw, existingDx,
                    request.Apply.TweakIds.Contains(TweakId.DisableMouseAcceleration),
                    request.Apply.TweakIds.Contains(TweakId.DisableNetworkThrottling),
                    request.Apply.ForceOnBattery);

            var snapshot = await backupEngine.CreateSnapshotAsync(request.Apply.Preset, plan.RegistryChanges, ct).ConfigureAwait(false);
            try
            {
                foreach (var cmd in plan.PowerCommands)
                {
                    var (code, output) = await powerPlan.RunPowerCfgAsync(cmd.Arguments, ct).ConfigureAwait(false);
                    if (code != 0 && cmd.Arguments.Count > 0 && cmd.Arguments[0] == "/setactive")
                        throw new InvalidOperationException($"powercfg /setactive failed ({code}): {output}");
                }

                foreach (var rc in plan.RegistryChanges)
                    registry.WriteValue(rc.Path, rc.Name, rc.Value, rc.ValueKind);

                if (plan.HibernationAction == HibernationAction.Off)
                {
                    var (hCode, hOut) = await powerPlan.RunPowerCfgAsync(["/h", "off"], ct).ConfigureAwait(false);
                    if (hCode != 0) throw new InvalidOperationException($"powercfg /h off failed ({hCode}): {hOut}");
                }
                else if (plan.HibernationAction == HibernationAction.Reduced)
                {
                    var (hCode, hOut) = await powerPlan.RunPowerCfgAsync(["/h", "/type", "reduced"], ct).ConfigureAwait(false);
                    if (hCode != 0) throw new InvalidOperationException($"powercfg /h /type reduced failed ({hCode}): {hOut}");
                }

                return new ElevatedResult(request.RequestId, true, snapshot.SnapshotId, request.Apply.TweakIds, "OK", $"{plan.Profile} profile applied (Snapshot {snapshot.SnapshotId:N}).");
            }
            catch (Exception ex)
            {
                await backupEngine.RestoreSnapshotAsync(snapshot.SnapshotId, CancellationToken.None).ConfigureAwait(false);
                return new ElevatedResult(request.RequestId, false, snapshot.SnapshotId, [], "APPLY_FAILED", $"Apply failed & auto-rolled back: {ex.Message}");
            }
        }

        return new ElevatedResult(request.RequestId, false, null, [], "UNSUPPORTED", "Unsupported worker mode.");
    }
}
