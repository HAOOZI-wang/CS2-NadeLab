function Start-NlInputCompanion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GameContentRoot,[Parameter(Mandatory)][string]$ClientExe,
        [Parameter(Mandatory)][string]$KeysFile,[Parameter(Mandatory)][string]$HelperDirectory,
        [Parameter(Mandatory)][string]$LogDirectory)
    $inputRuntime=Join-Path $GameContentRoot 'addons/counterstrikesharp/dotnet/dotnet.exe'
    $inputDll=Join-Path $HelperDirectory 'NadeLab.Input.dll'
    foreach ($inputPath in @($inputRuntime,$inputDll,$ClientExe,$KeysFile)) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "输入模块缺少文件：$inputPath" }
        if ($inputPath -match '["\r\n]') { throw '输入模块路径包含无效字符。' }
    }
    if ($KeysFile -notmatch '[\\/]userdata[\\/](\d+)[\\/]730[\\/]') { throw '无法确认个人键位对应的 Steam 账号，输入模块未启动。' }
    $inputSteam=([UInt64]76561197960265728+[UInt64]$Matches[1]).ToString()
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $inputStamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $inputArguments=@($inputDll,'--game',$GameContentRoot,'--client',$ClientExe,'--keys',$KeysFile,'--steam',$inputSteam)
    $inputQuoted=@($inputArguments | ForEach-Object {'"'+$_+'"'})
    $inputProcess=Start-Process -FilePath $inputRuntime -ArgumentList $inputQuoted -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $LogDirectory "$inputStamp-output.log") `
        -RedirectStandardError (Join-Path $LogDirectory "$inputStamp-error.log")
    Write-Host '训练输入模块已启动：F1 菜单，滚轮/方向键选择；个人键位文件保持原样。'
    return $inputProcess.Id
}
function Resolve-NlInputKeys {
    param([string]$SelectedProfile='', [string]$SteamRoot='', [long]$Account=0)
    if ($SelectedProfile -and (Test-Path -LiteralPath $SelectedProfile -PathType Leaf)) { return $SelectedProfile }
    if (-not $SteamRoot) { $SteamRoot=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath }
    if (-not $Account) { $Account=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction Stop).ActiveUser }
    if ($Account -le 0) { throw 'Steam 尚未登录，无法确认训练输入对应的账号。' }
    $inputProfile=Join-Path $SteamRoot "userdata/$Account/730/local/cfg/cs2_user_keys_0_slot0.vcfg"
    if (-not (Test-Path -LiteralPath $inputProfile -PathType Leaf)) { throw '当前 Steam 账号没有可读取的 CS2 键位配置，请先正常启动一次 CS2。' }
    return $inputProfile
}
