[CmdletBinding()]
param([switch]$NoPackage, [switch]$SourceOnly)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/env.ps1"
$env:DOTNET_SYSTEM_GLOBALIZATION_USENLS = '1'
$taskPython = (Get-Command python -ErrorAction Stop).Source
$taskSourceFingerprint = & $taskPython -s "$PSScriptRoot/package-release.py" --fingerprint
if ($LASTEXITCODE -ne 0) { throw 'Build source fingerprint failed.' }
& $taskDotnet build "$taskRoot/NadeLab.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$taskVersion = ([xml](Get-Content -LiteralPath "$taskRoot/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
foreach ($taskBinary in @('NadeLab.dll','NadeLab.Core.dll')) {
    $taskBinaryPath = Join-Path $taskRoot "src/NadeLab.Plugin/bin/Release/net10.0/$taskBinary"
    $taskProductVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($taskBinaryPath).ProductVersion
    if ($taskProductVersion -ne $taskVersion) { throw "Release binary version mismatch: $taskBinary ($taskProductVersion, expected $taskVersion)." }
}
& $taskDotnet run --project "$taskRoot/tests/NadeLab.Tests" -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
& $taskDotnet run --project "$taskRoot/tests/NadeLab.Lifecycle.Tests" -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle tests failed.' }
$taskPreviousSourceMode = $env:NADELAB_SOURCE_ONLY_TESTS
try {
    # Full builds override any inherited source-only flag. The one real UI
    # artifact test must not be skipped when producing release evidence.
    $env:NADELAB_SOURCE_ONLY_TESTS = if ($SourceOnly) { '1' } else { '0' }
    & $taskPython -s -m unittest discover -s "$taskRoot/tests/python" -v
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed.' }
} finally {
    $env:NADELAB_SOURCE_ONLY_TESTS = $taskPreviousSourceMode
}
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/release/Test-ReleaseTools.ps1" -OutputDirectory "$taskRoot/artifacts/release-tests"
if ($LASTEXITCODE -ne 0) { throw 'Windows launcher tests failed.' }
if ($SourceOnly) {
    Write-Output 'Source checks passed. One real compiled-UI artifact check was skipped; no release build record, package, installation or game test was produced.'
    return
}
& $taskPython -s "$PSScriptRoot/package-release.py" --record-build --expected-source-fingerprint $taskSourceFingerprint --verified-version $taskVersion
if ($LASTEXITCODE -ne 0) { throw 'Release build evidence failed; source or output changed during verification.' }
if (-not $NoPackage) {
    & $taskPython -s "$PSScriptRoot/package-release.py"
    if ($LASTEXITCODE -ne 0) { throw 'Release packaging or integrity checks failed.' }
}
