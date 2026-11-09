[CmdletBinding()]
param([string]$KeysFile)
$ErrorActionPreference='Stop'
$inputProject=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release/NadeLab.Input.ps1')
$inputInstance=Get-Content -LiteralPath (Join-Path $inputProject 'server/instance.json') -Raw | ConvertFrom-Json
$inputClient=Join-Path $inputInstance.SourceGameDirectory 'bin/win64/cs2.exe'
if (-not $KeysFile) {
    $inputSteam=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
    $inputAccount=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction Stop).ActiveUser
    if (-not $inputAccount) { throw 'Steam 尚未登录，无法选择个人键位；登录后重新进入训练。' }
    $KeysFile=Join-Path $inputSteam "userdata/$inputAccount/730/local/cfg/cs2_user_keys_0_slot0.vcfg"
}
$null=Start-NlInputCompanion -GameContentRoot $inputInstance.GameDirectory -ClientExe $inputClient -KeysFile $KeysFile `
    -HelperDirectory (Join-Path $inputProject 'src/NadeLab.Input/bin/Release/net10.0') -LogDirectory (Join-Path $inputProject 'artifacts/input-logs')
