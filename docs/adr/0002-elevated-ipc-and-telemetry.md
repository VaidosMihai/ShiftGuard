# ADR-0002: Hardened Elevated IPC, Hybrid Telemetry & Cross-Reboot Revisions

- **Status:** Accepted
- **Date:** 2026-11-26

## Context
v1 used `%TEMP%\WinPerfOptimizer` JSON files for `--elevated-worker` IPC and only captured flat `HardwareProfile` + `DiffRow` lists. v2 adds hardware technical revision (Battery XML, CPU/Pdh, DIMM/Commit, SSD SMART), optional low-level thermal sensors, and persistent Before/After baselines across reboots.

## Decision
1. **Named Pipe + SHA-256 IPC:** Replace `%TEMP%` request/result exchange with a per-request Named Pipe (`WinPerfOptimizer_IPC_{Guid:N}`) secured to the current user SID + `Administrators`, verified by SHA-256 payload digest inside `--elevated-worker`.
2. **Hybrid Telemetry (`2AB`):**
   - Default non-elevated probes use inbox Windows APIs (`WMI/CIM`, `powercfg /batteryreport /xml` parsed via `XmlReader` with `DtdProcessing.Prohibit`, `PdhAddEnglishCounterW`, `GetLogicalProcessorInformationEx`, `CallNtPowerInformation`, `GlobalMemoryStatusEx`).
   - `MSFT_StorageReliabilityCounter` gracefully returns `ProbeStatus.RequiresElevation` under standard user tokens and can be refreshed via on-demand elevated deep scan.
   - Optional thermal/fan sensor provider (`IOptionalThermalSensorProvider`) is opt-in and isolated behind `Infrastructure` so `Core` remains zero-dependency and standard runs leave zero kernel-driver footprint.
3. **Cross-Reboot & Manual Revision Continuity (`3B`):**
   - `JsonRevisionStore` persists `SystemRevisionSnapshot` entries (both automatic pre/post optimization baselines and user-saved manual checkpoints) under `%ProgramData%\WinPerfOptimizer\Revisions\`, enabling authentic Before/After comparisons across reboots.
