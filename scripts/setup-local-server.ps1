[CmdletBinding()]
param([string]$GameInstallation = 'D:\apps\steam\steamapps\common\Counter-Strike Global Offensive')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSourceGame = Join-Path (Resolve-Path -LiteralPath $GameInstallation).Path 'game'
$taskServerRoot = Join-Path $taskRoot 'server'
$taskServerGame = Join-Path $taskServerRoot 'game/csgo'
$taskExistingPlugin = Join-Path $taskServerGame 'addons/counterstrikesharp/plugins/NadeLab'
$taskPreservePlugin = Test-Path -LiteralPath (Join-Path $taskExistingPlugin 'NadeLab.dll') -PathType Leaf
if (Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $taskServerRoot 'game/bin/win64/cs2.exe') }) {
    throw 'Stop the project-owned server before refreshing its installation.'
}
if (-not (Test-Path -LiteralPath (Join-Path $taskSourceGame 'bin/win64/cs2.exe'))) { throw 'CS2 executable not found.' }
foreach ($taskDependency in @('metamod','css-runtime')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot ".tools/downloads/$taskDependency/addons"))) { throw "Missing extracted dependency: $taskDependency" }
    if (Test-Path -LiteralPath (Join-Path $taskRoot ".tools/downloads/$taskDependency/addons/counterstrikesharp/plugins/NadeLab")) { throw 'Dependency cache contains a NadeLab plugin; refusing to overwrite the training plugin or player data.' }
}
New-Item -ItemType Directory -Path $taskServerGame,(Join-Path $taskServerGame 'cfg'),(Join-Path $taskServerRoot 'logs') -Force | Out-Null
# Some server systems look up pak01 relative to MOD rather than GAME search paths.
# Keep packs available locally through read-only-in-use hard links (same NTFS volume).
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'csgo') -File -Filter 'pak01*.vpk' | ForEach-Object {
    $taskPakLink = Join-Path $taskServerGame $_.Name
    if (Test-Path -LiteralPath $taskPakLink) {
        $taskCurrentPak = Get-Item -LiteralPath $taskPakLink
        if ($taskCurrentPak.Length -ne $_.Length -or $taskCurrentPak.LastWriteTimeUtc -ne $_.LastWriteTimeUtc) {
            $taskResolvedPak = [System.IO.Path]::GetFullPath($taskPakLink)
            if (-not $taskResolvedPak.StartsWith([System.IO.Path]::GetFullPath($taskServerGame)+[System.IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe pack link path.' }
            Remove-Item -LiteralPath $taskResolvedPak
        }
    }
    if (-not (Test-Path -LiteralPath $taskPakLink)) {
        New-Item -ItemType HardLink -Path $taskPakLink -Target $_.FullName | Out-Null
    }
}
# CS2 selects its game root from its executable location; -game alone is insufficient.
$taskServerBin = Join-Path $taskServerRoot 'game/bin/win64'
New-Item -ItemType Directory -Path $taskServerBin -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'bin/win64') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskServerBin -Force
}
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'bin') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskServerRoot 'game/bin') -Force
}
# Binaries and core files are copied; large packs use read-only-in-use links/search paths.
Copy-Item -LiteralPath (Join-Path $taskSourceGame 'csgo/bin') -Destination $taskServerGame -Recurse -Force
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'csgo/cfg') -File | ForEach-Object {
    if (-not (Test-Path -LiteralPath (Join-Path $taskServerGame ('cfg/'+$_.Name)))) {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskServerGame 'cfg')
    }
}
foreach ($taskName in @('steam.inf','gameinfo_branchspecific.gi')) {
    Copy-Item -LiteralPath (Join-Path $taskSourceGame "csgo/$taskName") -Destination $taskServerGame -Force
}
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'csgo') -File -Filter 'shaders_pc*.vpk' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskServerGame -Force
}
$taskMaps = Join-Path $taskServerGame 'maps'
New-Item -ItemType Directory -Path $taskMaps -Force | Out-Null
$taskMapRoot = [System.IO.Path]::GetFullPath($taskMaps)+[System.IO.Path]::DirectorySeparatorChar
$taskMapNames = @('de_mirage','de_dust2','de_inferno','de_nuke','de_ancient','de_anubis','de_cache','de_overpass','de_train','de_vertigo')
# Steam may replace an inode during an update. Refresh only our bounded link
# names, never the source, and never modify VPK contents through a hard link.
foreach ($taskMapName in $taskMapNames) {
    foreach ($taskMapSuffix in @('.vpk','_vanity.vpk')) {
        $taskMapSource = Join-Path $taskSourceGame "csgo/maps/$taskMapName$taskMapSuffix"
        $taskMapTarget = [System.IO.Path]::GetFullPath((Join-Path $taskMaps "$taskMapName$taskMapSuffix"))
        if (-not $taskMapTarget.StartsWith($taskMapRoot,[System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe map link path.' }
        $taskSourceExists = Test-Path -LiteralPath $taskMapSource -PathType Leaf
        if (Test-Path -LiteralPath $taskMapTarget) {
            if (-not (Test-Path -LiteralPath $taskMapTarget -PathType Leaf)) { throw "Map target is not a file: $taskMapTarget" }
            $taskReplaceMap = -not $taskSourceExists
            if ($taskSourceExists) {
                $taskSourceInfo = Get-Item -LiteralPath $taskMapSource
                $taskTargetInfo = Get-Item -LiteralPath $taskMapTarget
                $taskReplaceMap = $taskSourceInfo.Length -ne $taskTargetInfo.Length -or $taskSourceInfo.LastWriteTimeUtc -ne $taskTargetInfo.LastWriteTimeUtc
            }
            if ($taskReplaceMap) { Remove-Item -LiteralPath $taskMapTarget }
        }
        if ($taskSourceExists -and -not (Test-Path -LiteralPath $taskMapTarget)) {
            try { New-Item -ItemType HardLink -Path $taskMapTarget -Target $taskMapSource -ErrorAction Stop | Out-Null }
            catch {
                # A different installation volume cannot share NTFS hard links.
                # Never overwrite through a link if creation partially succeeded.
                if (Test-Path -LiteralPath $taskMapTarget) {
                    if (-not (Test-Path -LiteralPath $taskMapTarget -PathType Leaf)) { throw 'Unexpected map link target.' }
                    Remove-Item -LiteralPath $taskMapTarget
                }
                Copy-Item -LiteralPath $taskMapSource -Destination $taskMapTarget -Force
            }
        }
    }
}
$taskPrefabs = Join-Path $taskMaps 'prefabs'
New-Item -ItemType Directory -Path $taskPrefabs -Force | Out-Null
# Copy the shared prefab tree rather than guessing map directory names
# (Overpass uses "overpass", not "de_overpass").
$taskSourcePrefabs = Join-Path $taskSourceGame 'csgo/maps/prefabs'
if (Test-Path -LiteralPath $taskSourcePrefabs -PathType Container) {
    Get-ChildItem -LiteralPath $taskSourcePrefabs -File -Recurse | ForEach-Object {
        $taskPrefabRelative = [System.IO.Path]::GetRelativePath($taskSourcePrefabs,$_.FullName)
        $taskPrefabTarget = [System.IO.Path]::GetFullPath((Join-Path $taskPrefabs $taskPrefabRelative))
        if (-not $taskPrefabTarget.StartsWith($taskMapRoot,[System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe prefab copy path.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskPrefabTarget) -Force | Out-Null
        $taskCopyPrefab = $true
        if (Test-Path -LiteralPath $taskPrefabTarget -PathType Leaf) {
            $taskExistingPrefab = Get-Item -LiteralPath $taskPrefabTarget
            $taskCopyPrefab = $_.Length -ne $taskExistingPrefab.Length -or $_.LastWriteTimeUtc -ne $taskExistingPrefab.LastWriteTimeUtc
        }
        if ($taskCopyPrefab) {
            # Detach any old link before copying; never write Steam-backed bytes.
            if (Test-Path -LiteralPath $taskPrefabTarget) {
                if (-not (Test-Path -LiteralPath $taskPrefabTarget -PathType Leaf)) { throw 'Unexpected prefab target.' }
                Remove-Item -LiteralPath $taskPrefabTarget
            }
            Copy-Item -LiteralPath $_.FullName -Destination $taskPrefabTarget -Force
        }
    }
}
$taskCore = Join-Path $taskServerRoot 'game/core'
New-Item -ItemType Directory -Path $taskCore -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskSourceGame 'core') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskCore -Force
}
$taskInfo = Get-Content -LiteralPath (Join-Path $taskSourceGame 'csgo/gameinfo.gi') -Raw
$taskModPath = 'csgo'
$taskAssetPath = [System.IO.Path]::GetRelativePath((Join-Path $taskServerRoot 'game'),$taskSourceGame).Replace('\','/')
$taskSearchPaths = @"
SearchPaths
        {
            Game_LowViolence "$taskAssetPath/csgo_lv"
            Game $taskModPath/addons/metamod
            Game $taskModPath
            Game "$taskAssetPath/csgo"
            Game "$taskAssetPath/core"
            Mod "$taskModPath"
            AddonRoot "csgo_addons"
            OfficialAddonRoot "$taskAssetPath/csgo_community_addons"
        }
"@
$taskInfo = [regex]::Replace($taskInfo,'SearchPaths\s*\{[^}]*\}',[System.Text.RegularExpressions.MatchEvaluator]{ param($match) $taskSearchPaths },1)
Set-Content -LiteralPath (Join-Path $taskServerGame 'gameinfo.gi') -Value $taskInfo -Encoding utf8
foreach ($taskDependency in @('metamod','css-runtime')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot ".tools/downloads/$taskDependency/addons") -Destination $taskServerGame -Recurse -Force
}
if (Test-Path -LiteralPath (Join-Path $taskRoot '.tools/downloads/gamedata-current.json')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/downloads/gamedata-current.json') -Destination (Join-Path $taskServerGame 'addons/counterstrikesharp/gamedata/gamedata.json') -Force
}
if ($taskPreservePlugin) {
    Write-Host 'Preserved the existing NadeLab plugin and player data during game resource refresh.'
} else {
    # Fresh instances install only a verified current build. An old extracted
    # package must never replace an updated plugin during a Steam refresh.
    & (Join-Path $PSScriptRoot 'install-plugin.ps1') -ServerRoot $taskServerRoot
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'configs/nadelab_practice.cfg') -Destination (Join-Path $taskServerGame 'cfg') -Force
Set-Content -LiteralPath (Join-Path $taskServerGame 'cfg/gamemode_competitive_server.cfg') -Value 'exec nadelab_practice' -Encoding ascii
$taskRconFile = Join-Path $taskServerRoot 'rcon.local'
if (-not (Test-Path -LiteralPath $taskRconFile)) {
    $taskRconKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    Set-Content -LiteralPath $taskRconFile -Value $taskRconKey -NoNewline -Encoding ascii
} else { $taskRconKey = Get-Content -LiteralPath $taskRconFile -Raw }
@"
hostname "NadeLab Local Training Lab"
sv_lan 1
sv_cheats 1
sv_hibernate_when_empty 0
rcon_password "$taskRconKey"
exec nadelab_practice
"@ | Set-Content -LiteralPath (Join-Path $taskServerGame 'cfg/server.cfg') -Encoding utf8
@{Executable=(Join-Path $taskServerBin 'cs2.exe');GameDirectory=$taskServerGame;Port=27025;SourceGameDirectory=$taskSourceGame;PowerShellExecutable=(Get-Process -Id $PID).Path} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskServerRoot 'instance.json') -Encoding utf8
Write-Host "Local training instance: $taskServerRoot"
Write-Host 'The Steam installation is used as a read-only asset source; its gameinfo and addons are untouched.'
