[CmdletBinding()]
param(
    [string]$DataDir,
    [string]$MpvPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($DataDir) {
    $env:DOWNKYI_DATA_DIR = [IO.Path]::GetFullPath($DataDir)
}

if ($MpvPath) {
    $env:DOWNKYI_MPV_PATH = [IO.Path]::GetFullPath($MpvPath)
}

dotnet run --project (Join-Path $repositoryRoot 'DownKyi\DownKyi.csproj') -c Release -- --watch

exit $LASTEXITCODE
