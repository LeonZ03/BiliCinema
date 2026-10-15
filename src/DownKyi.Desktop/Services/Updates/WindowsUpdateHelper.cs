namespace DownKyi.Services.Updates;

internal static class WindowsUpdateHelper
{
    // Only the fixed executable-name validation pattern is inserted into the script.
    // Paths are journal data; replacement uses atomic same-volume File.Replace.
    internal static readonly string Script = Template.Replace(
        "__EXECUTABLE_NAME_PATTERN__", WindowsUpdateService.PublishedExecutableNamePattern,
        System.StringComparison.Ordinal);
    private const string Template = """
        param([Parameter(Mandatory=$true)][string]$Journal)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $Journal -Raw | ConvertFrom-Json
        function Save-Phase([string]$phase) {
            $state.Phase = $phase
            $json = $state | ConvertTo-Json -Depth 4
            [System.IO.File]::WriteAllText($Journal, $json)
        }
        $replaced = $false
        $step = 'validate_paths'
        $failureCode = 'helper_failed'
        try {
            $directory = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($Journal))
            $target = [System.IO.Path]::GetFullPath($state.TargetPath)
            $targetDirectory = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($target))
            if ([System.IO.Path]::GetFileName($target) -notmatch '__EXECUTABLE_NAME_PATTERN__' -or
                [System.IO.Path]::GetDirectoryName($directory) -ne $targetDirectory -or
                $state.PackagePath -ne (Join-Path $directory 'BiliCinema.exe') -or
                $state.BackupPath -ne (Join-Path $directory 'previous.exe') -or
                $state.HelperReadyPath -ne (Join-Path $directory 'helper-ready')) {
                $failureCode = 'invalid_paths'
                throw 'Update paths are invalid.'
            }
            [System.IO.File]::WriteAllText($state.HelperReadyPath, 'ready')
            $step = 'await_parent'
            $parent = Get-Process -Id $state.ParentProcessId -ErrorAction SilentlyContinue
            if ($null -ne $parent -and -not $parent.WaitForExit(120000)) {
                $failureCode = 'shutdown_timeout'
                throw 'Application did not finish shutting down.'
            }
            $step = 'read_commit'
            $state = Get-Content -LiteralPath $Journal -Raw | ConvertFrom-Json
            if ($state.Phase -ne 'ready') { $failureCode = 'uncommitted'; throw 'Update was not committed.' }
            $step = 'verify_package'
            # Use the runtime directly. A Windows PowerShell child may inherit a
            # PowerShell 7 module path where the Get-FileHash function is unavailable.
            $package = [System.IO.File]::OpenRead($state.PackagePath)
            $sha256 = $null
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                $digest = [BitConverter]::ToString($sha256.ComputeHash($package)).Replace('-', '')
            }
            finally {
                if ($null -ne $sha256) { $sha256.Dispose() }
                $package.Dispose()
            }
            if ($digest -ne $state.ExpectedSha256) { $failureCode = 'checksum_mismatch'; throw 'Update checksum mismatch.' }
            $step = 'record_replacing'
            Save-Phase 'replacing'
            $step = 'replace_executable'
            [System.IO.File]::Replace($state.PackagePath, $target, $state.BackupPath)
            $replaced = $true
            $step = 'record_restart'
            Save-Phase 'restarting'
            $step = 'launch_updated'
            $app = Start-Process -FilePath $target -WorkingDirectory $targetDirectory -WindowStyle Normal -PassThru
            # Keep the process handle available even if the app closes immediately.
            $null = $app.Handle
            $step = 'observe_updated'
            if ($app.WaitForExit(10000) -and $app.ExitCode -ne 0) {
                $failureCode = 'startup_failed'
                throw 'Updated application failed during startup.'
            }
            Save-Phase 'completed'
        }
        catch {
            $failure = $_.Exception.GetBaseException()
            $state.FailureStep = $step
            $state.FailureCode = $failureCode
            $state.FailureType = $failure.GetType().FullName
            $state.FailureHResult = $failure.HResult
            [Console]::Error.WriteLine(('Update helper failure: step={0}; code={1}; type={2}; hresult={3}' -f $step, $failureCode, $state.FailureType, $state.FailureHResult))
            if ($replaced -and (Test-Path -LiteralPath $state.BackupPath)) {
                $restored = $false
                try {
                    [System.IO.File]::Replace($state.BackupPath, $state.TargetPath, $state.PackagePath)
                    $restored = $true
                    Save-Phase 'rolled-back'
                    Start-Process -FilePath $state.TargetPath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($state.TargetPath)) -WindowStyle Normal
                }
                catch {
                    $recovery = $_.Exception.GetBaseException()
                    $state.RecoveryFailureCode = if ($restored) { 'restart_previous_failed' } else { 'rollback_failed' }
                    $state.RecoveryFailureType = $recovery.GetType().FullName
                    $state.RecoveryFailureHResult = $recovery.HResult
                    if ($restored) { Save-Phase 'rolled-back' } else { Save-Phase 'rollback-failed' }
                    [Console]::Error.WriteLine(('Update recovery failure: code={0}; type={1}; hresult={2}' -f $state.RecoveryFailureCode, $state.RecoveryFailureType, $state.RecoveryFailureHResult))
                }
            }
            else {
                Save-Phase 'failed'
            }
            # Keep the recovery journal and old executable for diagnosis/manual recovery.
            exit 1
        }
        """;
}
