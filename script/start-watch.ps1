[CmdletBinding()]
param(
    [string]$DataDir
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($DataDir) {
    $env:DOWNKYI_DATA_DIR = [IO.Path]::GetFullPath($DataDir)
}

dotnet run --project (Join-Path $repositoryRoot 'DownKyi\DownKyi.csproj') -c Release -- --watch

exit $LASTEXITCODE
