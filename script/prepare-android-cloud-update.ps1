[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ApkPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $root 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.txt.' }
$name = "BiliCinema-$version-android-universal.apk"
$source = (Resolve-Path -LiteralPath $ApkPath).Path
if ([IO.Path]::GetFileName($source) -ne $name) { throw "Expected APK filename $name." }
$length = (Get-Item -LiteralPath $source).Length
if ($length -lt 1000 -or $length -gt 25MB) { throw 'APK must fit the Cloudflare static asset size limit (25 MiB).' }
$parts = $version.Split('.') | ForEach-Object { [int]$_ }
$code = $parts[0] * 1000000 + $parts[1] * 1000 + $parts[2]
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$downloads = Join-Path $root 'cloud/public/downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
& node (Join-Path $root 'cloud/scripts/restore-android-assets.mjs') --preserve-local
if ($LASTEXITCODE -ne 0) { throw 'Could not restore existing Cloudflare APK assets.' }
$history = @()
$existingManifest = Join-Path $downloads 'android-manifest.json'
if (Test-Path -LiteralPath $existingManifest) {
    $prior = Get-Content -LiteralPath $existingManifest -Raw | ConvertFrom-Json
    $entries = @($prior) + @($prior.history)
    foreach ($entry in $entries | Select-Object -First 2) {
        if ($entry.version -eq $version) {
            if ($entry.sha256 -ne $hash) { throw 'Published version cannot be replaced with different APK bytes.' }
            continue
        }
        if ($entry.filename -notmatch '^BiliCinema-\d+\.\d+\.\d+-android-universal\.apk$' -or $entry.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Prior manifest is invalid.' }
        $oldApk = Join-Path $downloads $entry.filename
        if (-not (Test-Path -LiteralPath $oldApk) -or (Get-Item -LiteralPath $oldApk).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $oldApk -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'Prior APK is missing or corrupt.' }
        $history += [ordered]@{ version = $entry.version; filename = $entry.filename; bytes = $entry.bytes; sha256 = $entry.sha256 }
    }
}
$target = Join-Path $downloads $name
$createdTarget = $false
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target).Length -ne $length -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Staged APK conflicts with this version.' }
} else {
    $temporaryApk = Join-Path $downloads ('.staging-' + [guid]::NewGuid().ToString('N') + '.apk')
    try {
        Copy-Item -LiteralPath $source -Destination $temporaryApk
        if ((Get-FileHash -LiteralPath $temporaryApk -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Staged APK hash mismatch.' }
        Move-Item -LiteralPath $temporaryApk -Destination $target
        $createdTarget = $true
    } finally { if (Test-Path -LiteralPath $temporaryApk) { Remove-Item -LiteralPath $temporaryApk } }
}
$manifest = [ordered]@{
    version = $version
    versionCode = $code
    applicationId = 'com.leonz03.bilicinema'
    filename = $name
    bytes = $length
    sha256 = $hash
    history = $history
}
$temporaryManifest = Join-Path $downloads ('.staging-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryManifest -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryManifest -Destination $existingManifest -Force
} catch {
    if ($createdTarget) { Remove-Item -LiteralPath $target }
    throw
} finally { if (Test-Path -LiteralPath $temporaryManifest) { Remove-Item -LiteralPath $temporaryManifest } }
Write-Output "Staged $name and android-manifest.json for Cloudflare deployment."
Write-Output 'From cloud/: npm test; npx wrangler deploy --dry-run; npx wrangler deploy'
