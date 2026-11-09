"""Start/stop the project-owned localhost CS2 dedicated training instance; issue RCON commands."""
import argparse
import hashlib
import json
import os
import re
import shutil
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
INSTANCE = ROOT / 'server'
SUPPORTED_MAPS = ('de_mirage', 'de_dust2', 'de_inferno', 'de_nuke', 'de_ancient',
                  'de_anubis', 'de_cache', 'de_overpass', 'de_train', 'de_vertigo')
DEFAULT_MAP = 'de_mirage'


def read_version(game_directory):
    path = Path(game_directory)/'steam.inf'
    if not path.exists():
        return {}
    return dict(line.split('=', 1) for line in path.read_text(encoding='utf-8-sig').splitlines() if '=' in line)


def find_source_game(config):
    if config.get('SourceGameDirectory'):
        return Path(config['SourceGameDirectory'])
    # Recover metadata for instances created by older setup scripts.
    game = Path(config['GameDirectory'])
    for asset in re.findall(r'^\s*Game\s+"([^"]+)"', (game/'gameinfo.gi').read_text(encoding='utf-8-sig'), re.M):
        candidate = (game.parent/asset).resolve()
        if candidate != game.resolve() and (candidate/'steam.inf').exists():
            return candidate.parent
    raise RuntimeError('Steam asset source not found. Run scripts/setup-local-server.ps1 again.')


def require_completed_update(source):
    manifest = source.parents[2]/'appmanifest_730.acf'
    if manifest.exists():
        state = re.search(r'"StateFlags"\s+"(\d+)"', manifest.read_text(encoding='utf-8-sig'))
        if state is None or state[1] != '4':
            raise RuntimeError('Steam is updating CS2 or has a pending update. Finish the Steam update before starting the lab.')
    index = source/'csgo/pak01_dir.vpk'
    with index.open('rb') as stream:
        header = stream.read(12)
    if len(header) != 12:
        raise RuntimeError('Steam CS2 resource index is incomplete. Verify game files in Steam.')
    magic, version, tree_size = struct.unpack('<III', header)
    if magic != 0x55AA1234 or version not in (1, 2) or tree_size == 0 or index.stat().st_size < (28 if version == 2 else 12)+tree_size:
        raise RuntimeError('Steam CS2 resource index is invalid. Verify game files in Steam.')


def needs_refresh(config, source):
    local = Path(config['GameDirectory'])
    installed = read_version(source/'csgo')
    copied = read_version(local)
    if not installed.get('ServerVersion') or not installed.get('SourceRevision'):
        raise RuntimeError('Steam CS2 version metadata is incomplete.')
    if any(installed.get(key) != copied.get(key) for key in ('ServerVersion', 'SourceRevision')):
        return f"CS2 {copied.get('PatchVersion', 'unknown')} -> {installed.get('PatchVersion', 'unknown')}"
    # Steam replaces pack files during updates; old hard links still reference old files.
    for current in (source/'csgo').glob('pak01*.vpk'):
        target = local/current.name
        if resource_changed(current, target):
            return f'Steam resource link changed: {current.name}'
    for name in SUPPORTED_MAPS:
        for suffix in ('.vpk', '_vanity.vpk'):
            relative = Path('maps')/(name+suffix)
            current, target = source/'csgo'/relative, local/relative
            if current.is_file():
                if resource_changed(current, target):
                    return f'Training map resource changed: {relative.as_posix()}'
            elif target.exists():
                return f'Training map removed from Steam installation: {relative.as_posix()}'
    prefabs = source/'csgo/maps/prefabs'
    if prefabs.is_dir():
        for current in sorted(prefabs.rglob('*')):
            if current.is_file():
                relative = current.relative_to(source/'csgo')
                if resource_changed(current, local/relative):
                    return f'Training prefab changed: {relative.as_posix()}'
    return None


def resource_changed(current, target):
    if not target.is_file():
        return True
    source_info, target_info = current.stat(), target.stat()
    return (source_info.st_size, source_info.st_mtime_ns) != (target_info.st_size, target_info.st_mtime_ns)


