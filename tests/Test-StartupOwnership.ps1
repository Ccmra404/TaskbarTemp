$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '..\installer\ManageStartup.ps1'
$helperSource = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $helper).Path)
$program = [IO.File]::ReadAllText((Resolve-Path (Join-Path $PSScriptRoot '..\src\Program.cs')).Path)
$root = Join-Path ([IO.Path]::GetTempPath()) ('TaskbarTemp-startup-test-' + [guid]::NewGuid().ToString('N'))
$firstDir = Join-Path $root "first folder's app"
$secondDir = Join-Path $root 'second folder'
$first = Join-Path $firstDir 'TaskbarTemp.exe'
$second = Join-Path $secondDir 'TaskbarTemp.exe'
$mockState = [pscustomobject]@{ Task = $null }
$createdRoot = $false

function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) return $mockState.Task }
function New-ScheduledTaskAction { param($Execute) return [pscustomobject]@{ Execute = $Execute } }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn) return [pscustomobject]@{ AtLogOn = $AtLogOn.IsPresent; Delay = '' } }
function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) return [pscustomobject]@{ RunLevel = $RunLevel } }
function New-ScheduledTaskSettingsSet {
    param([switch]$DontStopIfGoingOnBatteries, [switch]$AllowStartIfOnBatteries, $ExecutionTimeLimit, [switch]$StartWhenAvailable)
    return [pscustomobject]@{ BatteryAllowed = $AllowStartIfOnBatteries.IsPresent; NoBatteryStop = $DontStopIfGoingOnBatteries.IsPresent }
}
function Register-ScheduledTask {
    param($TaskName, $Action, $Trigger, $Principal, $Settings, $Description, [switch]$Force)
    $mockState.Task = [pscustomobject]@{ Actions = @($Action); Trigger = $Trigger; Principal = $Principal; Settings = $Settings; Description = $Description }
}
function Unregister-ScheduledTask { param($TaskName, $TaskPath, [switch]$Confirm) $mockState.Task = $null }
function Assert([bool]$condition, [string]$message) {
    if (!$condition) { throw "Failed: $message" }
    Write-Host "PASS $message"
}

try {
    Assert ($program.Contains('Ccmra404.TaskbarTemp.Startup.v1')) 'tray startup uses the installer ownership marker'
    Assert ($program.Contains('if ($task -and -not $owned -and -not $samePath)')) 'tray startup rejects an unrelated same-named task'
    Assert ($program.Contains('if ($samePath) { Unregister-ScheduledTask')) 'tray removal only removes the current executable task'
    Assert (!$program.Contains('schtasks.exe"')) 'tray removal does not delete the task unconditionally'
    $identitySource = '[Security.Principal.WindowsIdentity]::GetCurrent().Name'
    Assert ($program.Contains($identitySource) -and $helperSource.Contains($identitySource)) 'tray and installer use the same complete account identity'
    if (Test-Path -LiteralPath $root) { throw 'The test directory already exists.' }
    New-Item -ItemType Directory -Path $root | Out-Null
    $createdRoot = $true
    New-Item -ItemType Directory -Path $firstDir, $secondDir -Force | Out-Null
    [IO.File]::WriteAllText($first, '')
    [IO.File]::WriteAllText($second, '')

    & $helper -Action Install -ExecutablePath $first
    Assert ($mockState.Task.Actions[0].Execute -ieq $first) 'startup uses the selected path'
    Assert ($mockState.Task.Trigger.Delay -eq 'PT30S') 'startup waits for hardware sensors to initialize'
    Assert ($mockState.Task.Description -eq 'Ccmra404.TaskbarTemp.Startup.v1') 'startup records its ownership marker'
    Assert ($mockState.Task.Principal.RunLevel -eq 'Highest') 'startup uses highest privileges'
    Assert ($mockState.Task.Settings.BatteryAllowed -and $mockState.Task.Settings.NoBatteryStop) 'battery use is allowed'
    & $helper -Action Install -ExecutablePath $second
    Assert ($mockState.Task.Actions[0].Execute -ieq $second) 'existing startup moves to the new installation'
    & $helper -Action Remove -ExecutablePath $first
    Assert ($null -ne $mockState.Task) 'uninstall does not remove another installation startup'
    & $helper -Action Remove -ExecutablePath $second
    Assert ($null -eq $mockState.Task) 'uninstall removes its own startup'

    $legacy = [pscustomobject]@{ Actions = @([pscustomobject]@{ Execute = $first }); Description = '' }
    $mockState.Task = $legacy
    & $helper -Action Install -ExecutablePath $first
    Assert ($mockState.Task.Description -eq 'Ccmra404.TaskbarTemp.Startup.v1') 'legacy task at the exact installation path is migrated'

    $unrelatedPath = Join-Path (Join-Path $root 'unrelated app') 'TaskbarTemp.exe'
    $unrelated = [pscustomobject]@{ Actions = @([pscustomobject]@{ Execute = $unrelatedPath }); Description = '' }
    $mockState.Task = $unrelated
    $rejected = $false
    try { & $helper -Action Install -ExecutablePath $first } catch { $rejected = $true }
    Assert $rejected 'same-named executable in another directory is rejected'
    Assert ($mockState.Task.Actions[0].Execute -ieq $unrelatedPath) 'same-named unrelated task is unchanged'

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
