[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$ServerRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskServer = (Resolve-Path -LiteralPath $ServerRoot -ErrorAction Stop).Path
$taskGame = Join-Path $taskServer 'game/csgo'
$taskServerExe = Join-Path $taskServer 'game/bin/win64/cs2.exe'
function Assert-DevelopmentServerStopped {
    foreach ($taskProcess in @(Get-Process -Name cs2 -ErrorAction SilentlyContinue)) {
        if (-not $taskProcess.Path) { throw 'Cannot identify a running CS2 process; stop the target training server before installing.' }
        if ($taskProcess.Path -eq $taskServerExe) { throw 'Stop this dedicated training server before installing its plugin.' }
    }
}
Assert-DevelopmentServerStopped
if (-not (Test-Path -LiteralPath (Join-Path $taskGame 'gameinfo.gi'))) { throw 'ServerRoot must contain game/csgo/gameinfo.gi.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskGame 'addons/counterstrikesharp/api/CounterStrikeSharp.API.dll'))) { throw 'Install CounterStrikeSharp 1.0.378 (with-runtime) and Metamod on this training server first.' }

# A resource refresh must never reinstall the retired artifacts/package tree.
# scripts/build.ps1 -NoPackage supplies the verified current build record.
$taskBuild = Join-Path $taskRoot 'src/NadeLab.Plugin/bin/Release/net10.0'
$taskRecordPath = Join-Path $taskRoot 'artifacts/release-build.json'
if (-not (Test-Path -LiteralPath $taskRecordPath -PathType Leaf)) { throw 'Verified build record missing. Run scripts/build.ps1 -NoPackage first.' }
$taskRecord = Get-Content -LiteralPath $taskRecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$taskPython = (Get-Command python -ErrorAction Stop).Source
$taskFingerprint = & $taskPython -s (Join-Path $taskRoot 'scripts/package-release.py') --fingerprint
if ($LASTEXITCODE -ne 0 -or $taskFingerprint -notmatch '^[0-9a-f]{64}$' -or
    $taskRecord.schema -ne 1 -or $taskRecord.version -ne $taskVersion -or
    $taskRecord.verifiedProductVersion -ne $taskVersion -or $taskRecord.sourceFingerprint -ne $taskFingerprint) {
    throw 'Build evidence is stale. Run scripts/build.ps1 -NoPackage before installing.'
}
$taskPayload = [ordered]@{}
$taskPluginRelative = 'addons/counterstrikesharp/plugins/NadeLab'
$taskBinaryNames = @('NadeLab.dll','NadeLab.Core.dll','NadeLab.deps.json')
if (@($taskRecord.files).Count -ne $taskBinaryNames.Count) { throw 'Build file inventory is invalid.' }
foreach ($taskName in $taskBinaryNames) {
    $taskMatches = @($taskRecord.files | Where-Object path -eq $taskName)
    $taskSource = Join-Path $taskBuild $taskName
    if ($taskMatches.Count -ne 1 -or -not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Missing verified build file: $taskName" }
    $taskInfo = Get-Item -LiteralPath $taskSource
    if (($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $taskInfo.Length -ne $taskMatches[0].bytes -or (Get-FileHash -LiteralPath $taskSource).Hash -ne $taskMatches[0].sha256) {
        throw "Build output changed after verification: $taskName"
    }
    $taskPayload[($taskPluginRelative+'/'+$taskName)] = $taskSource
}
$taskPanelRoot = Join-Path $taskRoot 'artifacts/panel-ui'
$taskPanelManifest = Get-Content -LiteralPath (Join-Path $taskPanelRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskUiSources = [ordered]@{
    'panorama/styles/custom_game/nadelab_panel.vcss_c' = 'panorama/styles/custom_game/nadelab_panel.css'
    'panorama/layout/custom_game/nadelab_panel.vxml_c' = 'panorama/layout/custom_game/nadelab_panel.xml'
    'maps/scripts/nadelab_panel_bridge.vjs_c' = 'maps/scripts/nadelab_panel_bridge.js'
}
if ($taskPanelManifest.schema -ne 1 -or @($taskPanelManifest.files).Count -ne 3) { throw 'Compiled UI manifest is invalid.' }
foreach ($taskUi in $taskUiSources.GetEnumerator()) {
    $taskMatches = @($taskPanelManifest.files | Where-Object path -eq $taskUi.Key)
    $taskCompiled = Join-Path $taskPanelRoot $taskUi.Key
    $taskUiSource = Join-Path (Join-Path $taskRoot 'ui') $taskUi.Value
    if ($taskMatches.Count -ne 1 -or $taskMatches[0].source -ne $taskUi.Value -or
        (Get-FileHash -LiteralPath $taskCompiled).Hash -ne $taskMatches[0].sha256 -or
        (Get-FileHash -LiteralPath $taskUiSource).Hash -ne $taskMatches[0].sourceSha256) {
        throw "Compiled UI or source changed: $($taskUi.Key). Rebuild the UI before installing."
    }
    $taskPayload[$taskUi.Key] = $taskCompiled
}
$taskPayload[($taskPluginRelative+'/physics.example.json')] = Join-Path $taskRoot 'configs/physics.example.json'
$taskPayload['cfg/nadelab_practice.cfg'] = Join-Path $taskRoot 'configs/nadelab_practice.cfg'
$taskPlan = @()
$taskGamePrefix = [IO.Path]::GetFullPath($taskGame)+[IO.Path]::DirectorySeparatorChar
foreach ($taskEntry in $taskPayload.GetEnumerator()) {
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskGame $taskEntry.Key))
    if (-not $taskDestination.StartsWith($taskGamePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installation destination.' }
    $taskPlan += [pscustomobject]@{ Relative=$taskEntry.Key; Source=$taskEntry.Value; Destination=$taskDestination; Hash=(Get-FileHash -LiteralPath $taskEntry.Value).Hash; Existed=(Test-Path -LiteralPath $taskDestination -PathType Leaf) }
}
if ($PSCmdlet.ShouldProcess($taskGame,'Install the verified current plugin and UI; back up changed files and preserve player data')) {
    Assert-DevelopmentServerStopped
    $taskBackup = Join-Path $taskRoot ('artifacts/backups/plugin-' + [guid]::NewGuid().ToString('N').Substring(0,16))
    New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
    foreach ($taskItem in $taskPlan) {
        if ($taskItem.Existed) {
            $taskSaved = Join-Path $taskBackup $taskItem.Relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskSaved) | Out-Null
            Copy-Item -LiteralPath $taskItem.Destination -Destination $taskSaved
        }
    }
    $taskWritten = @()
    try {
        Assert-DevelopmentServerStopped
        foreach ($taskItem in $taskPlan) {
            if ((Get-FileHash -LiteralPath $taskItem.Source).Hash -ne $taskItem.Hash) { throw "Source changed during installation: $($taskItem.Relative)" }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskItem.Destination) | Out-Null
            $taskWritten += $taskItem
            Copy-Item -LiteralPath $taskItem.Source -Destination $taskItem.Destination -Force
            if ((Get-FileHash -LiteralPath $taskItem.Destination).Hash -ne $taskItem.Hash) { throw "Installed file did not match: $($taskItem.Relative)" }
        }
    }
    catch {
        $taskFailure = $_
        foreach ($taskItem in $taskWritten) {
            if ($taskItem.Existed) { Copy-Item -LiteralPath (Join-Path $taskBackup $taskItem.Relative) -Destination $taskItem.Destination -Force }
            elseif (Test-Path -LiteralPath $taskItem.Destination -PathType Leaf) { Remove-Item -LiteralPath $taskItem.Destination }
        }
        throw $taskFailure
    }
    Write-Host "Backup: $taskBackup"
    Write-Host 'Installed verified current plugin and UI. Player data and custom physics settings were preserved.'
    Write-Host 'Restart this dedicated training server. Client UI and bindings are installed separately.'
}
