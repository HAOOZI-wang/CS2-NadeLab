[CmdletBinding()]
param([switch]$Restore, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSteam = Get-ItemProperty 'HKCU:/Software/Valve/Steam'
$taskSteamExe = $taskSteam.SteamExe
if (-not (Test-Path -LiteralPath $taskSteamExe)) { throw 'Steam executable not found.' }
$taskClient = Get-Process -Name cs2 -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path -notlike "$taskRoot/server/*" -and $_.Path -notlike "$taskRoot\server\*"
}
if ($taskClient) { throw 'Please exit the CS2 client first. The training server can remain running. No settings changed.' }
$taskUser = (Get-ItemProperty 'HKCU:/Software/Valve/Steam/ActiveProcess').ActiveUser
if (-not $taskUser) { throw 'No active Steam user. Open Steam first.' }
$taskCfg = Join-Path (Split-Path -Parent $taskSteamExe) "userdata/$taskUser/730/local/cfg"
$taskBackupRoot = Join-Path $taskRoot 'artifacts/client-settings'
$taskManifest = Join-Path $taskBackupRoot 'last-profile.json'
if ($CheckOnly) {
    Write-Output "Ready: fullscreen 1920x1440, fps_max 120, fps_max_ui 60. Restore=$Restore"
    return
}
if ($Restore) {
    $taskSaved = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($taskSaved.ClientCfg) -ne [IO.Path]::GetFullPath($taskCfg)) { throw 'Backup belongs to another Steam account.' }
    $taskBackup = [IO.Path]::GetFullPath($taskSaved.Backup)
    if (-not $taskBackup.StartsWith([IO.Path]::GetFullPath($taskBackupRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup path.' }
    foreach ($taskName in @('cs2_video.txt','cs2_machine_convars.vcfg')) {
        $taskOriginal = Join-Path $taskBackup $taskName
        if (Test-Path -LiteralPath $taskOriginal) { Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $taskCfg $taskName) -Force }
    }
    Start-Process -FilePath $taskSteamExe -ArgumentList @('-applaunch','730') -WindowStyle Hidden
    Write-Output 'Original client settings restored; launching CS2.'
} else {
    # Preserve the first baseline so repeated test launches do not overwrite it.
    if (-not (Test-Path -LiteralPath $taskManifest)) {
        $taskBackup = Join-Path $taskBackupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
        New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
        foreach ($taskName in @('cs2_video.txt','cs2_machine_convars.vcfg')) {
            $taskOriginal = Join-Path $taskCfg $taskName
            if (Test-Path -LiteralPath $taskOriginal) { Copy-Item -LiteralPath $taskOriginal -Destination $taskBackup }
        }
        @{ClientCfg=$taskCfg;Backup=$taskBackup} | ConvertTo-Json | Set-Content -LiteralPath $taskManifest -Encoding UTF8
    }
    Start-Process -FilePath $taskSteamExe -ArgumentList @('-applaunch','730','-fullscreen','-w','1920','-h','1440','+fps_max','120','+fps_max_ui','60') -WindowStyle Hidden
    Write-Output 'Launching fullscreen 1920x1440 at 120 FPS maximum. This is a diagnostic profile, not a confirmed fix.'
}
