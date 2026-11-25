using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static class DriverGuardrails
{
    private static readonly HashSet<string> HighRiskClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Firmware",
        "SecurityDevices",
        "System",
        "DiskDrive",
        "SCSIAdapter",
        "HDC"
    };

    private static readonly HashSet<string> AllowedWingetCompanionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Nvidia.App",
        "AMD.RyzenMaster",
        "Intel.DriverAndSupportAssistant",
        "Dell.CommandUpdate",
        "Lenovo.SystemUpdate"
    };

    public static bool IsHighRiskDeviceClass(string? deviceClass)
        => !string.IsNullOrWhiteSpace(deviceClass) && HighRiskClasses.Contains(deviceClass.Trim());

    public static bool IsAllowedWingetPackage(string? packageId)
        => !string.IsNullOrWhiteSpace(packageId) && AllowedWingetCompanionIds.Contains(packageId.Trim());

    public static bool IsTrustedSignature(DriverSignatureTrust trust)
        => trust is DriverSignatureTrust.MicrosoftInbox
            or DriverSignatureTrust.WhqlSigned
            or DriverSignatureTrust.ThirdPartyTrusted;

    public static DriverRiskTier ClassifyRiskTier(string? deviceClass)
    {
        if (IsHighRiskDeviceClass(deviceClass))
            return DriverRiskTier.CriticalBootOrFirmware;
        if (string.Equals(deviceClass, "Display", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceClass, "Net", StringComparison.OrdinalIgnoreCase))
            return DriverRiskTier.HighDisplayNetwork;
        if (string.Equals(deviceClass, "Media", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceClass, "USB", StringComparison.OrdinalIgnoreCase))
            return DriverRiskTier.MediumPeripheral;
        return DriverRiskTier.LowUserland;
    }

    public static bool CanAutoInstall(DriverUpdateCandidate candidate, bool systemRebootPending, out string? blockReason)
    {
        if (systemRebootPending)
        {
            blockReason = "A system reboot is already pending. Reboot before installing drivers.";
            return false;
        }
        if (candidate.RiskTier == DriverRiskTier.CriticalBootOrFirmware)
        {
            blockReason = "Boot, TPM, System, or Firmware updates are blocked from automated install; use official OEM utility.";
            return false;
        }
        if (candidate.Source == DriverUpdateSource.WingetOemCompanion && !IsAllowedWingetPackage(candidate.CandidateId))
        {
            blockReason = $"Winget package '{candidate.CandidateId}' is not on the official OEM allowlist.";
            return false;
        }
        if (!candidate.IsAutoApplyAllowed)
        {
            blockReason = candidate.BlockReason ?? "Driver candidate is marked manual-only.";
            return false;
        }
        blockReason = null;
        return true;
    }

    public static void AssertSafeDriverCandidate(DriverUpdateCandidate candidate, bool systemRebootPending)
    {
        if (!CanAutoInstall(candidate, systemRebootPending, out var reason))
            throw new UnauthorizedAccessException($"DRIVER GUARDRAIL: {reason}");
    }
}
