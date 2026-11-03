[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$InnoCompilerPath,
    [string]$PublishDirectory,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\TaskbarTemp.csproj'
if (!$PublishDirectory) { $PublishDirectory = Join-Path $repo 'publish\win-x64' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (!$InnoCompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompilerPath = $command.Source }
    else { $InnoCompilerPath = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) { throw 'Inno Setup 7 ISCC.exe was not found; specify -InnoCompilerPath.' }
if (!$SkipPublish) {
    & $DotnetPath publish $project -c Release -r win-x64 --self-contained true -o $PublishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'TaskbarTemp.exe') -PathType Leaf)) { throw 'Published TaskbarTemp.exe was not found.' }
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'The project version must be numeric.' }
& $InnoCompilerPath "/DPublishDir=$PublishDirectory" "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'TaskbarTemp.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Host "Installer: $(Join-Path $repo "artifacts\installer\TaskbarTemp-Setup-$version-x64.exe")"
