#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$taskName = 'MSIThrottleFix'
$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryDirectory = Split-Path -Parent $scriptDirectory
$executablePath = Join-Path $repositoryDirectory 'dist\MSIThrottleFix.exe'
$logPath = Join-Path $scriptDirectory 'MSIThrottleFix.log'

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "MSIThrottleFix.exe was not found at '$executablePath'. Publish the project first."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($NoStart) {
        $arguments += ' -NoStart'
    }

    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$userId = $identity.Name
$actionArguments = 'cycle --balanced-ms 750 --extreme-ms 4250 --tray --hidden' +
    " --log-file `"$logPath`""
$action = New-ScheduledTaskAction `
    -Execute $executablePath `
    -Argument $actionArguments `
    -WorkingDirectory (Split-Path -Parent $executablePath)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$trigger.Delay = 'PT10S'

$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $taskPrincipal `
    -Settings $settings `
    -Description 'Starts MSI Throttle Fix silently at logon with the privileges required by PawnIO.' `
    -Force | Out-Null

if (-not $NoStart) {
    Start-ScheduledTask -TaskName $taskName
}

Write-Host "Installed Scheduled Task '$taskName' for $userId."
if ($NoStart) {
    Write-Host 'It will start automatically at the next logon.'
} else {
    Write-Host 'It has been started now; look for MSI Throttle Fix in the notification area.'
}
