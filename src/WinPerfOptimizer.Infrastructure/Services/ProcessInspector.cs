using System.Diagnostics;
using System.Management;
using System.Text.Json;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class ProcessInspector : IProcessInspector
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPerfOptimizer", "session-guard-state.json");

    private static readonly string[] HeavyTargets =
        ["Docker Desktop", "DriverBooster", "IObit", "Adobe", "CCXProcess", "utweb", "uTorrent", "Blitz"];

    private static readonly string[] CommsTargets = ["Discord", "OneDrive", "ms-teams"];

    public IReadOnlyList<StartupEntry> GetStartupEntries()
    {
        var list = new List<StartupEntry>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, Command, Location FROM Win32_StartupCommand");
        using var results = searcher.Get();
        foreach (var obj in results)
        using (obj)
        {
            var name = obj["Name"]?.ToString() ?? "";
            var cmd = obj["Command"]?.ToString() ?? "";
            var loc = obj["Location"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            var cat = StartupClassifier.Classify(name, cmd);
            list.Add(new StartupEntry(name, cmd, loc, cat, cat == StartupCategory.Protected));
        }
        return list;
    }

    public IReadOnlyList<HeavyProcessInfo> GetHeavyProcesses(bool includeComms)
    {
        var targets = includeComms ? HeavyTargets.Concat(CommsTargets).ToArray() : HeavyTargets;
        var found = new Dictionary<string, HeavyProcessInfo>(StringComparer.OrdinalIgnoreCase);

        var exeMap = new Dictionary<int, string>();
        try
        {
            using var wmi = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process");
            using var wmiResults = wmi.Get();
            foreach (var obj in wmiResults)
            using (obj)
            {
                if (int.TryParse(obj["ProcessId"]?.ToString(), out var pid))
                {
                    var exe = obj["ExecutablePath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(exe)) exeMap[pid] = exe;
                }
            }
        }
        catch { /* non-fatal fallback */ }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.WorkingSet64 < 40 * 1024 * 1024) continue;
                if (!targets.Any(t => p.ProcessName.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                if (!found.ContainsKey(p.ProcessName))
                {
                    exeMap.TryGetValue(p.Id, out var exe);
                    found[p.ProcessName] = new HeavyProcessInfo(p.ProcessName, p.Id, Math.Round(p.WorkingSet64 / (1024.0 * 1024), 1), exe);
                }
            }
            catch { /* exited process */ }
            finally { p.Dispose(); }
        }

        return found.Values.OrderByDescending(x => x.MemoryMb).ToList();
    }

    public async Task<IReadOnlyList<HeavyProcessInfo>> CloseGracefullyAsync(IReadOnlyList<HeavyProcessInfo> targets, CancellationToken ct = default)
    {
        var closed = new List<HeavyProcessInfo>();
        await Task.Run(() =>
        {
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                var sent = false;
                foreach (var p in Process.GetProcessesByName(t.ProcessName))
                {
                    try { if (p.CloseMainWindow()) sent = true; }
                    catch { /* exited process */ }
                    finally { p.Dispose(); }
                }
                if (sent) closed.Add(t);
            }
            if (closed.Count > 0)
            {
                var dir = Path.GetDirectoryName(StatePath)!;
                Guardrails.AssertSafeDirectoryPath(dir);
                Directory.CreateDirectory(dir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(closed));
            }
        }, ct).ConfigureAwait(false);
        return closed;
    }

    public async Task<IReadOnlyList<string>> ResumeSavedAppsAsync(CancellationToken ct = default)
    {
        var resumed = new List<string>();
        if (!File.Exists(StatePath)) return resumed;

        var dir = Path.GetDirectoryName(StatePath)!;
        Guardrails.AssertSafeDirectoryPath(dir);

        var items = JsonSerializer.Deserialize<List<HeavyProcessInfo>>(await File.ReadAllTextAsync(StatePath, ct).ConfigureAwait(false)) ?? [];
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ExecutablePath)
                || !item.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(item.ExecutablePath))
            {
                continue;
            }
            Process.Start(new ProcessStartInfo { FileName = item.ExecutablePath, UseShellExecute = false });
            resumed.Add(item.ProcessName);
        }
        return resumed;
    }
}
