[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$androidRoot = Join-Path $repoRoot 'android'
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'version.txt must contain a three-part stable version.' }
if (-not $env:ANDROID_HOME -and -not $env:ANDROID_SDK_ROOT) {
    $localSdk = Join-Path $androidRoot '.tools/sdk'
    if (Test-Path -LiteralPath (Join-Path $localSdk 'platforms/android-35/android.jar')) { $env:ANDROID_HOME = $localSdk }
    else { throw 'Install Android SDK 35 and set ANDROID_HOME before building.' }
}
$signingNames = @('BILICINEMA_KEYSTORE', 'BILICINEMA_KEY_ALIAS', 'BILICINEMA_KEYSTORE_PASSWORD', 'BILICINEMA_KEY_PASSWORD')
$provided = @($signingNames | Where-Object { [Environment]::GetEnvironmentVariable($_) })
if ($provided.Count -gt 0 -and $provided.Count -ne 4) { throw 'Supply all four BILICINEMA_KEYSTORE / KEY_ALIAS / KEYSTORE_PASSWORD / KEY_PASSWORD variables.' }
if ($provided.Count -eq 0) {
    # Keep this local identity for every subsequent build. Never commit or regenerate it for a published app.
    $keyRoot = Join-Path $androidRoot '.keys'
    $identityFile = Join-Path $keyRoot 'release-identity.json'
    $keyFile = Join-Path $keyRoot 'bilicinema-release.jks'
    New-Item -ItemType Directory -Path $keyRoot -Force | Out-Null
    if (-not (Test-Path -LiteralPath $identityFile)) {
        if (Test-Path -LiteralPath $keyFile) { throw 'Release key exists without its credential file. Restore the matching credentials; do not replace the key.' }
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        @{ alias = 'bilicinema'; password = $password } | ConvertTo-Json | Set-Content -LiteralPath $identityFile -Encoding utf8
    }
    $identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json
    $env:BILICINEMA_KEYSTORE = $keyFile
    $env:BILICINEMA_KEY_ALIAS = $identity.alias
    $env:BILICINEMA_KEYSTORE_PASSWORD = $identity.password
    $env:BILICINEMA_KEY_PASSWORD = $identity.password
    if (-not (Test-Path -LiteralPath $keyFile)) {
        & (Join-Path $env:JAVA_HOME 'bin/keytool.exe') -genkeypair -keystore $keyFile -alias $identity.alias -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=BiliCinema Release, O=BiliCinema, C=CN' -storepass:env BILICINEMA_KEYSTORE_PASSWORD -keypass:env BILICINEMA_KEY_PASSWORD -storetype JKS
        if ($LASTEXITCODE -ne 0) { throw 'Release signing key creation failed.' }
    }
}
$localGradle = Join-Path $androidRoot '.tools/gradle-8.9/bin/gradle.bat'
$wrapper = if (Test-Path -LiteralPath $localGradle) { $localGradle } else { Join-Path $androidRoot 'gradlew.bat' }
if (-not (Test-Path -LiteralPath $wrapper)) { throw 'Gradle wrapper is missing.' }
$tasks = if ($SkipChecks) { @(':app:assembleRelease') } else { @(':app:testReleaseUnitTest', ':app:lintRelease', ':app:assembleRelease') }
& $wrapper -p $androidRoot --no-daemon @tasks
if ($LASTEXITCODE -ne 0) { throw 'Android build or verification failed.' }
$outputPath = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $repoRoot) }
    else { Join-Path $repoRoot "artifacts/releases/$version/android" }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$apkName = "BiliCinema-$version-android-universal.apk"
$apkPath = Join-Path $outputPath $apkName
Copy-Item -LiteralPath (Join-Path $androidRoot 'app/build/outputs/apk/release/app-release.apk') -Destination $apkPath
$digest = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $apkName" | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Built signed Android package: $apkName"
