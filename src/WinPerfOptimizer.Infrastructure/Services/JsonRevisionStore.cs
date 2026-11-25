using System.Text.Json;
using System.Text.Json.Serialization;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class JsonRevisionStore : IRevisionStore
{
    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPerfOptimizer", "Revisions");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveRevisionAsync(SystemRevisionSnapshot snapshot, CancellationToken ct = default)
    {
        Guardrails.AssertSafeDirectoryPath(RootDir);
        Directory.CreateDirectory(RootDir);
        var path = Guardrails.AssertSafeDirectoryPath(Path.Combine(RootDir, $"{snapshot.RevisionId:N}.json"), RootDir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOpts), ct).ConfigureAwait(false);
    }

    public async Task<SystemRevisionSnapshot?> LoadLatestByKindAsync(RevisionKind kind, CancellationToken ct = default)
    {
        var all = await ListRevisionsAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(r => r.Kind == kind);
    }

    public async Task<SystemRevisionSnapshot?> LoadRevisionAsync(Guid revisionId, CancellationToken ct = default)
    {
        if (!Directory.Exists(RootDir)) return null;
        var path = Guardrails.AssertSafeDirectoryPath(Path.Combine(RootDir, $"{revisionId:N}.json"), RootDir);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SystemRevisionSnapshot>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), JsonOpts);
    }

    public async Task<IReadOnlyList<SystemRevisionSnapshot>> ListRevisionsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(RootDir)) return [];
        Guardrails.AssertSafeDirectoryPath(RootDir);

        var list = new List<SystemRevisionSnapshot>();
        foreach (var file in Directory.GetFiles(RootDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snap = JsonSerializer.Deserialize<SystemRevisionSnapshot>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), JsonOpts);
                if (snap is not null) list.Add(snap);
            }
            catch { /* skip corrupt entry */ }
        }
        return list.OrderByDescending(x => x.CapturedAtUtc).ToList();
    }
}
