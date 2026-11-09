"""Stage optional NadeLab controls without changing personal keys by default."""
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGACY = {'z': 'css_nl_panel_up', 'c': 'css_nl_panel_down'}
NATIVE_CFG = '// NadeLab native controls: safe for historical autoexec entries; no personal bindings.\ncss_nl_controls native\n'


def steam_roots():
    """Discover the local Steam installation without choosing a user profile."""
    candidates = []
    try:
        import winreg
        for hive, name, value in ((winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
                                  (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath')):
            try:
                with winreg.OpenKey(hive, name) as key:
                    candidates.append(Path(winreg.QueryValueEx(key, value)[0]))
            except OSError:
                pass
    except ImportError:
        pass
    for name in ('ProgramFiles(x86)', 'ProgramFiles'):
        if os.environ.get(name):
            candidates.append(Path(os.environ[name]) / 'Steam')
    return list(dict.fromkeys(path.resolve() for path in candidates if path.is_dir()))


def discover_clients():
    cfgs, profiles = set(), set()
    for steam in steam_roots():
        libraries = {steam}
        folders = steam / 'steamapps/libraryfolders.vdf'
        if folders.is_file():
            text = folders.read_text(encoding='utf-8-sig')
            for value in re.findall(r'"(?:path|\d+)"\s*"((?:\\.|[^"\\])*)"', text):
                libraries.add(Path(value.replace('\\\\', '\\')).resolve())
        for library in libraries:
            cfg = library / 'steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg'
            if cfg.is_dir():
                cfgs.add(cfg.resolve())
        userdata = steam / 'userdata'
        if userdata.is_dir():
            profiles.update(path.resolve() for path in userdata.glob('*/730/local/cfg/cs2_user_keys_0_slot*.vcfg') if path.is_file())
    return {'cfg_directories': sorted(map(str, cfgs)), 'keys_files': sorted(map(str, profiles))}


def selected_paths(args):
    found = discover_clients() if args.list_clients or args.cfg_dir is None or (args.keys_file is None and not args.stage_for_next_launch) else None
    if args.list_clients:
        print(json.dumps(found, ensure_ascii=False, indent=2))
        return None
    if args.keys_file is None and (not args.stage_for_next_launch or args.legacy_current):
        candidates = '\n'.join((found or discover_clients())['keys_files']) or '(none discovered)'
        raise RuntimeError('Choose the intended Steam profile explicitly with --keys-file. No files changed. Discovered profiles:\n' + candidates)
    if args.cfg_dir is None:
        candidates = found['cfg_directories']
        if len(candidates) != 1:
            raise RuntimeError('Choose --cfg-dir; exactly one CS2 installation was not found. No files changed. Discovered directories:\n' + ('\n'.join(candidates) or '(none discovered)'))
        cfg = Path(candidates[0])
    else:
        cfg = args.cfg_dir
    return (args.keys_file.resolve() if args.keys_file else None), cfg.resolve()


def binding_match(block, key):
    pattern = rf'(?im)^(\s*"{re.escape(key)}"\s*")(?P<command>(?:\\.|[^"\\\r\n])*)("[^\r\n]*)\r?$'
    matches = list(re.finditer(pattern, block))
    if len(matches) > 1:
        raise RuntimeError(f'Duplicate {key} binding; no files changed.')
    return matches[0] if matches else None


def training_bindings(source):
    """Read both single commands and quoted client-side command sequences."""
    bindings = {}
    for line in source.splitlines():
        if not re.match(r'^\s*bind\s', line, re.I):
            continue
        match = re.fullmatch(r'\s*bind\s+(\S+)\s+(?:"([^"\r\n]+)"|(\S+))\s*', line, re.I)
        if not match:
            raise RuntimeError('Invalid training binding: ' + line)
        key, quoted, bare = match.groups()
        if key.lower() in {item.lower() for item in bindings}:
            raise RuntimeError('Duplicate training binding: ' + key)
        bindings[key] = quoted or bare
    return bindings


def original_legacy_binding(keys, key):
    """Use an attributed pre-install backup, never a guessed radio default."""
    folder = ROOT / 'artifacts/client-bindings'
    if not folder.exists():
        return None
    for backup in sorted(folder.iterdir()):
        manifest_file = backup / 'manifest.json'
        if not manifest_file.is_file():
            continue
        manifest = json.loads(manifest_file.read_text(encoding='utf-8'))
        # The early flight installer used {"keys": path, ...}; newer full
        # installers use [{"path": path, "existed": bool}, ...]. Both require
        # an exact profile match before a historical binding is trusted.
        if isinstance(manifest, dict):
            manifest = [{'path': manifest['keys'], 'existed': True}] if isinstance(manifest.get('keys'), str) else []
        if not isinstance(manifest, list) or not any(isinstance(item, dict) and item.get('existed') and
                isinstance(item.get('path'), str) and Path(item['path']).resolve() == keys for item in manifest):
            continue
        source = backup / keys.name
        if source.is_file():
            match = binding_match(source.read_text(encoding='utf-8-sig'), key)
            command = match['command'] if match else '<unbound>'
            if command != LEGACY[key]:
                return command, str(source)
        # Older installer receipts may retain an original line even when a
        # complete vcfg copy is unavailable. Its manifest still must match.
        for receipt_name in ('bindings.json', 'binds.json'):
            receipt = backup / receipt_name
            if not receipt.is_file():
                continue
            entries = json.loads(receipt.read_text(encoding='utf-8'))
            if not isinstance(entries, list):
                continue
            for entry in entries:
                if str(entry.get('key', '')).lower() != key or 'previous_line' not in entry:
                    continue
                line = entry['previous_line']
                match = binding_match(line, key) if line else None
                command = match['command'] if match else '<unbound>'
                if command != LEGACY[key]:
                    return command, str(receipt)
    return None


def legacy_plan(keys, current):
    plan = []
    for key, old_command in LEGACY.items():
        value = current.get(key)
        if value != old_command:
            plan.append({'key': key, 'status': 'kept_current', 'current_command': value})
            continue
        original = original_legacy_binding(keys, key)
        if original is None:
            plan.append({'key': key, 'status': 'missing_backup', 'current_command': value})
            continue
        command, evidence = original
        plan.append({'key': key, 'status': 'restore', 'current_command': value,
                     'original_command': command, 'source': evidence})
    return plan


def native_restore(keys, cfg, dry_run):
    """Restore only attributed NadeLab values; preserve later custom bindings."""
    owned = {key.lower(): {command} for key, command in
             training_bindings((ROOT / 'configs/nadelab_keys.cfg').read_text(encoding='utf-8')).items()}
    owned['f1'].add('css_nl')
    for key, command in LEGACY.items():
        owned.setdefault(key, set()).add(command)
    records = []
    for folder in sorted((ROOT / 'artifacts/client-bindings').glob('*')):
        manifest_file = folder / 'manifest.json'
        if not manifest_file.is_file() or (folder / 'native-restore.json').exists():
            continue
        manifest = json.loads(manifest_file.read_text(encoding='utf-8-sig'))
        entries = []
        if isinstance(manifest, dict):
            if 'previous_v' in manifest:
                entries.append({'key': 'v', 'command': 'noclip', 'previous_line': manifest['previous_v']})
            manifest = [{'path': manifest['keys'], 'existed': True}] if isinstance(manifest.get('keys'), str) else []
        if not isinstance(manifest, list) or not any(isinstance(item, dict) and item.get('existed') and
                isinstance(item.get('path'), str) and Path(item['path']).resolve() == keys for item in manifest):
            continue
        for name in ('bindings.json', 'binds.json'):
            receipt = folder / name
            if receipt.is_file():
                data = json.loads(receipt.read_text(encoding='utf-8-sig'))
                if isinstance(data, list):
                    entries.extend(item for item in data if isinstance(item, dict))
        for item in entries:
            key, command = str(item.get('key', '')).lower(), item.get('command')
            if key in owned and isinstance(command, str):
                owned[key].add(command)
        original = folder / keys.name
        records.append((original.read_text(encoding='utf-8-sig') if original.is_file() else None, entries, str(folder)))
    before = keys.read_bytes()
    text = before.decode('utf-8-sig')
    block = re.search(r'("bindings"\s*\{)([^{}]*)(\})', text)
    if not block:
        raise RuntimeError('Unknown client binding format; no files changed.')
    body = block[2]
    plan = []
    for key, commands in owned.items():
        current = binding_match(body, key)
        if current is None or current['command'] not in commands:
            plan.append({'key': key, 'status': 'kept_current'})
            continue
        evidence = None
        for original, entries, folder in reversed(records):
            # A full-file backup proves the profile identity, not which keys
            # that installer owned. Require a per-key write receipt first.
            entry = next((item for item in entries if str(item.get('key', '')).lower() == key and
                          isinstance(item.get('command'), str)), None)
            if entry is None:
                continue
            if 'previous_line' in entry:
                found = binding_match(entry['previous_line'], key) if entry['previous_line'] else None
                value = found['command'] if found else None
            elif original is not None:
                found = binding_match(original, key)
                value = found['command'] if found else None
            else:
                continue
            # A pre-upgrade snapshot may already contain an earlier training
            # command (for example F5 retarget). It is not a personal original.
            if value not in commands and not (value and re.search(r'(?i)(?:^|;)\s*css_nl(?:_\w+)?\b', value)):
                evidence = (value, folder)
                break
        if evidence is None:
            plan.append({'key': key, 'status': 'missing_backup', 'current_command': current['command']})
            continue
        value, folder = evidence
        plan.append({'key': key, 'status': 'restore', 'current_command': current['command'],
                     'original_command': value, 'source': folder})
        if value is None:
            body = body[:current.start()] + body[current.end():]
        else:
            body = body[:current.start('command')] + value + body[current.end('command'):]
    binding_cfg = cfg / 'nadelab_keys.cfg'
    cfg_before = binding_cfg.read_bytes() if binding_cfg.exists() else None
    cfg_after = NATIVE_CFG.encode('utf-8')
    report = {'mode': 'restore-native', 'keys': str(keys), 'bindings': plan,
              'native_cfg': str(binding_cfg), 'neutralize_legacy_bootstrap': cfg_before != cfg_after}
    if dry_run:
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return
    if body != block[2] or cfg_before != cfg_after:
        backup = ROOT / 'artifacts/client-bindings' / datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f-native')
        backup.mkdir(parents=True, exist_ok=False)
        (backup / keys.name).write_bytes(before)
        if cfg_before is not None:
            (backup / binding_cfg.name).write_bytes(cfg_before)
        (backup / 'manifest.json').write_text(json.dumps([{'path': str(keys), 'existed': True},
            {'path': str(binding_cfg), 'existed': cfg_before is not None}]), encoding='utf-8')
        (backup / 'native-restore.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        if keys.read_bytes() != before:
            raise RuntimeError('Client bindings changed during restoration; no keys changed.')
        updated = text[:block.start(2)] + body + text[block.end(2):]
        try:
            if body != block[2]:
                keys.write_bytes((b'\xef\xbb\xbf' if before.startswith(b'\xef\xbb\xbf') else b'') + updated.encode('utf-8'))
            binding_cfg.write_bytes(cfg_after)
        except Exception:
            keys.write_bytes(before)
            if cfg_before is not None:
                binding_cfg.write_bytes(cfg_before)
            elif binding_cfg.exists():
                binding_cfg.unlink()
            raise
    print(json.dumps(report, ensure_ascii=False, indent=2))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--keys-file', type=Path,
                        help='explicitly select the CS2 user keys .vcfg to modify; never chooses an account automatically')
    parser.add_argument('--cfg-dir', type=Path,
                        help='CS2 game/csgo/cfg directory; auto-discovered only if exactly one installation exists')
    parser.add_argument('--list-clients', action='store_true', help='list discovered CS2 installations and profile paths without changing files')
    parser.add_argument('--dry-run', action='store_true', help='show binding changes without writing files')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--legacy-bindings', action='store_true',
                      help='explicitly opt in to fourteen legacy bindings, replacing their current personal assignments')
    mode.add_argument('--restore-native', action='store_true',
                      help='restore only still-owned legacy bindings from matching historical backups; supports --dry-run')
    parser.add_argument('--stage-for-next-launch', action='store_true',
                        help='write only the standalone training cfg; does not edit user keys or autoexec; run exec nadelab_keys manually to apply')
    parser.add_argument('--legacy-current', action='append', default=[], metavar='KEY=COMMAND',
                        help='stage only: caller-verified current Z/C binding; creates a manual, once-only migration cfg using matching backups')
    args = parser.parse_args()
    if args.restore_native and (args.stage_for_next_launch or args.legacy_current):
        parser.error('--restore-native cannot be combined with staging or --legacy-current')
    if not args.legacy_bindings and args.legacy_current:
        parser.error('--legacy-current requires explicit --legacy-bindings')
    if not args.legacy_bindings and not args.restore_native:
        args.stage_for_next_launch = True
    selected = selected_paths(args)
    if selected is None:
        return
    keys, cfg = selected
    if args.legacy_current and not args.stage_for_next_launch:
        raise RuntimeError('--legacy-current is only for staging; normal installs inspect the closed client vcfg directly.')
    if not cfg.is_dir() or (not args.stage_for_next_launch and (keys.suffix.lower() != '.vcfg' or not keys.is_file())):
        raise RuntimeError('Client keys file or cfg directory does not exist; use --keys-file and --cfg-dir. No files changed.')
    # Client shutdown would overwrite its vcfg. Never close it automatically.
    if not args.dry_run and not args.stage_for_next_launch:
        running = subprocess.run(['powershell', '-NoProfile', '-Command',
            "Get-Process cs2 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path"],
            capture_output=True, text=True, check=False)
        client_exe = str(cfg.parents[1] / 'bin/win64/cs2.exe').replace('\\', '/').lower()
        if any(line.strip().replace('\\', '/').lower() == client_exe for line in running.stdout.splitlines()):
            raise RuntimeError('Exit the CS2 client before installing bindings; no files changed.')
    if args.restore_native:
        native_restore(keys, cfg, args.dry_run)
        return
    source = (ROOT / 'configs/nadelab_keys.cfg').read_text(encoding='utf-8') if args.legacy_bindings else NATIVE_CFG
    bindings = training_bindings(source)
    changes = []
    updated = None
    migration = []
    migration_text = None
    if args.stage_for_next_launch:
        # The running client owns its vcfg. Do not inspect, back up, or change
        # that file in staging mode; only install the next-launch cfg layer.
        changes = [{'key': key, 'command': command} for key, command in bindings.items()] if args.legacy_bindings else []
        current = {}
        for value in args.legacy_current:
            key, separator, command = value.partition('=')
            key = key.lower()
            if not separator or key not in LEGACY or key in current or any(c in command for c in '\r\n\0'):
                raise RuntimeError('--legacy-current needs one unique Z/C=COMMAND value per verified key.')
            current[key] = command
        if current:
            migration = legacy_plan(keys, current)
            if any(item['status'] == 'missing_backup' for item in migration):
                raise RuntimeError('No matching original Z/C backup; refusing to guess the previous binding. No files changed.')
            commands = []
            for item in migration:
                if item['status'] != 'restore':
                    continue
                key, command = item['key'], item['original_command']
                commands.append(f'unbind {key}' if command in ('', '<unbound>') else f'bind {key} "{command}"')
            migration_text = ('// Run manually ONCE for this verified upgrade; never add to autoexec.\n' +
                              '\n'.join(commands) + '\nexec nadelab_keys\n')
    else:
        original = keys.read_text(encoding='utf-8-sig')
        match = re.search(r'("bindings"\s*\{)([^{}]*)(\})', original)
        if not match:
            raise RuntimeError('Unknown client binding format; no files changed.')
        block = match[2]
        current = {key: found['command'] if (found := binding_match(block, key)) else None for key in LEGACY}
        migration = legacy_plan(keys, current)
        replacements = dict(bindings)
        for item in migration:
            if item['status'] == 'restore':
                replacements[item['key']] = item['original_command']
        for key, command in replacements.items():
            before = binding_match(block, key)
            changes.append({'key': key, 'command': command, 'previous_line': before[0].strip() if before else None})
            if before:
                block = block[:before.start('command')] + command + block[before.end('command'):]
            else:
                block = block.rstrip()+f'\n\t\t"{key}"\t\t"{command}"\n\t'
        updated = original[:match.start(2)]+block+original[match.end(2):]
    if args.dry_run:
        print(json.dumps({'mode': 'native' if not args.legacy_bindings else ('stage-for-next-launch' if args.stage_for_next_launch else 'install'),
                          'keys': None if args.stage_for_next_launch else str(keys), 'cfg': str(cfg),
                          'bindings': changes, 'legacy_navigation': migration,
                          'manual_migration_cfg': migration_text}, ensure_ascii=False, indent=2))
        return
    backup = ROOT / 'artifacts/client-bindings' / datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f-controls')
    backup.mkdir(parents=True, exist_ok=False)
    files = ([keys] if not args.stage_for_next_launch else []) + [cfg / 'nadelab_keys.cfg']
    if migration_text is not None:
        files.append(cfg / 'nadelab_migrate_keys.cfg')
    manifest = []
    for file in files:
        manifest.append({'path': str(file), 'existed': file.exists()})
        if file.exists():
            shutil.copy2(file, backup / file.name)
    (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    (backup / 'bindings.json').write_text(json.dumps(changes, ensure_ascii=False, indent=2), encoding='utf-8')
    (backup / 'legacy-navigation.json').write_text(json.dumps(migration, ensure_ascii=False, indent=2), encoding='utf-8')
    try:
        if updated is not None:
            keys.write_text(updated, encoding='utf-8')
        (cfg / 'nadelab_keys.cfg').write_text(source, encoding='utf-8')
        if migration_text is not None:
            (cfg / 'nadelab_migrate_keys.cfg').write_text(migration_text, encoding='utf-8')
        if updated is not None:
            for key, command in bindings.items():
                assert len(re.findall(rf'(?im)^\s*"{re.escape(key)}"\s*"{re.escape(command)}"\s*$', keys.read_text(encoding='utf-8'))) == 1
    except Exception:
        for entry in manifest:
            file = Path(entry['path'])
            if entry['existed']:
                shutil.copy2(backup / file.name, file)
            elif file.exists():
                file.unlink()
        raise
    if not args.legacy_bindings:
        print(f'Native controls ready. Personal keys and autoexec were not read or changed. Hold Walk+Reload to open the menu. Historical exec nadelab_keys entries now load a native shim with no bindings. Install optional legacy shortcuts only with --legacy-bindings. Restore a previous installation with --restore-native (preview with --dry-run). Backup: {backup}')
    elif args.stage_for_next_launch:
        print(f'Staged training cfg files. User vcfg and autoexec were not read or changed. Run exec nadelab_keys manually in the CS2 console to apply; restarting alone does not load these controls. WASD are not rebound and always move, including with menus open. The wheel selects menus or cycles weapons outside menus. UI resources must be installed separately. Backup: {backup}')
        if migration_text is not None:
            print('After restarting, run exec nadelab_migrate_keys ONCE to restore only the verified legacy Z/C bindings and load the new controls. This file is not in autoexec; do not run it again after customizing Z/C.')
    else:
        print(f'Installed F1-F9, wheel, Up/Down arrows and V in the explicitly selected profile. Autoexec was not read or changed. F1 panel; wheel or arrows select; E confirms; R returns. W/S retain normal movement bindings. Legacy Z/C are restored only when still bound to NadeLab and an original backup exists; details: legacy-navigation.json. E/R/Shift, G/X and number weapon slots unchanged. UI resources must be installed separately. Backup: {backup}')
        if any(item['status'] == 'missing_backup' for item in migration):
            print('WARNING: old NadeLab Z/C bindings remain because no matching original backup was found; inspect legacy-navigation.json. No radio defaults were guessed.')

if __name__ == '__main__':
    main()