def refresh_instance(config, source):
    shell = config.get('PowerShellExecutable') or shutil.which('pwsh')
    if not shell or not Path(shell).exists():
        raise RuntimeError('PowerShell 7 is required to refresh this instance. Run scripts/setup-local-server.ps1 in PowerShell 7.')
    subprocess.run([shell, '-NoProfile', '-NonInteractive', '-File', str(ROOT/'scripts/setup-local-server.ps1'),
                    '-GameInstallation', str(source.parent)], cwd=ROOT, check=True)
    updated = json.loads((INSTANCE/'instance.json').read_text(encoding='utf-8-sig'))
    require_completed_update(source)
    if needs_refresh(updated, source):
        raise RuntimeError('Steam files changed during refresh. Wait for the update to finish and start again.')
    return updated


def startup_error():
    log = INSTANCE/'logs/stdout.log'
    if not log.exists():
        return ''
    with log.open('rb') as stream:
        stream.seek(max(0, log.stat().st_size-65536))
        tail = stream.read().decode('utf-8', errors='replace')
    fatal = re.findall(r'FATAL ERROR:[^\r\n]*(?:\r?\n[^\r\n]+)?', tail)
    return fatal[-1] if fatal else ''


def read_packet(sock):
    def exact(n):
        data = bytearray()
        while len(data) < n:
            chunk = sock.recv(n-len(data))
            if not chunk:
                raise ConnectionError('RCON socket closed')
            data.extend(chunk)
        return bytes(data)
    size = struct.unpack('<i', exact(4))[0]
    if not 10 <= size <= 1024*1024:
        raise ValueError('Invalid RCON packet length')
    payload = exact(size)
    return struct.unpack('<ii', payload[:8]) + (payload[8:-2].decode('utf-8', errors='replace'),)


def send_packet(sock, ident, kind, body):
    data = struct.pack('<ii', ident, kind) + body.encode() + b'\0\0'
    sock.sendall(struct.pack('<i', len(data)) + data)


def rcon(command, timeout=8):
    port = json.loads((INSTANCE/'instance.json').read_text(encoding='utf-8-sig'))['Port']
    with socket.create_connection(('127.0.0.1', port), timeout=timeout) as sock:
        send_packet(sock, 1, 3, (INSTANCE/'rcon.local').read_text().strip())
        for _ in range(4):
            ident, kind, body = read_packet(sock)
            if ident == -1:
                raise RuntimeError('RCON authentication rejected')
            if kind == 2:
                break
        else:
            raise RuntimeError('No RCON authentication response')
        send_packet(sock, 2, 2, command)
        output = []
        for _ in range(100):
            try:
                ident, kind, body = read_packet(sock)
            except TimeoutError:
                break
            except ConnectionError:
                if command.strip() == 'quit':
                    return 'Local server stopped.'
                raise
            if ident == 2:
                output.append(body)
                # CS2 can discard the first response when a second command is sent
                # in the same burst. Collect fragments using an idle timeout instead.
                sock.settimeout(.5)
        return ''.join(output)


def validate_map(map_name):
    if map_name not in SUPPORTED_MAPS:
        raise ValueError(f'Unsupported training map: {map_name}. Choose one of: {", ".join(SUPPORTED_MAPS)}')
    return map_name


def ready_map(diagnostic):
    match = re.search(r'SELFTEST PASS:\s+native\s+(de_[a-z0-9_]+)\b', diagnostic)
    return match[1] if match and match[1] in SUPPORTED_MAPS else None


def server_arguments(config, map_name=DEFAULT_MAP):
    validate_map(map_name)
    return [config['Executable'], '-dedicated', '-insecure', '-usercon',
            '-ip', '127.0.0.1', '-port', str(config['Port']), '-game', 'csgo',
            '+sv_lan', '1', '+game_type', '0', '+game_mode', '1', '+bot_quota', '0', '+map', map_name,
            '+exec', 'server.cfg']


