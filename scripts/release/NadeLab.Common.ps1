# NadeLab release support. Windows PowerShell 5.1; no Python or SDK required.
Set-StrictMode -Version 2.0
$script:NlMaps = @('de_mirage','de_dust2','de_inferno','de_nuke','de_ancient','de_anubis','de_cache','de_overpass','de_train','de_vertigo')
$script:NlUi = @('panorama/styles/custom_game/nadelab_panel.vcss_c','panorama/layout/custom_game/nadelab_panel.vxml_c','maps/scripts/nadelab_panel_bridge.vjs_c')
function Get-NlNativeControlsText { return "// NadeLab native controls: safe for historical autoexec entries; no personal bindings.`ncss_nl_controls native`n" }

function Write-NlJson($Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $temporary = $Path + '.new'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Read-NlJson($Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-NlHash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-NlLegacyPath($Path,[switch]$Directory) {
    # Windows PowerShell 5.1/.NET Framework file and ZIP APIs do not reliably
    # honor the system long-path switch. Fail before writing or copying data.
    try { $full=[IO.Path]::GetFullPath($Path) }
    catch [IO.PathTooLongException] { throw '安装路径过长。请将工具移到较短的全英文路径，例如 D:\NadeLab，然后重试；Windows PowerShell 5.1 不保证支持长路径。' }
    $parent=Split-Path -Parent $full
    if ($full.Length -gt 259 -or ($Directory -and $full.Length -gt 248) -or ($parent -and $parent.Length -gt 248)) {
        throw "安装路径过长（$($full.Length) 个字符）。请将工具解压到较短的全英文路径，例如 D:\NadeLab，然后重试。目标：$full"
    }
}
function Get-NlChild($Root, $Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw "Invalid relative path: $Relative" }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $joined=Join-Path $base $Relative
    Assert-NlLegacyPath $joined
    $path = [IO.Path]::GetFullPath($joined)
    if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes its directory: $Relative" }
    # Do not follow a junction/symlink supplied in an archive or in the instance.
    $cursor = $path
    while ($cursor -and $cursor.Length -ge $base.TrimEnd('\','/').Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "A linked directory/file is not permitted here: $cursor" }
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $path
}
function Copy-NlFile($Source, $Destination) {
    Assert-NlLegacyPath $Destination
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    # Detach old hard links before copying; never overwrite Steam asset bytes.
    if (Test-Path -LiteralPath $Destination) {
        if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) { throw "Expected a file: $Destination" }
        Remove-Item -LiteralPath $Destination -Force
    }
    Copy-Item -LiteralPath $Source -Destination $Destination
}
function Copy-NlTree($Source, $Destination) {
    $base = [IO.Path]::GetFullPath($Source).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $Source -File -Recurse | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected source link: $($_.FullName)" }
        Copy-NlFile $_.FullName (Get-NlChild $Destination $_.FullName.Substring($base.Length))
    }
}
function Test-NlPackage($Root) {
    $manifest = Read-NlJson (Get-NlChild $Root 'release-manifest.json')
    if ($manifest.schema -ne 1 -or -not $manifest.version -or -not $manifest.files.Count) { throw 'Invalid release manifest.' }
    $seen = @{}
    foreach ($file in $manifest.files) {
        $path = Get-NlChild $Root $file.path
        if ($seen.ContainsKey($file.path) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid manifest entry: $($file.path)" }
        $seen[$file.path] = $true
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-NlHash $path) -ne $file.sha256.ToLowerInvariant()) { throw "Package file is missing or changed: $($file.path). Extract a fresh release." }
    }
    foreach ($required in @('game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll','game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.deps.json','game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.Core.dll','game/csgo/cfg/nadelab_keys.cfg','game/csgo/cfg/nadelab_practice.cfg','scripts/release/dependencies.lock.json') + @($script:NlUi | ForEach-Object { 'game/csgo/' + $_ })) {
        if (-not $seen.ContainsKey($required)) { throw "Required release file is not in the manifest: $required" }
    }
    return $manifest
}
function Get-NlSteamRoots {
    $roots = @()
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
        if (Test-Path $registryPath) {
            try { $item = Get-ItemProperty $registryPath -ErrorAction Stop } catch { continue }
            foreach ($name in @('SteamPath','InstallPath')) {
                $property = $item.PSObject.Properties[$name]
                if ($property -and $property.Value -and (Test-Path -LiteralPath $property.Value -PathType Container)) { $roots += [IO.Path]::GetFullPath($property.Value) }
            }
        }
    }
    return @($roots | Sort-Object -Unique)
}
function Get-NlGameCandidates {
    $libraries = @(Get-NlSteamRoots)
    foreach ($steam in @(Get-NlSteamRoots)) {
        $vdf = Join-Path $steam 'steamapps/libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"(?<path>(?:\\.|[^"\\])*)"')) { $libraries += $match.Groups['path'].Value.Replace('\\','\') }
        }
    }
    return @($libraries | Sort-Object -Unique | ForEach-Object { Join-Path $_ 'steamapps/common/Counter-Strike Global Offensive' } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'game/bin/win64/cs2.exe') -PathType Leaf })
}
function Resolve-NlGame($Requested, [switch]$NonInteractive) {
    if (-not $Requested) {
        $candidates = @(Get-NlGameCandidates)
        if ($candidates.Count -eq 1) { $Requested = $candidates[0] }
        elseif ($NonInteractive) { throw '请通过 -GameInstallation 指定 CS2 游戏安装目录。' }
        elseif ($candidates.Count -gt 1) {
            for ($index=0; $index -lt $candidates.Count; $index++) { Write-Host ('{0}. {1}' -f ($index+1),$candidates[$index]) }
            $choice = Read-Host '请选择 CS2 安装目录的编号'
            if ($choice -notmatch '^\d+$' -or [int]$choice -lt 1 -or [int]$choice -gt $candidates.Count) { throw '选择无效，请输入列表中的编号。' }
            $Requested = $candidates[[int]$choice-1]
        } else { $Requested = Read-Host '粘贴 CS2 文件夹路径（Steam → CS2 → 管理 → 浏览本地文件）' }
    }
    $Requested = [IO.Path]::GetFullPath($Requested.Trim('"')).TrimEnd('\','/')
    if ($Requested -match '["\r\n]') { throw 'Unsupported characters in the game path.' }
    foreach ($relative in @('game/bin/win64/cs2.exe','game/csgo/gameinfo.gi','game/csgo/steam.inf','game/csgo/pak01_dir.vpk')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Requested $relative) -PathType Leaf)) { throw "Not a complete CS2 installation: $Requested" }
    }
    return $Requested
}
function Assert-NlGameReady($Game) {
    $manifest = Join-Path (Split-Path -Parent (Split-Path -Parent $Game)) 'appmanifest_730.acf'
    if (Test-Path -LiteralPath $manifest) {
        if ([IO.File]::ReadAllText($manifest) -notmatch '"StateFlags"\s+"4"') { throw '请等待 Steam 完成 CS2 更新，再安装或启动训练服。' }
    }
    $stream = [IO.File]::OpenRead((Join-Path $Game 'game/csgo/pak01_dir.vpk'))
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $magic=$reader.ReadUInt32(); $version=$reader.ReadUInt32(); $tree=$reader.ReadUInt32()
        if ($magic -ne 1437209140 -or $version -notin @(1,2) -or $tree -eq 0 -or $stream.Length -lt ($tree + $(if ($version -eq 2) {28} else {12}))) { throw 'CS2 资源不完整，请在 Steam 验证游戏文件完整性。' }
    } finally { $stream.Dispose() }
}
function Get-NlCsProcesses {
    $items = @(Get-Process -Name cs2 -ErrorAction SilentlyContinue)
    foreach ($item in $items) {
        $item.Refresh()
        if (-not $item.HasExited -and -not $item.Path) { throw "Cannot identify CS2 process $($item.Id). Close CS2 before changing its files." }
    }
    return @($items | Where-Object { -not $_.HasExited })
}
function Assert-NlClientClosed($Game) {
    $exe = Join-Path $Game 'game/bin/win64/cs2.exe'
    if (@(Get-NlCsProcesses | Where-Object { $_.Path -eq $exe }).Count) { throw '请先退出 CS2 客户端，再安装或恢复按键与面板。工具没有关闭任何游戏进程。' }
}
function Assert-NlInstanceStopped($Instance) {
    $exe = Join-Path $Instance 'game/bin/win64/cs2.exe'
    if (@(Get-NlCsProcesses | Where-Object { $_.Path -eq $exe }).Count) { throw '请先停止本工具的训练服，再安装或更新。' }
}
function Assert-NlRuntimePath($Root) {
    if ([IO.Path]::GetFullPath($Root) -match '[^\x00-\x7F]') { throw '请将工具解压到全英文路径，例如 D:\NadeLab（可有空格）。运行框架暂不支持中文训练实例路径；本次尚未复制游戏资源。' }
}
function Expand-NlVerifiedZip($Archive, $Destination,[string]$InstallDestination='') {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveFile = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $archiveFile.Entries) {
            if ($entry.FullName.TrimEnd('/','\')) {
                $directory=$entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')
                $output=Get-NlChild $Destination $entry.FullName
                Assert-NlLegacyPath $output -Directory:$directory
                if ($InstallDestination) {
                    $installed=Get-NlChild $InstallDestination $entry.FullName
                    Assert-NlLegacyPath $installed -Directory:$directory
                }
            }
            if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw 'Dependency archive contains a symbolic link.' }
        }
    } finally { $archiveFile.Dispose() }
    Assert-NlLegacyPath $Destination -Directory
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
}
function Get-NlDependencies($Root, $State,[string]$InstallDestination='') {
    $lock = Read-NlJson (Join-Path $Root 'scripts/release/dependencies.lock.json')
    if ($lock.schema -ne 1 -or @($lock.packages).Count -ne 2) { throw 'Unsupported dependency lock.' }
    $result = @{}
    foreach ($package in $lock.packages) {
        if ($package.name -notin @('metamod','css-runtime') -or $result.ContainsKey($package.name) -or $package.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid dependency lock entry.' }
        $uri = [Uri]$package.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('github.com','mms.alliedmods.net','www.metamodsource.net','metamodsource.net')) { throw "Unapproved dependency source: $($uri.Host)" }
        $cache = Get-NlChild $State ('downloads/' + $package.name + '-' + $package.sha256 + '.zip')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cache) | Out-Null
        if (-not (Test-Path -LiteralPath $cache)) {
            $offline = Join-Path $Root ('dependencies/' + $package.name + '.zip')
            if (Test-Path -LiteralPath $offline) { Copy-Item -LiteralPath $offline -Destination $cache }
            else {
                Write-Host "正在从官方发布地址下载 $($package.name)……"
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $partial = $cache + '.partial'
                Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $partial -UseBasicParsing
                if ((Get-NlHash $partial) -ne $package.sha256.ToLowerInvariant()) { Remove-Item -LiteralPath $partial; throw "Dependency hash mismatch: $($package.name)" }
                Move-Item -LiteralPath $partial -Destination $cache -Force
            }
        }
        if ((Get-NlHash $cache) -ne $package.sha256.ToLowerInvariant()) { throw "Cached dependency is damaged: $cache" }
        # Keep the verified ZIP cache hash-addressed, but avoid a 64-character
        # hash in every extracted .NET runtime path on Windows PowerShell 5.1.
        $extract = Get-NlChild $State ('dependencies/' + $package.name)
        # Re-extract from the verified archive; cached binaries are not trusted.
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        Expand-NlVerifiedZip $cache $extract -InstallDestination $InstallDestination
        if (-not (Test-Path -LiteralPath (Join-Path $extract 'addons') -PathType Container)) { throw "Dependency has an unexpected layout: $($package.name)" }
        $result[$package.name] = $extract
    }
    return $result
}
function Get-NlBindings($Text) {
    $bindings = @{}
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -notmatch '^\s*bind\s') { continue }
        $match=[regex]::Match($line,'(?i)^\s*bind\s+(?<key>\S+)\s+(?:"(?<quoted>[^"\r\n]+)"|(?<bare>\S+))\s*$')
        if (-not $match.Success) { throw "Invalid training binding: $line" }
        $key=$match.Groups['key'].Value.ToLowerInvariant()
        if ($bindings.ContainsKey($key)) { throw "Duplicate control: $key" }
        $bindings[$key]=$(if ($match.Groups['quoted'].Success) {$match.Groups['quoted'].Value} else {$match.Groups['bare'].Value})
    }
    if ($bindings.Count -ne 14) { throw 'Training controls are incomplete.' }
    return $bindings
}
function Get-NlBindingBlock($Text) {
    $blocks = [regex]::Matches($Text,'(?is)"bindings"\s*\{(?<body>[^{}]*)\}')
    if ($blocks.Count -ne 1) { throw 'Unknown CS2 binding file format; no guessed defaults will be written.' }
    return $blocks[0].Groups['body']
}
function Get-NlBinding($Block, $Key) {
    $matches = [regex]::Matches($Block,('(?im)^\s*"' + [regex]::Escape($Key) + '"\s*"(?<value>(?:\\.|[^"\\\r\n])*)"[^\r\n]*\r?$'))
    if ($matches.Count -gt 1) { throw "Duplicate CS2 binding: $Key" }
    if ($matches.Count) { return $matches[0].Groups['value'].Value }
    return $null
}
function Set-NlBinding($Block, $Key, $Value) {
    $pattern='(?im)^\s*"' + [regex]::Escape($Key) + '"\s*"(?<value>(?:\\.|[^"\\\r\n])*)"[^\r\n]*\r?$'
    $matches=[regex]::Matches($Block,$pattern)
    if ($matches.Count -gt 1) { throw "Duplicate CS2 binding: $Key" }
    if ($matches.Count) {
        $match=$matches[0]
        if ($null -eq $Value) { return $Block.Remove($match.Index,$match.Length) }
        $group=$match.Groups['value']
        return $Block.Substring(0,$group.Index)+$Value+$Block.Substring($group.Index+$group.Length)
    }
    if ($null -eq $Value) { return $Block }
    return $Block.TrimEnd()+"`r`n`t`t"+'"'+$Key+'"'+"`t`t"+'"'+$Value+'"'+"`r`n`t"
}
function Get-NlProfiles {
    $profiles=@()
    foreach ($steam in @(Get-NlSteamRoots)) {
        $userdata=Join-Path $steam 'userdata'
        if (Test-Path -LiteralPath $userdata -PathType Container) {
            foreach ($profile in @(Get-ChildItem -LiteralPath $userdata -Directory)) {
                $keys=Join-Path $profile.FullName '730/local/cfg/cs2_user_keys_0_slot0.vcfg'
                if (Test-Path -LiteralPath $keys -PathType Leaf) { $profiles += $keys }
            }
        }
    }
    return @($profiles | Sort-Object -Unique)
}
function Install-NlClient($Root,$State,$Game,[string]$KeysFile='', [switch]$NonInteractive, [switch]$LegacyBindings) {
    Assert-NlClientClosed $Game
    $client=Join-Path $Game 'game/csgo'
    $receiptPath=Join-Path $State 'client-receipt.json'
    $receipt=$null
    if (Test-Path -LiteralPath $receiptPath) {
        $receipt=Read-NlJson $receiptPath
        if ($receipt.game -ne $Game -and -not $receipt.restored) { throw '请先恢复上一套 CS2 安装的客户端文件，再选择另一个游戏目录。' }
        $wasNative=$receipt.PSObject.Properties['controlsMode'] -and $receipt.controlsMode -eq 'native'
        if (-not $receipt.restored -and $wasNative -eq [bool]$LegacyBindings) {
            $conflicts=@(Restore-NlClient $State)
            if ($conflicts.Count) { throw ('旧客户端安装存在另行修改的文件，已保留。请先处理恢复报告：'+($conflicts -join '; ')) }
            $receipt=Read-NlJson $receiptPath
        }
        if ($receipt.restored) {
            $history=Get-NlChild $State ('client-history/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')+'-'+[Guid]::NewGuid().ToString('N'))
            $oldBackups=Get-NlChild $State 'client-backup'
            if (Test-Path -LiteralPath $oldBackups) { Copy-NlTree $oldBackups (Get-NlChild $history 'client-backup') }
            foreach ($file in $receipt.files) { $file.backup=Get-NlChild $history ('client-backup/'+$file.relative) }
            Write-NlJson (Get-NlChild $history 'receipt.json') $receipt
            $receipt=$null
        }
    }
    $bindingSource=[IO.File]::ReadAllText((Join-Path $Root 'game/csgo/cfg/nadelab_keys.cfg'))
    $bindings=@{}
    $availableProfiles=@()
    $selectedOriginal=@{}
    $installMenu=$false
    $menuEntry=$null
    if ($LegacyBindings) {
        $bindings=Get-NlBindings $bindingSource
        $availableProfiles=@(Get-NlProfiles)
        if ($receipt) {
            if ($KeysFile -and [IO.Path]::GetFullPath($KeysFile) -ne $receipt.selectedProfile) { throw '要改选 Steam 账号，请先恢复客户端，再重新安装；原备份会自动归档。' }
            $KeysFile=$receipt.selectedProfile
        }
        elseif (-not $KeysFile) {
            if ($availableProfiles.Count -eq 1) { $KeysFile=$availableProfiles[0] }
            elseif ($availableProfiles.Count -eq 0) { throw '请先正常启动一次 CS2 再退出，以便保存你的原按键，然后重试安装。仅安装服务器可使用 -ServerOnly。' }
            elseif ($NonInteractive) { throw '发现多个 Steam 账号，请通过 -KeysFile 指定训练账号的按键文件，或使用交互安装选择。' }
            else {
                for ($index=0;$index -lt $availableProfiles.Count;$index++) { Write-Host ('{0}. {1}' -f ($index+1),$availableProfiles[$index]) }
                $choice=Read-Host '请选择用于训练的 Steam 账号资料编号'
                if ($choice -notmatch '^\d+$' -or [int]$choice -lt 1 -or [int]$choice -gt $availableProfiles.Count) { throw '账号资料选择无效。' }
                $KeysFile=$availableProfiles[[int]$choice-1]
            }
        }
        $KeysFile=[IO.Path]::GetFullPath($KeysFile)
        if ($KeysFile -notin $availableProfiles) { throw '所选按键文件不属于已发现的 Steam CS2 账号资料。' }
        $selectedOriginal=@{}
        if ($receipt) {
            $selected=@($receipt.profiles | Where-Object path -eq $KeysFile)
            if ($selected.Count -ne 1) { throw 'The selected original profile snapshot is missing.' }
            # Upgrades can add keys. Capture each newly affected binding before
            # installing its cfg; an old 12-key receipt has no wheel originals.
            foreach ($profile in $receipt.profiles) {
                $originalBlock=Get-NlBindingBlock ([IO.File]::ReadAllText($profile.path))
                foreach ($key in $bindings.Keys) {
                    if (-not $profile.bindings.PSObject.Properties[$key]) {
                        $value=Get-NlBinding $originalBlock.Value $key
                        if ($value -eq $bindings[$key]) { throw "新增按键 $key 已被训练绑定，但没有原始备份；已停止更新，避免丢失你的原按键。" }
                        $profile.bindings | Add-Member -NotePropertyName $key -NotePropertyValue $value
                    }
                }
            }
            foreach ($property in $selected[0].bindings.PSObject.Properties) { $selectedOriginal[$property.Name]=$property.Value }
        } else {
            $block=Get-NlBindingBlock ([IO.File]::ReadAllText($KeysFile))
            foreach ($key in $bindings.Keys) { $selectedOriginal[$key]=Get-NlBinding $block.Value $key }
        }
        # Persist only the menu bootstrap in the selected account. The rest of the
        # training layer is applied explicitly by its cfg; platform cfgs stay intact.
        $menuText=[IO.File]::ReadAllText($KeysFile)
        $menuBlock=Get-NlBindingBlock $menuText
        $menuCurrent=Get-NlBinding $menuBlock.Value 'f1'
        $menuKnown=@('css_nl',$bindings.f1)
        $hadMenuEntry=$receipt -and $receipt.PSObject.Properties['menuEntry']
        if ($hadMenuEntry) { $menuKnown+=@($receipt.menuEntry.ownedCommands) }
        $pendingMenu=$hadMenuEntry -and $receipt.menuEntry.status -eq 'planned'
        $installMenu=(-not $receipt) -or ($menuCurrent -in $menuKnown) -or
            ((-not $hadMenuEntry -or $pendingMenu) -and $menuCurrent -eq $selectedOriginal['f1'])
        $menuEntry=[pscustomobject]@{path=$KeysFile;command=$bindings.f1;ownedCommands=@($menuKnown | Sort-Object -Unique);status=$(if ($installMenu) {'planned'} else {'preserved-custom'})}
    } else { $KeysFile='' }
    $restoreLines=@('// NadeLab: restore the selected Steam profile controls saved during explicit legacy setup.', 'css_nl_controls native')
    foreach ($key in @($bindings.Keys | Sort-Object)) {
        $value=$selectedOriginal[$key]
        if ($null -eq $value -or $value -eq '' -or $value -eq '<unbound>') { $restoreLines += ('unbind "'+$key+'"') }
        else { $restoreLines += ('bind "'+$key+'" "'+$value+'"') }
    }
    $generated=Get-NlChild $State 'generated/nadelab_restore_keys.cfg'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $generated) | Out-Null
    [IO.File]::WriteAllText($generated,($restoreLines -join "`r`n")+"`r`n",(New-Object Text.UTF8Encoding($false)))
    $nativeGenerated=Get-NlChild $State 'generated/nadelab_keys.cfg'
    if (-not $LegacyBindings) { [IO.File]::WriteAllText($nativeGenerated,(Get-NlNativeControlsText),(New-Object Text.UTF8Encoding($false))) }
    $files=@()
    $plans=@()
    foreach ($relative in @('cfg/nadelab_keys.cfg','cfg/nadelab_restore_keys.cfg') + $script:NlUi) {
        $source=Join-Path $Root ('game/csgo/'+$relative)
        if ($relative -eq 'cfg/nadelab_restore_keys.cfg') { $source=$generated }
        if ($relative -eq 'cfg/nadelab_keys.cfg' -and -not $LegacyBindings) { $source=$nativeGenerated }
        $target=Get-NlChild $client $relative
        $prior=@(); if ($receipt) { $prior=@($receipt.files | Where-Object relative -eq $relative) }
        if ($prior.Count) {
            $known=@($prior[0].installedHash,$prior[0].originalHash)
            foreach ($field in @('plannedHash','previousHash')) { if ($prior[0].PSObject.Properties[$field]) { $known += $prior[0].$field } }
            if ((Test-Path -LiteralPath $target) -and (Get-NlHash $target) -notin $known) { throw "客户端文件被另行修改，已保留：$relative。请先恢复或处理冲突再更新。" }
            foreach ($field in @('plannedHash','previousHash','restored')) { if (-not $prior[0].PSObject.Properties[$field]) { $prior[0] | Add-Member -NotePropertyName $field -NotePropertyValue $null } }
            $prior[0].previousHash=$(if (Test-Path -LiteralPath $target) {Get-NlHash $target} else {$null})
            $prior[0].plannedHash=Get-NlHash $source
            $prior[0].restored=$false
            $files += $prior[0]
        } else {
            $backup=Get-NlChild $State ('client-backup/'+$relative)
            $existed=Test-Path -LiteralPath $target -PathType Leaf
            $originalHash=$null
            if ($existed) { Copy-NlFile $target $backup; $originalHash=Get-NlHash $backup }
            $files += [pscustomobject]@{relative=$relative;existed=[bool]$existed;backup=$backup;originalHash=$originalHash;installedHash=$null;previousHash=$originalHash;plannedHash=(Get-NlHash $source);restored=$false}
        }
        $plans += [pscustomobject]@{source=$source;target=$target;relative=$relative}
    }
    if (-not $receipt) {
        $profiles=@()
        foreach ($keys in $availableProfiles) {
            $text=[IO.File]::ReadAllText($keys); $block=Get-NlBindingBlock $text
            $original=@{}
            foreach ($key in $bindings.Keys) { $original[$key]=Get-NlBinding $block.Value $key }
            $profiles += [pscustomobject]@{path=$keys;bindings=$original}
        }
        $receipt=[pscustomobject]@{schema=1;controlsMode=$(if ($LegacyBindings) {'legacy'} else {'native'});game=$Game;installedAtUtc=[DateTime]::UtcNow.ToString('o');restored=$false;files=@();profiles=$profiles;selectedProfile=$KeysFile;bindings=$bindings}
    }
    # Save the complete original state before the first mutation for crash recovery.
    if ($receipt.PSObject.Properties['controlsMode']) { $receipt.controlsMode=$(if ($LegacyBindings) {'legacy'} else {'native'}) }
    else { $receipt | Add-Member -NotePropertyName controlsMode -NotePropertyValue $(if ($LegacyBindings) {'legacy'} else {'native'}) }
    $receipt.files=$files
    $receipt.bindings=$bindings
    if ($receipt.PSObject.Properties['menuEntry']) { $receipt.menuEntry=$menuEntry }
    else { $receipt | Add-Member -NotePropertyName menuEntry -NotePropertyValue $menuEntry }
    Write-NlJson $receiptPath $receipt
    foreach ($plan in $plans) {
        Copy-NlFile $plan.source $plan.target
        $entry=@($receipt.files | Where-Object relative -eq $plan.relative)[0]
        $entry.installedHash=Get-NlHash $plan.target
    }
    if ($installMenu -and $menuCurrent -ne $bindings.f1) {
        Assert-NlClientClosed $Game
        if ([IO.File]::ReadAllText($KeysFile) -cne $menuText) { throw '所选账号按键在安装期间发生变化，F1 尚未写入；请关闭游戏后重试。原备份已保留。' }
        $snapshot=Get-NlChild $State ('client-key-snapshots/'+[Guid]::NewGuid().ToString('N')+'.vcfg')
        Copy-NlFile $KeysFile $snapshot
        $body=Set-NlBinding $menuBlock.Value 'f1' $bindings.f1
        [IO.File]::WriteAllText($KeysFile,($menuText.Substring(0,$menuBlock.Index)+$body+$menuText.Substring($menuBlock.Index+$menuBlock.Length)),(New-Object Text.UTF8Encoding($false)))
    }
    if ($installMenu) { $receipt.menuEntry.status='installed' }
    Write-NlJson $receiptPath $receipt
    if (-not $LegacyBindings) {
        Write-Host '客户端面板已安装；个人按键与 autoexec 保持原样。旧安装仍由 NadeLab 占用的按键已按原备份恢复。'
        Write-Host '从进入训练启动输入模块：F1 菜单，滚轮/方向键选择。旧绑定仅在明确选择 -LegacyBindings 时启用。'
        return
    }
    Write-Host '已明确启用兼容快捷键。客户端面板与训练按键文件已安装。原按键已备份；其他账号、平台配置与 autoexec 均未修改。'
    if ($installMenu) { Write-Host '所选账号 F1 已设置为加载训练按键并开关菜单；其余账号按键保持原样。' }
    else { Write-Warning '检测到安装后另行设置的 F1，已保留。进入训练时执行 exec nadelab_keys 可临时加载训练按键；安装器未覆盖你的自定义 F1。' }
}
function Restore-NlClient($State) {
    $receiptPath=Join-Path $State 'client-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath)) { Write-Host '此目录没有客户端安装记录，无需恢复。'; return @() }
    $receipt=Read-NlJson $receiptPath
    Assert-NlClientClosed $receipt.game
    if ($receipt.restored) { Write-Host '客户端文件和按键此前已经恢复。'; return @() }
    $client=Join-Path $receipt.game 'game/csgo'; $conflicts=@()
    foreach ($file in $receipt.files) {
        $target=Get-NlChild $client $file.relative
        $currentHash=$(if (Test-Path -LiteralPath $target -PathType Leaf) {Get-NlHash $target} else {$null})
        if ($file.existed -and $currentHash -eq $file.originalHash) { continue }
        if (-not $file.existed -and $null -eq $currentHash) { continue }
        $known=@($file.installedHash)
        foreach ($field in @('plannedHash','previousHash')) { if ($file.PSObject.Properties[$field]) { $known += $file.$field } }
        if ($null -ne $currentHash -and $currentHash -notin $known) { $conflicts += $target; continue }
        if ($file.existed) {
            $backup=Get-NlChild $State ('client-backup/'+$file.relative)
            if ((Get-NlHash $backup) -ne $file.originalHash) { throw "Client backup is damaged: $($file.relative)" }
            Copy-NlFile $backup $target
        } elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    foreach ($profile in $receipt.profiles) {
        # Restore only captured keys still holding the installed NadeLab value.
        if (-not (Test-Path -LiteralPath $profile.path -PathType Leaf)) { continue }
        $text=[IO.File]::ReadAllText($profile.path); $block=Get-NlBindingBlock $text; $body=$block.Value
        foreach ($property in $receipt.bindings.PSObject.Properties) {
            $current=Get-NlBinding $body $property.Name
            $owned=@($property.Value)
            if ($property.Name -eq 'f1') {
                if ($profile.path -ne $receipt.selectedProfile) { continue }
                if ($receipt.PSObject.Properties['menuEntry']) { $owned+=@($receipt.menuEntry.ownedCommands) }
            }
            if ($current -in $owned) { $body=Set-NlBinding $body $property.Name $profile.bindings.PSObject.Properties[$property.Name].Value }
        }
        if ($body -ne $block.Value) {
            $snapshot=Get-NlChild $State ('restore-snapshots/'+[Guid]::NewGuid().ToString('N')+'.vcfg')
            Copy-NlFile $profile.path $snapshot
            [IO.File]::WriteAllText($profile.path,($text.Substring(0,$block.Index)+$body+$text.Substring($block.Index+$block.Length)),(New-Object Text.UTF8Encoding($false)))
        }
    }
    # Profiles added after install have no known originals: never guess them.
    if (@($receipt.bindings.PSObject.Properties).Count -gt 0) {
        foreach ($keys in @(Get-NlProfiles)) {
            if ($keys -notin @($receipt.profiles | ForEach-Object path)) {
                $body=(Get-NlBindingBlock ([IO.File]::ReadAllText($keys))).Value
                foreach ($property in $receipt.bindings.PSObject.Properties) {
                    if ((Get-NlBinding $body $property.Name) -eq $property.Value) { $conflicts += ('新 Steam 资料没有原按键记录，请自行确认：'+$keys); break }
                }
            }
        }
    }
    if ($conflicts.Count -eq 0) { $receipt.restored=$true; Write-NlJson $receiptPath $receipt }
    Write-NlJson (Join-Path $State 'restore-report.json') ([ordered]@{completed=($conflicts.Count -eq 0);conflicts=$conflicts;recordedAtUtc=[DateTime]::UtcNow.ToString('o')})
    return $conflicts
}

function Get-NlAssets($Game) {
    $source=Join-Path $Game 'game'; $assets=@()
    foreach ($directory in @('bin/win64','bin','csgo','core')) {
        $folder=Join-Path $source $directory
        foreach ($file in @(Get-ChildItem -LiteralPath $folder -File)) {
            $include=$directory -ne 'csgo' -or $file.Name -like 'pak01*.vpk' -or $file.Name -like 'shaders_pc*.vpk' -or $file.Name -in @('steam.inf','gameinfo_branchspecific.gi')
            if ($directory -eq 'bin/win64') { $include=$file.Extension -in @('.dll','.exe','.signatures') }
            if ($directory -eq 'bin') { $include=$file.Name -in @('assettypes_common.txt','built_from_cl.txt','content_built_from_cl.txt','sdkenginetools.txt') }
            if ($file.Extension -in @('.mdmp','.dmp','.log') -or $file.Name -eq 'steam_appid.txt') { $include=$false }
            if ($include) { $assets += [pscustomobject]@{source=$file.FullName;relative=($directory+'/'+$file.Name);bytes=$file.Length;modified=$file.LastWriteTimeUtc.Ticks;link=($directory -eq 'csgo' -and $file.Extension -eq '.vpk')} }
        }
    }
    foreach ($directory in @('csgo/bin','csgo/maps/prefabs','core/cfg','core/config')) {
        $folder=Join-Path $source $directory
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        $base=[IO.Path]::GetFullPath($folder).TrimEnd('\','/')+'\'
        foreach ($file in @(Get-ChildItem -LiteralPath $folder -File -Recurse)) {
            if ($directory -eq 'csgo/bin' -and $file.Extension -notin @('.dll','.exe','.signatures')) { continue }
            $assets += [pscustomobject]@{source=$file.FullName;relative=($directory+'/'+$file.FullName.Substring($base.Length));bytes=$file.Length;modified=$file.LastWriteTimeUtc.Ticks;link=($directory -like '*/maps/*')}
        }
    }
    foreach ($map in $script:NlMaps) {
        foreach ($suffix in @('.vpk','_vanity.vpk')) {
            $path=Join-Path $source ('csgo/maps/'+$map+$suffix)
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $file=Get-Item -LiteralPath $path
                $assets += [pscustomobject]@{source=$file.FullName;relative=('csgo/maps/'+$map+$suffix);bytes=$file.Length;modified=$file.LastWriteTimeUtc.Ticks;link=$true}
            }
        }
    }
    return $assets
}
function Test-NlAssetChanged($Asset,$Instance) {
    $target=Get-NlChild (Join-Path $Instance 'game') $Asset.relative
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { return $true }
    $file=Get-Item -LiteralPath $target
    return $file.Length -ne $Asset.bytes -or $file.LastWriteTimeUtc.Ticks -ne $Asset.modified
}
function Assert-NlSpace($Instance,[long]$Bytes) {
    $volume=New-Object IO.DriveInfo([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Instance)))
    if ($volume.AvailableFreeSpace -lt $Bytes+256MB) { throw ('{0} 空间不足，至少还需 {1:N1} GB（含预留空间）。' -f $volume.Name,(($Bytes+256MB)/1GB)) }
}
function Install-NlInstance($Root,$State,$Game,[int]$Port=27025) {
    Assert-NlRuntimePath $Root
    if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
    $package=Test-NlPackage $Root
    Assert-NlGameReady $Game
    $instance=Get-NlChild $Root 'instance'
    Assert-NlInstanceStopped $instance
    $assets=@(Get-NlAssets $Game)
    # Validate every planned game/package target before the first large copy.
    foreach ($asset in $assets) { Assert-NlLegacyPath (Get-NlChild (Join-Path $instance 'game') $asset.relative) }
    foreach ($file in $package.files) {
        if ($file.path.StartsWith('game/csgo/',[StringComparison]::Ordinal)) { Assert-NlLegacyPath (Get-NlChild $instance $file.path) }
    }
    $changes=@($assets | Where-Object { Test-NlAssetChanged $_ $instance })
    [long]$needed=0
    foreach ($asset in $changes) {
        if (-not $asset.link -or [IO.Path]::GetPathRoot($asset.source) -ne [IO.Path]::GetPathRoot($instance)) { $needed += $asset.bytes }
    }
    Assert-NlSpace $instance ($needed+300MB)
    $dependencies=Get-NlDependencies $Root $State -InstallDestination (Join-Path $instance 'game/csgo')
    # A failed update must never be reported as a launchable instance.
    Write-NlJson (Join-Path $State 'installation.json') ([ordered]@{schema=1;ready=$false;version=$package.version;game=$Game;port=$Port;startedAtUtc=[DateTime]::UtcNow.ToString('o')})
    $targetGame=Join-Path $instance 'game/csgo'
    New-Item -ItemType Directory -Force -Path $targetGame,(Join-Path $targetGame 'cfg'),(Join-Path $instance 'logs') | Out-Null
    $count=0
    foreach ($asset in $changes) {
        $count++
        Write-Progress -Activity 'Preparing the isolated CS2 training instance' -Status $asset.relative -PercentComplete ([int](100*$count/[Math]::Max(1,$changes.Count)))
        $target=Get-NlChild (Join-Path $instance 'game') $asset.relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        if ($asset.link) {
            try { New-Item -ItemType HardLink -Path $target -Target $asset.source -ErrorAction Stop | Out-Null }
            catch {
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
                Assert-NlSpace $instance $asset.bytes
                Copy-NlFile $asset.source $target
            }
        } else { Copy-NlFile $asset.source $target }
    }
    Write-Progress -Activity 'Preparing the isolated CS2 training instance' -Completed
    # Remove only catalogued obsolete map resources, never game/user data trees.
    foreach ($map in $script:NlMaps) {
        foreach ($suffix in @('.vpk','_vanity.vpk')) {
            $relative='csgo/maps/'+$map+$suffix
            if (-not (Test-Path -LiteralPath (Join-Path $Game ('game/'+$relative)))) {
                $target=Get-NlChild (Join-Path $instance 'game') $relative
                if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target }
            }
        }
    }
    foreach ($cfg in @(Get-ChildItem -LiteralPath (Join-Path $Game 'game/csgo/cfg') -File)) {
        $target=Get-NlChild $targetGame ('cfg/'+$cfg.Name)
        if (-not (Test-Path -LiteralPath $target)) { Copy-NlFile $cfg.FullName $target }
    }
    # Source 2 prefixes Game paths with the instance root, even when a Windows
    # drive is written in the path. All runtime packs are mirrored above, so
    # local paths also work when Steam and the tool are on different volumes.
    $search="SearchPaths`r`n        {`r`n            Game_LowViolence csgo_lv`r`n            Game csgo/addons/metamod`r`n            Game csgo`r`n            Game core`r`n            Mod csgo`r`n            AddonRoot csgo_addons`r`n            OfficialAddonRoot csgo_community_addons`r`n        }"
    $info=[IO.File]::ReadAllText((Join-Path $Game 'game/csgo/gameinfo.gi'))
    if ([regex]::Matches($info,'SearchPaths\s*\{[^}]*\}').Count -ne 1) { throw 'Unexpected CS2 gameinfo format; cannot prepare safe search paths.' }
    $info=[regex]::Replace($info,'SearchPaths\s*\{[^}]*\}',[Text.RegularExpressions.MatchEvaluator]{param($match) $search})
    [IO.File]::WriteAllText((Join-Path $targetGame 'gameinfo.gi'),$info,(New-Object Text.UTF8Encoding($false)))
    foreach ($dependency in @('metamod','css-runtime')) { Copy-NlTree (Join-Path $dependencies[$dependency] 'addons') (Join-Path $targetGame 'addons') }
    $lock=Read-NlJson (Join-Path $Root 'scripts/release/dependencies.lock.json')
    if ($lock.PSObject.Properties['overrides']) {
        foreach ($override in $lock.overrides) {
            if ($override.path -ne 'addons/counterstrikesharp/gamedata/gamedata.json') { throw 'Unsupported dependency override.' }
            $source=Get-NlChild $Root $override.source
            if ((Get-NlHash $source) -ne $override.sha256.ToLowerInvariant()) { throw 'Dependency override hash mismatch.' }
            Copy-NlFile $source (Get-NlChild $targetGame $override.path)
        }
    }
    foreach ($file in $package.files) {
        if ($file.path.StartsWith('game/csgo/',[StringComparison]::Ordinal)) { Copy-NlFile (Get-NlChild $Root $file.path) (Get-NlChild $targetGame $file.path.Substring(10)) }
    }
    [IO.File]::WriteAllText((Join-Path $targetGame 'cfg/gamemode_competitive_server.cfg'),"exec nadelab_practice`r`n")
    $secretFile=Join-Path $State 'rcon.local'
    if (-not (Test-Path -LiteralPath $secretFile)) {
        $bytes=New-Object byte[] 24; $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        [IO.File]::WriteAllText($secretFile,([BitConverter]::ToString($bytes).Replace('-','')))
    }
    $secret=[IO.File]::ReadAllText($secretFile).Trim()
    if ($secret -notmatch '^[A-Fa-f0-9]{48}$') { throw 'Invalid local RCON secret. Preserve state and use a fresh installation folder.' }
    [IO.File]::WriteAllText((Join-Path $targetGame 'cfg/server.cfg'),("hostname `"NadeLab Local Training`"`r`nsv_lan 1`r`nsv_cheats 1`r`nsv_hibernate_when_empty 0`r`nrcon_password `"$secret`"`r`nexec nadelab_practice`r`n"))
    Assert-NlGameReady $Game
    foreach ($asset in $assets) { if (Test-NlAssetChanged $asset $instance) { throw 'Steam resources changed during installation. Finish the Steam update, then retry installation.' } }
    Write-NlJson (Join-Path $State 'assets.json') $assets
    $config=[ordered]@{schema=1;ready=$true;version=$package.version;game=$Game;instance=$instance;port=$Port;executable=(Join-Path $instance 'game/bin/win64/cs2.exe');installedAtUtc=[DateTime]::UtcNow.ToString('o')}
    Write-NlJson (Join-Path $State 'installation.json') $config
    return [pscustomobject]$config
}
function Read-NlInstallation($Root) {
    $state=Get-NlChild $Root 'release-state'
    $configFile=Join-Path $state 'installation.json'
    if (-not (Test-Path -LiteralPath $configFile)) { throw '请先完成安装（菜单选项 1）。' }
    $config=Read-NlJson $configFile
    if ($config.schema -ne 1 -or -not $config.ready) { throw '上次安装未完成，请重新运行“首次安装 / 更新修复”。' }
    if ($config.instance -ne (Get-NlChild $Root 'instance') -or $config.executable -ne (Get-NlChild $Root 'instance/game/bin/win64/cs2.exe')) { throw '工具目录已移动，请运行“首次安装 / 更新修复”更新路径。' }
    return $config
}
function Assert-NlInstalledPayload($Package,$Instance) {
    $required=@('addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll','addons/counterstrikesharp/plugins/NadeLab/NadeLab.Core.dll','addons/counterstrikesharp/plugins/NadeLab/NadeLab.deps.json')+$script:NlUi
    foreach ($relative in $required) {
        $entry=@($Package.files | Where-Object path -eq ('game/csgo/'+$relative))
        $target=Get-NlChild $Instance ('game/csgo/'+$relative)
        if ($entry.Count -ne 1 -or -not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-NlHash $target) -ne $entry[0].sha256.ToLowerInvariant()) {
            throw "训练实例的程序或面板与发布包不一致：$relative。请运行「首次安装 / 更新修复」，收藏和校准数据会保留。"
        }
    }
}
function Read-NlBytes($Stream,[int]$Count) {
    $buffer=New-Object byte[] $Count; $offset=0
    while ($offset -lt $Count) { $read=$Stream.Read($buffer,$offset,$Count-$offset); if ($read -eq 0) { throw 'RCON connection closed.' }; $offset+=$read }
    return ,$buffer
}
function Read-NlPacket($Stream) {
    $length=[BitConverter]::ToInt32((Read-NlBytes $Stream 4),0)
    if ($length -lt 10 -or $length -gt 1048576) { throw 'Invalid RCON response length.' }
    $body=Read-NlBytes $Stream $length
    if ($body[$length-1] -ne 0 -or $body[$length-2] -ne 0) { throw 'Invalid RCON response terminator.' }
    return [pscustomobject]@{id=[BitConverter]::ToInt32($body,0);type=[BitConverter]::ToInt32($body,4);text=[Text.Encoding]::UTF8.GetString($body,8,$length-10)}
}
function Send-NlPacket($Stream,[int]$Id,[int]$Type,[string]$Text) {
    $data=[Text.Encoding]::UTF8.GetBytes($Text)
    $packet=New-Object byte[] ($data.Length+14)
    [BitConverter]::GetBytes([int]($data.Length+10)).CopyTo($packet,0)
    [BitConverter]::GetBytes($Id).CopyTo($packet,4)
    [BitConverter]::GetBytes($Type).CopyTo($packet,8)
    $data.CopyTo($packet,12)
    $Stream.Write($packet,0,$packet.Length)
}
function Invoke-NlRcon($State,[int]$Port,[string]$Command,[int]$Timeout=2000) {
    $client=New-Object Net.Sockets.TcpClient
    try {
        $connect=$client.BeginConnect('127.0.0.1',$Port,$null,$null)
        if (-not $connect.AsyncWaitHandle.WaitOne($Timeout)) { throw 'The local training server did not answer.' }
        $client.EndConnect($connect)
        $stream=$client.GetStream(); $stream.ReadTimeout=$Timeout; $stream.WriteTimeout=$Timeout
        $secret=[IO.File]::ReadAllText((Join-Path $State 'rcon.local')).Trim()
        Send-NlPacket $stream 1 3 $secret
        $authenticated=$false
        for ($index=0;$index -lt 4;$index++) {
            $packet=Read-NlPacket $stream
            if ($packet.id -eq -1) { throw 'The occupied port belongs to a different server, or local authentication failed.' }
            if ($packet.id -eq 1 -and $packet.type -eq 2) { $authenticated=$true; break }
        }
        if (-not $authenticated) { throw 'No RCON authentication response.' }
        Send-NlPacket $stream 2 2 $Command
        $output=New-Object Text.StringBuilder
        for ($index=0;$index -lt 100;$index++) {
            try { $packet=Read-NlPacket $stream }
            catch { if ($output.Length -gt 0 -or $Command -eq 'quit') { break }; throw }
            if ($packet.id -eq 2) { $null=$output.Append($packet.text); $stream.ReadTimeout=400 }
        }
        return $output.ToString()
    } finally { $client.Close() }
}
function Test-NlPort([int]$Port) {
    $client=New-Object Net.Sockets.TcpClient
    try {
        $connect=$client.BeginConnect('127.0.0.1',$Port,$null,$null)
        if (-not $connect.AsyncWaitHandle.WaitOne(400)) { return $false }
        try { $client.EndConnect($connect); return $true } catch { return $false }
    } finally { $client.Close() }
}
function Get-NlRuntimeState($Text) {
    if ($Text -match 'BENCHMARKSTATUS\s+(\{[^\r\n]+\})') {
        try {
            $status=$Matches[1] | ConvertFrom-Json
            if ($status.SchemaVersion -eq 1 -and $status.Map -in $script:NlMaps -and $status.Humans -ge 0 -and $status.PSObject.Properties['Practice'] -and $status.PSObject.Properties['Idle']) { return $status }
        } catch { }
    }
    return $null
}
function Start-NlInstance($Root,[string]$Map='de_mirage') {
    Assert-NlRuntimePath $Root
    if ($Map -notin $script:NlMaps) { throw 'Unsupported map.' }
    $config=Read-NlInstallation $Root; $state=Get-NlChild $Root 'release-state'
    $package=Test-NlPackage $Root
    if ($config.version -ne $package.version) { throw '发布包已更新，请先运行“首次安装 / 更新修复”，再启动训练。' }
    Assert-NlInstalledPayload $package $config.instance
    Assert-NlGameReady $config.game
    if (Test-NlPort $config.port) {
        if (-not @(Get-NlCsProcesses | Where-Object { $_.Path -eq $config.executable }).Count) { throw '端口被其他服务器占用；没有连接或修改该服务器。请改用其他端口安装。' }
        $status=Get-NlRuntimeState (Invoke-NlRcon $state $config.port 'css_nl_benchmarkstatus')
        if (-not $status -or -not $status.Practice) { throw '端口已占用，但 NadeLab 尚未进入训练模式。请查看 instance/logs 日志。' }
        $mapReady=$status.Map
        Write-Host "训练服已运行：$mapReady；保留当前地图，没有重复启动。"
    } else {
        Assert-NlInstanceStopped $config.instance
        $changed=@(Get-NlAssets $config.game | Where-Object { Test-NlAssetChanged $_ $config.instance })
        if (Test-Path -LiteralPath (Join-Path $state 'assets.json')) {
            foreach ($asset in @(Read-NlJson (Join-Path $state 'assets.json'))) { if (-not (Test-Path -LiteralPath $asset.source -PathType Leaf)) { $changed += $asset } }
        }
        if ($changed.Count) { throw 'Steam 已更新 CS2，请先运行“首次安装 / 更新修复”，再启动训练服。' }
        if (-not (Test-Path -LiteralPath (Join-Path $config.instance ('game/csgo/maps/'+$Map+'.vpk')))) { throw '当前 CS2 未安装所选地图，请选择已有地图或在 Steam 更新游戏。' }
        $helper=Join-Path $Root 'scripts/release/NadeLab.ServerProcess.ps1'
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$helper+'" -Root "'+$Root+'" -Map "'+$Map+'"'
        $launcher=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -WorkingDirectory $Root
        Write-Host "正在加载 $Map，并检查插件及训练状态……"
        $deadline=[DateTime]::UtcNow.AddSeconds(90); $mapReady=$null
        while ([DateTime]::UtcNow -lt $deadline) {
            $launcher.Refresh()
            if ($launcher.HasExited) { throw "训练服启动时退出，请查看 instance/logs/stdout.log 与 launcher.log。" }
            try {
                $status=Get-NlRuntimeState (Invoke-NlRcon $state $config.port 'css_nl_benchmarkstatus' 1500)
                if ($status -and -not $status.Practice -and $status.Humans -eq 0 -and $status.Idle) {
                    $null=Invoke-NlRcon $state $config.port 'exec nadelab_practice' 1500
                    $status=Get-NlRuntimeState (Invoke-NlRcon $state $config.port 'css_nl_benchmarkstatus' 1500)
                }
                if ($status -and $status.Practice) { $mapReady=$status.Map }
            } catch { }
            if ($mapReady) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $mapReady) { throw '训练服在 90 秒内未就绪，可能仍在加载。请查看状态和 instance/logs 日志。' }
        $status=Get-NlRuntimeState (Invoke-NlRcon $state $config.port 'css_nl_benchmarkstatus')
        if ($status -and $status.Practice -and $status.Humans -eq 0 -and $status.Idle) {
            $test=Invoke-NlRcon $state $config.port 'css_nl_selftest' 6000
            if ($test -notmatch ('SELFTEST PASS:\s+native\s+'+[regex]::Escape($mapReady)+'\b')) { throw '训练模式已启用，但原生碰撞自检未通过。暂不标记为就绪；请查看 instance/logs。' }
            Write-Host "READY：$mapReady 已加载，插件与原生碰撞自检通过。"
        } else { Write-Warning '服务器已加载，但已有玩家进入或正在运行操作；为避免影响训练，本次未执行原生自检。' }
    }
    Write-Host ('CS2 控制台输入：connect 127.0.0.1:'+ $config.port)
    Write-Host '使用进入训练连接输入模块：F1 菜单，滚轮/方向键选择，E 确认，R 返回；备用聊天 !nl。个人键位文件保持原样。'
}
function Get-NlClientLaunchPlan($Root) {
    $config=Read-NlInstallation $Root
    $receiptPath=Get-NlChild $Root 'release-state/client-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath)) { throw '请先完成客户端安装，再使用一键进入训练。仅服务器安装可选择「仅启动训练服」。' }
    $receipt=Read-NlJson $receiptPath
    if ($receipt.restored -or $receipt.game -ne $config.game) { throw '客户端已恢复或游戏位置变化，请先运行安装 / 修复。' }
    $legacy=$receipt.PSObject.Properties['controlsMode'] -and $receipt.controlsMode -eq 'legacy'
    $package=Test-NlPackage $Root
    foreach ($relative in @('cfg/nadelab_keys.cfg')+$script:NlUi) {
        $entry=@($package.files | Where-Object path -eq ('game/csgo/'+$relative))
        $path=Get-NlChild (Join-Path $config.game 'game/csgo') $relative
        if ($relative -eq 'cfg/nadelab_keys.cfg' -and -not $legacy) {
            if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne (Get-NlNativeControlsText)) { throw '原生控制入口与安装记录不一致，请先运行安装 / 修复。' }
            continue
        }
        if ($entry.Count -ne 1 -or -not (Test-Path -LiteralPath $path) -or (Get-NlHash $path) -ne $entry[0].sha256) { throw '客户端面板或按键与发布包不一致，请先运行安装 / 修复。' }
    }
    if ($config.port -lt 1024 -or $config.port -gt 65535) { throw '安装记录的训练端口无效，请重新安装 / 修复。' }
    $running=@(Get-NlCsProcesses | Where-Object Path -eq (Join-Path $config.game 'game/bin/win64/cs2.exe')).Count -gt 0
    $steam=$null
    if (-not $running) {
        $candidates=@(Get-NlSteamRoots | ForEach-Object { Join-Path $_ 'steam.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique)
        if ($candidates.Count -ne 1) { throw '无法唯一定位 Steam。可选择「仅启动训练服」，然后正常从 Steam 打开 CS2 并使用连接命令。' }
        $steam=$candidates[0]
    }
    $arguments=@('-applaunch','730')
    if ($legacy) { $arguments+=@('+exec','nadelab_keys') }
    $arguments+=@('+connect',('127.0.0.1:'+ [int]$config.port))
    return [pscustomobject]@{AlreadyRunning=$running;Steam=$steam;Port=[int]$config.port;LegacyBindings=[bool]$legacy;Arguments=$arguments}
}
function Start-NlClient($Root) {
    $plan=Get-NlClientLaunchPlan $Root
    $inputConfig=Read-NlInstallation $Root
    $inputReceipt=Read-NlJson (Get-NlChild $Root 'release-state/client-receipt.json')
    . (Join-Path $PSScriptRoot 'NadeLab.Input.ps1')
    $inputKeys=Resolve-NlInputKeys -SelectedProfile $inputReceipt.selectedProfile
    $null=Start-NlInputCompanion -GameContentRoot (Join-Path $Root 'instance/game/csgo') `
        -ClientExe (Join-Path $inputConfig.game 'game/bin/win64/cs2.exe') -KeysFile $inputKeys `
        -HelperDirectory (Join-Path $Root 'tools/input') -LogDirectory (Join-Path $Root 'release-state/input-logs')
    if ($plan.AlreadyRunning) {
        Write-Host ('CS2 已在运行。控制台输入：'+$(if ($plan.LegacyBindings) {'exec nadelab_keys; '} else {''})+'connect 127.0.0.1:'+ $plan.Port)
        return
    }
    Start-Process -FilePath $plan.Steam -ArgumentList $plan.Arguments -WindowStyle Hidden | Out-Null
    Write-Host '已请求 Steam 启动 CS2 并连接本地训练服。请留意 Steam 的启动提示。'
    Write-Host '若出现区服选择窗口，按你平时的选择点击「开始」后继续。'
    if ($plan.LegacyBindings) { Write-Host '兼容快捷键已启用。结束训练后输入 exec nadelab_restore_keys；或退出 CS2 后使用恢复客户端入口。' }
}
function Stop-NlInstance($Root) {
    $config=Read-NlInstallation $Root; $state=Get-NlChild $Root 'release-state'
    $owned=@(Get-NlCsProcesses | Where-Object { $_.Path -eq $config.executable })
    if (-not $owned.Count) { Write-Host '本工具的训练服已停止。'; return }
    $status=Invoke-NlRcon $state $config.port 'status'
    if ($status -notmatch 'players\s*:\s*(\d+)\s+humans' -or [int]$Matches[1] -ne 0) { throw '训练服仍有玩家，或无法确认人数。请先断开连接，再停止训练服。' }
    $null=Invoke-NlRcon $state $config.port 'quit'
    foreach ($process in $owned) { if (-not $process.WaitForExit(10000)) { throw '训练服尚未退出。工具没有强制结束进程，请稍后查看状态。' } }
    Write-Host 'NadeLab 训练服已停止，CS2 客户端保持不变。'
}
