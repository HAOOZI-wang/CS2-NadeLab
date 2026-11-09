param(
    [Parameter(Mandatory=$true)][string]$Cs2Root,
    [string]$OutputDirectory = ""
)

# Build only. The addon gets a dedicated name and never overrides Valve files.
# Install Workshop Tools through CS2 game settings before running this script.
$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Cs2Root = [IO.Path]::GetFullPath($Cs2Root).TrimEnd('\','/')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/panel-ui" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $Cs2Root "game/bin/win64/resourcecompiler.exe"
$gameDir = Join-Path $Cs2Root "game/csgo"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Workshop Tools are required: $compiler is missing. In CS2 settings enable Install Counter-Strike Workshop Tools, exit, and let Steam finish downloading."
}
if (-not (Test-Path -LiteralPath (Join-Path $gameDir "gameinfo.gi") -PathType Leaf)) { throw "Not a CS2 install: $Cs2Root" }
$contentAddon = Join-Path $Cs2Root "content/csgo_addons/nadelab_panel_ui"
$gameAddon = Join-Path $Cs2Root "game/csgo_addons/nadelab_panel_ui"
$sources = @("panorama/styles/custom_game/nadelab_panel.css", "panorama/layout/custom_game/nadelab_panel.xml", "maps/scripts/nadelab_panel_bridge.js")
$compiled = @("panorama/styles/custom_game/nadelab_panel.vcss_c", "panorama/layout/custom_game/nadelab_panel.vxml_c", "maps/scripts/nadelab_panel_bridge.vjs_c")
# Read the identifier from the static client label, never from plugin/server
# state. This records what was compiled without claiming what a client loaded.
$layoutSource = [xml](Get-Content -LiteralPath (Join-Path $projectRoot "ui/panorama/layout/custom_game/nadelab_panel.xml") -Raw)
$revisionLabels = @($layoutSource.SelectNodes("//Label[@id='NadeLabResourceRevision']"))
if ($revisionLabels.Count -ne 1 -or $revisionLabels[0].text -notmatch '^UI ([A-Za-z0-9._-]+)$') { throw "Missing or invalid baked client UI resource revision." }
$clientResourceRevision = $Matches[1]
foreach ($relative in $sources) {
    $source = Join-Path $projectRoot "ui/$relative"
    $destination = Join-Path $contentAddon $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source: $source" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $contentAddon "panorama/preprocessor_config.txt"), '"PanzipCfg" { "BlockDefs" { } }', $utf8)
[IO.File]::WriteAllText((Join-Path $contentAddon "addoninfo.txt"), @'
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
{
    IsPlayable = false
}
'@, $utf8)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildStarted = [DateTime]::UtcNow
$log = Join-Path $OutputDirectory "resourcecompiler.log"
# The explicit game context also works when the dedicated addon has never been
# opened in Hammer. Compile style first so the layout dependency already exists.
foreach ($relative in $sources) {
    & $compiler -game $gameDir -i (Join-Path $contentAddon $relative) -f -nop4 -v 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { throw "resourcecompiler failed ($LASTEXITCODE); see $log" }
}
$files = @()
for ($i=0; $i -lt $compiled.Count; $i++) {
    $relative = $compiled[$i]
    $built = Join-Path $gameAddon $relative
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Compiler produced no $built" }
    $item = Get-Item -LiteralPath $built
    if ($item.Length -le 32 -or $item.LastWriteTimeUtc -lt $buildStarted.AddSeconds(-2)) { throw "Compiled output is empty or stale: $built" }
    $staged = Join-Path $OutputDirectory $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $staged) | Out-Null
    Copy-Item -LiteralPath $built -Destination $staged -Force
    $files += [ordered]@{ path=$relative; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash; source=$sources[$i]; sourceSha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot "ui/$($sources[$i])") -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{ schema=1; builtAtUtc=[DateTime]::UtcNow.ToString("o"); clientResourceRevision=$clientResourceRevision; layout="panorama/layout/custom_game/nadelab_panel.vxml_c"; files=$files }
[IO.File]::WriteAllText((Join-Path $OutputDirectory "manifest.json"), ($manifest | ConvertTo-Json -Depth 5), $utf8)
Write-Output "Built NadeLab panel resources: $OutputDirectory"
Write-Output "No base client files or launch settings were modified. Use install-panel-ui.ps1 for local installation."
