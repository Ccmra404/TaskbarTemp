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
$temporaryPublish = !$PublishDirectory -and !$SkipPublish
$publishCreated = $false
if (!$PublishDirectory) {
    if ($SkipPublish) { $PublishDirectory = Join-Path $repo 'publish\win-x64' }
    else { $PublishDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TaskbarTemp-publish-' + [guid]::NewGuid().ToString('N')) }
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (!$InnoCompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompilerPath = $command.Source }
    else { $InnoCompilerPath = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) { throw 'Inno Setup 7 ISCC.exe was not found; specify -InnoCompilerPath.' }
try {
    if (!$SkipPublish) {
        if (Test-Path -LiteralPath $PublishDirectory) {
            if ($temporaryPublish) { throw 'The unique temporary publish directory already exists.' }
            if (Get-ChildItem -LiteralPath $PublishDirectory -Force | Select-Object -First 1) {
                throw 'PublishDirectory must be empty when publishing, to prevent stale files entering the installer.'
            }
        } else {
            New-Item -ItemType Directory -Path $PublishDirectory | Out-Null
            if ($temporaryPublish) { $publishCreated = $true }
        }
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
} finally {
    if ($temporaryPublish -and $publishCreated -and (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
        $item = Get-Item -LiteralPath $publishRoot -Force
        if (!$publishRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($publishRoot) -notmatch '^TaskbarTemp-publish-[0-9a-f]{32}$' -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove a publish directory outside the unique temporary build path.'
        }
        [IO.Directory]::Delete($publishRoot, $true)
    }
}
