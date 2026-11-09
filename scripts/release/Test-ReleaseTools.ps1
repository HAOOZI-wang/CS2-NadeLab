param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NadeLab.Common.ps1')
$testParent=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'nadelab-tests'))
$testRoot=[IO.Path]::GetFullPath((Join-Path $testParent ([Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks=New-Object Collections.Generic.List[string]
function Assert-Test($Condition,$Name) { if (-not $Condition) { throw "FAIL: $Name" }; $checks.Add($Name); Write-Host "PASS: $Name" }
function Assert-Throws([scriptblock]$Code,$Name) { $threw=$false; try { & $Code | Out-Null } catch { $threw=$true }; Assert-Test $threw $Name }
try {
    Assert-Test ($PSVersionTable.PSVersion.Major -ge 5) 'PowerShell runtime is available'
    foreach ($file in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1')) {
        $tokens=$null; $errors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        Assert-Test ($errors.Count -eq 0) ('PowerShell syntax: '+$file.Name)
    }
    $safe=Get-NlChild $testRoot 'space folder/中文/file.txt'
    Assert-Test ($safe.StartsWith($testRoot+'\')) 'Unicode and space paths stay inside the installation'
    Assert-NlRuntimePath (Join-Path $testRoot 'ASCII space folder')
    Assert-Test $true 'An ASCII tool folder may contain spaces'
    Assert-Throws { Assert-NlRuntimePath (Join-Path $testRoot '中文实例') } 'A non-ASCII runtime folder is rejected before copying game data'
    Assert-Throws { Get-NlChild $testRoot '../outside.txt' } 'Traversal rejected'
    Assert-Throws { Get-NlChild $testRoot 'C:\outside.txt' } 'Absolute paths rejected'
    Assert-Throws { Get-NlChild $testRoot 'safe.txt:stream' } 'Alternate data streams rejected'
    $tooLong=Join-Path $testRoot ('p' * 230)
    Assert-Throws { Assert-NlLegacyPath $tooLong } 'Windows PowerShell file path limits fail with a repair instruction'
    $deepParent=$testRoot+'\'+('d' * (249-$testRoot.Length-1))
    Assert-Throws { Assert-NlLegacyPath ($deepParent+'\x.dll') } 'Directory limits are checked even when the full file is below 260 characters'
    $keys='"config"'+"`r`n{`r`n"+'"bindings"'+"`r`n{`r`n"+'"f1" "buymenu"'+"`r`n"+'"v" "+voicerecord"'+"`r`n"+'"w" "+forward"'+"`r`n}`r`n}`r`n"
    $block=Get-NlBindingBlock $keys
    Assert-Test ((Get-NlBinding $block.Value 'f1') -eq 'buymenu') 'Existing bindings read without guessed defaults'
    $updated=Set-NlBinding $block.Value 'f1' 'css_nl'
    Assert-Test ((Get-NlBinding $updated 'f1') -eq 'css_nl') 'Only requested binding replaced'
    Assert-Test ((Get-NlBinding $updated 'w') -eq '+forward') 'Movement key retained'
    $updated=Set-NlBinding $updated 'f2' 'css_nl_area'
    Assert-Test ((Get-NlBinding $updated 'f2') -eq 'css_nl_area') 'New binding inserted'
    $updated=Set-NlBinding $updated 'f2' $null
    Assert-Test ($null -eq (Get-NlBinding $updated 'f2')) 'Previously unbound key can be removed'
    Assert-Throws { Get-NlBinding ('"f1" "one"'+"`n"+'"f1" "two"') 'f1' } 'Duplicate bindings fail safely'
    Assert-Throws { Get-NlBindingBlock 'unrecognized format' } 'Unknown binding format rejected'
    Assert-Test ($null -eq (Get-NlRuntimeState 'Unknown command css_nl_benchmarkstatus')) 'An absent plugin is never reported ready'
    Assert-Test ($null -eq (Get-NlRuntimeState 'BENCHMARKSTATUS {invalid}')) 'Broken runtime status rejected'
    $status=Get-NlRuntimeState 'BENCHMARKSTATUS {"SchemaVersion":1,"Map":"de_mirage","Humans":1,"Practice":true,"Idle":false}'
    Assert-Test ($status.Humans -eq 1 -and $status.Practice -and -not $status.Idle) 'Read-only readiness distinguishes occupied busy training'
    $stream=New-Object IO.MemoryStream
    Send-NlPacket $stream 7 2 'status 中文'
    $stream.Position=0; $packet=Read-NlPacket $stream
    Assert-Test ($packet.id -eq 7 -and $packet.type -eq 2 -and $packet.text -eq 'status 中文') 'RCON packet framing and UTF-8 round trip'
    $stream.Dispose()
    $bad=New-Object IO.MemoryStream(,[BitConverter]::GetBytes([int]2147483647))
    Assert-Throws { Read-NlPacket $bad } 'Oversized RCON packet rejected before allocation'
    $bad.Dispose()
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=Join-Path $testRoot 'traversal.zip'
    $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$zip.CreateEntry('../escape.txt') } finally { $zip.Dispose() }
    Assert-Throws { Expand-NlVerifiedZip $archive (Join-Path $testRoot 'unzip') } 'Dependency ZIP traversal rejected before extraction'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $testRoot 'escape.txt'))) 'Unsafe archive produced no outside file'
    $longArchive=Join-Path $testRoot 'long-path.zip'
    $zip=[IO.Compression.ZipFile]::Open($longArchive,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$zip.CreateEntry(('inner/' + ('a'*120) + '/library.dll')) } finally { $zip.Dispose() }
    $deepDestination=Join-Path $testRoot ('deep'+('b'*70))
    Assert-Throws { Expand-NlVerifiedZip $longArchive $deepDestination } 'ZIP output paths are checked before extracting any entries'
    Assert-Test (-not (Test-Path -LiteralPath $deepDestination)) 'An excessive ZIP path creates no partial extraction tree'
    $shortDestination=Join-Path $testRoot 'short'
    Assert-Throws { Expand-NlVerifiedZip $longArchive $shortDestination -InstallDestination $deepDestination } 'Final instance paths are checked before dependency extraction'
    # Compile the exact launcher code without launching a game or allocating a process.
    $launcher=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NadeLab.ServerProcess.ps1'))
    $native=[regex]::Match($launcher,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
    Assert-Test ($native.Length -gt 100) 'Native launcher source found'
    if (-not ('NadeLabNativeLauncher' -as [type])) { Add-Type -TypeDefinition $native }
    Assert-Test ([NadeLabNativeLauncher]::Quote('D:\a b\') -eq '"D:\a b\\"') 'Windows command-line trailing slash quoting'
    Assert-Test ([NadeLabNativeLauncher]::Quote('a"b') -eq '"a\"b"') 'Windows command-line embedded quote escaping'
    # Real file-system integration in a disposable fake client; no Steam files touched.
    $package=Join-Path $testRoot 'package'; $game=Join-Path $testRoot 'fake game'; $state=Join-Path $package 'release-state'
    $script:testProfile=Join-Path $testRoot 'userdata/123/730/local/cfg/cs2_user_keys_0_slot0.vcfg'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:testProfile),(Join-Path $package 'game/csgo/cfg'),(Join-Path $game 'game/csgo/cfg') | Out-Null
    $keys=$keys.Replace('"f1" "buymenu"',('"f1" "buymenu"'+"`r`n"+'"f4" "<unbound>"'+"`r`n"+'"mwheelup" "+jump"'+"`r`n"+'"mwheeldown" "invnext"'))
    [IO.File]::WriteAllText($script:testProfile,$keys)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../configs/nadelab_keys.cfg') -Destination (Join-Path $package 'game/csgo/cfg/nadelab_keys.cfg')
    foreach ($resource in $script:NlUi) {
        $path=Get-NlChild $package ('game/csgo/'+$resource)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path,'test UI '+$resource)
    }
    $auto=Join-Path $game 'game/csgo/cfg/autoexec.cfg'; [IO.File]::WriteAllText($auto,'echo my existing autoexec')
    $autoHash=Get-NlHash $auto; $keysHash=Get-NlHash $script:testProfile
    $priorUi=Get-NlChild $game ('game/csgo/'+$script:NlUi[0])
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $priorUi) | Out-Null
    [IO.File]::WriteAllText($priorUi,'original custom panel')
    function Get-NlProfiles { return @($script:testProfile) }
    function Assert-NlClientClosed($Game) { }
    $nativeState=Join-Path $testRoot 'native-state'
    $nativeGame=Join-Path $testRoot 'native-game'
    Install-NlClient $package $nativeState $nativeGame -NonInteractive
    Assert-Test ((Get-NlHash $script:testProfile) -eq $keysHash) 'Default install leaves every byte of personal keys intact'
    $nativeCfg=[IO.File]::ReadAllText((Join-Path $nativeGame 'game/csgo/cfg/nadelab_keys.cfg'))
    Assert-Test ($nativeCfg -notmatch '(?im)^\s*bind\s' -and $nativeCfg.Contains('css_nl_controls native')) 'An old autoexec calling nadelab_keys reaches a native shim with no bindings'
    $nativeReceipt=Read-NlJson (Join-Path $nativeState 'client-receipt.json')
    Assert-Test ($nativeReceipt.controlsMode -eq 'native' -and @($nativeReceipt.profiles).Count -eq 0) 'Native installation never claims profile bindings'
    Install-NlClient $package $nativeState $nativeGame -NonInteractive
    Assert-Test ((Get-NlHash $script:testProfile) -eq $keysHash) 'Native repair also preserves personal keys'
    function Get-NlProfiles { throw 'Native file restoration must not inspect personal profile contents.' }
    $nativeConflicts=@(Restore-NlClient $nativeState)
    Assert-Test ($nativeConflicts.Count -eq 0 -and (Get-NlHash $script:testProfile) -eq $keysHash) 'Native uninstall needs no profile selection or inspection'
    function Get-NlProfiles { return @($script:testProfile) }
    Install-NlClient -LegacyBindings $package $state $game -NonInteractive
    Assert-Test ((Get-NlHash $auto) -eq $autoHash) 'Installation never changes autoexec'
    $installedKeys=[IO.File]::ReadAllText($script:testProfile)
    Assert-Test ((Get-NlBinding (Get-NlBindingBlock $installedKeys).Value 'f1') -eq 'exec nadelab_keys; css_nl') 'Fresh installation persists the F1 menu bootstrap in the selected account'
    Assert-Test ($installedKeys -ceq $keys.Replace('"f1" "buymenu"','"f1" "exec nadelab_keys; css_nl"')) 'Fresh installation changes only F1 and preserves all other account text'
    Assert-Test (@(Get-ChildItem -LiteralPath (Join-Path $state 'client-key-snapshots') -File | Where-Object { (Get-NlHash $_.FullName) -eq $keysHash }).Count -eq 1) 'The complete original account file is backed up before installing F1'
    $restoreCfg=[IO.File]::ReadAllText((Join-Path $game 'game/csgo/cfg/nadelab_restore_keys.cfg'))
    Assert-Test ($restoreCfg.Contains('bind "f1" "buymenu"') -and $restoreCfg.Contains('bind "v" "+voicerecord"') -and $restoreCfg.Contains('unbind "f2"')) 'In-game restore cfg retains exact original and unbound keys'
    Assert-Test ($restoreCfg.Contains('unbind "f4"') -and -not $restoreCfg.Contains('<unbound>')) 'Explicit VCFG unbound sentinel generates a console unbind command'
    Assert-Test ($restoreCfg.Contains('bind "mwheelup" "+jump"') -and $restoreCfg.Contains('bind "mwheeldown" "invnext"')) 'Wheel jump and weapon bindings have exact restore commands'
    # Simulate an older installation that did not yet own either wheel key.
    $legacyReceipt=Read-NlJson (Join-Path $state 'client-receipt.json')
    $legacyReceipt.PSObject.Properties.Remove('menuEntry')
    $legacyReceipt.bindings.f1='css_nl'
    [IO.File]::WriteAllText($script:testProfile,$keys.Replace('"f1" "buymenu"','"f1" "css_nl"'))
    foreach ($key in @('mwheelup','mwheeldown')) {
        $legacyReceipt.bindings.PSObject.Properties.Remove($key)
        foreach ($profile in $legacyReceipt.profiles) { $profile.bindings.PSObject.Properties.Remove($key) }
    }
    Write-NlJson (Join-Path $state 'client-receipt.json') $legacyReceipt
    Install-NlClient -LegacyBindings $package $state $game -NonInteractive
    $upgrade=Read-NlJson (Join-Path $state 'client-receipt.json')
    Assert-Test ((Get-NlBinding (Get-NlBindingBlock ([IO.File]::ReadAllText($script:testProfile))).Value 'f1') -eq 'exec nadelab_keys; css_nl' -and $upgrade.profiles[0].bindings.f1 -eq 'buymenu') 'Legacy css_nl upgrades to the F1 bootstrap without replacing the original backup'
    Assert-Test ('css_nl' -in $upgrade.menuEntry.ownedCommands -and 'exec nadelab_keys; css_nl' -in $upgrade.menuEntry.ownedCommands) 'Upgrade retains both old and new F1 ownership for restoration'
    Assert-Test ($upgrade.bindings.mwheelup -eq 'css_nl_wheel_up' -and $upgrade.profiles[0].bindings.mwheelup -eq '+jump') 'Upgrade captures newly owned wheel originals and updates restore ownership'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $state 'client-backup/panorama/styles/custom_game/nadelab_panel.vcss_c')) -eq 'original custom panel') 'Repair keeps the first original UI backup'
    # Simulate training writing binds, then a user customizing F2 afterwards.
    $profileText=[IO.File]::ReadAllText($script:testProfile)
    $body=Get-NlBindingBlock $profileText; $changed=$body.Value
    $bindings=Get-NlBindings ([IO.File]::ReadAllText((Join-Path $package 'game/csgo/cfg/nadelab_keys.cfg')))
    Assert-Test ($bindings.f1 -eq 'exec nadelab_keys; css_nl') 'Quoted F1 binding keeps the complete client-side reload and menu sequence'
    Assert-Throws { Get-NlBindings 'bind F1 "unterminated' } 'Malformed quoted training bindings are rejected'
    foreach ($key in $bindings.Keys) { $changed=Set-NlBinding $changed $key $bindings[$key] }
    $changed=Set-NlBinding $changed 'f2' 'my later custom key'
    [IO.File]::WriteAllText($script:testProfile,($profileText.Substring(0,$body.Index)+$changed+$profileText.Substring($body.Index+$body.Length)))
    $changedUi=Get-NlChild $game ('game/csgo/'+$script:NlUi[1]); [IO.File]::WriteAllText($changedUi,'later UI edit')
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 1 -and $conflicts[0] -eq $changedUi) 'Restore reports a changed UI without overwriting it'
    Assert-Test ([IO.File]::ReadAllText($changedUi) -eq 'later UI edit') 'Later UI customization preserved'
    Assert-Test ([IO.File]::ReadAllText($priorUi) -eq 'original custom panel') 'Original UI restored byte-for-byte'
    $after=Get-NlBindingBlock ([IO.File]::ReadAllText($script:testProfile))
    Assert-Test ((Get-NlBinding $after.Value 'f1') -eq 'buymenu') 'Original key restored when still owned by NadeLab'
    Assert-Test ((Get-NlBinding $after.Value 'f2') -eq 'my later custom key') 'Later player key customization preserved'
    Assert-Test ($null -eq (Get-NlBinding $after.Value 'f3')) 'Originally unbound key unbound on restoration'
    Assert-Test ((Get-NlBinding $after.Value 'f4') -eq '<unbound>') 'Client restoration preserves the explicit VCFG unbound sentinel'
    Assert-Test ((Get-NlBinding $after.Value 'w') -eq '+forward') 'Movement binding survives install and restore'
    Assert-Test ((Get-NlHash $auto) -eq $autoHash) 'Autoexec remains byte-identical after restoration'
    Copy-NlFile (Join-Path $package ('game/csgo/'+$script:NlUi[1])) $changedUi
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 0) 'A second restore completes after resolving the one conflict'
    Assert-Test ([IO.File]::ReadAllText($priorUi) -eq 'original custom panel') 'Retry recognizes already-restored original files'
    Install-NlClient -LegacyBindings $package $state $game -NonInteractive
    Assert-Test (@(Get-ChildItem -LiteralPath (Join-Path $state 'client-history') -Directory).Count -eq 1) 'Install after restoration automatically archives the completed receipt'
    Remove-Item -LiteralPath $priorUi
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 0 -and [IO.File]::ReadAllText($priorUi) -eq 'original custom panel') 'A missing originally-existing file is recovered from backup'
    Install-NlClient -LegacyBindings $package $state $game -NonInteractive
    [IO.File]::WriteAllText((Join-Path $package ('game/csgo/'+$script:NlUi[0])),'new release UI content')
    $script:realCopy=(Get-Item Function:Copy-NlFile).ScriptBlock
    $script:failDestination=$changedUi
    function Copy-NlFile($Source,$Destination) {
        if ($Destination -eq $script:failDestination) { throw 'Injected interruption after the first updated UI file.' }
        & $script:realCopy $Source $Destination
    }
    Assert-Throws { Install-NlClient -LegacyBindings $package $state $game -NonInteractive } 'Interrupted update leaves a durable pending receipt'
    Set-Item Function:Copy-NlFile -Value $script:realCopy
    Assert-Test ([IO.File]::ReadAllText($priorUi) -eq 'new release UI content') 'Failure injection occurred after a new file was installed'
    $pending=Read-NlJson (Join-Path $state 'client-receipt.json')
    $pendingUi=@($pending.files | Where-Object relative -eq $script:NlUi[0])[0]
    Assert-Test ($pendingUi.plannedHash -eq (Get-NlHash $priorUi) -and $pendingUi.installedHash -ne $pendingUi.plannedHash) 'Receipt recognizes both old and planned installed hashes'
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 0 -and [IO.File]::ReadAllText($priorUi) -eq 'original custom panel') 'Interrupted update can be restored without losing the original'
    $script:failDestination=Join-Path $game 'game/csgo/cfg/nadelab_keys.cfg'
    function Copy-NlFile($Source,$Destination) {
        if ($Destination -eq $script:failDestination) { throw 'Injected first-copy failure.' }
        & $script:realCopy $Source $Destination
    }
    Assert-Throws { Install-NlClient -LegacyBindings $package $state $game -NonInteractive } 'First-copy interruption remains recoverable'
    Set-Item Function:Copy-NlFile -Value $script:realCopy
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 0) 'Restore after first-copy interruption recognizes untouched originals'
    Install-NlClient -LegacyBindings $package $state $game -NonInteractive
    $script:newProfile=Join-Path $testRoot 'userdata/456/730/local/cfg/cs2_user_keys_0_slot0.vcfg'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:newProfile) | Out-Null
    [IO.File]::WriteAllText($script:newProfile,$keys)
    function Get-NlProfiles { return @($script:testProfile,$script:newProfile) }
    Assert-Throws { Install-NlClient -LegacyBindings $package $state $game -KeysFile $script:newProfile -NonInteractive } 'An account change requires restoring the previous account first'
    $conflicts=@(Restore-NlClient $state)
    Assert-Test ($conflicts.Count -eq 0) 'A new profile without NadeLab bindings does not block restoration'
    # Persist only the selected account bootstrap, and never reclaim a later
    # custom F1 during repair. Other profiles may independently use the same key.
    $menuState=Join-Path $testRoot 'menu-key-state'
    $menuBefore=[IO.File]::ReadAllText($script:testProfile)
    [IO.File]::WriteAllText($script:newProfile,$keys.Replace('"f1" "buymenu"','"f1" "css_nl"'))
    $otherBefore=Get-NlHash $script:newProfile
    function Assert-NlClientClosed($Game) { throw 'Injected running-client guard.' }
    Assert-Throws { Install-NlClient -LegacyBindings $package $menuState $game -KeysFile $script:testProfile -NonInteractive } 'A running client prevents persistent F1 installation before any changes'
    Assert-Test ([IO.File]::ReadAllText($script:testProfile) -ceq $menuBefore -and -not (Test-Path -LiteralPath $menuState)) 'Running-client rejection leaves the profile and receipt untouched'
    function Assert-NlClientClosed($Game) { }
    Install-NlClient -LegacyBindings $package $menuState $game -KeysFile $script:testProfile -NonInteractive
    Assert-Test ((Get-NlHash $script:newProfile) -eq $otherBefore) 'F1 installation leaves the non-selected account byte-identical'
    $menuReceipt=Read-NlJson (Join-Path $menuState 'client-receipt.json')
    $menuOriginal=@($menuReceipt.profiles | Where-Object path -eq $script:testProfile)[0].bindings.f1
    $menuText=[IO.File]::ReadAllText($script:testProfile); $menuBlock=Get-NlBindingBlock $menuText
    $menuBody=Set-NlBinding $menuBlock.Value 'f1' 'my later F1'
    [IO.File]::WriteAllText($script:testProfile,($menuText.Substring(0,$menuBlock.Index)+$menuBody+$menuText.Substring($menuBlock.Index+$menuBlock.Length)))
    $menuCustomHash=Get-NlHash $script:testProfile
    $menuMessages=@(Install-NlClient -LegacyBindings $package $menuState $game -KeysFile $script:testProfile -NonInteractive 3>&1)
    Assert-Test ((Get-NlHash $script:testProfile) -eq $menuCustomHash -and @($menuMessages | Where-Object { $_ -is [Management.Automation.WarningRecord] }).Count -eq 1) 'Repair preserves a later custom F1 and reports the conflict'
    $menuReceipt=Read-NlJson (Join-Path $menuState 'client-receipt.json')
    Assert-Test ($menuReceipt.menuEntry.status -eq 'preserved-custom' -and @($menuReceipt.profiles | Where-Object path -eq $script:testProfile)[0].bindings.f1 -eq $menuOriginal) 'A preserved custom F1 never replaces its original restore value'
    $conflicts=@(Restore-NlClient $menuState)
    Assert-Test ($conflicts.Count -eq 0 -and (Get-NlHash $script:testProfile) -eq $menuCustomHash) 'Restore leaves a later custom F1 untouched'
    Assert-Test ((Get-NlHash $script:newProfile) -eq $otherBefore) 'Restore does not rewrite a non-selected account F1 matching a legacy command'
    Install-NlClient -LegacyBindings $package $menuState $game -KeysFile $script:testProfile -NonInteractive
    $menuText=[IO.File]::ReadAllText($script:testProfile); $menuBlock=Get-NlBindingBlock $menuText
    $menuBody=Set-NlBinding $menuBlock.Value 'f1' 'css_nl'
    [IO.File]::WriteAllText($script:testProfile,($menuText.Substring(0,$menuBlock.Index)+$menuBody+$menuText.Substring($menuBlock.Index+$menuBlock.Length)))
    $conflicts=@(Restore-NlClient $menuState)
    Assert-Test ($conflicts.Count -eq 0 -and (Get-NlBinding (Get-NlBindingBlock ([IO.File]::ReadAllText($script:testProfile))).Value 'f1') -eq 'my later F1') 'Restore recognizes a legacy css_nl F1 after installing the new bootstrap'
    [IO.File]::WriteAllText($script:newProfile,$keys)
    # The default upgrade removes old owned bindings, retains original evidence,
    # and keeps keys the player changed after installation.
    [IO.File]::WriteAllText($script:testProfile,$keys)
    Install-NlClient -LegacyBindings $package $menuState $game -KeysFile $script:testProfile -NonInteractive
    $migrationText=[IO.File]::ReadAllText($script:testProfile)
    $migrationBlock=Get-NlBindingBlock $migrationText; $migrationBody=$migrationBlock.Value
    foreach ($key in $bindings.Keys) { $migrationBody=Set-NlBinding $migrationBody $key $bindings[$key] }
    $migrationBody=Set-NlBinding $migrationBody 'f2' 'later custom F2'
    [IO.File]::WriteAllText($script:testProfile,($migrationText.Substring(0,$migrationBlock.Index)+$migrationBody+$migrationText.Substring($migrationBlock.Index+$migrationBlock.Length)))
    $oldMigrationReceipt=Read-NlJson (Join-Path $menuState 'client-receipt.json')
    $oldMigrationReceipt.PSObject.Properties.Remove('controlsMode')
    Write-NlJson (Join-Path $menuState 'client-receipt.json') $oldMigrationReceipt
    Install-NlClient $package $menuState $game -NonInteractive
    $migratedBody=(Get-NlBindingBlock ([IO.File]::ReadAllText($script:testProfile))).Value
    Assert-Test ((Get-NlBinding $migratedBody 'f1') -eq 'buymenu' -and (Get-NlBinding $migratedBody 'mwheelup') -eq '+jump' -and (Get-NlBinding $migratedBody 'v') -eq '+voicerecord') 'Default migration restores owned F1 wheel and V from legacy receipt originals'
    Assert-Test ((Get-NlBinding $migratedBody 'f2') -eq 'later custom F2') 'Native migration keeps later custom bindings'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $game 'game/csgo/cfg/nadelab_keys.cfg')) -notmatch '(?im)^\s*bind\s') 'Migration neutralizes the old cfg bootstrap so a later launch cannot rebind'
    $migrationReceipt=Read-NlJson (Join-Path $menuState 'client-receipt.json')
    Assert-Test ($migrationReceipt.controlsMode -eq 'native' -and @($migrationReceipt.profiles).Count -eq 0 -and @(Get-ChildItem -LiteralPath (Join-Path $menuState 'client-history') -Directory).Count -gt 0) 'Migration archives old ownership before native installation'
    $migrationHash=Get-NlHash $script:testProfile
    $null=Restore-NlClient $menuState
    Assert-Test ((Get-NlHash $script:testProfile) -eq $migrationHash) 'Native uninstall never reclaims formerly owned keys'
    # Minimal fake Steam tree + signed-by-manifest fake dependencies exercise the
    # complete installation path. No downloaded/native game code is executed.
    foreach ($folder in @('game/bin/win64','game/csgo/bin','game/core','game/csgo/maps/prefabs','game/csgo/cfg')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $game $folder) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $game 'game/bin/win64/cs2.exe'),'inert fake executable for installation tests')
    [IO.File]::WriteAllText((Join-Path $game 'game/bin/win64/steam_appid.txt'),'730')
    [IO.File]::WriteAllText((Join-Path $game 'game/bin/win64/private-crash.mdmp'),'private runtime crash fixture')
    [IO.File]::WriteAllText((Join-Path $game 'game/bin/debug.log'),'private runtime log fixture')
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/steam.inf'),"ServerVersion=123`r`nSourceRevision=456`r`n")
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/gameinfo.gi'),'GameInfo { SearchPaths { Game csgo } }')
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/bin/server.dll'),'inert fake server file')
    [IO.File]::WriteAllText((Join-Path $game 'game/core/core.txt'),'fake core file')
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/maps/prefabs/test.vpk'),'fake prefab')
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/maps/de_mirage.vpk'),'fake map')
    $vpk=New-Object byte[] 29
    [BitConverter]::GetBytes([uint32]1437209140).CopyTo($vpk,0)
    [BitConverter]::GetBytes([uint32]2).CopyTo($vpk,4)
    [BitConverter]::GetBytes([uint32]1).CopyTo($vpk,8)
    [IO.File]::WriteAllBytes((Join-Path $game 'game/csgo/pak01_dir.vpk'),$vpk)
    $sourceInfoHash=Get-NlHash (Join-Path $game 'game/csgo/gameinfo.gi')
    foreach ($pluginFile in @('NadeLab.dll','NadeLab.Core.dll','NadeLab.deps.json')) {
        $path=Get-NlChild $package ('game/csgo/addons/counterstrikesharp/plugins/NadeLab/'+$pluginFile)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path,'fake plugin payload')
    }
    [IO.File]::WriteAllText((Join-Path $package 'game/csgo/cfg/nadelab_practice.cfg'),'css_nl_training 1')
    New-Item -ItemType Directory -Force -Path (Join-Path $package 'dependencies') | Out-Null
    $locked=@()
    foreach ($name in @('metamod','css-runtime')) {
        $path=Join-Path $package ('dependencies/'+$name+'.zip')
        $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry=$zip.CreateEntry('addons/'+$name+'/fixture.txt')
            $writer=New-Object IO.StreamWriter($entry.Open())
            try { $writer.Write('verified dependency fixture') } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
        $locked += [ordered]@{name=$name;url=('https://github.com/test/fixture/'+$name+'.zip');sha256=(Get-NlHash $path)}
    }
    Write-NlJson (Join-Path $package 'scripts/release/dependencies.lock.json') ([ordered]@{schema=1;packages=$locked})
    $manifestFiles=@()
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $package 'game') -File -Recurse)+@(Get-Item -LiteralPath (Join-Path $package 'scripts/release/dependencies.lock.json'))) {
        $manifestFiles += [ordered]@{path=$file.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-NlHash $file.FullName)}
    }
    Write-NlJson (Join-Path $package 'release-manifest.json') ([ordered]@{schema=1;version='test-fixture';files=$manifestFiles})
    $validated=Test-NlPackage $package
    Assert-Test ($validated.version -eq 'test-fixture') 'Complete release manifest accepted'
    $config=Install-NlInstance $package $state $game 27999
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $config.instance 'game/bin/win64/steam_appid.txt'))) 'Mutable engine app-id file is not mirrored as a Steam asset'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $config.instance 'game/bin/win64/private-crash.mdmp')) -and -not (Test-Path -LiteralPath (Join-Path $config.instance 'game/bin/debug.log'))) 'Client crash dumps and runtime logs are excluded from the instance'
    [IO.File]::WriteAllText((Join-Path $config.instance 'game/bin/win64/steam_appid.txt'),'2347773')
    $freshness=@(Get-NlAssets $game | Where-Object { Test-NlAssetChanged $_ $config.instance })
    Assert-Test ($freshness.Count -eq 0) 'A normal server-written app-id does not look like a Steam update'
    Assert-Test ($config.ready -and (Read-NlInstallation $package).ready) 'Full fake Steam installation reaches a durable ready state'
    Assert-NlInstalledPayload $validated $config.instance
    Assert-Test $true 'Instance binaries and UI match the package manifest'
    $instanceUi=Get-NlChild $config.instance ('game/csgo/'+$script:NlUi[0])
    [IO.File]::WriteAllText($instanceUi,'a stale UI from another installation')
    Assert-Throws { Assert-NlInstalledPayload $validated $config.instance } 'Start refuses stale installed UI even when the version string matches'
    Copy-NlFile (Get-NlChild $package ('game/csgo/'+$script:NlUi[0])) $instanceUi
    $instanceDll=Get-NlChild $config.instance 'game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll'
    [IO.File]::WriteAllText($instanceDll,'old assembly with the same version label')
    Assert-Throws { Assert-NlInstalledPayload $validated $config.instance } 'Start refuses stale installed binaries even when the version string matches'
    Copy-NlFile (Get-NlChild $package 'game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll') $instanceDll
    Assert-Test ((Get-NlHash (Join-Path $game 'game/csgo/gameinfo.gi')) -eq $sourceInfoHash) 'Instance setup never changes Steam gameinfo'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $game 'game/csgo/addons'))) 'Instance setup never installs plugins into Steam'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $config.instance 'game/csgo/gameinfo.gi')).Contains('Game csgo/addons/metamod')) 'Metamod search path only appears in the isolated instance'
    $searchPaths=[regex]::Match([IO.File]::ReadAllText((Join-Path $config.instance 'game/csgo/gameinfo.gi')),'SearchPaths\s*\{[^}]*\}').Value
    Assert-Test (-not $searchPaths.Contains(':') -and $searchPaths.Contains('Game core')) 'Source 2 uses local search paths that work across Steam volumes'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $state 'rcon.local')).Length -eq 48) 'A fresh cryptographic local RCON secret was generated'
    $savedSecret=Get-NlHash (Join-Path $state 'rcon.local')
    $record=Join-Path $config.instance 'game/csgo/addons/counterstrikesharp/plugins/NadeLab/player-library.json'
    [IO.File]::WriteAllText($record,'player route fixture')
    $null=Install-NlInstance $package $state $game 27999
    Assert-Test ([IO.File]::ReadAllText($record) -eq 'player route fixture') 'Repair preserves saved player data'
    Assert-Test ((Get-NlHash (Join-Path $state 'rcon.local')) -eq $savedSecret) 'Repair preserves the existing private local secret'
    # An obsolete cache file must never be copied into the live dependency tree.
    $cached=Get-NlChild $state 'dependencies/metamod/addons/metamod/unexpected.dll'
    [IO.File]::WriteAllText($cached,'unexpected stale cached binary')
    $null=Install-NlInstance $package $state $game 27999
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $config.instance 'game/csgo/addons/metamod/unexpected.dll'))) 'Verified dependency extraction discards extra cached files'
    $asset=@(Get-NlAssets $game | Where-Object relative -eq 'csgo/maps/de_mirage.vpk')[0]
    Assert-Test (-not (Test-NlAssetChanged $asset $config.instance)) 'Linked/copied map freshness is recognized'
    $mapTarget=Join-Path $config.instance 'game/csgo/maps/de_mirage.vpk'
    $sourceMap=Join-Path $game 'game/csgo/maps/de_mirage.vpk'
    $newMap=Join-Path $testRoot 'new-map.vpk'; [IO.File]::WriteAllText($newMap,'new test resource with another size')
    Copy-NlFile $newMap $sourceMap
    $asset=@(Get-NlAssets $game | Where-Object relative -eq 'csgo/maps/de_mirage.vpk')[0]
    Assert-Test (Test-NlAssetChanged $asset $config.instance) 'Steam-style source file replacement invalidates old links'
    $null=Install-NlInstance $package $state $game 27999
    Assert-Test ((Get-NlHash $mapTarget) -eq (Get-NlHash $sourceMap)) 'Repair refreshes a replaced map resource'
    $sourceMapHash=Get-NlHash $sourceMap
    Copy-NlFile (Join-Path $game 'game/core/core.txt') $mapTarget
    Assert-Test ((Get-NlHash $sourceMap) -eq $sourceMapHash) 'Replacing an instance hard link never overwrites Steam bytes'
    # Exercise one-click entry with an inert Steam file and a mocked launcher.
    # Never dispatch a real application or rewrite Steam launch options.
    $script:testSteam=Join-Path $testRoot 'steam'
    New-Item -ItemType Directory -Force -Path $script:testSteam | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:testSteam 'steam.exe'),'inert Steam fixture')
    function Get-NlSteamRoots { return @($script:testSteam,$script:testSteam.ToUpperInvariant()) }
    $script:fakeClientProcesses=@(); $script:launchRequests=@()
    function Get-NlCsProcesses { return @($script:fakeClientProcesses) }
    function Start-Process { param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru,$RedirectStandardOutput,$RedirectStandardError)
        $script:launchRequests+=@([pscustomobject]@{FilePath=$FilePath;Arguments=$ArgumentList;WindowStyle=$WindowStyle})
        if($PassThru) { return [pscustomobject]@{Id=4242} }
    }
    function Get-ItemProperty { param($LiteralPath,$ErrorAction)
        if($LiteralPath -eq 'HKCU:\Software\Valve\Steam') { return [pscustomobject]@{SteamPath=$testRoot} }
        if($LiteralPath -eq 'HKCU:\Software\Valve\Steam\ActiveProcess') { return [pscustomobject]@{ActiveUser=123} }
        throw 'Unexpected registry access in launch fixture'
    }
    foreach($helper in @('tools/input/NadeLab.Input.dll','instance/game/csgo/addons/counterstrikesharp/dotnet/dotnet.exe')) {
        $fixture=Join-Path $package $helper
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixture) | Out-Null
        [IO.File]::WriteAllText($fixture,'inert input fixture')
    }
    $fixture=Join-Path $game 'game/bin/win64/cs2.exe'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixture) | Out-Null
    [IO.File]::WriteAllText($fixture,'inert client fixture')
    Assert-Throws { Get-NlClientLaunchPlan $package } 'One-click entry refuses a restored client before starting the game'
    Install-NlClient $package $state $game -KeysFile $script:testProfile -NonInteractive
    $launch=Get-NlClientLaunchPlan $package
    Assert-Test (-not $launch.AlreadyRunning -and $launch.Steam -eq (Join-Path $script:testSteam 'steam.exe')) 'Duplicate Windows path casing resolves to one Steam installation'
    Assert-Test (($launch.Arguments -join ' ') -eq '-applaunch 730 +connect 127.0.0.1:27999') 'Default Steam entry connects without executing any binding cfg'
    $launchReceipt=Read-NlJson (Join-Path $state 'client-receipt.json')
    $launchReceipt.controlsMode='legacy'
    Copy-NlFile (Join-Path $package 'game/csgo/cfg/nadelab_keys.cfg') (Join-Path $game 'game/csgo/cfg/nadelab_keys.cfg')
    Write-NlJson (Join-Path $state 'client-receipt.json') $launchReceipt
    Assert-Test (((Get-NlClientLaunchPlan $package).Arguments -join ' ') -eq '-applaunch 730 +exec nadelab_keys +connect 127.0.0.1:27999') 'Only an explicit legacy-mode receipt authorizes loading the legacy cfg'
    $launchReceipt.PSObject.Properties.Remove('controlsMode')
    [IO.File]::WriteAllText((Join-Path $game 'game/csgo/cfg/nadelab_keys.cfg'),$nativeCfg,(New-Object Text.UTF8Encoding($false)))
    Write-NlJson (Join-Path $state 'client-receipt.json') $launchReceipt
    Assert-Test ('+exec' -notin (Get-NlClientLaunchPlan $package).Arguments) 'An old receipt without opt-in does not authorize rebinding at launch'
    Start-NlClient $package
    Assert-Test ($script:launchRequests.Count -eq 2 -and @($script:launchRequests | Where-Object WindowStyle -ne 'Hidden').Count -eq 0) 'Native entry with empty receipt profile starts hidden input companion and Steam'
    Assert-Test (($script:launchRequests[0].Arguments -join ' ') -match [regex]::Escape($script:testProfile)) 'Native companion resolves the active profile read-only'
    $script:fakeClientProcesses=@([pscustomobject]@{Path=(Join-Path $game 'game/bin/win64/cs2.exe')})
    Assert-Test (Get-NlClientLaunchPlan $package).AlreadyRunning 'A running game is detected without stopping it'
    Start-NlClient $package
    Assert-Test (@($script:launchRequests | Where-Object FilePath -eq (Join-Path $script:testSteam 'steam.exe')).Count -eq 1) 'An already-running game receives instructions without a duplicate Steam launch'
    $clientKeys=Join-Path $game 'game/csgo/cfg/nadelab_keys.cfg'
    [IO.File]::WriteAllText($clientKeys,'changed by player')
    Assert-Throws { Get-NlClientLaunchPlan $package } 'One-click entry refuses mismatched client controls'
    Assert-Test ([IO.File]::ReadAllText($clientKeys) -eq 'changed by player') 'Launch preflight preserves later client modifications'
    $tampered=Join-Path $package 'game/csgo/addons/counterstrikesharp/plugins/NadeLab/NadeLab.dll'
    [IO.File]::WriteAllText($tampered,'tampered release payload')
    Assert-Throws { Test-NlPackage $package } 'A modified plugin fails package verification'
    $result=[ordered]@{passed=$checks.Count;powerShell=$PSVersionTable.PSVersion.ToString();checks=@($checks.ToArray());nativeGameLaunched=$false;steamFilesModified=$false}
    if ($OutputDirectory) { Write-NlJson (Join-Path $OutputDirectory 'release-tools-tests.json') $result }
    Write-Host ("PASS: {0} release tool checks. No game processes launched or Steam files modified." -f $checks.Count)
} finally {
    # This is the literal, unique fixture directory created at the start of this test.
    $expected=$testParent.TrimEnd('\','/')+'\'
    if ($testRoot.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $testRoot)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