def require_current_plugin(config):
    """Do not silently start an old developer instance after building an update."""
    built = ROOT/'src/NadeLab.Plugin/bin/Release/net10.0'
    installed = Path(config['GameDirectory'])/'addons/counterstrikesharp/plugins/NadeLab'
    for name in ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json'):
        source, target = built/name, installed/name
        if source.is_file() and (not target.is_file() or
                hashlib.sha256(source.read_bytes()).digest() != hashlib.sha256(target.read_bytes()).digest()):
            raise RuntimeError(f'Developer training plugin is outdated: {target}. '
                               'Stop this training server, run scripts/build.ps1, then '
                               'scripts/install-plugin.ps1 -ServerRoot server. Player data is preserved.')


def start(map_name=DEFAULT_MAP):
    validate_map(map_name)
    config = json.loads((INSTANCE/'instance.json').read_text(encoding='utf-8-sig'))
    require_current_plugin(config)
    source = find_source_game(config)
    require_completed_update(source)
    refresh_reason = needs_refresh(config, source)
    # Prevent two instances from racing on the local port.
    try:
        with socket.create_connection(('127.0.0.1', config['Port']), timeout=.5):
            pass
    except (ConnectionRefusedError, TimeoutError):
        pass
    else:
        if refresh_reason:
            raise RuntimeError(f'Running lab needs an update ({refresh_reason}). Stop the lab, finish the Steam update, then start again.')
        diagnostic = rcon('css_nl_selftest', timeout=3)
        current_map = ready_map(diagnostic)
        if current_map is None:
            raise RuntimeError('Port is in use, but a supported training map / NadeLab is not ready. See server logs.')
        print(f'NadeLab is already ready on {current_map}. No second instance was started; the current map was kept.')
        print(f'In CS2 console: connect 127.0.0.1:{config["Port"]} (launcher input: F1 menu; fallback chat !nl; personal bindings unchanged)')
        return
    if refresh_reason:
        print(f'Update detected: {refresh_reason}. Refreshing the local training instance...', flush=True)
        config = refresh_instance(config, source)
        # Refresh touches the installation; verify again before any process
        # starts so even a faulty/old refresh script cannot launch a downgrade.
        require_current_plugin(config)
    if not (Path(config['GameDirectory'])/'maps'/f'{map_name}.vpk').is_file():
        raise RuntimeError(f'{map_name} is not installed in the training instance. Install/update the game and refresh the instance first.')
    args = server_arguments(config, map_name)
    with (INSTANCE/'logs/stdout.log').open('w', encoding='utf-8') as out, (INSTANCE/'logs/stderr.log').open('w', encoding='utf-8') as err:
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        process = subprocess.Popen([sys.executable, str(ROOT/'scripts/server-process.py'), *args],
                                   cwd=Path(config['Executable']).parent, stdout=out, stderr=err,
                                   stdin=subprocess.DEVNULL, creationflags=subprocess.CREATE_NEW_CONSOLE,
                                   startupinfo=startup)
    print(f'Starting local server (launcher PID {process.pid}); waiting for {map_name} and NadeLab...', flush=True)
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f'Server exited during startup ({process.returncode}). {startup_error()} See {INSTANCE / "logs"}.')
        try:
            diagnostic = rcon('css_nl_selftest', timeout=2)
            current_map = ready_map(diagnostic)
            if current_map is not None:
                print(f'READY: {current_map} loaded, NadeLab loaded, native collision selftest passed.')
                break
        except (OSError, ConnectionError, RuntimeError):
            pass
        time.sleep(.5)
    else:
        raise RuntimeError(f'Server did not become ready within 60 seconds. See {INSTANCE / "logs"}.')
    print(f'Logs: {INSTANCE / "logs"}')
    print(f'In CS2 console: connect 127.0.0.1:{config["Port"]} (launcher input: F1 menu; fallback chat !nl; personal bindings unchanged)')


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['start','stop','command'])
    parser.add_argument('command', nargs='?')
    parser.add_argument('--map', dest='map_name', choices=SUPPORTED_MAPS, default=DEFAULT_MAP,
                        help='Initial map for a new server only; an already running server keeps its current map.')
    args = parser.parse_args()
    if args.action == 'start':
        start(args.map_name)
    elif args.action == 'stop':
        print(rcon('quit'))
    else:
        if not args.command:
            parser.error('command text is required')
        print(rcon(args.command))


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        sys.exit(1)
