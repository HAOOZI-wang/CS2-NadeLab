[CmdletBinding()]
param(
    [ValidateSet('Menu','StartDev','Player','Status','Build','Acceptance','InstallPlugin','ProjectLogs','PlayerLogs','ProjectData','DebugLogs','Guide')]
    [string]$Action = 'Menu',
    [string]$InstalledRoot = 'D:\apps\NadeLab',
    [ValidateSet('de_mirage','de_dust2','de_inferno','de_nuke','de_ancient','de_anubis','de_cache','de_overpass','de_train','de_vertigo')]
    [string]$Map = 'de_mirage',
    [switch]$Plan,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

function Get-NlwFullPath([string]$Path, [string]$Base) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $Base $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
}

function Read-NlwMetadata([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Assert-NlwMetadataPath([object]$Value, [string]$Expected, [string]$Base, [string]$Label) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label 缺少有效路径；预期：$Expected。请修复实例元数据。"
    }
    try { $actual=Get-NlwFullPath $Value $Base }
    catch { throw "$Label 路径无效；预期：$Expected。请修复实例元数据。" }
    if (-not $actual.Equals((Get-NlwFullPath $Expected $Base),[StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 与当前实例不一致；预期：$Expected。未采用元数据中的其他位置。"
    }
}

function Get-NlwMetadataPort([object]$Value, [string]$Label) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt 1024 -or $Value -gt 65535) {
        throw "$Label 必须是 1024–65535 的整数。请修复实例元数据。"
    }
    return [int]$Value
}

function New-NlwContext([string]$Root, [string]$PlayerRoot) {
    $project = Get-NlwFullPath $Root (Get-Location).Path
    $player = Get-NlwFullPath $PlayerRoot $project
    $server = Join-Path $project 'server'
    $projectGame = Join-Path $server 'game/csgo'
    $playerInstance = Join-Path $player 'instance'
    $projectExe = Join-Path $server 'game/bin/win64/cs2.exe'
    $playerExe = Join-Path $playerInstance 'game/bin/win64/cs2.exe'
    $projectPort = 27025; $playerPort = 27025
    $metadataIssues = @()
    # An instance is identified by its fixed location under its selected root.
    # Metadata may confirm this identity, but may never redefine its owner.
    try {
        $development = Read-NlwMetadata (Join-Path $server 'instance.json')
        if ($development) {
            try { Assert-NlwMetadataPath $development.GameDirectory $projectGame $server '项目 GameDirectory' } catch { $metadataIssues += $_.Exception.Message }
            try { Assert-NlwMetadataPath $development.Executable $projectExe $server '项目 Executable' } catch { $metadataIssues += $_.Exception.Message }
            try { $projectPort=Get-NlwMetadataPort $development.Port '项目 Port' } catch { $metadataIssues += $_.Exception.Message }
        }
    } catch { $metadataIssues += '项目 server/instance.json 无法读取；以下为默认路径，不能据此确认运行状态。' }
    try {
        $installation = Read-NlwMetadata (Join-Path $player 'release-state/installation.json')
        if ($installation) {
            try { Assert-NlwMetadataPath $installation.instance $playerInstance $player '玩家 instance' } catch { $metadataIssues += $_.Exception.Message }
            try { Assert-NlwMetadataPath $installation.executable $playerExe $player '玩家 executable' } catch { $metadataIssues += $_.Exception.Message }
            try { $playerPort=Get-NlwMetadataPort $installation.port '玩家 port' } catch { $metadataIssues += $_.Exception.Message }
        }
    } catch { $metadataIssues += '玩家 release-state/installation.json 无法读取；以下为默认路径，不能据此确认运行状态。' }
    return [pscustomobject]@{
        Root=$project; InstalledRoot=$player; Server=$server; ProjectGame=$projectGame
        ProjectExecutable=(Get-NlwFullPath $projectExe $project)
        PlayerExecutable=(Get-NlwFullPath $playerExe $player)
        ProjectPort=$projectPort; PlayerPort=$playerPort
        ProjectLogs=(Join-Path $server 'logs'); PlayerLogs=(Join-Path $playerInstance 'logs')
        ProjectData=(Join-Path $projectGame 'addons/counterstrikesharp/plugins/NadeLab/data')
        ProjectPlugin=(Join-Path $projectGame 'addons/counterstrikesharp/plugins/NadeLab')
        PlayerPlugin=(Join-Path $playerInstance 'game/csgo/addons/counterstrikesharp/plugins/NadeLab')
        BuildPlugin=(Join-Path $project 'src/NadeLab.Plugin/bin/Release/net10.0')
        DebugRoot=(Join-Path $project 'artifacts/debug-sessions')
        Guide=(Join-Path $project 'docs/训练与调试.txt'); MetadataIssues=@($metadataIssues)
    }
}

