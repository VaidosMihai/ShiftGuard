using System.Diagnostics;
using System.Text.RegularExpressions;
using WinPerfOptimizer.Core.Interfaces;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed partial class PowerCfgRunner : IPowerPlanService
{
    private static readonly Regex GuidRegex = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    public async Task<(int ExitCode, string Output)> RunPowerCfgAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powercfg.exe.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = (await stdoutTask.ConfigureAwait(false) + "\n" + await stderrTask.ConfigureAwait(false)).Trim();
        return (proc.ExitCode, output);
    }

    public async Task<string> GetActiveSchemeAsync(CancellationToken ct = default)
    {
        var (code, output) = await RunPowerCfgAsync(["/getactivescheme"], ct).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException($"powercfg /getactivescheme failed ({code}): {output}");
        return output;
    }

    public static string ExtractGuid(string powerCfgOutput)
    {
        var m = GuidRegex.Match(powerCfgOutput);
        if (!m.Success) throw new InvalidOperationException($"Could not parse GUID from '{powerCfgOutput}'.");
        return m.Groups[1].Value;
    }
}
