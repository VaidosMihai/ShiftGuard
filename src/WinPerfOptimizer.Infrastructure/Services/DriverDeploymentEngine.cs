using System.Diagnostics;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Models;
using WinPerfOptimizer.Core.Services;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed class DriverDeploymentEngine(ISystemRestoreService restoreService) : IDriverDeploymentService
{
    public async Task<DriverInstallOutcome> StageAndInstallAsync(
        DriverUpdateCandidate candidate,
        Guid snapshotId,
        CancellationToken ct = default)
    {
        DriverGuardrails.AssertSafeDriverCandidate(candidate, systemRebootPending: false);

        _ = await restoreService.CreateRestorePointAsync($"ShiftGuard Driver Backup {snapshotId:N}", ct).ConfigureAwait(false);

        if (candidate.Source == DriverUpdateSource.WingetOemCompanion)
        {
            if (!DriverGuardrails.IsAllowedWingetPackage(candidate.CandidateId))
                return new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, "Blocked: winget ID not on OEM allowlist.");

            var wingetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(wingetPath))
                return new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, "winget.exe not found.");

            var psi = new ProcessStartInfo { FileName = wingetPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("upgrade");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add(candidate.CandidateId);
            psi.ArgumentList.Add("--exact");
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--accept-package-agreements");
            psi.ArgumentList.Add("--accept-source-agreements");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch winget.");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0
                ? new DriverInstallOutcome(candidate.CandidateId, candidate.Title, true, false, candidate.RequiresReboot, $"Updated {candidate.Title} via winget.")
                : new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, $"winget exited with code {proc.ExitCode}.");
        }

        return await Task.Run(() => InstallViaWua(candidate), ct).ConfigureAwait(false);
    }

    private static DriverInstallOutcome InstallViaWua(DriverUpdateCandidate candidate)
    {
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
                return new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, "Windows Update COM session unavailable.");

            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

            dynamic? matched = null;
            int count = searchResult.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = searchResult.Updates.Item(i);
                string uid = u.Identity?.UpdateID ?? "";
                if (string.Equals(uid, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
                {
                    matched = u;
                    break;
                }
            }

            if (matched is null)
                return new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, "Driver update no longer pending in Windows Update.");

            var colType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            dynamic coll = Activator.CreateInstance(colType)!;
            coll.Add(matched);

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = coll;
            _ = downloader.Download();

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = coll;
            dynamic installRes = installer.Install();
            int resultCode = installRes.ResultCode;
            bool rebootReq = installRes.RebootRequired;

            var ok = resultCode is 2 or 3;
            return new DriverInstallOutcome(
                candidate.CandidateId,
                candidate.Title,
                ok,
                false,
                rebootReq,
                ok ? $"Installed {candidate.Title} via Windows Update (ResultCode={resultCode})." : $"Windows Update install failed (ResultCode={resultCode}).");
        }
        catch (Exception ex)
        {
            return new DriverInstallOutcome(candidate.CandidateId, candidate.Title, false, false, false, ex.Message);
        }
    }
}
