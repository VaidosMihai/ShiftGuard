using System.Net;
using System.Text;
using System.Text.Json;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static class RevisionDiffCalculator
{
    public static RevisionDiffReport Compute(
        SystemRevisionSnapshot before,
        SystemRevisionSnapshot after,
        PresetProfile? appliedProfile = null,
        IReadOnlyList<DiffRow>? configChanges = null)
    {
        var deltas = new List<MetricDelta>
        {
            new("Health", "Overall Score", $"{before.Scores.OverallScore}/100", $"{after.Scores.OverallScore}/100",
                FormatSignedDelta(after.Scores.OverallScore - before.Scores.OverallScore, "pts")),
            new("Drivers", "PnP Errors",
                before.Drivers.Data?.ErroredCount.ToString() ?? "N/A",
                after.Drivers.Data?.ErroredCount.ToString() ?? "N/A",
                before.Drivers.Data is not null && after.Drivers.Data is not null
                    ? FormatSignedDelta(after.Drivers.Data.ErroredCount - before.Drivers.Data.ErroredCount, "errors")
                    : "N/A"),
            new("Power", "Active Scheme", before.ActivePowerScheme, after.ActivePowerScheme,
                string.Equals(before.ActivePowerScheme, after.ActivePowerScheme, StringComparison.OrdinalIgnoreCase) ? "Unchanged" : "Changed"),
            new("GPU", "HAGS Enabled",
                before.Gpu.Data?.HagsEnabled.ToString() ?? "N/A",
                after.Gpu.Data?.HagsEnabled.ToString() ?? "N/A",
                before.Gpu.Data?.HagsEnabled == after.Gpu.Data?.HagsEnabled ? "Unchanged" : "Updated"),
            new("Memory", "Available RAM (GB)",
                before.Memory.Data is not null ? $"{before.Memory.Data.AvailablePhysicalGb:0.0}" : "N/A",
                after.Memory.Data is not null ? $"{after.Memory.Data.AvailablePhysicalGb:0.0}" : "N/A",
                before.Memory.Data is not null && after.Memory.Data is not null
                    ? FormatSignedDelta(Math.Round(after.Memory.Data.AvailablePhysicalGb - before.Memory.Data.AvailablePhysicalGb, 1), "GB")
                    : "N/A"),
            new("Startup", "Startup Items", before.StartupEntries.Count.ToString(), after.StartupEntries.Count.ToString(),
                FormatSignedDelta(after.StartupEntries.Count - before.StartupEntries.Count, "items"))
        };

        return new RevisionDiffReport(appliedProfile, before, after, configChanges ?? [], deltas);
    }

    private static string FormatSignedDelta(double delta, string unit)
        => delta == 0 ? $"0 {unit}" : (delta > 0 ? $"+{delta:0.#} {unit}" : $"{delta:0.#} {unit}");
}

public sealed class RevisionReportFormatter : IRevisionReportFormatter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson(RevisionDiffReport report)
    {
        var envelope = new { schemaVersion = 2, report };
        return JsonSerializer.Serialize(envelope, JsonOpts);
    }

    public string ToMarkdown(RevisionDiffReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ShiftGuard / WinPerfOptimizer Technical Revision Report");
        sb.AppendLine();
        sb.AppendLine($"- **Computer:** {report.After.Hardware.Manufacturer} {report.After.Hardware.Model}");
        sb.AppendLine($"- **Before Revision:** `{report.Before.RevisionId}` ({report.Before.CapturedAtUtc:u})");
        sb.AppendLine($"- **After Revision:** `{report.After.RevisionId}` ({report.After.CapturedAtUtc:u})");
        sb.AppendLine($"- **Health Score:** `{report.Before.Scores.OverallScore}/100` -> `{report.After.Scores.OverallScore}/100`");
        sb.AppendLine();
        sb.AppendLine("## Before / After Metrics");
        sb.AppendLine("| Category | Metric | Before | After | Delta |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
        foreach (var d in report.TelemetryDeltas)
            sb.AppendLine($"| {d.Category} | {d.Metric} | {d.Before} | {d.After} | {d.DeltaAssessment} |");

        if (report.ConfigChanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Configuration Changes");
            sb.AppendLine("| Setting | Before | After | Reason |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var c in report.ConfigChanges)
                sb.AppendLine($"| {c.Item} | {c.Before} | {c.After} | {c.Reason} |");
        }
        return sb.ToString();
    }

    public string ToHtml(RevisionDiffReport report)
    {
        var deltaRows = new StringBuilder();
        foreach (var d in report.TelemetryDeltas)
        {
            deltaRows.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(d.Category)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(d.Metric)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(d.Before)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(d.After)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(d.DeltaAssessment)).AppendLine("</td></tr>");
        }

        var cfgRows = new StringBuilder();
        foreach (var c in report.ConfigChanges)
        {
            cfgRows.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(c.Item)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(c.Before)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(c.After)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(c.Reason)).AppendLine("</td></tr>");
        }

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>ShiftGuard Technical Revision Report</title>
<style>
body { font-family: "Segoe UI", sans-serif; margin: 24px; background: #0F1115; color: #F3F4F6; }
h1, h2 { color: #38BDF8; }
.card { background: #171A21; border: 1px solid #2A303C; border-radius: 10px; padding: 16px; margin-bottom: 16px; }
table { border-collapse: collapse; width: 100%; background: #171A21; margin-bottom: 16px; }
th, td { border: 1px solid #2A303C; padding: 8px 12px; text-align: left; }
th { background: #1E3A5F; color: #F8FAFC; }
@media print { body { background: #FFFFFF; color: #111827; } .card, table { background: #FFFFFF; border-color: #D1D5DB; } th { background: #E5E7EB; color: #111827; } }
</style>
</head>
<body>
<h1>ShiftGuard Technical Revision (Before &amp; After)</h1>
<div class="card">
  <div><strong>Computer:</strong> {{WebUtility.HtmlEncode($"{report.After.Hardware.Manufacturer} {report.After.Hardware.Model}")}}</div>
  <div><strong>Health Score:</strong> {{report.Before.Scores.OverallScore}}/100 &rarr; {{report.After.Scores.OverallScore}}/100</div>
  <div><strong>Before:</strong> {{WebUtility.HtmlEncode(report.Before.RevisionId.ToString())}} ({{report.Before.CapturedAtUtc:u}})</div>
  <div><strong>After:</strong> {{WebUtility.HtmlEncode(report.After.RevisionId.ToString())}} ({{report.After.CapturedAtUtc:u}})</div>
</div>
<h2>Telemetry &amp; Hardware Delta</h2>
<table>
  <thead><tr><th>Category</th><th>Metric</th><th>Before</th><th>After</th><th>Delta</th></tr></thead>
  <tbody>{{deltaRows}}</tbody>
</table>
<h2>Configuration Changes</h2>
<table>
  <thead><tr><th>Setting</th><th>Before</th><th>After</th><th>Reason</th></tr></thead>
  <tbody>{{cfgRows}}</tbody>
</table>
</body>
</html>
""";
    }
}
