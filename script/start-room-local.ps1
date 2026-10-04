[CmdletBinding()]
param([string]$ListenUrl = 'http://127.0.0.1:5077')

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$env:RoomServer__ListenUrl = $ListenUrl
dotnet run --project (Join-Path $repositoryRoot 'src\DownKyi.RoomServer\DownKyi.RoomServer.csproj') -c Release

exit $LASTEXITCODE
