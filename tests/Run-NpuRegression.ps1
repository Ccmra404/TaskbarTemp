[CmdletBinding()]
param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This regression requires Windows Forms on Windows.' }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceProgram = Join-Path $repo 'src\Program.cs'
$dotnetCandidate = $DotnetPath
if ([string]::IsNullOrWhiteSpace($dotnetCandidate)) { $dotnetCandidate = $env:TASKBARTEMP_DOTNET }
if ([string]::IsNullOrWhiteSpace($dotnetCandidate)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { throw 'dotnet.exe not found; pass -DotnetPath or set TASKBARTEMP_DOTNET.' }
    $dotnetCandidate = $dotnetCommand.Source
}
if (-not (Test-Path -LiteralPath $dotnetCandidate -PathType Leaf)) { throw "dotnet.exe not found: $dotnetCandidate" }
$dotnet = (Resolve-Path -LiteralPath $dotnetCandidate).Path

function Get-OccurrenceCount([string]$Text, [string]$Needle) {
    $count = 0
    $offset = 0
    while (($offset = $Text.IndexOf($Needle, $offset, [StringComparison]::Ordinal)) -ge 0) {
        $count++
        $offset += $Needle.Length
    }
    return $count
}

function Assert-One([string]$Text, [string]$Needle, [string]$Description) {
    $count = Get-OccurrenceCount $Text $Needle
    if ($count -ne 1) { throw "Unsafe source anchor '$Description': expected once, found $count." }
}

function Replace-One([string]$Text, [string]$Needle, [string]$Replacement, [string]$Description) {
    Assert-One $Text $Needle $Description
    return $Text.Replace($Needle, $Replacement)
}

function Replace-Span([string]$Text, [string]$Start, [string]$End, [string]$Replacement, [string]$Description) {
    Assert-One $Text $Start "$Description start"
    Assert-One $Text $End "$Description end"
    $startAt = $Text.IndexOf($Start, [StringComparison]::Ordinal)
    $endAt = $Text.IndexOf($End, $startAt + $Start.Length, [StringComparison]::Ordinal)
    if ($endAt -lt 0) { throw "Unsafe source span '$Description': end precedes start." }
    return $Text.Substring(0, $startAt) + $Replacement + $Text.Substring($endAt)
}

function Quote-Xml([string]$Text) {
    return [System.Security.SecurityElement]::Escape($Text)
}

