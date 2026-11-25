using Microsoft.Win32;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class RegistryAdapter : IRegistryService
{
    public RegistrySnapshotEntry ReadState(string path, string name)
    {
        var (hive, subKey) = ParsePath(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        if (key is null)
            return new RegistrySnapshotEntry(path, name, false, null, null);

        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
            return new RegistrySnapshotEntry(path, name, false, null, null);

        var kind = key.GetValueKind(name);
        var serialized = kind switch
        {
            RegistryValueKind.Binary when raw is byte[] bytes => Convert.ToBase64String(bytes),
            RegistryValueKind.MultiString when raw is string[] lines => string.Join("\0", lines),
            _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
        };

        return new RegistrySnapshotEntry(path, name, true, kind.ToString(), serialized);
    }

    public bool KeyExists(string path)
    {
        var (hive, subKey) = ParsePath(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    public void WriteValue(string path, string name, object value, string valueKind)
    {
        Guardrails.AssertSafeRegistryTarget(path, name);
        var (hive, subKey) = ParsePath(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Failed to open/create registry key '{path}'.");

        var kind = Enum.Parse<RegistryValueKind>(valueKind, ignoreCase: true);
        var strVal = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        object typedValue = kind switch
        {
            RegistryValueKind.DWord => value is int i ? i : int.Parse(strVal, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => value is long l ? l : long.Parse(strVal, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => value is byte[] b ? b : Convert.FromBase64String(strVal),
            RegistryValueKind.MultiString => value is string[] arr ? arr : strVal.Split('\0'),
            _ => strVal
        };

        key.SetValue(name, typedValue, kind);
    }

    public void DeleteValue(string path, string name)
    {
        Guardrails.AssertSafeRegistryTarget(path, name);
        var (hive, subKey) = ParsePath(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static (RegistryHive Hive, string SubKey) ParsePath(string path)
    {
        var norm = path.Replace('/', '\\');
        if (norm.StartsWith(@"HKCU:\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, norm[6..]);
        if (norm.StartsWith(@"HKLM:\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, norm[6..]);
        throw new ArgumentException($"Unsupported registry path: '{path}'.");
    }
}
