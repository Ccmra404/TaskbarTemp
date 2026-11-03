$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '..\installer\ManageStartup.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('TaskbarTemp-startup-test-' + [guid]::NewGuid().ToString('N'))
$firstDir = Join-Path $root "first folder's app"
$secondDir = Join-Path $root 'second folder'
$first = Join-Path $firstDir 'TaskbarTemp.exe'
$second = Join-Path $secondDir 'TaskbarTemp.exe'
$mockState = [pscustomobject]@{ Task = $null }
$createdRoot = $false

function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) return $mockState.Task }
function New-ScheduledTaskAction { param($Execute) return [pscustomobject]@{ Execute = $Execute } }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn) return [pscustomobject]@{ AtLogOn = $AtLogOn.IsPresent } }
function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) return [pscustomobject]@{ RunLevel = $RunLevel } }
function New-ScheduledTaskSettingsSet {
    param([switch]$DontStopIfGoingOnBatteries, [switch]$AllowStartIfOnBatteries, $ExecutionTimeLimit, [switch]$StartWhenAvailable)
    return [pscustomobject]@{ BatteryAllowed = $AllowStartIfOnBatteries.IsPresent; NoBatteryStop = $DontStopIfGoingOnBatteries.IsPresent }
}
function Register-ScheduledTask {
    param($TaskName, $Action, $Trigger, $Principal, $Settings, [switch]$Force)
    $mockState.Task = [pscustomobject]@{ Actions = @($Action); Trigger = $Trigger; Principal = $Principal; Settings = $Settings }
}
function Unregister-ScheduledTask { param($TaskName, $TaskPath, [switch]$Confirm) $mockState.Task = $null }
function Assert([bool]$condition, [string]$message) {
    if (!$condition) { throw "Failed: $message" }
    Write-Host "PASS $message"
}

try {
    if (Test-Path -LiteralPath $root) { throw 'The test directory already exists.' }
    New-Item -ItemType Directory -Path $root | Out-Null
    $createdRoot = $true
    New-Item -ItemType Directory -Path $firstDir, $secondDir -Force | Out-Null
    [IO.File]::WriteAllText($first, '')
    [IO.File]::WriteAllText($second, '')

    & $helper -Action Install -ExecutablePath $first
    Assert ($mockState.Task.Actions[0].Execute -ieq $first) 'startup uses the selected path'
    Assert ($mockState.Task.Principal.RunLevel -eq 'Highest') 'startup uses highest privileges'
    Assert ($mockState.Task.Settings.BatteryAllowed -and $mockState.Task.Settings.NoBatteryStop) 'battery use is allowed'
    & $helper -Action Install -ExecutablePath $second
    Assert ($mockState.Task.Actions[0].Execute -ieq $second) 'existing startup moves to the new installation'
    & $helper -Action Remove -ExecutablePath $first
    Assert ($null -ne $mockState.Task) 'uninstall does not remove another installation startup'
    & $helper -Action Remove -ExecutablePath $second
    Assert ($null -eq $mockState.Task) 'uninstall removes its own startup'

    $mockState.Task = [pscustomobject]@{ Actions = @([pscustomobject]@{ Execute = 'C:\Windows\System32\notepad.exe' }) }
    $rejected = $false
    try { & $helper -Action Install -ExecutablePath $first } catch { $rejected = $true }
    Assert $rejected 'unrelated scheduled task is rejected'
    Assert ($mockState.Task.Actions[0].Execute -ieq 'C:\Windows\System32\notepad.exe') 'unrelated task is unchanged'
} finally {
    if ($createdRoot -and (Test-Path -LiteralPath $root -PathType Container)) {
        $rootItem = Get-Item -LiteralPath $root -Force
        if (-not ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            foreach ($file in @($first, $second)) { if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force } }
            foreach ($dir in @($firstDir, $secondDir, $root)) { if (Test-Path -LiteralPath $dir -PathType Container) { Remove-Item -LiteralPath $dir -Force } }
        }
    }
}
