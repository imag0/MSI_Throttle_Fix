# MSI Throttle Fix

`MSIThrottleFix.exe` is a small C# background helper for AMD Dragon Range HX
laptops, including Ryzen 7 7840HX, Ryzen 9 7845HX, and Ryzen 9 7945HX systems.
It detects the processor family and directly reapplies UXTU's Dragon Range SMU
values without opening or automating the UXTU GUI.

The default loop is:

1. Apply Balanced.
2. Sleep for 750 ms.
3. Apply Extreme.
4. Sleep for 4250 ms.
5. Repeat.

The waits use `Task.Delay`; there is no sensor polling, busy loop, WPF window, or
Python process. Normal mode writes startup, first-cycle confirmation, shutdown,
and error events to disk. Successful command-by-command logging is enabled only
by `--verbose`.

> **Warning:** This tool writes AMD SMU power/current parameters using UXTU's
> PawnIO backend and UXTU's Dragon Range values. The normal CPU guard requires
> an HX processor detected as AMD Family 25, Model 97 and rejects unrelated
> processors. Do not use `--force` without reviewing the command table and
> values.

## Install and start automatically

The published executable is self-contained. PawnIO still needs to be installed
and working on Windows 11 x64.

Double-click:

```text
workaround\install_autorun.bat
```

Accept the one-time Administrator/UAC prompt. The installer creates a Scheduled
Task named `MSIThrottleFix` for the current user, configured to:

- run 10 seconds after logon;
- run with highest privileges for PawnIO access;
- launch `dist\MSIThrottleFix.exe` directly and hide its console window;
- show a notification-area icon while the helper is running.

The task is also started immediately after installation. Hover over the tray
icon for the current preset, cycle number, and hardware-write failure count.
Right-click it and select **Exit MSI Throttle Fix** for a graceful stop. The
icon switches to the standard Windows error icon if an SMU write fails.
If Explorer is not ready at startup or restarts later, the helper retries tray
registration every five seconds and restores the icon when Explorer returns.

The same right-click menu has **Balanced wait** and **Extreme wait** submenus.
Each can be shortened or lengthened by 250 ms or one second. The menu shows the
current value. Changes apply when the next wait for that preset begins and are
saved in `%LOCALAPPDATA%\MSIThrottleFix\cycle-timing.json`, so they survive
logoff and restart. **Reset both waits to startup defaults** restores the
command-line values (750 ms Balanced and 4250 ms Extreme for the installed task).

To remove automatic startup, double-click:

```text
workaround\remove_autorun.bat
```

Removing the task does not kill an already-running instance; use the tray menu
to stop it.

## Visible/manual run

For debugging, right-click `workaround\run_workaround.bat` and choose
**Run as administrator**. This deliberately keeps the console visible and also
creates the tray icon. Press Ctrl+C or use the tray menu to stop. The helper
applies Extreme once more during graceful shutdown unless
`--no-final-extreme` is supplied.

Direct Administrator commands:

```powershell
.\dist\MSIThrottleFix.exe info
.\dist\MSIThrottleFix.exe balanced
.\dist\MSIThrottleFix.exe extreme
.\dist\MSIThrottleFix.exe cycle --balanced-ms 750 --extreme-ms 4250 --tray
.\dist\MSIThrottleFix.exe watch-extreme --interval 5000 --tray
.\dist\MSIThrottleFix.exe apply-command fast 75000
```

`apply-command` accepts only `stapm`, `fast`, `slow`, `tdc`, `edc`, `tctl`,
`chtc`, `stapm-time`, and `slow-time`; it does not expose arbitrary SMU
messages or registers.

## Build and verify

From the repository root:

```powershell
dotnet run --project .\UXTU.Headless.Tests\UXTU.Headless.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\workaround\verify_tray.ps1
dotnet publish .\UXTU.Headless\UXTU.Headless.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\dist
```

Dry-run exercises the real ordering and timing without opening PawnIO:

```powershell
.\dist\MSIThrottleFix.exe info --dry-run
.\dist\MSIThrottleFix.exe balanced --dry-run --verbose
.\dist\MSIThrottleFix.exe cycle --dry-run --tray --verbose
```

The publish copies `Assets\AMD\PawnIO\RyzenSMU.bin` into the `dist` tree.
The tray check uses a dry run, simulates Explorer recreating its notification
area, changes both waits through the tray command path, and verifies Exit without
writing to the SMU. Use `--settings-file <path>` with `cycle` to keep a separate
timing file for a manual run.

## Logs and troubleshooting

The default JSON-lines log is:

```text
%LOCALAPPDATA%\MSIThrottleFix\MSIThrottleFix.log
```

The installed autorun task uses `workaround\MSIThrottleFix.log` instead so its
elevated and interactive launches always share one predictable location.

Failures include the preset, command, mailbox, message ID, argument, response,
and error. Use `--verbose` only when detailed successful-write evidence is
needed.
`tray_ready`, `tray_unavailable`, and `tray_restored` events show whether Windows
accepted the icon and whether registration recovered after a shell reset.

- Run `MSIThrottleFix.exe info` in an Administrator terminal and inspect
  `pawnIoInitialized`, `ryzenSmuModuleExists`, and `initializationError`.
- Confirm `dist\Assets\AMD\PawnIO\RyzenSMU.bin` exists.
- PawnIO's driver must be installed; the module file alone is not the driver.
- Close UXTU or disable its auto-reapply/adaptive features so two tools do not
  compete for SMU settings.
- The global writer mutex prevents two real helper instances from issuing
  hardware writes at the same time.

The command sequence and dry-run behavior are verified locally. Only testing on
the target laptop can confirm that the firmware frequency lock is cleared under
real operating conditions.
