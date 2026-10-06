[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\BiliCinema-win-x64'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'assets\external-assets.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $PSScriptRoot 'downloads'
$stage = Join-Path $root 'src\DownKyi.Desktop\EmbeddedTools'
$publish = [IO.Path]::GetFullPath($OutputDirectory, $root)
[IO.Directory]::CreateDirectory($downloads) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory($publish) | Out-Null

function Assert-Hash([string]$path, [string]$expected) {
    if (-not [IO.File]::Exists($path)) { throw "Missing asset: $path" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) { throw "Checksum mismatch: $path" }
}

function Get-VerifiedFile([string]$url, [string]$path, [string]$expected) {
    if ([IO.File]::Exists($path)) {
        try { Assert-Hash $path $expected; return } catch { Remove-Item -LiteralPath $path -Force }
    }
    Write-Host "Downloading $([IO.Path]::GetFileName($path))..."
    Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing -TimeoutSec 900
    Assert-Hash $path $expected
}

function Extract-ZipFile([string]$archive, [string]$fileName, [string]$destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.Entries | Where-Object {
            $_.FullName.EndsWith('/' + $fileName, [StringComparison]::OrdinalIgnoreCase) -or
            $_.FullName.Equals($fileName, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $entry) { throw "$fileName is missing from $archive" }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    } finally { $zip.Dispose() }
}

$aria = $manifest.aria2.assets.'win-x64'
$ariaArchive = Join-Path $downloads 'aria2-x64.zip'
Get-VerifiedFile $aria.url $ariaArchive $aria.sha256
$ariaBinary = Join-Path $root 'DownKyi.Core\Binary\win-x64\aria2\aria2c.exe'
Extract-ZipFile $ariaArchive 'aria2c.exe' $ariaBinary
Assert-Hash $ariaBinary $aria.binarySha256
[IO.File]::WriteAllText($ariaBinary + '.sha256', $aria.binarySha256)

$ffmpeg = $manifest.ffmpeg.assets.'win-x64'
$ffmpegArchive = Join-Path $downloads 'ffmpeg-x64.zip'
Get-VerifiedFile $ffmpeg.url $ffmpegArchive $ffmpeg.sha256
$ffmpegRoot = Join-Path $root 'DownKyi.Core\Binary\win-x64\ffmpeg'
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt')) {
    $binary = Join-Path $ffmpegRoot $name
    Extract-ZipFile $ffmpegArchive $name $binary
}

$cloudflaredUrl = 'https://github.com/cloudflare/cloudflared/releases/download/2026.9.0/cloudflared-windows-amd64.exe'
$cloudflaredHash = '547057326266f0e1c7d50d102dbd22ff283d740c055bd61e94f10e2c606f89af'
$cloudflared = Join-Path $downloads 'cloudflared-windows-amd64.exe'
Get-VerifiedFile $cloudflaredUrl $cloudflared $cloudflaredHash

$sources = @{
    'aria2c.exe' = $ariaBinary
    'ffmpeg.exe' = (Join-Path $ffmpegRoot 'ffmpeg.exe')
    'ffprobe.exe' = (Join-Path $ffmpegRoot 'ffprobe.exe')
    'ffmpeg-LICENSE.txt' = (Join-Path $ffmpegRoot 'LICENSE.txt')
    'cloudflared.exe' = $cloudflared
}
foreach ($name in $sources.Keys) {
    $target = Join-Path $stage $name
    Copy-Item -LiteralPath $sources[$name] -Destination $target -Force
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($target + '.sha256', $hash)
}

$sdkCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'BiliCinema\dotnet\dotnet.exe'),
    (Join-Path $env:TEMP 'downkyi-dotnet\dotnet.exe')
)
$dotnet = $sdkCandidates | Where-Object { [IO.File]::Exists($_) } | Select-Object -First 1
if (-not $dotnet) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    $dotnet = $command.Source
}
if (-not $dotnet -or (& $dotnet --version) -notmatch '^10\.') {
    throw 'Building requires the .NET 10 SDK. The resulting EXE does not require an SDK.'
}

$env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($dotnet)
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'DownKyi\DownKyi.csproj'
$publishArgs = @(
    'publish', $project, '-c', 'BiliCinema', '-r', 'win-x64',
    '--self-contained', 'true', '--no-restore',
    '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=false',
    '-p:PublishTrimmed=false', '-p:DebugType=none', '-p:DebugSymbols=false',
    '-o', $publish, '-v', 'q'
)
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Restoring .NET packages and retrying...'
    & $dotnet restore $project -r win-x64 -p:Configuration=BiliCinema `
        -p:PublishSingleFile=true -v q
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE" }
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
}

$exe = Join-Path $publish 'BiliCinema.exe'
if (-not [IO.File]::Exists($exe)) { throw 'The single executable was not produced.' }
foreach ($name in @('appsettings.json', 'appsettings.Example.json',
                     'libHarfBuzzSharp.pdb', 'libSkiaSharp.pdb')) {
    $loose = Join-Path $publish $name
    if ([IO.File]::Exists($loose)) { Remove-Item -LiteralPath $loose -Force }
}
$extra = Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -ne 'BiliCinema.exe' }
if ($extra) { throw "Unexpected loose publish files: $($extra.Name -join ', ')" }
Write-Host "Ready: $exe"