function Get-NlwProcessInventory {
    try {
        # Do not query command lines: they may contain server credentials.
        return @(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object {
            [pscustomobject]@{ Id=[int]$_.ProcessId; Name=[string]$_.Name; Path=[string]$_.ExecutablePath }
        })
    } catch {
        try {
            return @(Get-Process -ErrorAction Stop | ForEach-Object {
                $path=''; try { $path=[string]$_.Path } catch { }
                [pscustomobject]@{ Id=[int]$_.Id; Name=($_.ProcessName+'.exe'); Path=$path }
            })
        } catch { throw '无法读取进程路径，不能确认端口属于哪个实例。请使用任务管理器检查。' }
    }
}

function ConvertFrom-NlwNetstat([string[]]$Lines, [int[]]$Ports) {
    foreach ($line in $Lines) {
        # TCP rows must be listeners; UDP rows have no connection-state column.
        if ($line -notmatch '^\s*(TCP|UDP)\s+(\S+)\s+\S+(?:\s+(LISTENING))?\s+(\d+)\s*$') { continue }
        $protocol=$Matches[1]; $local=$Matches[2]; $state=$Matches[3]; $processId=[int]$Matches[4]
        if ($protocol -eq 'TCP' -and $state -ne 'LISTENING') { continue }
        $separator=$local.LastIndexOf(':')
        if ($separator -lt 0) { continue }
        $portNumber=0
        if (-not [int]::TryParse($local.Substring($separator+1),[ref]$portNumber) -or $Ports -notcontains $portNumber) { continue }
        [pscustomobject]@{ Protocol=$protocol; Port=$portNumber; Address=$local.Substring(0,$separator); ProcessId=$processId }
    }
}

function Get-NlwPortInventory([int[]]$Ports) {
    try {
        $entries = @()
        foreach ($connection in @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $Ports -contains [int]$_.LocalPort })) {
            $entries += [pscustomobject]@{ Protocol='TCP'; Port=[int]$connection.LocalPort; Address=[string]$connection.LocalAddress; ProcessId=[int]$connection.OwningProcess }
        }
        foreach ($endpoint in @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $Ports -contains [int]$_.LocalPort })) {
            $entries += [pscustomobject]@{ Protocol='UDP'; Port=[int]$endpoint.LocalPort; Address=[string]$endpoint.LocalAddress; ProcessId=[int]$endpoint.OwningProcess }
        }
        return @($entries)
    } catch {
        $netstat=Join-Path $env:SystemRoot 'System32/netstat.exe'
        $lines=@(& $netstat -ano)
        if ($LASTEXITCODE -ne 0) { throw '无法读取 TCP/UDP 端口。' }
        return @(ConvertFrom-NlwNetstat $lines $Ports)
    }
}

function Get-NlwOwner([object]$Context, [int]$ProcessId, [object[]]$Processes) {
    $process = @($Processes | Where-Object Id -eq $ProcessId | Select-Object -First 1)
    if (-not $process.Count -or -not $process[0].Path) { return 'Unknown' }
    if ($process[0].Path -eq $Context.ProjectExecutable) { return 'Project' }
    if ($process[0].Path -eq $Context.PlayerExecutable) { return 'Player' }
    return 'Other'
}

