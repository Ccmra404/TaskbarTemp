$ErrorActionPreference = 'Stop'
$setupPath = Join-Path $PSScriptRoot '..\installer\TaskbarTemp.iss'
$setup = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $setupPath).Path)

function Assert([bool]$condition, [string]$message) {
    if (!$condition) { throw "Failed: $message" }
    Write-Host "PASS $message"
}

$trustedPath = '{autopf}\TaskbarTemp\Support\ManageStartup.ps1'
Assert ($setup -match ('(?im)^Source: "ManageStartup\.ps1"; DestDir: "' + [regex]::Escape('{autopf}\TaskbarTemp\Support') + '"')) 'startup helper installs under protected Program Files'
Assert ($setup.Contains($trustedPath)) 'install and uninstall commands use the protected helper path'
Assert (!$setup.Contains('{app}\ManageStartup.ps1')) 'no elevated command loads a helper from the selectable app directory'
Assert ($setup -match "ExpandConstant\('\{autopf\}\\TaskbarTemp\\Support\\ManageStartup\.ps1'\)") 'post-install startup setup invokes the protected helper'
Assert ($setup -match '(?im)^Filename: "\{sys\}\\WindowsPowerShell\\v1\.0\\powershell\.exe"; Parameters: "[^\r\n]*-File ""\{autopf\}\\TaskbarTemp\\Support\\ManageStartup\.ps1""[^\r\n]*"; Flags:') 'uninstall invokes the protected helper'
