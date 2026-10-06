[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '../DownKyi.sln'
# dotnet format's analyzer pass ignores diagnostic suppressors, including xUnit's
# public-test-class and test-await rules (dotnet/sdk#51364). The strict build owns
# analyzer validation with the real compiler and suppressors; formatting still
# checks all files, whitespace, imports and code style without changing rules.
foreach ($kind in @('whitespace', 'style')) {
    & dotnet format $kind $solution --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "Formatting ($kind) failed with exit code $LASTEXITCODE." }
}
