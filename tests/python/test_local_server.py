import importlib.util
import contextlib
import io
import json
import os
import shutil
import struct
import tempfile
import unittest
from unittest import mock
from pathlib import Path


spec = importlib.util.spec_from_file_location('local_server', Path(__file__).resolve().parents[2]/'scripts/local-server.py')
lab = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lab)


class StartupChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nadelab-startup-tests-')
        self.root = Path(self.temp.name).resolve()
        self.assertTrue(self.root.is_relative_to(Path(tempfile.gettempdir()).resolve()))
        self.addCleanup(self.temp.cleanup)
        self.source = self.root/'steam/steamapps/common/CS2/game'
        self.local = self.root/'server/game/csgo'
        (self.source/'csgo').mkdir(parents=True)
        self.local.mkdir(parents=True)
        self.manifest = self.source.parents[2]/'appmanifest_730.acf'
        self.manifest.write_text('"AppState" { "StateFlags" "4" }')
        self.version = 'ServerVersion=2000924\nSourceRevision=11076591\nPatchVersion=1.41.8.8\n'
        (self.source/'csgo/steam.inf').write_text(self.version)
        (self.local/'steam.inf').write_text(self.version)
        index = self.source/'csgo/pak01_dir.vpk'
        index.write_bytes(struct.pack('<7I', 0x55AA1234, 2, 1, 0, 0, 0, 0)+b'\0')
        shutil.copy2(index, self.local/index.name)
        self.config = {'GameDirectory': str(self.local), 'SourceGameDirectory': str(self.source)}
        self.root_patch = mock.patch.object(lab, 'ROOT', self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)

    def test_stale_developer_plugin_is_rejected_without_changing_files(self):
        built = self.root/'src/NadeLab.Plugin/bin/Release/net10.0'
        installed = self.local/'addons/counterstrikesharp/plugins/NadeLab'
        built.mkdir(parents=True)
        installed.mkdir(parents=True)
        for name in ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json'):
            (built/name).write_bytes(b'new')
            (installed/name).write_bytes(b'new')
        lab.require_current_plugin(self.config)
        for name in ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json'):
            with self.subTest(name=name):
                (installed/name).write_bytes(b'old')
                with self.assertRaisesRegex(RuntimeError, 'outdated'):
                    lab.require_current_plugin(self.config)
                self.assertEqual((installed/name).read_bytes(), b'old')
                (installed/name).unlink()
                with self.assertRaisesRegex(RuntimeError, 'outdated'):
                    lab.require_current_plugin(self.config)
                (installed/name).write_bytes(b'new')

    def test_current_build_and_resources_do_not_trigger_refresh(self):
        lab.require_completed_update(self.source)
        self.assertIsNone(lab.needs_refresh(self.config, self.source))

    def test_new_steam_build_requires_refresh_without_editing_the_copy(self):
        (self.source/'csgo/steam.inf').write_text('ServerVersion=2000927\nSourceRevision=11087167\nPatchVersion=1.41.8.9\n')
        self.assertIn('1.41.8.8 -> 1.41.8.9', lab.needs_refresh(self.config, self.source))
        self.assertEqual((self.local/'steam.inf').read_text(), self.version)

    def test_changed_or_missing_resource_link_requires_refresh(self):
        chunk = self.source/'csgo/pak01_000.vpk'
        chunk.write_bytes(b'new resource')
        self.assertIn(chunk.name, lab.needs_refresh(self.config, self.source))
        shutil.copy2(chunk, self.local/chunk.name)
        self.assertIsNone(lab.needs_refresh(self.config, self.source))
        # A replaced pack with unchanged length still has a different timestamp.
        os.utime(chunk, ns=(chunk.stat().st_atime_ns, chunk.stat().st_mtime_ns+2_000_000_000))
        self.assertIn(chunk.name, lab.needs_refresh(self.config, self.source))

    def test_in_progress_steam_update_is_rejected(self):
        self.manifest.write_text('"StateFlags" "1540"')
        with self.assertRaisesRegex(RuntimeError, 'Finish the Steam update'):
            lab.require_completed_update(self.source)

    def test_truncated_resource_index_is_rejected(self):
        (self.source/'csgo/pak01_dir.vpk').write_bytes(struct.pack('<7I', 0x55AA1234, 2, 1000, 0, 0, 0, 0))
        with self.assertRaisesRegex(RuntimeError, 'resource index is invalid'):
            lab.require_completed_update(self.source)

    def test_legacy_instance_recovers_its_original_asset_source(self):
        asset = Path(os.path.relpath(self.source/'csgo', self.local.parent)).as_posix()
        (self.local/'gameinfo.gi').write_text(f'SearchPaths\n{{\nGame "{asset}"\n}}')
        self.assertEqual(lab.find_source_game({'GameDirectory': str(self.local)}), self.source)

    def resource(self, relative, payload=b'test resource'):
        source, target = self.source/'csgo'/relative, self.local/relative
        source.parent.mkdir(parents=True, exist_ok=True)
        target.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(payload)
        shutil.copy2(source, target)
        return source, target

    def test_supported_map_and_vanity_missing_or_replaced_files_require_refresh(self):
        for name in lab.SUPPORTED_MAPS:
            for suffix in ('.vpk', '_vanity.vpk'):
                relative = f'maps/{name}{suffix}'
                with self.subTest(resource=relative):
                    source, target = self.resource(relative)
                    self.assertIsNone(lab.needs_refresh(self.config, self.source))
                    target.unlink()
                    self.assertIn(relative, lab.needs_refresh(self.config, self.source))
                    shutil.copy2(source, target)
                    # Steam can replace a same-size VPK without a build-number change.
                    os.utime(source, ns=(source.stat().st_atime_ns, source.stat().st_mtime_ns+2_000_000_000))
                    self.assertIn(relative, lab.needs_refresh(self.config, self.source))
                    shutil.copy2(source, target)
                    source.write_bytes(b'a different length map resource')
                    self.assertIn(relative, lab.needs_refresh(self.config, self.source))
                    shutil.copy2(source, target)
                    self.assertIsNone(lab.needs_refresh(self.config, self.source))

    def test_nested_overpass_prefabs_are_checked_under_the_actual_directory_name(self):
        relative = 'maps/prefabs/overpass/nested/example.vmap_c'
        source, target = self.resource(relative)
        target.unlink()
        self.assertIn(relative, lab.needs_refresh(self.config, self.source))
        shutil.copy2(source, target)
        os.utime(source, ns=(source.stat().st_atime_ns, source.stat().st_mtime_ns+2_000_000_000))
        self.assertIn(relative, lab.needs_refresh(self.config, self.source))
        shutil.copy2(source, target)
        self.assertIsNone(lab.needs_refresh(self.config, self.source))

    def test_removed_supported_map_is_not_silently_kept_as_an_installed_choice(self):
        source, target = self.resource('maps/de_train.vpk')
        source.unlink()
        self.assertIn('removed from Steam', lab.needs_refresh(self.config, self.source))
        self.assertTrue(target.exists(), 'read-only check changed the local instance')

    def test_unrelated_community_maps_do_not_expand_the_fixed_resource_pool(self):
        source = self.source/'csgo/maps/de_unknown.vpk'
        source.parent.mkdir(parents=True)
        source.write_bytes(b'unrelated community map')
        self.assertIsNone(lab.needs_refresh(self.config, self.source))

    def test_launch_arguments_accept_only_allowlisted_maps_and_default_to_mirage(self):
        config = self.config | {'Executable': 'cs2.exe', 'Port': 27025}
        default = lab.server_arguments(config)
        self.assertEqual(default[default.index('+map')+1], 'de_mirage')
        for name in lab.SUPPORTED_MAPS:
            arguments = lab.server_arguments(config, name)
            self.assertEqual(arguments[arguments.index('+map')+1], name)
        for name in ('de_unknown', '../de_mirage', 'de_mirage;quit', 'de_mirage\nquit', 'workshop/12/de_mirage'):
            with self.subTest(map=name), self.assertRaises(ValueError):
                lab.server_arguments(config, name)

    def test_ready_detection_reports_the_actual_supported_map(self):
        for name in lab.SUPPORTED_MAPS:
            self.assertEqual(lab.ready_map(f'[NadeLab] SELFTEST PASS: native {name} floor Z=10; simulated smoke Rested.'), name)
        self.assertIsNone(lab.ready_map('SELFTEST FAIL: de_mirage has no supported spawn'))
        self.assertIsNone(lab.ready_map('SELFTEST PASS: native de_unknown floor Z=0'))
        self.assertIsNone(lab.ready_map('NadeLab loaded on de_nuke'))

    def test_repeated_start_never_changes_the_running_map_even_with_another_requested_map(self):
        instance = self.local.parents[1]
        config = self.config | {'Executable': 'cs2.exe', 'Port': 27025}
        (instance/'instance.json').write_text(json.dumps(config), encoding='utf-8')
        diagnostic = 'SELFTEST PASS: native de_train floor Z=0; simulated smoke Rested.'
        with mock.patch.object(lab, 'INSTANCE', instance), \
             mock.patch.object(lab.socket, 'create_connection'), \
             mock.patch.object(lab, 'rcon', return_value=diagnostic) as rcon, \
             mock.patch.object(lab.subprocess, 'Popen') as launch, \
             mock.patch.object(lab, 'refresh_instance') as refresh:
            for requested in (lab.DEFAULT_MAP, 'de_cache'):
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    lab.start(requested)
                self.assertIn('already ready on de_train', output.getvalue())
                self.assertIn('current map was kept', output.getvalue())
            self.assertEqual(rcon.call_args_list, [mock.call('css_nl_selftest', timeout=3)]*2)
            launch.assert_not_called()
            refresh.assert_not_called()

    def test_a_missing_selected_map_fails_before_launch_on_non_windows_hosts_too(self):
        instance = self.local.parents[1]
        config = self.config | {'Executable': 'cs2.exe', 'Port': 27025}
        (instance/'instance.json').write_text(json.dumps(config), encoding='utf-8')
        with mock.patch.object(lab, 'INSTANCE', instance), \
             mock.patch.object(lab.socket, 'create_connection', side_effect=ConnectionRefusedError), \
             mock.patch.object(lab.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(RuntimeError, 'de_cache is not installed'):
                lab.start('de_cache')
            launch.assert_not_called()

    def test_refresh_cannot_replace_a_current_plugin_with_an_older_one_and_launch(self):
        built = self.root/'src/NadeLab.Plugin/bin/Release/net10.0'
        installed = self.local/'addons/counterstrikesharp/plugins/NadeLab'
        built.mkdir(parents=True)
        installed.mkdir(parents=True)
        for name in ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json'):
            (built/name).write_bytes(b'current')
            (installed/name).write_bytes(b'current')
        instance = self.local.parents[1]
        config = self.config | {'Executable': 'cs2.exe', 'Port': 27025}
        (instance/'instance.json').write_text(json.dumps(config), encoding='utf-8')
        def downgrade(*args):
            (installed/'NadeLab.dll').write_bytes(b'retired package')
            return config
        with mock.patch.object(lab, 'INSTANCE', instance), \
             mock.patch.object(lab.socket, 'create_connection', side_effect=ConnectionRefusedError), \
             mock.patch.object(lab, 'needs_refresh', return_value='Steam asset update'), \
             mock.patch.object(lab, 'refresh_instance', side_effect=downgrade) as refresh, \
             mock.patch.object(lab.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(RuntimeError, 'outdated'), contextlib.redirect_stdout(io.StringIO()):
                lab.start()
            refresh.assert_called_once()
            launch.assert_not_called()


if __name__ == '__main__':
    unittest.main()
