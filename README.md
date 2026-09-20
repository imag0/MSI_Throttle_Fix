# MSI Alpha 17 C7VG 0.55 GHz Throttle Workaround

A small headless workaround originally developed for the **MSI Alpha 17 C7VG / Ryzen 9 7945HX** issue where the CPU can suddenly become stuck around **0.55 GHz** / a **~400 MHz global frequency limit**. The helper now detects and accepts the wider AMD Dragon Range HX family automatically.

> [!IMPORTANT]
> ## Installation
>
> The actual fix is inside the **`workaround`** folder.
>
> 1. Download and extract the repository.
> 2. Open the **`workaround`** folder.
> 3. Right-click **`install_autorun.bat`** and choose **Run as administrator**.
> 4. Approve the Windows UAC prompt.
> 5. Done.
>
> The workaround will start automatically with Windows. You do **not** need to keep UXTU open.

---

## What it does

The workaround periodically reapplies known-good AMD SMU power/current settings derived from the behavior of **Universal x86 Tuning Utility (UXTU)**.

The default cycle is:

```text
Balanced
750 ms
↓
Extreme
4250 ms
↓
repeat
```

On the affected laptop this prevents or clears the firmware/SMU state that can force the CPU down to roughly **0.55 GHz**.

The program runs headlessly in the background and is designed to remain effectively idle between SMU writes.

---

## Supported hardware

The workaround has been verified on:

- **MSI Alpha 17 C7VG / MS-17KK**
- **AMD Ryzen 9 7945HX**
- **Windows 11**

The software guard supports AMD **Dragon Range HX** processors detected as
Family 25, Model 97, including the Ryzen 7 7840HX, Ryzen 9 7845HX, and Ryzen 9
7945HX. UXTU itself uses the same Dragon Range preset for these processors.
Other laptop models remain unverified hardware.

> [!WARNING]
> Do not use this on unrelated CPUs or laptops unless you have reviewed the SMU values and backend behavior yourself.
>
> The workaround writes AMD SMU power/current parameters directly.

---

## Files you actually need

Everything required to install/run the workaround is inside:

```text
workaround/
```

The important file is:

```text
workaround/install_autorun.bat
```

Run that **as Administrator**.

You do **not** need to build the source code just to use the fix.

---

## Uninstall

Open the `workaround` folder and run:

```text
remove_autorun.bat
```

Run it as Administrator if Windows asks.

This removes the automatic startup task.

---

## Manual testing

If you want to test the helper before installing autorun, open **Terminal / PowerShell as Administrator** in the workaround folder.

Check hardware detection:

```powershell
.\MSIThrottleFix.exe info
```

Run the workaround manually:

```powershell
.\MSIThrottleFix.exe cycle
```

Stop it with:

```text
Ctrl+C
```

Normal users can skip this section and just use `install_autorun.bat`.

---

## How autorun works

`install_autorun.bat` installs the workaround as a Windows scheduled task with elevated privileges.

After installation:

- it starts automatically after login
- no UXTU window is required
- no permanent Command Prompt window should remain open
- no UAC prompt should appear on every login
- the process remains visible normally in Task Manager
- failures are written to the included log files

---

## Why this exists

Some MSI Alpha 17 C7VG systems can enter a state where the Ryzen 9 7945HX becomes locked to an extremely low frequency even though normal CPU thermal-throttling flags do not necessarily indicate a conventional thermal throttle.

Reapplying UXTU power/current presets was found to clear the condition, so this project isolates that behavior into a small background workaround instead of requiring the full UXTU interface to remain open.

This is an **unofficial workaround**, not an MSI firmware fix.

---

## UXTU / licensing

Parts of the SMU/PawnIO implementation are derived from or based on:

**Universal x86 Tuning Utility (UXTU)**  
https://github.com/JamesCJ60/Universal-x86-Tuning-Utility

UXTU is licensed under the **GNU General Public License v3.0**.

This project is not affiliated with or endorsed by **MSI**, **AMD**, or the **UXTU project**.

See [`LICENSE`](LICENSE) and any included third-party notices for licensing information.

---

## Disclaimer

This software interacts directly with AMD SMU power/current controls.

Use it at your own risk.

It was created specifically for the **MSI Alpha 17 C7VG + Ryzen 9 7945HX**
frequency-lock issue. Although its CPU guard now adapts to the Dragon Range HX
family, it should not be treated as a general-purpose Ryzen tuning utility.
