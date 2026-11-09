[CmdletBinding()]
param(
    [ValidateSet('Menu','Install','Play','Start','Stop','Status','RestoreClient','Verify','Uninstall')][string]$Action='Menu',
    [string]$GameInstallation='',
    [string]$KeysFile='',
    [ValidateSet('de_mirage','de_dust2','de_inferno','de_nuke','de_ancient','de_anubis','de_cache','de_overpass','de_train','de_vertigo')][string]$Map='de_mirage',
    [ValidateRange(1024,65535)][int]$Port=27025,
    [switch]$ServerOnly,
    [switch]$LegacyBindings,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'NadeLab.Common.ps1')
$state=Get-NlChild $root 'release-state'
function Invoke-NlAction([string]$SelectedAction) {
    switch ($SelectedAction) {
        'Verify' { $package=Test-NlPackage $root; Write-Host "Release $($package.version): all packaged files match the manifest." }
        'Install' {
            Assert-NlRuntimePath $root
            $null=Test-NlPackage $root
            $requested=$GameInstallation
            if (-not $requested -and (Test-Path -LiteralPath (Join-Path $state 'installation.json'))) { $requested=(Read-NlJson (Join-Path $state 'installation.json')).game }
            $game=Resolve-NlGame $requested -NonInteractive:$NonInteractive
            if (-not $ServerOnly) { Assert-NlClientClosed $game }
            $config=Install-NlInstance $root $state $game $Port
            if (-not $ServerOnly) { Install-NlClient $root $state $game -KeysFile $KeysFile -NonInteractive:$NonInteractive -LegacyBindings:$LegacyBindings }
            Write-Host "安装完成：$($config.version)。"
            if ($ServerOnly) { Write-Host '下一步选择「仅启动训练服」。' }
            else { Write-Host '下一步选择「进入训练」。' }
        }
        'Start' { Start-NlInstance $root $Map }
        'Play' { $null=Get-NlClientLaunchPlan $root; Start-NlInstance $root $Map; Start-NlClient $root }
        'Stop' { Stop-NlInstance $root }
        'Status' {
            $config=Read-NlInstallation $root
            Write-Host "版本：$($config.version) | 本机端口：$($config.port)"
            $owned=@(Get-NlCsProcesses | Where-Object Path -eq $config.executable)
            if (-not $owned.Count) { Write-Host '本工具的训练服已停止。'; return }
            $status=Get-NlRuntimeState (Invoke-NlRcon $state $config.port 'css_nl_benchmarkstatus')
            if (-not $status) { throw '服务器正在运行，但 NadeLab 尚未回应，请查看 instance/logs。' }
            Write-Host "地图：$($status.Map) | 训练已启用：$($status.Practice) | 玩家数：$($status.Humans) | 空闲：$($status.Idle)"
        }
        'RestoreClient' {
            $conflicts=@(Restore-NlClient $state)
            if ($conflicts.Count) { $conflicts | ForEach-Object { Write-Warning $_ }; throw '已保留后来修改的客户端文件；部分恢复尚未完成。请查看 release-state/restore-report.json 的冲突列表。' }
            Write-Host '客户端恢复完成；后来自定义的按键已保留，autoexec 从未修改。'
        }
        'Uninstall' {
            if (Test-Path -LiteralPath (Join-Path $state 'installation.json')) { Stop-NlInstance $root }
            Invoke-NlAction 'RestoreClient'
            Write-Host 'NadeLab 已停止，客户端修改已恢复。收藏投法和备份文件保留在工具目录。'
            Write-Host '需要完全移除时，先备份收藏和记录，再关闭本窗口并删除解压后的 NadeLab 文件夹。'
        }
    }
}
$mutex=$null; $locked=$false
try {
    $hash=[Security.Cryptography.SHA256]::Create()
    try { $key=[BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Replace('-','') } finally { $hash.Dispose() }
    $mutex=New-Object Threading.Mutex($false,('Local\NadeLabRelease-'+$key))
    try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (-not $locked) { throw '已有另一个 NadeLab 窗口正在管理此目录，请关闭它后再继续。' }
    if ($Action -ne 'Menu') { Invoke-NlAction $Action }
    else {
        if ($NonInteractive) { throw 'Choose an explicit -Action when using -NonInteractive.' }
        while ($true) {
            Write-Host ''
            Write-Host 'NadeLab | CS2 道具训练工具' -ForegroundColor Cyan
            Write-Host '1  首次安装 / 更新修复（先退出 CS2）'
            Write-Host '2  进入训练（启动训练服与 CS2）'
            Write-Host '3  查看状态'
            Write-Host '4  停止训练服（先断开连接）'
            Write-Host '5  恢复原按键与客户端文件（先退出 CS2）'
            Write-Host '6  校验发布包'
            Write-Host '7  卸载指引与恢复'
            Write-Host '8  仅启动训练服 / 显示连接命令'
            Write-Host '0  退出工具（训练服继续运行）'
            $selection=Read-Host '请选择'
            if ($selection -eq '0') { break }
            $actions=@{'1'='Install';'2'='Play';'3'='Status';'4'='Stop';'5'='RestoreClient';'6'='Verify';'7'='Uninstall';'8'='Start'}
            if (-not $actions.ContainsKey($selection)) { Write-Host '请输入 0–8。'; continue }
            try { Invoke-NlAction $actions[$selection] }
            catch { Write-Host $_.Exception.Message -ForegroundColor Red }
        }
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
