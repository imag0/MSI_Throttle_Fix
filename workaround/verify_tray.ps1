#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
if (-not $Executable) {
    $Executable = Join-Path $PSScriptRoot '..\UXTU.Headless\bin\Release\net8.0-windows\win-x64\MSIThrottleFix.exe'
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Build the Release helper first. Missing: $Executable"
}

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TrayInterop {
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    public static IntPtr FindTrayWindowForProcess(uint targetProcessId) {
        IntPtr match = IntPtr.Zero;
        EnumWindows((window, state) => {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId != targetProcessId) return true;
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (!className.ToString().StartsWith("MSIThrottleFix.Tray.", StringComparison.Ordinal)) return true;
            match = window;
            return false;
        }, IntPtr.Zero);
        return match;
    }
}
'@

function Find-TrayWindow([uint32]$ProcessId) {
    return [TrayInterop]::FindTrayWindowForProcess($ProcessId)
}

function Wait-LogEvent([string]$Path, [string]$EventName) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ((Test-Path -LiteralPath $Path) -and
            (Select-String -LiteralPath $Path -SimpleMatch "`"event`":`"$EventName`"" -Quiet)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $EventName. Log: $Path"
}

$testLog = Join-Path $env:TEMP "MSIThrottleFix-tray-test-$PID.log"
$testSettings = Join-Path $env:TEMP "MSIThrottleFix-timing-test-$PID.json"
$testTemperature = Join-Path $env:TEMP "MSIThrottleFix-temperature-test-$PID.json"
Set-Content -LiteralPath $testTemperature -Value '{"Celsius":95}'
$arguments = "cycle --dry-run --tray --balanced-ms 750 --extreme-ms 4250 --no-final-extreme --log-file `"$testLog`" --settings-file `"$testSettings`" --temperature-file `"$testTemperature`""
$helper = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    Wait-LogEvent $testLog 'tray_ready'
    $window = Find-TrayWindow ([uint32]$helper.Id)
    if ($window -eq [IntPtr]::Zero) { throw 'Tray window was not found.' }

    $taskbarCreated = [TrayInterop]::RegisterWindowMessage('TaskbarCreated')
    if ($taskbarCreated -eq 0 -or
        -not [TrayInterop]::PostMessage($window, $taskbarCreated, [UIntPtr]::Zero, [IntPtr]::Zero)) {
        throw 'Could not simulate Explorer tray recreation.'
    }
    Wait-LogEvent $testLog 'tray_restored'

    if (-not [TrayInterop]::PostMessage($window, 0x0111, [UIntPtr]::new([uint32]2003), [IntPtr]::Zero) -or
        -not [TrayInterop]::PostMessage($window, 0x0111, [UIntPtr]::new([uint32]2102), [IntPtr]::Zero)) {
        throw 'Could not send the tray timing commands.'
    }
    $timingSaved = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (Test-Path -LiteralPath $testSettings) {
            try {
                $saved = Get-Content -LiteralPath $testSettings -Raw | ConvertFrom-Json
                if ($saved.BalancedMilliseconds -eq 1000 -and $saved.ExtremeMilliseconds -eq 4000) {
                    $timingSaved = $true
                    break
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $timingSaved) { throw 'Tray timing changes were not saved.' }

    if (-not [TrayInterop]::PostMessage($window, 0x0111, [UIntPtr]::new([uint32]2304), [IntPtr]::Zero)) {
        throw 'Could not send the tray temperature command.'
    }
    $temperatureSaved = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (Test-Path -LiteralPath $testTemperature) {
            try {
                $savedTemperature = Get-Content -LiteralPath $testTemperature -Raw | ConvertFrom-Json
                if ($savedTemperature.Celsius -eq 100) {
                    $temperatureSaved = $true
                    break
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $temperatureSaved) { throw 'Tray temperature change was not saved.' }

    if (-not [TrayInterop]::PostMessage($window, 0x0111, [UIntPtr]::new([uint32]1001), [IntPtr]::Zero)) {
        throw 'Could not send the tray Exit command.'
    }
    if (-not $helper.WaitForExit(10000) -or $helper.ExitCode -ne 0) {
        throw 'The tray Exit command did not stop the helper cleanly.'
    }
    Write-Host 'PASS: tray registration, Explorer recovery, timing and temperature changes, and Exit command'
}
finally {
    if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
    Write-Host "Tray test log: $testLog"
}