function Invoke-OwnedProcess([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory,
                             [string]$StdoutPath, [string]$StderrPath, [int]$TimeoutMs) {
    $process = Start-Process -FilePath $FilePath -ArgumentList ($Arguments -join ' ') `
        -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru `
        -RedirectStandardOutput $StdoutPath -RedirectStandardError $StderrPath
    [void]$script:OwnedProcesses.Add($process)
    if (-not $process.WaitForExit($TimeoutMs)) {
        $process.Kill()
        $process.WaitForExit()
        throw "Timed out waiting for process started by this runner: $FilePath"
    }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $StdoutPath) { Get-Content -LiteralPath $StdoutPath }
        if (Test-Path -LiteralPath $StderrPath) { Get-Content -LiteralPath $StderrPath }
        throw "Runner-owned process failed ($($process.ExitCode)): $FilePath"
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$token = [Guid]::NewGuid().ToString('N')
$runName = "TaskbarTemp-NpuRegression-$token"
$runDir = Join-Path $tempRoot $runName
$sentinel = Join-Path $runDir '.owned-by-npu-regression'
$createdRunDir = $false
$script:OwnedProcesses = [System.Collections.ArrayList]::new()
$savedEnvironment = @{}
foreach ($name in @('DOTNET_ROOT', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'NUGET_CLI_CERT_REVOCATION_MODE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    if (Test-Path -LiteralPath $runDir) { throw "Generated temp path already exists: $runDir" }
    [void][IO.Directory]::CreateDirectory($runDir)
    $createdRunDir = $true
    [IO.File]::WriteAllText($sentinel, $token, [Text.Encoding]::ASCII)

    $tempSrc = Join-Path $runDir 'src'
    [void][IO.Directory]::CreateDirectory($tempSrc)
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo 'src') -File) {
        Copy-Item -LiteralPath $file.FullName -Destination $tempSrc
    }
    Copy-Item -LiteralPath (Join-Path $repo 'lib') -Destination (Join-Path $runDir 'lib') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NpuRegression.cs') -Destination (Join-Path $tempSrc 'NpuRegression.cs')

    $programPath = Join-Path $tempSrc 'Program.cs'
    $programBefore = [IO.File]::ReadAllText($programPath)
    $sourceHash = (Get-FileHash -LiteralPath $programPath -Algorithm SHA256).Hash
    if ($programBefore.Contains('NpuRegressionFake') -or $programBefore.Contains('NpuRegression.Start')) {
        throw 'Source already contains test hooks; refusing to run against a pre-patched or stale test copy.'
    }

    foreach ($anchor in @(
        'private readonly object _npuLock = new();',
        'private int _npuVisible, _npuGeneration, _npuWorkerRunning;',
        'private void RequestNpuSample()',
        'private void CompleteNpuSample(Task<float?> worker, int generation)',
        'private SampleSnapshot SampleHardware()',
        'private float? ReadNpuUsage()'
    )) { Assert-One $programBefore $anchor $anchor }

    $program = Replace-Span $programBefore '            using var mutex' '            ApplicationConfiguration.Initialize();' '' 'test entry point'
    $program = Replace-One $program '            var context = new TrayContext();' `
        ('            var context = new TrayContext();' + [Environment]::NewLine + '            NpuRegression.Start(context);') `
        'test entry hook'
    $program = Replace-One $program '            _style = Settings.Style;' '            _style = IconStyle.Number;' 'test-only style'
    $program = Replace-Span $program '        private static bool IsDarkMode()' '        private static Color CardFillColor(' `
        '        private static bool IsDarkMode() => false;' `
        'test-only theme'
    $program = Replace-Span $program '            _logPath = Path.Combine(' `
        '            try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)); } catch { }' `
        ('            _logPath = Path.Combine(AppContext.BaseDirectory, "npu-regression.log");' + [Environment]::NewLine + '            ') `
        'test-only log path'
    $program = Replace-Span $program '        private SampleSnapshot SampleHardware()' '        private void ApplySnapshot(' `
        @'
        private SampleSnapshot SampleHardware()
        {
            _computerOpenAttempted = true;
            NpuRegressionFake.HardwareSample();
            return new SampleSnapshot(51, 24, 3200, "Fake CPU", 43, 12, 512, 2048, "Fake GPU", 8, 16, 100, 50);
        }

'@ `
        'fake hardware sampler'
    $program = Replace-Span $program '        private float? ReadNpuUsage()' '        private float FilterTemperature(' `
        @'
        private float? ReadNpuUsage() => NpuRegressionFake.Read();

'@ `
        'fake WMI query'
    $completeStart = $program.IndexOf('        private void CompleteNpuSample(Task<float?> worker, int generation)', [StringComparison]::Ordinal)
    $completeEnd = $program.IndexOf('        private void StartSampleWorker()', $completeStart, [StringComparison]::Ordinal)
    if ($completeStart -lt 0 -or $completeEnd -lt 0) { throw 'Unsafe completion method span.' }
    $complete = $program.Substring($completeStart, $completeEnd - $completeStart)
    $complete = Replace-One $complete '                _npuWorkerRunning = 0;' `
        ('                _npuWorkerRunning = 0;' + [Environment]::NewLine + '                NpuRegressionFake.BeforeCompletionDecision();') `
        'completion-lock race gate'
    $program = $program.Substring(0, $completeStart) + $complete + $program.Substring($completeEnd)
    $program = Replace-One $program 'if (_computerOpenAttempted) _computer.Close();' `
        'if (_computerOpenAttempted) NpuRegressionFake.HardwareClose();' 'fake hardware close'

    $originalHashAfterCopy = (Get-FileHash -LiteralPath $sourceProgram -Algorithm SHA256).Hash
    if ($sourceHash -ne $originalHashAfterCopy) {
        throw 'src/Program.cs changed while this runner was making its isolated test snapshot.'
    }
    [IO.File]::WriteAllText($programPath, $program, [Text.UTF8Encoding]::new($false))
    Write-Host "Program.cs snapshot SHA256: $sourceHash"
    Write-Host "Isolated temp directory: $runDir"

    $packageCache = Join-Path $env:USERPROFILE '.nuget\packages'
    if (-not (Test-Path -LiteralPath $packageCache -PathType Container)) {
        throw "Offline NuGet cache not found: $packageCache"
    }
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="existing-cache" value="$(Quote-Xml $packageCache)" /></packageSources>
  <config><add key="globalPackagesFolder" value="$(Quote-Xml (Join-Path $runDir 'packages'))" /></config>
</configuration>
"@
    [IO.File]::WriteAllText((Join-Path $runDir 'NuGet.Config'), $nugetConfig, [Text.UTF8Encoding]::new($false))
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_CLI_CERT_REVOCATION_MODE = 'offline'

    Invoke-OwnedProcess $dotnet @('restore', '.\src\TaskbarTemp.csproj', '--configfile', '.\NuGet.Config', '--verbosity', 'minimal', '-p:NuGetAudit=false') `
        $runDir (Join-Path $runDir 'restore.stdout.log') (Join-Path $runDir 'restore.stderr.log') 120000
    Invoke-OwnedProcess $dotnet @('build', '.\src\TaskbarTemp.csproj', '--no-restore', '--configuration', 'Debug', '--verbosity', 'minimal') `
        $runDir (Join-Path $runDir 'build.stdout.log') (Join-Path $runDir 'build.stderr.log') 120000

    $testExe = Join-Path $runDir 'src\bin\Debug\net10.0-windows\win-x64\TaskbarTemp.exe'
    if (-not (Test-Path -LiteralPath $testExe -PathType Leaf)) { throw 'Built regression apphost not found.' }
    $testProcess = Start-Process -FilePath $testExe -WorkingDirectory (Split-Path -Parent $testExe) -WindowStyle Hidden -PassThru
    [void]$script:OwnedProcesses.Add($testProcess)
    if (-not $testProcess.WaitForExit(30000)) {
        $testProcess.Kill()
        $testProcess.WaitForExit()
        throw 'The isolated regression process timed out.'
    }
    $report = Join-Path (Split-Path -Parent $testExe) 'NpuRegressionResult.txt'
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        $reportText = [IO.File]::ReadAllText($report)
        Write-Host $reportText
        if (-not $reportText.StartsWith('RESULT=PASS', [StringComparison]::Ordinal)) {
            throw 'Regression assertions failed.'
        }
    }
    else { throw 'Regression process did not produce a result report.' }
    if ($testProcess.ExitCode -ne 0) { throw "Regression process failed with exit code $($testProcess.ExitCode)." }
}
finally {
    foreach ($process in $script:OwnedProcesses) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        catch { Write-Warning "Could not stop runner-owned process $($process.Id): $_" }
    }

    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }

    if ($createdRunDir -and (Test-Path -LiteralPath $runDir -PathType Container)) {
        $resolvedRun = [IO.Path]::GetFullPath($runDir).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedParent = [IO.Path]::GetFullPath([IO.Directory]::GetParent($resolvedRun).FullName).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $item = Get-Item -LiteralPath $resolvedRun -Force
        $safeParent = [string]::Equals($resolvedParent, $resolvedTemp, [StringComparison]::OrdinalIgnoreCase)
        $safeName = [string]::Equals($item.Name, $runName, [StringComparison]::Ordinal)
        $safeSentinel = (Test-Path -LiteralPath $sentinel -PathType Leaf) -and
            [string]::Equals([IO.File]::ReadAllText($sentinel), $token, [StringComparison]::Ordinal)
        $noRunnerProcess = @($script:OwnedProcesses | Where-Object { -not $_.HasExited }).Count -eq 0
        if ($safeParent -and $safeName -and $safeSentinel -and $noRunnerProcess -and
            -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolvedRun -Recurse -Force
            Write-Host 'Removed the runner-owned temporary directory after ownership checks.'
        }
        else {
            Write-Warning "Skipped cleanup because ownership/path/process checks failed: $resolvedRun"
        }
    }
}
