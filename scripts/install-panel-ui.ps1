param(
    [Parameter(Mandatory=$true)][string]$Cs2Root,
    [string]$BuildDirectory = "",
    [string]$ServerGameDirectory = "",
    [switch]$StageForNextLaunch
)

# Installs only the three uniquely named NadeLab resources. Never modifies
# gameinfo.gi, Steam settings, bindings, or stock resources; no game restart.
$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Cs2Root = [IO.Path]::GetFullPath($Cs2Root).TrimEnd('\','/')
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $projectRoot "artifacts/panel-ui" }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$clientGame = Join-Path $Cs2Root "game/csgo"
if (-not (Test-Path -LiteralPath (Join-Path $clientGame "gameinfo.gi") -PathType Leaf)) { throw "Not a CS2 client root: $Cs2Root" }
$clientExe = Join-Path $Cs2Root "game/bin/win64/cs2.exe"
$processes = @(Get-Process -Name cs2 -ErrorAction SilentlyContinue)
foreach ($process in $processes) {
    $process.Refresh()
    if ($process.HasExited) { continue }
    if (-not $process.Path) { throw "Cannot determine whether CS2 process $($process.Id) is the client. Close the client or run with permission to read process paths." }
}
$running = $processes | Where-Object { -not $_.HasExited -and [string]::Equals($_.Path,$clientExe,[StringComparison]::OrdinalIgnoreCase) }
if ($running -and -not $StageForNextLaunch) { throw "Exit the CS2 client before installing: Panorama caches resources for the client session. Alternatively use -StageForNextLaunch to stage files without stopping the client. The server can stay running." }
if ($running) { Write-Warning "Staging for next launch. The running client may keep cached resources until CS2 is restarted; no process will be stopped." }
$manifestPath = Join-Path $BuildDirectory "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1) { throw "Unsupported panel manifest schema." }
$relativeFiles = @("panorama/styles/custom_game/nadelab_panel.vcss_c", "panorama/layout/custom_game/nadelab_panel.vxml_c", "maps/scripts/nadelab_panel_bridge.vjs_c")
foreach ($relative in $relativeFiles) {
    $entry = @($manifest.files | Where-Object path -EQ $relative)
    if ($entry.Count -ne 1) { throw "Missing or duplicate manifest entry: $relative" }
    $source = Join-Path $BuildDirectory $relative
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry[0].sha256) { throw "Compiled resource hash mismatch: $relative" }
}
$roots = @($clientGame)
if ($ServerGameDirectory) {
    $serverGame = [IO.Path]::GetFullPath($ServerGameDirectory).TrimEnd('\','/')
    if (-not (Test-Path -LiteralPath (Join-Path $serverGame "gameinfo.gi") -PathType Leaf)) { throw "Not a server game directory: $serverGame" }
    $roots += $serverGame
}
$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss-fff")
$backupRoot = Join-Path $projectRoot "artifacts/panel-ui/install-backups/$stamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$installed = @()
for ($rootIndex=0; $rootIndex -lt $roots.Count; $rootIndex++) {
    foreach ($relative in $relativeFiles) {
        $source = Join-Path $BuildDirectory $relative
        $destination = [IO.Path]::GetFullPath((Join-Path $roots[$rootIndex] $relative))
        if (-not $destination.StartsWith(($roots[$rootIndex] + [IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) { throw "Destination escaped the selected game directory." }
        $backup = $null
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $backup = Join-Path $backupRoot "$rootIndex/$relative"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup -Force
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw "Copy verification failed: $destination" }
        $installed += [ordered]@{ path=$destination; sha256=$hash; previousBackup=$backup }
    }
}
$receipt = [ordered]@{ installedAtUtc=[DateTime]::UtcNow.ToString("o"); files=$installed; note="File verification only; client loading and Chinese text rendering must be checked in game." }
$receiptPath = Join-Path $backupRoot "receipt.json"
[IO.File]::WriteAllText($receiptPath,($receipt | ConvertTo-Json -Depth 5),(New-Object System.Text.UTF8Encoding($false)))
Write-Output "NadeLab panel resources installed and hash-verified. Receipt: $receiptPath"
Write-Output "Launch the client normally for training. F1 opens the panel; installation is not proof of visible pixels."
