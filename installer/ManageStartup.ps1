param(
    [Parameter(Mandatory)] [ValidateSet('Install', 'Remove')] [string]$Action,
    [Parameter(Mandatory)] [string]$ExecutablePath,
    [string]$TaskName = 'TaskbarTemp'
)

$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction SilentlyContinue
$expected = [IO.Path]::GetFullPath($ExecutablePath)
$existing = $null
if ($null -ne $task -and $task.Actions.Count -eq 1 -and $task.Actions[0].Execute) {
    $existing = [Environment]::ExpandEnvironmentVariables($task.Actions[0].Execute.Trim().Trim('"'))
}

if ($Action -eq 'Remove') {
    if ($existing -and [string]::Equals([IO.Path]::GetFullPath($existing), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        Unregister-ScheduledTask -TaskName $TaskName -TaskPath '\' -Confirm:$false
    }
    return
}

if ($null -ne $task -and (!$existing -or [IO.Path]::GetFileName($existing) -ine 'TaskbarTemp.exe')) {
    throw 'A different scheduled task already uses this name.'
}
if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { throw 'TaskbarTemp executable was not found.' }
$taskAction = New-ScheduledTaskAction -Execute $expected
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
