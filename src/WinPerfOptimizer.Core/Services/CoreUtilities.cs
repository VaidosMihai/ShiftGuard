using System.Net;
using System.Text;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static class DirectXTokenMerger
{
    public static string Merge(string? existingValue, IReadOnlyDictionary<string, string> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        if (!string.IsNullOrWhiteSpace(existingValue))
        {
            foreach (var part in existingValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var k = part[..idx].Trim();
                var v = part[(idx + 1)..].Trim();
                if (!map.ContainsKey(k)) order.Add(k);
                map[k] = v;
            }
        }

        foreach (var kv in tokens)
        {
            if (!map.ContainsKey(kv.Key)) order.Add(kv.Key);
            map[kv.Key] = kv.Value;
        }

        var sb = new StringBuilder();
        foreach (var k in order)
            sb.Append(k).Append('=').Append(map[k]).Append(';');
        return sb.ToString();
    }
}

public static class StartupClassifier
{
    private static readonly string[] LauncherKeywords = ["steam", "epicgames", "riotclient", "battle.net", "ubisoft", "eadesktop", "gog"];
    private static readonly string[] BloatKeywords = ["utorrent", "utweb", "bittorrent", "blitz", "medal", "edgeautolaunch", "googleupdater", "adobe", "ccleaner", "driverbooster", "iobit", "onenote"];

    public static StartupCategory Classify(string name, string command)
    {
        if (Guardrails.IsProtectedStartupItem(name, command))
            return StartupCategory.Protected;

        var combined = $"{name} {command}".ToLowerInvariant();
        if (LauncherKeywords.Any(k => combined.Contains(k, StringComparison.Ordinal)))
            return StartupCategory.GamingLauncher;
        if (BloatKeywords.Any(k => combined.Contains(k, StringComparison.Ordinal) || name.Equals("ut", StringComparison.OrdinalIgnoreCase)))
            return StartupCategory.BloatUpdater;
        return StartupCategory.Other;
    }
}

public static class StartupApprovedEncoder
{
    public static byte[] Encode(byte[]? originalBytes, bool disable)
    {
        var bytes = new byte[12];
        if (originalBytes is { Length: >= 12 })
            Array.Copy(originalBytes, bytes, 12);
        bytes[0] = disable ? (byte)0x03 : (byte)0x02;
        return bytes;
    }
}

public static class DiffReportFormatter
{
    public static string ToMarkdown(PresetProfile profile, string computer, string powerBefore, string powerAfter, IReadOnlyList<DiffRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# WinPerfOptimizer Report ({profile})");
        sb.AppendLine();
        sb.AppendLine($"- **Computer:** {computer}");
        sb.AppendLine($"- **Power Plan:** `{powerBefore}` -> `{powerAfter}`");
        sb.AppendLine();
        sb.AppendLine("| Setting | Before | After | Reason |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Item} | {r.Before} | {r.After} | {r.Reason} |");
        return sb.ToString();
    }

    public static string ToHtml(PresetProfile profile, string computer, string powerBefore, string powerAfter, IReadOnlyList<DiffRow> rows)
    {
        var bodyRows = new StringBuilder();
        foreach (var r in rows)
        {
            bodyRows.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(r.Item)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(r.Before)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(r.After)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(r.Reason)).AppendLine("</td></tr>");
        }

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>WinPerfOptimizer Report</title>
<style>
body { font-family: "Segoe UI", sans-serif; margin: 24px; background: #0F141B; color: #E2E8F0; }
h1 { color: #38BDF8; }
.meta { padding: 12px 16px; background: #161E29; border-radius: 8px; margin-bottom: 16px; border: 1px solid #263244; }
table { border-collapse: collapse; width: 100%; background: #161E29; }
th, td { border: 1px solid #263244; padding: 8px 12px; text-align: left; }
th { background: #0284C7; color: #FFFFFF; }
</style>
</head>
<body>
<h1>WinPerfOptimizer ({{WebUtility.HtmlEncode(profile.ToString())}})</h1>
<div class="meta">
  <div><strong>Computer:</strong> {{WebUtility.HtmlEncode(computer)}}</div>
  <div><strong>Power Plan:</strong> {{WebUtility.HtmlEncode(powerBefore)}} &rarr; {{WebUtility.HtmlEncode(powerAfter)}}</div>
</div>
<table>
  <thead><tr><th>Setting</th><th>Before</th><th>After</th><th>Reason</th></tr></thead>
  <tbody>
{{bodyRows}}  </tbody>
</table>
</body>
</html>
""";
    }
}
