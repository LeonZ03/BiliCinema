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
$requiredSdkVersion = (Get-Content (Join-Path $repositoryRoot 'global.json') -Raw |
    ConvertFrom-Json).sdk.version
$localSdkRoot = Join-Path $env:LOCALAPPDATA 'BiliCinema\dotnet'
$localDotnet = Join-Path $localSdkRoot 'dotnet.exe'

if ($DataDir) {
    $env:DOWNKYI_DATA_DIR = [IO.Path]::GetFullPath($DataDir)
}

function Test-RequiredSdk([string]$dotnetPath) {
    if (-not [IO.File]::Exists($dotnetPath)) {
        return $false
    }

    try {
        $detected = (& $dotnetPath --version 2>$null | Select-Object -Last 1)
        if ($LASTEXITCODE -ne 0) {
            return $false
        }

        $required = [Version]$requiredSdkVersion
        $available = [Version]$detected
        return $available.Major -eq $required.Major -and
            $available.Minor -eq $required.Minor -and
            $available.Build -ge $required.Build
    } catch {
        return $false
    }
}

function Use-DotNetRoot([string]$dotnetPath) {
    $dotnetRoot = Split-Path -Parent $dotnetPath
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}

function Get-RequiredSdk {
    if (Test-RequiredSdk $localDotnet) {
        Use-DotNetRoot $localDotnet
        return $localDotnet
    }

    $systemDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -ne $systemDotnet -and (Test-RequiredSdk $systemDotnet.Source)) {
        Use-DotNetRoot $systemDotnet.Source
        return $systemDotnet.Source
    }

    $previouslyBundledDotnet = Join-Path $env:TEMP 'downkyi-dotnet\dotnet.exe'
    if (Test-RequiredSdk $previouslyBundledDotnet) {
        Use-DotNetRoot $previouslyBundledDotnet
        return $previouslyBundledDotnet
    }

    [IO.Directory]::CreateDirectory($stampDirectory) | Out-Null
    $installerPath = Join-Path $stampDirectory 'dotnet-install.ps1'
    Write-Host "No compatible .NET SDK found. Downloading SDK $requiredSdkVersion for this user..."
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing `
            -OutFile $installerPath -ErrorAction Stop
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath `
            -Version $requiredSdkVersion -InstallDir $localSdkRoot -NoPath | Out-Host
        $installerExitCode = $LASTEXITCODE
        if ($installerExitCode -ne 0 -or -not (Test-RequiredSdk $localDotnet)) {
            throw "SDK installation did not complete (exit code $installerExitCode)."
        }
    } catch {
        Write-Host "Could not install .NET SDK $requiredSdkVersion automatically: $($_.Exception.Message)" `
            -ForegroundColor Red
        Write-Host 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.'
        exit 1
    }

    Use-DotNetRoot $localDotnet
    return $localDotnet
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
    $dotnet = Get-RequiredSdk

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

# A locally installed SDK also supplies the runtime when the cached build is reused.
if (Test-RequiredSdk $localDotnet) {
    Use-DotNetRoot $localDotnet
} else {
    $systemDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -ne $systemDotnet -and (Test-RequiredSdk $systemDotnet.Source)) {
        Use-DotNetRoot $systemDotnet.Source
    } else {
        $previouslyBundledDotnet = Join-Path $env:TEMP 'downkyi-dotnet\dotnet.exe'
        if (Test-RequiredSdk $previouslyBundledDotnet) {
            Use-DotNetRoot $previouslyBundledDotnet
        }
    }
}

if ($BuildOnly) {
    Write-Host 'BiliCinema watch mode is ready.'
    exit 0
}

Write-Host 'Opening BiliCinema watch mode...'
$watchProcess = Start-Process -FilePath $application -ArgumentList '--watch' -WorkingDirectory $repositoryRoot -PassThru
$watchProcess.WaitForExit()
exit $watchProcess.ExitCode