function Get-NlwFileHash([string]$Path) {
    $stream=[IO.File]::OpenRead($Path)
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-NlwPluginComparison([string]$BuildDirectory, [string]$PluginDirectory) {
    return @(foreach ($name in @('NadeLab.dll','NadeLab.Core.dll','NadeLab.deps.json')) {
        $built = Join-Path $BuildDirectory $name; $installed = Join-Path $PluginDirectory $name
        $buildHash = $null; $installedHash = $null
        if (Test-Path -LiteralPath $built -PathType Leaf) { $buildHash = Get-NlwFileHash $built }
        if (Test-Path -LiteralPath $installed -PathType Leaf) { $installedHash = Get-NlwFileHash $installed }
        $result = 'Match'
        if (-not $buildHash) { $result = 'MissingBuild' }
        elseif (-not $installedHash) { $result = 'MissingInstalled' }
        elseif ($buildHash -ne $installedHash) { $result = 'Different' }
        [pscustomobject]@{ File=$name; Result=$result; BuildSha256=$buildHash; InstalledSha256=$installedHash }
    })
}

function Get-NlwStatus([object]$Context, [object[]]$Processes, [object[]]$Ports, [string[]]$Warnings=@()) {
    if (@($Processes | Where-Object { $_.Name -eq 'cs2.exe' -and -not $_.Path }).Count) {
        $Warnings += '有 CS2 进程的可执行路径无法读取；这些进程的实例归属仍未知。'
    }
    $listeners = @(foreach ($endpoint in $Ports) {
        $process = @($Processes | Where-Object Id -eq $endpoint.ProcessId | Select-Object -First 1)
        $path = $null; if ($process.Count) { $path = $process[0].Path }
        [pscustomobject]@{ Protocol=$endpoint.Protocol; Port=$endpoint.Port; Address=$endpoint.Address; ProcessId=$endpoint.ProcessId; Owner=(Get-NlwOwner $Context $endpoint.ProcessId $Processes); Executable=$path }
    })
    [pscustomobject]@{
        ProjectRoot=$Context.Root; InstalledRoot=$Context.InstalledRoot
        ProjectExecutable=$Context.ProjectExecutable; PlayerExecutable=$Context.PlayerExecutable
        ProjectPort=$Context.ProjectPort; PlayerPort=$Context.PlayerPort
        ProjectProcessIds=@($Processes | Where-Object { $_.Path -and $_.Path -eq $Context.ProjectExecutable } | ForEach-Object Id)
        PlayerProcessIds=@($Processes | Where-Object { $_.Path -and $_.Path -eq $Context.PlayerExecutable } | ForEach-Object Id)
        Ports=$listeners
        ProjectPlugin=@(Get-NlwPluginComparison $Context.BuildPlugin $Context.ProjectPlugin)
        PlayerPlugin=@(Get-NlwPluginComparison $Context.BuildPlugin $Context.PlayerPlugin)
        ProjectLogs=$Context.ProjectLogs; PlayerLogs=$Context.PlayerLogs; ProjectData=$Context.ProjectData
        DebugLogs=$Context.DebugRoot; Warnings=@($Context.MetadataIssues)+@($Warnings)
        Note='仅检查路径、进程、端口和文件哈希；未连接 RCON，未确认地图或插件就绪，未读取玩家记录。玩家版本与开发版不同不代表安装损坏。'
    }
}

function Assert-NlwStartOwnership([object]$Context, [object[]]$Processes, [object[]]$Ports) {
    if ($Context.MetadataIssues.Count) { throw ($Context.MetadataIssues -join ' ') }
    if (@($Processes | Where-Object { $_.Name -eq 'cs2.exe' -and -not $_.Path }).Count) {
        throw '有 CS2 进程的路径无法读取，不能确认是否属于项目或玩家实例。未启动或停止任何实例；请先检查进程权限与当前训练服。'
    }
    foreach ($endpoint in @($Ports | Where-Object Port -eq $Context.ProjectPort)) {
        $owner = Get-NlwOwner $Context $endpoint.ProcessId $Processes
        if ($owner -ne 'Project') {
            $description = @{ Player='玩家安装实例'; Other='其他进程'; Unknown='无法识别的进程' }[$owner]
            throw "项目端口 $($Context.ProjectPort) 已被${description}占用（PID $($endpoint.ProcessId)）。未启动或停止任何实例；请先确认当前训练服。"
        }
    }
}

function Get-NlwLatestDebug([object]$Context) {
    if (-not (Test-Path -LiteralPath $Context.DebugRoot -PathType Container)) { return $Context.DebugRoot }
    $latest = Get-ChildItem -LiteralPath $Context.DebugRoot -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $Context.DebugRoot
}

function New-NlwActionPlan([object]$Context, [string]$SelectedAction, [string]$SelectedMap) {
    $kind='Command'; $program=''; $arguments=@(); $target=''; $log=''
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    switch ($SelectedAction) {
        'StartDev' { $program='python'; $arguments=@('-s',(Join-Path $Context.Root 'scripts/local-server.py'),'start','--map',$SelectedMap) }
        'Player' { $program=$windowsPowerShell; $arguments=@('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Context.InstalledRoot 'scripts/release/NadeLab.ps1'),'-Action','Menu') }
        'Build' { $program='pwsh'; $arguments=@('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Context.Root 'scripts/build.ps1'),'-NoPackage'); $log=Join-Path $Context.DebugRoot '<timestamp>/build.log' }
        'InstallPlugin' { $program=$windowsPowerShell; $arguments=@('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Context.Root 'scripts/install-plugin.ps1'),'-ServerRoot',$Context.Server); $log=Join-Path $Context.DebugRoot '<timestamp>/install.log' }
        'Acceptance' { $program='python'; $arguments=@('-s',(Join-Path $Context.Root 'scripts/acceptance.py')) }
        'Status' { $kind='ReadOnly' }
        'ProjectLogs' { $kind='Open'; $target=$Context.ProjectLogs }
        'PlayerLogs' { $kind='Open'; $target=$Context.PlayerLogs }
        'ProjectData' { $kind='Open'; $target=$Context.ProjectData }
        'DebugLogs' { $kind='Open'; $target=Get-NlwLatestDebug $Context }
        'Guide' { $kind='Open'; $target=$Context.Guide }
        default { throw "未知操作：$SelectedAction" }
    }
    return [pscustomobject]@{ Action=$SelectedAction; Kind=$kind; Program=$program; Arguments=@($arguments); WorkingDirectory=$Context.Root; Target=$target; Log=$log }
}

function Format-NlwCommand([object]$ActionPlan) {
    $parts = @($ActionPlan.Program)+@($ActionPlan.Arguments)
    return '& '+(($parts | ForEach-Object { "'" + ([string]$_).Replace("'","''") + "'" }) -join ' ')
}

function Show-NlwPlan([object]$ActionPlan) {
    Write-Host "[$($ActionPlan.Action)] 仅预览，不执行。"
    if ($ActionPlan.Kind -eq 'Command') {
        Write-Host "工作目录：$($ActionPlan.WorkingDirectory)"
        Write-Host (Format-NlwCommand $ActionPlan)
        if ($ActionPlan.Log) { Write-Host "执行时保存日志：$($ActionPlan.Log)" }
    } elseif ($ActionPlan.Kind -eq 'Open') { Write-Host "打开：$($ActionPlan.Target)" }
    else { Write-Host '只读查看两套实例的路径、文件哈希、进程和端口归属。' }
}

function Show-NlwStatus([object]$Status) {
    Write-Host "项目目录：$($Status.ProjectRoot)"
    Write-Host "玩家目录：$($Status.InstalledRoot)"
    Write-Host "项目进程 PID：$($Status.ProjectProcessIds -join ', ') | 预期端口：$($Status.ProjectPort)"
    Write-Host "玩家进程 PID：$($Status.PlayerProcessIds -join ', ') | 预期端口：$($Status.PlayerPort)"
    Write-Host '没有 PID 表示未发现路径匹配的进程；如有下列读取警告，状态尚不能确认。'
    $labels=@{ Project='项目开发服'; Player='玩家安装实例'; Other='其他进程'; Unknown='无法识别' }
    foreach ($entry in $Status.Ports) { Write-Host ("{0} {1}:{2} | PID {3} | {4} | {5}" -f $entry.Protocol,$entry.Address,$entry.Port,$entry.ProcessId,$labels[$entry.Owner],$entry.Executable) }
    if (-not $Status.Ports.Count) { Write-Host '所检查端口未发现监听；这不是游戏就绪检测。' }
    $hashLabels=@{ Match='一致'; Different='不同'; MissingBuild='缺少本地构建'; MissingInstalled='尚未安装' }
    foreach ($entry in $Status.ProjectPlugin) { Write-Host "项目插件 $($entry.File)：$($hashLabels[$entry.Result])" }
    foreach ($entry in $Status.PlayerPlugin) { Write-Host "玩家插件 $($entry.File)：$($hashLabels[$entry.Result])" }
    Write-Host "项目日志：$($Status.ProjectLogs)"
    Write-Host "玩家日志：$($Status.PlayerLogs)"
    Write-Host "项目记录：$($Status.ProjectData)"
    Write-Host $Status.Note
    foreach ($warning in $Status.Warnings) { Write-Warning $warning }
}

function Invoke-NlwAction([object]$Context, [string]$SelectedAction, [string]$SelectedMap, [switch]$Preview, [switch]$AsJson) {
    $actionPlan = New-NlwActionPlan $Context $SelectedAction $SelectedMap
    if ($Preview) {
        if ($AsJson) { $actionPlan | ConvertTo-Json -Depth 5 } else { Show-NlwPlan $actionPlan }
        return
    }
    if ($SelectedAction -eq 'Status') {
        $warnings=@(); $processes=@(); $ports=@()
        try { $processes=@(Get-NlwProcessInventory) } catch { $warnings += $_.Exception.Message }
        try { $ports=@(Get-NlwPortInventory @($Context.ProjectPort,$Context.PlayerPort)) } catch { $warnings += '无法完整读取 TCP/UDP 端口，不能确认是否有端口冲突。' }
        $status=Get-NlwStatus $Context $processes $ports $warnings
        if ($AsJson) { $status | ConvertTo-Json -Depth 6 } else { Show-NlwStatus $status }
        return
    }
    if ($actionPlan.Kind -eq 'Open') {
        if ($SelectedAction -eq 'DebugLogs' -and $actionPlan.Target -eq $Context.DebugRoot) {
            Write-Host '尚无工作台构建 / 安装记录，请先执行 4 或 5；历史记录见 artifacts 目录导航。'
            return
        }
        if (-not (Test-Path -LiteralPath $actionPlan.Target)) {
            if ($SelectedAction -eq 'DebugLogs') {
                Write-Host '尚无工作台构建 / 安装记录，请先执行 4 或 5；历史记录见 artifacts 目录导航。'
                return
            }
            throw "目录或文件尚不存在：$($actionPlan.Target)。请先完成对应实例的设置，或查看操作指南。"
        }
        Invoke-Item -LiteralPath $actionPlan.Target
        return
    }
    foreach ($argument in $actionPlan.Arguments) {
        if ($argument -match '\.(py|ps1)$' -and -not (Test-Path -LiteralPath $argument -PathType Leaf)) {
            throw "入口脚本不存在：$argument。玩家工具请先完整安装，或用 -InstalledRoot 指定安装目录。"
        }
    }
    $command = Get-Command $actionPlan.Program -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw "缺少依赖：$($actionPlan.Program)。请安装该程序并加入 PATH 后重试。" }
    if ($SelectedAction -eq 'StartDev') {
        if (-not (Test-Path -LiteralPath (Join-Path $Context.Server 'instance.json'))) { throw '项目开发服尚未设置：缺少 server/instance.json。请按操作指南初始化；不会自动安装游戏。' }
        Assert-NlwStartOwnership $Context @(Get-NlwProcessInventory) @(Get-NlwPortInventory @($Context.ProjectPort))
    }
    $logFile=$null
    if ($SelectedAction -in @('Build','InstallPlugin')) {
        $session=Join-Path $Context.DebugRoot ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
        New-Item -ItemType Directory -Path $session -Force | Out-Null
        $logName='build.log'; if ($SelectedAction -eq 'InstallPlugin') { $logName='install.log' }
        $logFile=Join-Path $session $logName
        Write-Host "操作日志：$logFile"
    }
    Push-Location -LiteralPath $Context.Root
    $previousPreference=$ErrorActionPreference; $code=1
    try {
        # Native stderr should be recorded, while the child exit code determines success.
        $ErrorActionPreference='Continue'
        $nativeArguments=@($actionPlan.Arguments)
        if ($logFile) { & $command.Source @nativeArguments 2>&1 | Tee-Object -FilePath $logFile | Out-Host }
        else { & $command.Source @nativeArguments }
        $code=$LASTEXITCODE
        if ($code -eq 0 -and $SelectedAction -eq 'StartDev') {
            & (Join-Path $Context.Root 'scripts/start-input.ps1')
        }
    } finally {
        $ErrorActionPreference=$previousPreference
        Pop-Location
        if ($logFile) { [pscustomobject]@{ Action=$SelectedAction; ExitCode=$code; FinishedAtUtc=[DateTime]::UtcNow.ToString('o'); Workspace=$Context.Root; Log=$logFile } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $logFile) 'result.json') -Encoding UTF8 }
    }
    if ($code -ne 0) { throw "操作未完成（退出码 $code）。$(if($logFile){'日志：'+$logFile})" }
    if ($SelectedAction -eq 'Build') { Write-Host '构建与现有测试通过；未打包、未安装、未启动服务器。' }
}

# Tests may dot-source helpers without invoking a menu or touching services.
if ($MyInvocation.InvocationName -eq '.') { return }
try {
    if ($Json -and -not $Plan -and $Action -ne 'Status') { throw '-Json 仅用于 -Action Status 或 -Plan。' }
    $context=New-NlwContext (Split-Path -Parent $PSScriptRoot) $InstalledRoot
    if ($Action -ne 'Menu') { Invoke-NlwAction $context $Action $Map -Preview:$Plan -AsJson:$Json }
    elseif ($Plan) {
        $items=@('StartDev','Player','Status','Build','Acceptance','InstallPlugin','ProjectLogs','PlayerLogs','ProjectData','DebugLogs','Guide')
        if ($Json) { @($items | ForEach-Object { New-NlwActionPlan $context $_ $Map }) | ConvertTo-Json -Depth 5 }
        else { foreach ($item in $items) { Invoke-NlwAction $context $item $Map -Preview } }
    } else {
        Write-Host "项目：$($context.Root)"
        Write-Host "玩家工具：$($context.InstalledRoot)"
        while ($true) {
            Write-Host ''
            Write-Host 'NadeLab 工作台 | 训练与调试' -ForegroundColor Cyan
            Write-Host '1  启动项目开发服（使用已安装构建，初始地图可用 -Map 指定）'
            Write-Host '2  打开已安装的玩家工具菜单'
            Write-Host '3  查看两套实例状态（只读）'
            Write-Host '4  构建并验证源码（保存日志，不打包、不安装）'
            Write-Host '5  安装已验证插件到项目开发服（须已停止，不会自动停服）'
            Write-Host '6  打开项目日志        7  打开玩家日志'
            Write-Host '8  打开项目记录目录    9  打开最新构建 / 安装调试记录'
            Write-Host 'A  生成验收报告（离线审计当前记录，不启动游戏）'
            Write-Host 'G  操作指南            0  退出工作台（保持当前服务器状态）'
            $selection=(Read-Host '选择操作').Trim().ToUpperInvariant()
            if ($selection -eq '0') { break }
            $actions=@{ '1'='StartDev'; '2'='Player'; '3'='Status'; '4'='Build'; '5'='InstallPlugin'; '6'='ProjectLogs'; '7'='PlayerLogs'; '8'='ProjectData'; '9'='DebugLogs'; 'G'='Guide'; 'A'='Acceptance' }
            if (-not $actions.ContainsKey($selection)) { Write-Host '请输入 0–9、A 或 G。'; continue }
            try {
                $context=New-NlwContext (Split-Path -Parent $PSScriptRoot) $InstalledRoot
                Invoke-NlwAction $context $actions[$selection] $Map
            } catch { Write-Host $_.Exception.Message -ForegroundColor Red }
        }
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
