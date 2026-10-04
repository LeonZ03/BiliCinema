[CmdletBinding()]
param(
    [string]$DataDir,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'DownKyi\DownKyi.csproj'
$application = Join-Path $repositoryRoot 'DownKyi\bin\Release\net10.0\DownKyi.exe'
$stampDirectory = Join-Path $repositoryRoot 'artifacts\watch-startup'
$stampPath = Join-Path $stampDirectory 'source-stamp.txt'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'

if ($DataDir) {
    $env:DOWNKYI_DATA_DIR = [IO.Path]::GetFullPath($DataDir)
}

function Get-SourceManifest([string]$directory) {
    foreach ($path in [IO.Directory]::EnumerateFiles($directory)) {
        $file = [IO.FileInfo]::new($path)
        '{0}|{1}|{2}' -f $file.FullName, $file.LastWriteTimeUtc.Ticks, $file.Length
    }

    foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
        $name = [IO.Path]::GetFileName($child)
        if ($name -notin @('bin', 'obj', '.git', '.vs')) {
            Get-SourceManifest $child
        }
    }
}

$projectDirectories = @(
    'DownKyi', 'DownKyi.Core',
    'src\DownKyi.Desktop', 'src\DownKyi.Application',
    'src\DownKyi.Domain', 'src\DownKyi.Infrastructure'
)
$manifest = @(
    foreach ($relativeDirectory in $projectDirectories) {
        Get-SourceManifest (Join-Path $repositoryRoot $relativeDirectory)
    }
    foreach ($relativeFile in @('Directory.Build.props', 'Directory.Build.targets',
                                'Directory.Packages.props', 'NuGet.Config', 'global.json',
                                'THIRD-PARTY-NOTICES.md')) {
        $path = Join-Path $repositoryRoot $relativeFile
        if ([IO.File]::Exists($path)) {
            $file = [IO.FileInfo]::new($path)
            '{0}|{1}|{2}' -f $file.FullName, $file.LastWriteTimeUtc.Ticks, $file.Length
        }
    }
) | Sort-Object
$sourceStamp = [string]::Join("`n", $manifest)
$currentBuild = [IO.File]::Exists($application) -and [IO.File]::Exists($stampPath) -and
    [IO.File]::ReadAllText($stampPath) -ceq $sourceStamp

if (-not $currentBuild) {
    $bundledDotnet = Join-Path $env:TEMP 'downkyi-dotnet\dotnet.exe'
    if ([IO.File]::Exists($bundledDotnet)) {
        $dotnet = $bundledDotnet
        $env:DOTNET_ROOT = Split-Path -Parent $bundledDotnet
        $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    } else {
        $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            Write-Error '.NET 10 SDK was not found. Install it and double-click Start-Watch.cmd again.'
            exit 1
        }
        $dotnet = $command.Source
    }

    $restoreInputs = @(
        foreach ($relativeDirectory in $projectDirectories) {
            Join-Path $repositoryRoot (Join-Path $relativeDirectory ([IO.Path]::GetFileName($relativeDirectory) + '.csproj'))
        }
        foreach ($relativeFile in @('Directory.Build.props', 'Directory.Build.targets',
                                    'Directory.Packages.props', 'NuGet.Config', 'global.json')) {
            Join-Path $repositoryRoot $relativeFile
        }
    ) | Where-Object { [IO.File]::Exists($_) }
    $latestRestoreInput = ($restoreInputs | ForEach-Object { [IO.File]::GetLastWriteTimeUtc($_) } |
        Sort-Object -Descending | Select-Object -First 1)
    $needsRestore = $false
    foreach ($relativeDirectory in $projectDirectories) {
        $assets = Join-Path $repositoryRoot (Join-Path $relativeDirectory 'obj\project.assets.json')
        if (-not [IO.File]::Exists($assets) -or
            [IO.File]::GetLastWriteTimeUtc($assets) -lt $latestRestoreInput) {
            $needsRestore = $true
            break
        }
    }

    Write-Host 'Source changed; updating BiliCinema watch mode...'
    $buildArguments = @('build', $project, '-c', 'Release', '--nologo', '--verbosity', 'quiet')
    if (-not $needsRestore) {
        $buildArguments += '--no-restore'
    }
    & $dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    [IO.Directory]::CreateDirectory($stampDirectory) | Out-Null
    [IO.File]::WriteAllText($stampPath, $sourceStamp)
}

if ($BuildOnly) {
    Write-Host 'BiliCinema watch mode is ready.'
    exit 0
}

Write-Host 'Opening BiliCinema watch mode...'
$watchProcess = Start-Process -FilePath $application -ArgumentList '--watch' -WorkingDirectory $repositoryRoot -PassThru
$watchProcess.WaitForExit()
exit $watchProcess.ExitCode
