using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
    IBackupEngine backupEngine,
    ISystemRestoreService? restoreService = null,
    IDriverDeploymentService? driverDeployment = null) : IElevatedExecutor
{
    private static readonly JsonSerializerOptions StrictJsonOpts = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static string GetIpcRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPerfOptimizer", "Ipc");

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

        var ipcDir = GetIpcRoot();
        Guardrails.AssertSafeDirectoryPath(ipcDir);
        Directory.CreateDirectory(ipcDir);
        var reqPath = Path.Combine(ipcDir, $"{request.RequestId:N}.json");
        var resPath = $"{reqPath}.result.json";

        var json = JsonSerializer.Serialize(request, StrictJsonOpts);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await File.WriteAllTextAsync(reqPath, json, ct).ConfigureAwait(false);

        try
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--elevated-worker --request \"{reqPath}\" --sha256 {sha}",
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
        finally
        {
            try { if (File.Exists(reqPath)) File.Delete(reqPath); } catch { }
            try { if (File.Exists(resPath)) File.Delete(resPath); } catch { }
        }
    }

    public async Task<int> RunHeadlessCliAsync(string requestPath, string? expectedSha256 = null)
    {
        var ipcRoot = GetIpcRoot();
        var safeReq = Guardrails.AssertSafeDirectoryPath(requestPath, ipcRoot);
        if (!safeReq.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new FileInfo(safeReq).Length > 262144)
            throw new InvalidOperationException("Invalid elevated request file.");

        var rawJson = await File.ReadAllTextAsync(safeReq).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson)));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualSha.ToUpperInvariant()),
                    Encoding.ASCII.GetBytes(expectedSha256.Trim().ToUpperInvariant())))
            {
                throw new UnauthorizedAccessException("IPC integrity check failed (SHA-256 mismatch).");
            }
        }

        var req = JsonSerializer.Deserialize<ElevatedRequest>(rawJson, StrictJsonOpts)
            ?? throw new InvalidOperationException("Null elevated request.");
        Guardrails.ValidateElevatedRequest(req);

        var safeRes = Guardrails.AssertSafeDirectoryPath($"{safeReq}.result.json", ipcRoot);
        var result = await ExecuteInProcessAsync(req, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(safeRes, JsonSerializer.Serialize(result, StrictJsonOpts)).ConfigureAwait(false);
        return result.Success ? 0 : 1;
    }

    private async Task<ElevatedResult> ExecuteInProcessAsync(ElevatedRequest request, CancellationToken ct)
    {
        if (request.Mode == WorkerMode.CreateRestorePoint)
        {
            if (restoreService is null)
                return new ElevatedResult(request.RequestId, false, null, [], "UNSUPPORTED", "Restore service not registered.");
            var seq = await restoreService.CreateRestorePointAsync("ShiftGuard Manual Checkpoint", ct).ConfigureAwait(false);
            return new ElevatedResult(request.RequestId, seq > 0, null, [], seq > 0 ? "OK" : "RESTORE_FAILED",
                seq > 0 ? $"System Restore Point created (seq {seq})." : "System Restore is disabled or throttled by Windows.");
        }

        if (request.Mode == WorkerMode.InstallDrivers && request.DriverUpdate is not null)
        {
            if (driverDeployment is null)
                return new ElevatedResult(request.RequestId, false, null, [], "UNSUPPORTED", "Driver deployment service not registered.");
            var outcomes = new List<DriverInstallOutcome>();
            var snapId = Guid.NewGuid();
            foreach (var cand in request.DriverUpdate.Candidates)
            {
                DriverGuardrails.AssertSafeDriverCandidate(cand, request.DriverUpdate.SystemRebootPending);
                outcomes.Add(await driverDeployment.StageAndInstallAsync(cand, snapId, ct).ConfigureAwait(false));
            }
            var ok = outcomes.All(o => o.Success);
            return new ElevatedResult(
                request.RequestId, ok, snapId, [],
                ok ? "OK" : "DRIVER_PARTIAL",
                $"Processed {outcomes.Count} driver update(s): {outcomes.Count(o => o.Success)} succeeded.",
                outcomes);
        }

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

            if (request.Apply.CreateRestorePoint && restoreService is not null)
                _ = await restoreService.CreateRestorePointAsync($"ShiftGuard {plan.Profile} Pre-Apply", ct).ConfigureAwait(false);

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
