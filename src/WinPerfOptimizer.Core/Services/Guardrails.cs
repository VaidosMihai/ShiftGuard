using System.Text.RegularExpressions;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static partial class Guardrails
{
    private static readonly HashSet<string> ForbiddenServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService",
        "mpssvc", "BFE", "wuauserv", "UsoSvc", "bits", "cryptsvc",
        "RpcSs", "DcomLaunch", "LSM", "EventLog", "Schedule",
        "AudioSrv", "AudioEndpointBuilder", "WlanSvc", "Dhcp", "Dnscache", "PlugPlay"
    };

    private static readonly string[] ForbiddenPatterns =
    [
        @"\\Windows Defender\\",
        @"\\Policies\\Microsoft\\Windows Defender",
        @"\\SharedAccess\\Parameters\\FirewallPolicy",
        @"\\(CurrentControlSet|ControlSet\d{3})\\Control\\Session Manager\\Memory Management\\PagingFiles",
        @"\\(CurrentControlSet|ControlSet\d{3})\\Control\\Session Manager\\Memory Management\\DisablePagingExecutive",
        @"\\Policies\\System\\EnableLUA",
        @"\\CurrentVersion\\Run(Once)?(\\|$)",
        @"\\Image File Execution Options\\"
    ];

    private static readonly string[] ProtectedStartupKeywords =
    [
        "realtek", "nahimic", "dolby", "waves", "sound", "audio",
        "synaptics", "elan", "touchpad", "alps", "precision",
        "nvidia", "amd", "radeon", "igfx", "intel graphics",
        "defender", "securityhealth", "windows security",
        "vanguard", "easyanticheat", "battleye", "faceit",
        "asus", "armoury", "g-helper", "myasus", "lenovo", "dell", "hp "
    ];

    public static void AssertSafeRegistryTarget(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Registry path and name must be non-empty.");

        var norm = Regex.Replace(path.Replace('/', '\\'), @"\\+", @"\").TrimEnd('\\');
        if (!Regex.IsMatch(norm, @"^HK(CU|LM):\\", RegexOptions.IgnoreCase) && !Regex.IsMatch(norm, @"^HK(CU|LM):$", RegexOptions.IgnoreCase))
            throw new UnauthorizedAccessException($"ANTI-BRICK GUARDRAIL: Non-canonical registry path '{path}'.");

        var full = $"{norm}\\{name.Trim('\\')}";
        foreach (var pat in ForbiddenPatterns)
        {
            if (Regex.IsMatch(full, pat, RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException($"ANTI-BRICK GUARDRAIL: Protected registry target '{full}'.");
        }

        foreach (var svc in ForbiddenServices)
        {
            if (Regex.IsMatch(norm, $@"\\(CurrentControlSet|ControlSet\d{{3}})\\Services\\{Regex.Escape(svc)}(\\|$)", RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException($"ANTI-BRICK GUARDRAIL: Protected service key '{full}'.");
        }
    }

    public static void AssertSafeServiceTarget(string serviceName)
    {
        if (ForbiddenServices.Contains(serviceName))
            throw new UnauthorizedAccessException($"ANTI-BRICK GUARDRAIL: Protected core service '{serviceName}'.");
    }

    public static string AssertSafeDirectoryPath(string path, string? allowedRoot = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrEmpty(allowedRoot))
        {
            var fullRoot = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullTarget = fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"SECURITY GUARDRAIL: Path '{fullPath}' escapes '{fullRoot}'.");
        }

        var cursor = fullPath;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (Directory.Exists(cursor) || File.Exists(cursor))
            {
                var attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException($"SECURITY GUARDRAIL: Reparse point rejected at '{cursor}'.");
            }
            var parent = Path.GetDirectoryName(cursor);
            if (string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase)) break;
            cursor = parent;
        }

        return fullPath;
    }

    public static bool IsProtectedStartupItem(string name, string command = "")
    {
        var combined = $"{name} {command}".ToLowerInvariant();
        return ProtectedStartupKeywords.Any(kw => combined.Contains(kw, StringComparison.Ordinal));
    }

    public static void ValidateElevatedRequest(ElevatedRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported IPC schemaVersion: {request.SchemaVersion}.");

        var payloadCount = (request.Apply is not null ? 1 : 0)
                         + (request.Rollback is not null ? 1 : 0)
                         + (request.Restore is not null ? 1 : 0)
                         + (request.DriverUpdate is not null ? 1 : 0);
        if (payloadCount != 1)
            throw new InvalidOperationException("ElevatedRequest must contain exactly one mode payload.");

        var valid = request.Mode switch
        {
            WorkerMode.ApplyProfile => request.Apply is not null,
            WorkerMode.Rollback => request.Rollback is not null && request.Rollback.SnapshotId != Guid.Empty,
            WorkerMode.CreateRestorePoint => request.Restore is not null,
            WorkerMode.InstallDrivers => request.DriverUpdate is { Candidates.Count: > 0 },
            _ => false
        };

        if (!valid)
            throw new InvalidOperationException($"Mode '{request.Mode}' does not match provided payload.");
    }
}
