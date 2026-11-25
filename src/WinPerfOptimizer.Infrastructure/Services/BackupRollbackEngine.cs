using System.Text.Json;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class BackupRollbackEngine(IPowerPlanService powerPlan, IRegistryService registry) : IBackupEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinPerfOptimizer", "Backups");

    public async Task<RestoreSnapshot> CreateSnapshotAsync(PresetProfile profile, IReadOnlyList<RegistryChange> changes, CancellationToken ct = default)
    {
        var safeRoot = Guardrails.AssertSafeDirectoryPath(BackupRoot);
        Directory.CreateDirectory(safeRoot);

        var id = Guid.NewGuid();
        var activeOut = await powerPlan.GetActiveSchemeAsync(ct).ConfigureAwait(false);
        var activeGuid = PowerCfgRunner.ExtractGuid(activeOut);

        var powPath = Path.Combine(safeRoot, $"{id:N}.pow");
        var (expCode, expOut) = await powerPlan.RunPowerCfgAsync(["/export", powPath, activeGuid], ct).ConfigureAwait(false);
        if (expCode != 0) throw new InvalidOperationException($"powercfg /export failed ({expCode}): {expOut}");

        var regEntries = changes.Select(c => registry.ReadState(c.Path, c.Name)).ToList();
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var hiberOn = File.Exists(Path.Combine(sysDrive, "hiberfil.sys"));

        var snapshot = new RestoreSnapshot(id, DateTimeOffset.UtcNow, profile, activeGuid, powPath, hiberOn, regEntries);
        var jsonPath = Path.Combine(safeRoot, $"{id:N}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(snapshot, JsonOpts), ct).ConfigureAwait(false);
        return snapshot;
    }

    public Guid? GetLatestSnapshotId()
    {
        var safeRoot = Guardrails.AssertSafeDirectoryPath(BackupRoot);
        if (!Directory.Exists(safeRoot)) return null;

        var latest = new DirectoryInfo(safeRoot).GetFiles("*.json")
            .OrderByDescending(f => f.CreationTimeUtc)
            .FirstOrDefault();
        if (latest is null) return null;

        var stem = Path.GetFileNameWithoutExtension(latest.Name);
        return Guid.TryParse(stem, out var g) ? g : null;
    }

    public async Task<IReadOnlyList<string>> RestoreSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
    {
        var safeRoot = Guardrails.AssertSafeDirectoryPath(BackupRoot);
        var jsonPath = Guardrails.AssertSafeDirectoryPath(Path.Combine(safeRoot, $"{snapshotId:N}.json"), safeRoot);
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"Backup snapshot '{snapshotId}' not found.", jsonPath);

        var snapshot = JsonSerializer.Deserialize<RestoreSnapshot>(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Corrupt snapshot JSON.");

        if (!Guid.TryParseExact(snapshot.OriginalPowerGuid, "D", out _))
            throw new InvalidOperationException($"Invalid OriginalPowerGuid in snapshot: '{snapshot.OriginalPowerGuid}'.");

        var log = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.PowerExportPath))
        {
            var safePow = Guardrails.AssertSafeDirectoryPath(snapshot.PowerExportPath, safeRoot);
            if (File.Exists(safePow))
                await powerPlan.RunPowerCfgAsync(["/import", safePow, snapshot.OriginalPowerGuid], ct).ConfigureAwait(false);
        }

        var (setCode, setOut) = await powerPlan.RunPowerCfgAsync(["/setactive", snapshot.OriginalPowerGuid], ct).ConfigureAwait(false);
        if (setCode != 0) throw new InvalidOperationException($"Failed to restore power plan ({setCode}): {setOut}");
        log.Add($"PowerPlan -> {snapshot.OriginalPowerGuid}");

        if (snapshot.HibernationWasOn)
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            if (!File.Exists(Path.Combine(sysDrive, "hiberfil.sys")))
            {
                await powerPlan.RunPowerCfgAsync(["/h", "on"], ct).ConfigureAwait(false);
                log.Add("Hibernation -> On");
            }
        }

        foreach (var entry in snapshot.RegistryEntries)
        {
            Guardrails.AssertSafeRegistryTarget(entry.Path, entry.Name);
            if (entry.Existed && entry.ValueKind is not null && entry.SerializedValue is not null)
            {
                registry.WriteValue(entry.Path, entry.Name, entry.SerializedValue, entry.ValueKind);
                log.Add($"Restored {entry.Path}\\{entry.Name}");
            }
            else
            {
                registry.DeleteValue(entry.Path, entry.Name);
                log.Add($"Removed {entry.Path}\\{entry.Name}");
            }
        }
        return log;
    }
}
