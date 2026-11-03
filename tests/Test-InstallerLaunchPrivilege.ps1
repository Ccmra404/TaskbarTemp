[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\installer\TaskbarTemp.iss'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\app.manifest')
)

$ErrorActionPreference = 'Stop'
$setup = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InstallerPath).Path)
[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$level = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").GetAttribute('level')
if ($level -ne 'requireAdministrator') { throw 'The app no longer requires administrator; review the installer launch policy.' }
if ($setup -notmatch '(?im)^PrivilegesRequired\s*=\s*admin\s*$') { throw 'Setup must run elevated.' }

$run = [regex]::Match($setup, '(?ms)^\[Run\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
if (!$run.Success) { throw 'Installer [Run] section not found.' }
$entries = @($run.Groups['body'].Value -split '\r?\n' | Where-Object { $_ -match '^Filename:\s*"\{app\}\\TaskbarTemp\.exe"' })
if ($entries.Count -ne 1) { throw 'Expected exactly one application launch entry.' }
$match = [regex]::Match($entries[0], '(?i)\bFlags:\s*(?<flags>[^;\r\n]+)')
if (!$match.Success) { throw 'Application launch flags not found.' }
$flags = @($match.Groups['flags'].Value -split '\s+' | Where-Object { $_ })
if ('postinstall' -notin $flags -or 'runascurrentuser' -notin $flags -or 'runasoriginaluser' -in $flags) {
    throw 'An administrator-required application must inherit the elevated installer token after installation.'
}
Write-Host 'PASS administrator-required application launches with the elevated installer token'
