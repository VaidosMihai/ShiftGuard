using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static class OptimizationPlanBuilder
{
    public const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformancePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcMinGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    public static OptimizationPlan BuildDaily(
        HardwareProfile hw,
        bool edgeInstalled,
        bool chromeInstalled,
        HibernationAction hibernation = HibernationAction.Keep)
    {
        var changes = new List<RegistryChange>
        {
            new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, "DWord", "Disable background GameDVR capture overhead"),
            new(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled", 0, "DWord", "Disable background GameDVR recording"),
            new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, "DWord", "Disable Windows Tips prompts"),
            new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, "DWord", "Disable tailored diagnostic ads"),
            new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, "DWord", "Enable Storage Sense temp cleanup"),
            new(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, "DWord", "HTTP-only Delivery Optimization (zero S0 P2P wakeups)")
        };

        if (edgeInstalled)
        {
            changes.Add(new(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, "DWord", "Stop Edge pre-loading at boot"));
            changes.Add(new(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, "DWord", "Stop Edge background mode when closed"));
        }

        if (chromeInstalled)
            changes.Add(new(@"HKLM:\SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", 0, "DWord", "Stop Chrome background mode when closed"));

        var powerCmds = new List<PowerCfgCommand>
        {
            new(["/setactive", BalancedPlanGuid], "Activate Balanced plan"),
            new(["/setacvalueindex", BalancedPlanGuid, SubProcessorGuid, ProcMinGuid, "5"], "AC min CPU 5%"),
            new(["/setacvalueindex", BalancedPlanGuid, SubProcessorGuid, ProcMaxGuid, "100"], "AC max CPU 100%")
        };

        if (hw.HasBattery)
        {
            powerCmds.Add(new(["/setdcvalueindex", BalancedPlanGuid, SubProcessorGuid, ProcMinGuid, "5"], "DC min CPU 5%"));
            powerCmds.Add(new(["/setdcvalueindex", BalancedPlanGuid, SubProcessorGuid, ProcMaxGuid, "99"], "DC max CPU 99% (quiet fans)"));
        }
        powerCmds.Add(new(["/setactive", BalancedPlanGuid], "Apply Balanced plan"));

        var notices = new List<string>();
        if (!string.IsNullOrWhiteSpace(hw.ActiveOemTools))
            notices.Add($"OEM suite active ({hw.ActiveOemTools}); keep fan profile Balanced/Silent there too.");

        return new OptimizationPlan(PresetProfile.Daily, BalancedPlanGuid, powerCmds, changes, hibernation, [], notices);
    }

    public static OptimizationPlan BuildGaming(
        HardwareProfile hw,
        string? existingDirectXGlobal,
        bool disableMouseAccel,
        bool optimizeNetworkLatency,
        bool forceOnBattery)
    {
        var warnings = new List<string>();
        var notices = new List<string>();
        string? targetGuid = HighPerformancePlanGuid;

        if (hw.HasBattery && !hw.OnAcPower && !forceOnBattery)
        {
            warnings.Add("On BATTERY: High Performance plan skipped (use ForceOnBattery to override).");
            targetGuid = null;
        }

        var mergedDx = DirectXTokenMerger.Merge(existingDirectXGlobal, new Dictionary<string, string>
        {
            ["SwapEffectUpgradeEnable"] = "1",
            ["VRROptimizeEnable"] = "1"
        });

        var changes = new List<RegistryChange>
        {
            new(@"HKCU:\Software\Microsoft\GameBar", "AllowAutoGameMode", 1, "DWord", "Enable Windows Game Mode"),
            new(@"HKCU:\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, "DWord", "Enable Windows Auto Game Mode"),
            new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, "DWord", "Disable background GameDVR capture"),
            new(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled", 0, "DWord", "Disable GameDVR recording"),
            new(@"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", mergedDx, "String", "Enable Windowed Games & VRR optimizations"),
            new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, "DWord", "Reserve 90% CPU for foreground games"),
            new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, "DWord", "MMCSS Games GPU priority"),
            new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6, "DWord", "MMCSS Games CPU priority"),
            new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High", "String", "MMCSS High scheduling"),
            new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High", "String", "MMCSS High I/O priority")
        };

        if (hw.HagsEligible)
        {
            changes.Add(new(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, "DWord", "Enable HAGS (reboot required)"));
            notices.Add("HAGS (HwSchMode=2) requires a Windows reboot to take effect.");
        }
        else
        {
            notices.Add("HAGS skipped: discrete WDDM 2.7+ GPU not detected.");
        }

        if (optimizeNetworkLatency)
            changes.Add(new(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, "DWord", "Disable multimedia network throttling"));

        if (disableMouseAccel)
        {
            changes.Add(new(@"HKCU:\Control Panel\Mouse", "MouseSpeed", "0", "String", "Disable Windows mouse acceleration"));
            changes.Add(new(@"HKCU:\Control Panel\Mouse", "MouseThreshold1", "0", "String", "Mouse threshold 1 = 0"));
            changes.Add(new(@"HKCU:\Control Panel\Mouse", "MouseThreshold2", "0", "String", "Mouse threshold 2 = 0"));
        }

        var powerCmds = new List<PowerCfgCommand>();
        if (targetGuid is not null)
        {
            powerCmds.Add(new(["/setactive", targetGuid], "Activate High Performance plan"));
            powerCmds.Add(new(["/setacvalueindex", targetGuid, SubProcessorGuid, ProcMinGuid, "5"], "AC min CPU 5%"));
            powerCmds.Add(new(["/setacvalueindex", targetGuid, SubProcessorGuid, ProcMaxGuid, "100"], "AC max CPU 100%"));
            powerCmds.Add(new(["/setactive", targetGuid], "Apply High Performance plan"));
        }

        return new OptimizationPlan(PresetProfile.Gaming, targetGuid, powerCmds, changes, HibernationAction.Keep, warnings, notices);
    }
}
