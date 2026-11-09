"""Exercise developer installation in disposable instances, never the real game."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
PWSH = shutil.which('pwsh')
PLUGIN = Path('addons/counterstrikesharp/plugins/NadeLab')
BINARIES = ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json')
UI = {
    'panorama/styles/custom_game/nadelab_panel.vcss_c': 'panorama/styles/custom_game/nadelab_panel.css',
    'panorama/layout/custom_game/nadelab_panel.vxml_c': 'panorama/layout/custom_game/nadelab_panel.xml',
    'maps/scripts/nadelab_panel_bridge.vjs_c': 'maps/scripts/nadelab_panel_bridge.js',
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == 'nt' and PWSH, 'Windows PowerShell 7 required for disposable deployment')
class DeveloperDeployment(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nadelab-deploy-test-')
        self.root = Path(self.temp.name).resolve()
        self.assertTrue(self.root.is_relative_to(Path(tempfile.gettempdir()).resolve()))
        self.addCleanup(self.temp.cleanup)
        self.server = self.root/'server'
        self.game = self.server/'game/csgo'
        self.plugin = self.game/PLUGIN
        self.build = self.root/'src/NadeLab.Plugin/bin/Release/net10.0'
        for name in ('install-plugin.ps1', 'setup-local-server.ps1'):
            self.write('scripts/'+name, (ROOT/'scripts'/name).read_bytes())
        self.write('scripts/package-release.py', b'print("a"*64)\n')
        self.write('Directory.Build.props', b'<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>')
        files = []
        for name in BINARIES:
            data = ('verified-current-'+name).encode()
            self.write('src/NadeLab.Plugin/bin/Release/net10.0/'+name, data)
            files.append({'path': name, 'bytes': len(data), 'sha256': digest(data)})
        self.write('artifacts/release-build.json', json.dumps({
            'schema': 1, 'version': '1.0.0', 'verifiedProductVersion': '1.0.0',
            'sourceFingerprint': 'a'*64, 'files': files}).encode())
        ui = []
        for compiled, source in UI.items():
            compiled_data, source_data = ('compiled-'+compiled).encode(), ('source-'+source).encode()
            self.write('artifacts/panel-ui/'+compiled, compiled_data)
            self.write('ui/'+source, source_data)
            ui.append({'path': compiled, 'source': source, 'sha256': digest(compiled_data), 'sourceSha256': digest(source_data)})
        self.write('artifacts/panel-ui/manifest.json', json.dumps({'schema': 1, 'files': ui}).encode())
        self.write('configs/physics.example.json', b'{"current-example":true}')
        self.write('configs/nadelab_practice.cfg', b'current practice configuration')
        self.write('server/game/csgo/gameinfo.gi', b'SearchPaths { Game csgo }')
        self.write('server/game/csgo/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll', b'test api')
        # A retired package is deliberately available. Neither path may use it.
        self.write('artifacts/package/game/csgo/'+PLUGIN.as_posix()+'/NadeLab.dll', b'old packaged plugin')
        self.write('artifacts/package/game/csgo/'+PLUGIN.as_posix()+'/data/dummy-layouts/private.json', b'old package data')

    def write(self, relative, data):
        path = self.root/relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def old_installation(self):
        for name in BINARIES:
            self.write('server/game/csgo/'+PLUGIN.as_posix()+'/'+name, ('existing-'+name).encode())
        self.write('server/game/csgo/'+PLUGIN.as_posix()+'/physics.json', b'user-calibration')
        self.write('server/game/csgo/'+PLUGIN.as_posix()+'/data/dummy-layouts/player.json', b'user-combination')
        self.write('server/game/csgo/cfg/nadelab_practice.cfg', b'existing practice configuration')

    def inventory(self):
        return {p.relative_to(self.game).as_posix(): p.read_bytes() for p in self.game.rglob('*') if p.is_file()}

    def run_script(self, script='install-plugin.ps1', extra='', prelude='', ok=True, runtime=None):
        # Process inspection is isolated from actual desktop/client processes.
        stub = 'function Get-Process { [CmdletBinding()] param($Name,$Id); if($Id){[pscustomobject]@{Path=(Join-Path $PSHOME "pwsh.exe")}} }; '
        command = "$ErrorActionPreference='Stop'; " + stub + prelude + ' & ' + quote(self.root/'scripts'/script)
        command += (' -ServerRoot '+quote(self.server)) if script == 'install-plugin.ps1' else (' -GameInstallation '+quote(self.root/'source'))
        environment = os.environ.copy()
        if runtime:
            # A PowerShell 7 parent can otherwise point 5.1 at incompatible
            # module manifests instead of 5.1's own built-in commands.
            for name in list(environment):
                if name.lower() == 'psmodulepath':
                    environment.pop(name)
        result = subprocess.run([runtime or PWSH, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', command+' '+extra], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=45, env=environment)
        if ok:
            self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout+result.stderr)
        return result.stdout+result.stderr

    def seed_assets(self):
        for path in ('bin/win64/cs2.exe', 'bin/core.dll', 'csgo/bin/server.dll', 'csgo/cfg/game.cfg',
                     'csgo/steam.inf', 'csgo/gameinfo_branchspecific.gi', 'core/base.vpk'):
            self.write('source/game/'+path, b'fake game resource')
        self.write('source/game/csgo/gameinfo.gi', b'SearchPaths { Game csgo }')
        self.write('.tools/downloads/metamod/addons/metamod/metamod.vdf', b'test metamod')
        self.write('.tools/downloads/css-runtime/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll', b'test api')

    def test_install_uses_verified_build_and_ui_preserving_player_files_and_backing_up(self):
        self.old_installation()
        old = self.inventory()
        self.run_script()
        for name in BINARIES:
            self.assertEqual((self.plugin/name).read_bytes(), (self.build/name).read_bytes())
        for compiled in UI:
            self.assertEqual((self.game/compiled).read_bytes(), (self.root/'artifacts/panel-ui'/compiled).read_bytes())
        self.assertEqual((self.plugin/'physics.json').read_bytes(), b'user-calibration')
        self.assertEqual((self.plugin/'data/dummy-layouts/player.json').read_bytes(), b'user-combination')
        self.assertFalse((self.plugin/'data/dummy-layouts/private.json').exists())
        backups = list((self.root/'artifacts/backups').iterdir())
        self.assertEqual(len(backups), 1)
        for name in BINARIES:
            relative = (PLUGIN/name).as_posix()
            self.assertEqual((backups[0]/relative).read_bytes(), old[relative])

    def test_stale_binary_fails_before_writing_or_creating_backup(self):
        self.old_installation()
        old = self.inventory()
        (self.build/'NadeLab.Core.dll').write_bytes(b'changed after verification')
        self.assertIn('changed after verification', self.run_script(ok=False))
        self.assertEqual(self.inventory(), old)
        self.assertFalse((self.root/'artifacts/backups').exists())

    def test_installer_also_works_in_windows_powershell_51(self):
        shell = Path(os.environ['SystemRoot'])/'System32/WindowsPowerShell/v1.0/powershell.exe'
        if not shell.is_file():
            self.skipTest('Windows PowerShell 5.1 is unavailable')
        self.old_installation()
        self.run_script(runtime=str(shell))
        for name in BINARIES:
            self.assertEqual((self.plugin/name).read_bytes(), (self.build/name).read_bytes())
        self.assertEqual((self.plugin/'data/dummy-layouts/player.json').read_bytes(), b'user-combination')

    def test_stale_source_record_and_ui_source_are_rejected(self):
        self.old_installation()
        old = self.inventory()
        fp = self.root/'scripts/package-release.py'
        fp.write_text('print("b"*64)\n')
        self.assertIn('evidence is stale', self.run_script(ok=False))
        fp.write_text('print("a"*64)\n')
        (self.root/'ui'/next(iter(UI.values()))).write_bytes(b'edited UI source')
        self.assertIn('UI or source changed', self.run_script(ok=False))
        self.assertEqual(self.inventory(), old)

    def test_running_target_is_rejected_and_whatif_does_not_mutate(self):
        self.old_installation()
        old = self.inventory()
        self.run_script(extra='-WhatIf')
        self.assertEqual(self.inventory(), old)
        self.assertFalse((self.root/'artifacts/backups').exists())
        running = 'function Get-Process { [CmdletBinding()] param($Name,$Id); [pscustomobject]@{Path='+quote(self.server/'game/bin/win64/cs2.exe')+'} }; '
        self.assertIn('Stop this dedicated', self.run_script(prelude=running, ok=False))
        self.assertEqual(self.inventory(), old)

    def test_failed_copy_rolls_back_existing_files_and_removes_new_partial_files(self):
        fail = 'function Copy-Item { [CmdletBinding()] param($LiteralPath,$Destination,[switch]$Force); if($LiteralPath -like "*src*NadeLab.Core.dll"){throw "simulated copy failure"}; Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters }; '
        self.old_installation()
        old = self.inventory()
        self.assertIn('simulated copy failure', self.run_script(prelude=fail, ok=False))
        self.assertEqual(self.inventory(), old)
        for name in BINARIES:
            (self.plugin/name).unlink()
        before_fresh = self.inventory()
        self.run_script(prelude=fail, ok=False)
        self.assertEqual(self.inventory(), before_fresh)

    def test_resource_refresh_preserves_current_plugin_and_player_data(self):
        self.seed_assets()
        self.old_installation()
        original = {p.relative_to(self.plugin): p.read_bytes() for p in self.plugin.rglob('*') if p.is_file()}
        self.run_script('setup-local-server.ps1')
        self.assertEqual({p.relative_to(self.plugin): p.read_bytes() for p in self.plugin.rglob('*') if p.is_file()}, original)
        self.assertFalse((self.plugin/'data/dummy-layouts/private.json').exists())

    def test_fresh_setup_uses_verified_build_instead_of_retired_package(self):
        self.seed_assets()
        self.run_script('setup-local-server.ps1')
        for name in BINARIES:
            self.assertEqual((self.plugin/name).read_bytes(), (self.build/name).read_bytes())
        for compiled in UI:
            self.assertTrue((self.game/compiled).is_file())
        self.assertFalse((self.plugin/'data/dummy-layouts/private.json').exists())


if __name__ == '__main__':
    unittest.main()
