using System.Runtime.InteropServices;
using WinPerfOptimizer.Core.Interfaces;

namespace WinPerfOptimizer.Infrastructure.Services;

public sealed partial class SystemRestoreAdapter : ISystemRestoreService
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ApplicationInstall = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);

    public Task<long> CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var safeDesc = string.IsNullOrWhiteSpace(description) ? "ShiftGuard Restore Point" : description;
            if (safeDesc.Length > 250) safeDesc = safeDesc[..250];

            var info = new RESTOREPOINTINFOW
            {
                dwEventType = BeginSystemChange,
                dwRestorePtType = ApplicationInstall,
                llSequenceNumber = 0,
                szDescription = safeDesc
            };

            if (!SRSetRestorePointW(ref info, out var status))
                return 0L;

            var endInfo = new RESTOREPOINTINFOW
            {
                dwEventType = EndSystemChange,
                dwRestorePtType = ApplicationInstall,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = safeDesc
            };
            _ = SRSetRestorePointW(ref endInfo, out _);
            return status.llSequenceNumber;
        }, ct);
    }
}
