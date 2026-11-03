[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath)

$ErrorActionPreference = 'Stop'
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
$installDir = Join-Path $runnerTemp ('TaskbarTemp writable install ' + [guid]::NewGuid().ToString('N'))
$marker = Join-Path $installDir 'modified-helper-was-run.txt'
$supportHelper = Join-Path $env:ProgramFiles 'TaskbarTemp\Support\ManageStartup.ps1'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{647AB0E5-B0D6-4756-BF1E-33D3138F9394}_is1'

if (Test-Path -LiteralPath $uninstallKey) { throw 'Refusing to run installer integration test over an existing TaskbarTemp installation.' }
if (-not $installDir.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test install path is outside RUNNER_TEMP.' }
if (Test-Path -LiteralPath $installDir) { throw 'Unexpected test install path collision.' }
if (Get-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'Refusing to overwrite an existing TaskbarTemp scheduled task.' }
$setupStarted = $false

try {
    $setupArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=autostart /DIR=`"$installDir`""
    $setupStarted = $true
    $setup = Start-Process -FilePath $InstallerPath -ArgumentList $setupArgs -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Test install failed with exit code $($setup.ExitCode)." }
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Installer did not create its uninstaller.' }
    if (-not (Test-Path -LiteralPath $supportHelper -PathType Leaf)) { throw 'Trusted startup helper was not installed under Program Files.' }
    $task = Get-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\' -ErrorAction Stop
    $action = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($task.Actions[0].Execute.Trim().Trim('"')))
    if ($action -ine (Join-Path $installDir 'TaskbarTemp.exe') -or $task.Description -ne 'Ccmra404.TaskbarTemp.Startup.v1') { throw 'Install did not create the correctly owned startup task.' }

    $markerForScript = $marker.Replace("'", "''")
    Set-Content -LiteralPath (Join-Path $installDir 'ManageStartup.ps1') -Value "Set-Content -LiteralPath '$markerForScript' -Value executed"
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Test uninstall failed with exit code $($uninstall.ExitCode)." }
    if (Test-Path -LiteralPath $marker) { throw 'Uninstaller executed the modified helper from the writable app directory.' }
    if (Get-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'Uninstaller did not remove its own startup task.' }
    if (Test-Path -LiteralPath $supportHelper) { throw 'Uninstaller did not remove its trusted startup helper.' }
    Write-Host 'PASS startup install/uninstall uses the protected helper and ignores a modified app-directory script'
} finally {
    if ($setupStarted -and (Test-Path -LiteralPath $installDir -PathType Container)) {
        $item = Get-Item -LiteralPath $installDir -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clean a redirected test install directory.' }
        [IO.Directory]::Delete($installDir, $true)
    }
}
