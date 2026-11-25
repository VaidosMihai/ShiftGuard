# ADR-0001: Driver Verification & Safe Update Policy

- **Status:** Accepted
- **Date:** 2026-11-26

## Context
Users need full visibility into missing, errored, unsigned, and outdated Windows drivers alongside safe update execution without introducing third-party driver-updater bloatware or bricking boot/firmware devices.

## Decision
1. **Read-Only Audit Without UAC:** `PnPDriverProbe` queries `Win32_PnPEntity` and `Win32_PnPSignedDriver` under the standard user token (`asInvoker`), checking `ConfigManagerErrorCode`, `IsSigned`, and `.cat`/`.sys` signature trust.
2. **Allowlisted Official Sources Only (`1A`):**
   - **Windows Update Agent COM (`Microsoft.Update.Session`)** for WHQL-signed driver updates.
   - **`winget`** restricted to a static allowlist of official OEM/GPU companion packages (`Nvidia.App`, `AMD.RyzenMaster`, `Intel.DriverAndSupportAssistant`, `Dell.CommandUpdate`, `Lenovo.SystemUpdate`).
   - **Guided OEM Launch** for complex BIOS/chipset/firmware suites (Dell Command Update, Lenovo Vantage, HP Image Assistant, MyASUS/G-Helper).
3. **Anti-Brick Guardrails (`DriverGuardrails`):**
   - Reject unsigned or self-signed drivers.
   - Block automated install for `Firmware`, `SecurityDevices` (TPM), `System`, and boot storage controllers (`DiskDrive`, `SCSIAdapter`, `HDC`).
   - Require triple pre-install safety: Windows System Restore Point (`SRSetRestorePointW`), `pnputil /export-driver` backup, and `RestoreSnapshot` persistence.
   - Halt batch installs when `RebootPending` is active; verify `ConfigManagerErrorCode == 0` post-install and auto-revert via `pnputil` on failure.
