# WinPerfOptimizer

Minimalist, dark-mode **C# (.NET 8) WPF** Windows 10/11 performance and resource-efficiency utility for **Daily Use** and **Gaming** — built with strict anti-brick guardrails, non-elevated Audit/Dry-Run previews, on-demand UAC elevation, and deterministic Rollback.

## Key Features
- **Audit & Dry-Run without Admin:** Inspect hardware, active power scheme, startup apps, heavy background processes, and preview every planned registry/powercfg change before applying anything.
- **Daily Efficiency Mode:** Balanced power plan (`5%–100%` AC, `5%–99%` battery), GameDVR capture off, HTTP-only Delivery Optimization (`DODownloadMode=0`), Storage Sense enabled, and Edge/Chrome background boost off.
- **Gaming Performance Mode:** High Performance plan (with battery guard), Windows Game Mode, Windowed Games & VRR token-merge, MMCSS `Games` priority, HAGS (`HwSchMode=2` when eligible), optional network throttling & mouse acceleration toggles.
- **Session Guard & Rollback:** Snapshot-backed (`%ProgramData%\WinPerfOptimizer\Backups\<Guid>.json` + `.pow` export) with one-click Rollback and automatic rollback on partial apply failures.
- **Anti-Brick Guardrails:** Never touches Windows Defender, Firewall, Windows Update, UAC, Pagefile, or core OS services.

## Build & Publish
```powershell
dotnet test -c Release
dotnet publish src\WinPerfOptimizer.App\WinPerfOptimizer.App.csproj -p:PublishProfile=win-x64-singlefile
dotnet publish src\WinPerfOptimizer.App\WinPerfOptimizer.App.csproj -p:PublishProfile=win-x64-framework
```
