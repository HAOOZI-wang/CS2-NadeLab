"""Client binding regression tests. All destinations are isolated temp files."""
import contextlib
import importlib.util
import io
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


PROJECT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('nadelab_controls_installer', PROJECT / 'scripts/install-client-controls.py')
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)

INITIAL_KEYS = '''"config"
{
    "bindings"
    {
        "E" "+use"
        "R" "+reload"
        "G" "drop"
        "X" "radio2"
        "1" "slot1"
        "w" "+forward"
        "s" "+back"
        "a" "+left"
        "d" "+right"
        "SPACE" "+jump"
        "z" "radio1"
        "C" "radio3"
    }
    "metadata" { "unchanged" "yes" }
}
'''


class ClientControlsTests(unittest.TestCase):
    def test_default_install_preserves_every_profile_byte_and_has_no_binding_plan(self):
        before = self.keys.read_bytes()
        with self.forbid_vcfg_io():
            plan = json.loads(self.invoke('--dry-run'))
            self.invoke()
        self.assertEqual(plan['mode'], 'native')
        self.assertEqual(plan['bindings'], [])
        self.assertEqual(self.keys.read_bytes(), before)
        self.assertNotIn('exec nadelab_keys', self.autoexec.read_text())

    def test_native_restore_removes_owned_f1_and_keeps_later_custom_keys(self):
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings')
            text = self.keys.read_text()
            text = text.replace('css_nl_area', 'my later custom F2')
            self.keys.write_text(text)
            before = self.keys.read_bytes()
            plan = json.loads(self.invoke('--restore-native', '--dry-run'))
            self.assertEqual(self.keys.read_bytes(), before)
            self.assertTrue(any(item['key'].lower() == 'f1' and item['status'] == 'restore' for item in plan['bindings']))
            self.invoke('--restore-native')
        restored = self.keys.read_text()
        self.assertIsNone(INSTALLER.binding_match(restored, 'f1'))
        self.assertEqual(INSTALLER.binding_match(restored, 'f2')['command'], 'my later custom F2')
        self.assertNotIn('css_nl_wheel', restored)

    def test_native_install_and_restore_neutralize_old_autoexec_without_editing_it(self):
        self.autoexec.write_bytes(b'// personal\r\nsensitivity 1.42\r\nexec nadelab_keys\r\n')
        original_autoexec = self.autoexec.read_bytes()
        self.invoke()
        self.assertEqual(INSTALLER.training_bindings(self.binding_cfg.read_text()), {})
        self.assertIn('css_nl_controls native', self.binding_cfg.read_text())
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings')
            self.assertEqual(len(INSTALLER.training_bindings(self.binding_cfg.read_text())), 14)
            self.invoke('--restore-native')
        self.assertEqual(INSTALLER.training_bindings(self.binding_cfg.read_text()), {})
        self.assertIn('css_nl_controls native', self.binding_cfg.read_text())
        self.assertEqual(self.autoexec.read_bytes(), original_autoexec)

    def test_native_restore_rolls_back_keys_and_legacy_cfg_if_shim_write_fails(self):
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings')
            before = self.snapshot()
            write_bytes = Path.write_bytes

            def interrupted_write(path, content):
                if path == self.binding_cfg and content.startswith(b'// NadeLab native controls:'):
                    write_bytes(path, b'partial native cfg')
                    raise OSError('injected native shim failure')
                return write_bytes(path, content)

            with mock.patch.object(Path, 'write_bytes', new=interrupted_write):
                with self.assertRaisesRegex(OSError, 'injected native shim failure'):
                    self.invoke('--restore-native')
        self.assert_destinations(before)

    def test_native_restore_uses_attributed_key_receipts_and_keeps_encoding(self):
        original = INITIAL_KEYS.replace('"z" "radio1"', '"z" "+voicerecord"')
        original = original.replace('"E" "+use"', '"f1" "buymenu"\n        "v" "say .n"\n        "mwheelup" "+jump"\n        "E" "+use"')
        backup = self.legacy_backup(original)
        (backup / 'manifest.json').write_text(json.dumps({'keys': str(self.keys), 'previous_v': '"v" "say .n"'}))
        (backup / 'bindings.json').write_text(json.dumps([
            {'key': 'f1', 'command': 'css_nl', 'previous_line': '"f1" "buymenu"'},
            {'key': 'mwheelup', 'command': 'css_nl_wheel_up', 'previous_line': '"mwheelup" "+jump"'},
            {'key': 'z', 'command': 'css_nl_panel_up', 'previous_line': '"z" "+voicerecord"'},
        ]))
        installed = original.replace('"f1" "buymenu"', '"f1" "css_nl"').replace('"v" "say .n"', '"v" "noclip"').replace('"mwheelup" "+jump"', '"mwheelup" "css_nl_wheel_up"').replace('"z" "+voicerecord"', '"z" "css_nl_panel_up"')
        self.keys.write_bytes(b'\xef\xbb\xbf' + installed.replace('\n', '\r\n').encode())
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--restore-native')
            self.assertEqual(self.keys.read_bytes(), b'\xef\xbb\xbf' + original.replace('\n', '\r\n').encode())
            before = self.keys.read_bytes()
            self.invoke('--restore-native')
            self.assertEqual(self.keys.read_bytes(), before)

    def test_native_restore_refuses_to_guess_from_another_profile(self):
        self.legacy_backup(profile=self.base / 'other-user' / self.keys.name)
        self.keys.write_text(INITIAL_KEYS.replace('"z" "radio1"', '"z" "css_nl_panel_up"'))
        before = self.keys.read_bytes()
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            plan = json.loads(self.invoke('--restore-native'))
        self.assertEqual(self.keys.read_bytes(), before)
        self.assertTrue(any(item['key'] == 'z' and item['status'] == 'missing_backup' for item in plan['bindings']))

    def test_native_restore_never_treats_an_older_training_command_as_personal_original(self):
        backup = self.legacy_backup(INITIAL_KEYS.replace('"E" "+use"', '"f5" "css_nl_retarget"\n        "E" "+use"'))
        (backup / 'bindings.json').write_text(json.dumps([{'key': 'f5', 'command': 'css_nl_place', 'previous_line': '"f5" "css_nl_retarget"'}]))
        self.keys.write_text(INITIAL_KEYS.replace('"E" "+use"', '"f5" "css_nl_place"\n        "E" "+use"'))
        plan = json.loads(self.invoke('--restore-native', '--dry-run'))
        self.assertEqual(next(item['status'] for item in plan['bindings'] if item['key'] == 'f5'), 'missing_backup')

    def test_native_restore_ignores_flight_snapshot_for_keys_that_install_did_not_own(self):
        old = INITIAL_KEYS.replace('"E" "+use"', '"mwheelup" "old A"\n        "E" "+use"')
        flight = self.legacy_backup(old)
        (flight / 'manifest.json').write_text(json.dumps({'keys': str(self.keys), 'previous_v': None}))
        later = self.root / 'artifacts/client-bindings/20210102-wheel-install'
        later.mkdir()
        (later / 'manifest.json').write_text(json.dumps([{'path': str(self.keys), 'existed': True}]))
        (later / self.keys.name).write_text(old.replace('old A', 'custom B'))
        (later / 'bindings.json').write_text(json.dumps([
            {'key': 'mwheelup', 'command': 'css_nl_wheel_up', 'previous_line': '"mwheelup" "custom B"'}]))
        self.keys.write_text(old.replace('old A', 'css_nl_wheel_up'))
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--restore-native')
        self.assertEqual(INSTALLER.binding_match(self.keys.read_text(), 'mwheelup')['command'], 'custom B')

    def test_native_restore_does_not_claim_keys_from_a_generic_full_file_backup(self):
        original = INITIAL_KEYS.replace('"E" "+use"', '"mwheelup" "old A"\n        "E" "+use"')
        self.legacy_backup(original)
        self.keys.write_text(original.replace('old A', 'css_nl_wheel_up'))
        plan = json.loads(self.invoke('--restore-native', '--dry-run'))
        self.assertEqual(next(item['status'] for item in plan['bindings'] if item['key'] == 'mwheelup'), 'missing_backup')

    def test_native_restore_running_client_refuses_before_any_mutation(self):
        before = self.snapshot()
        executable = str(self.cfg.parents[1] / 'bin/win64/cs2.exe') + '\n'
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, executable, '')):
            with self.assertRaisesRegex(RuntimeError, 'Exit the CS2 client'):
                self.invoke('--restore-native')
        self.assert_destinations(before)

    def test_quoted_repair_binding_preserves_full_command(self):
        bindings = INSTALLER.training_bindings((PROJECT/'configs/nadelab_keys.cfg').read_text(encoding='utf-8'))
        self.assertEqual(bindings['F1'], 'exec nadelab_keys; css_nl')
        self.assertEqual(bindings['MWHEELUP'], 'css_nl_wheel_up')
        with self.assertRaisesRegex(RuntimeError, 'Invalid training binding'):
            INSTALLER.training_bindings('bind F1 "exec nadelab_keys')
        with self.assertRaisesRegex(RuntimeError, 'Duplicate training binding'):
            INSTALLER.training_bindings('bind F1 one\nbind f1 two')

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nadelab-controls-test-')
        self.addCleanup(self.temp.cleanup)
        # Windows runners may expose TEMP through an 8.3 alias such as RUNNER~1.
        # Use the same canonical paths as the installer for faults and receipts.
        self.base = Path(self.temp.name).resolve()
        self.root = self.base / 'project'
        (self.root / 'configs').mkdir(parents=True)
        (self.root / 'configs/nadelab_keys.cfg').write_bytes((PROJECT / 'configs/nadelab_keys.cfg').read_bytes())
        self.cfg = self.base / 'client/game/csgo/cfg'
        self.cfg.mkdir(parents=True)
        self.keys = self.base / 'userdata/cs2_user_keys_0_slot0.vcfg'
        self.keys.parent.mkdir()
        self.keys.write_bytes(b'\xef\xbb\xbf' + INITIAL_KEYS.encode('utf-8'))
        self.autoexec = self.cfg / 'autoexec.cfg'
        self.binding_cfg = self.cfg / 'nadelab_keys.cfg'
        self.autoexec.write_bytes(b'// existing personal config\r\nsensitivity 1.42\r\n')
        self.binding_cfg.write_bytes(b'// old training bindings\r\nbind z radio1\r\n')
        self.destinations = (self.keys, self.autoexec, self.binding_cfg)

    def snapshot(self):
        return {path: path.read_bytes() if path.exists() else None for path in self.destinations}

    def invoke(self, *extra):
        output = io.StringIO()
        argv = ['install-client-controls.py', '--keys-file', str(self.keys), '--cfg-dir', str(self.cfg), *extra]
        with mock.patch.object(INSTALLER, 'ROOT', self.root), mock.patch.object(sys, 'argv', argv), contextlib.redirect_stdout(output):
            INSTALLER.main()
        return output.getvalue()

    def assert_destinations(self, expected):
        self.assertEqual(self.snapshot(), expected)

    def backups(self):
        parent = self.root / 'artifacts/client-bindings'
        return sorted(parent.iterdir()) if parent.exists() else []

    @contextlib.contextmanager
    def forbid_vcfg_io(self):
        target = self.keys.resolve()

        def guarded(original):
            def invoke(path, *args, **kwargs):
                if path.resolve() == target:
                    raise AssertionError('staging must not read or write vcfg content')
                return original(path, *args, **kwargs)
            return invoke

        with contextlib.ExitStack() as stack:
            for method in ('read_text', 'read_bytes', 'write_text', 'write_bytes'):
                stack.enter_context(mock.patch.object(Path, method, new=guarded(getattr(Path, method))))
            yield

    def test_dry_run_changes_no_files_and_does_not_query_running_client(self):
        before = self.snapshot()
        with mock.patch.object(INSTALLER.subprocess, 'run') as process:
            plan = json.loads(self.invoke('--legacy-bindings', '--dry-run'))
        process.assert_not_called()
        changes = {item['key'].lower(): item for item in plan['bindings']}
        self.assertEqual(changes['uparrow']['command'], 'css_nl_panel_up')
        self.assertEqual(changes['downarrow']['command'], 'css_nl_panel_down')
        self.assertTrue({'w', 's', 'z', 'c'}.isdisjoint(changes))
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_install_keeps_gameplay_keys_and_backs_up_exact_original_files(self):
        before = self.snapshot()
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings', )
        updated = self.keys.read_text(encoding='utf-8')
        for key, command in [('UPARROW', 'css_nl_panel_up'), ('DOWNARROW', 'css_nl_panel_down')]:
            self.assertEqual(len(re.findall(rf'(?im)^\s*"{key}"\s*"{command}"\s*$', updated)), 1)
        for preserved in ['"E" "+use"', '"R" "+reload"', '"G" "drop"', '"X" "radio2"',
                          '"1" "slot1"', '"w" "+forward"', '"s" "+back"', '"a" "+left"', '"d" "+right"',
                          '"SPACE" "+jump"', '"z" "radio1"', '"C" "radio3"', '"metadata" { "unchanged" "yes" }']:
            self.assertIn(preserved, updated)
        # This cfg is generated text: Git archives and Windows checkouts can
        # differ in line endings. Personal-file backups below remain byte-exact.
        self.assertEqual(self.binding_cfg.read_text(encoding='utf-8'),
                         (self.root / 'configs/nadelab_keys.cfg').read_text(encoding='utf-8'))
        self.assertEqual(self.autoexec.read_bytes(), before[self.autoexec])
        backups = self.backups()
        self.assertEqual(len(backups), 1)
        backup = backups[0]
        manifest = json.loads((backup / 'manifest.json').read_text())
        self.assertEqual({Path(item['path']) for item in manifest}, {self.keys, self.binding_cfg})
        self.assertTrue(all(item['existed'] for item in manifest))
        for original, data in before.items():
            if original == self.autoexec:
                continue
            self.assertEqual((backup / original.name).read_bytes(), data)
        receipt = {item['key'].lower(): item for item in json.loads((backup / 'bindings.json').read_text())}
        self.assertTrue({'w', 's', 'z', 'c'}.isdisjoint(receipt))

    def test_reinstall_does_not_duplicate_keys_or_autoexec(self):
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings', )
            installed = self.snapshot()
            self.invoke('--legacy-bindings', )
        self.assert_destinations(installed)
        self.assertEqual(len(self.backups()), 2)
        self.assertEqual(INSTALLER.binding_match(self.keys.read_text(), 'F1')['command'], 'exec nadelab_keys; css_nl')

    def test_running_client_refuses_before_writing_or_backup(self):
        before = self.snapshot()
        executable = str(self.cfg.parents[1] / 'bin/win64/cs2.exe') + '\n'
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, executable, '')):
            with self.assertRaisesRegex(RuntimeError, 'Exit the CS2 client'):
                self.invoke('--legacy-bindings', )
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_running_client_path_alias_refuses_before_writing_or_backup(self):
        executable = self.cfg.parents[1] / 'bin/win64/cs2.exe'
        executable.parent.mkdir(parents=True)
        executable.write_bytes(b'isolated fixture; never executed')
        alias = executable.parent / '..' / 'win64' / executable.name
        self.assertTrue(alias.samefile(executable))
        before = self.snapshot()
        running = subprocess.CompletedProcess([], 0, str(alias) + '\n', '')
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=running):
            with self.assertRaisesRegex(RuntimeError, 'Exit the CS2 client'):
                self.invoke('--legacy-bindings')
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_duplicate_case_insensitive_binding_refuses_before_writing(self):
        self.keys.write_text(INITIAL_KEYS.replace('"C" "radio3"', '"C" "radio3"\n        "c" "other"'))
        before = self.snapshot()
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            with self.assertRaisesRegex(RuntimeError, 'Duplicate c binding'):
                self.invoke('--legacy-bindings', )
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_stage_with_running_client_preserves_vcfg_without_reading_it(self):
        before = self.snapshot()
        executable = str(self.cfg.parents[1] / 'bin/win64/cs2.exe') + '\n'
        with self.forbid_vcfg_io(), mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, executable, '')) as process:
            output = self.invoke('--legacy-bindings', '--stage-for-next-launch')
        process.assert_not_called()
        self.assertEqual(self.keys.read_bytes(), before[self.keys])
        self.assertEqual(self.binding_cfg.read_text(encoding='utf-8'),
                         (self.root / 'configs/nadelab_keys.cfg').read_text(encoding='utf-8'))
        self.assertEqual(self.autoexec.read_bytes(), before[self.autoexec])
        self.assertIn('Run exec nadelab_keys manually', output)
        backup = self.backups()[0]
        manifest = json.loads((backup / 'manifest.json').read_text())
        self.assertEqual({Path(item['path']) for item in manifest}, {self.binding_cfg})
        self.assertFalse((backup / self.keys.name).exists())
        for path in (self.binding_cfg,):
            self.assertEqual((backup / path.name).read_bytes(), before[path])
        staged = json.loads((backup / 'bindings.json').read_text())
        self.assertTrue(all('previous_line' not in item for item in staged))
        self.assertEqual({item['key']: item['command'] for item in staged}['DOWNARROW'], 'css_nl_panel_down')
        self.assertFalse(re.search(r'(?im)^bind\s+[wscz]\s', self.binding_cfg.read_text()))

    def test_stage_dry_run_has_no_writes_or_vcfg_reads(self):
        before = self.snapshot()
        with self.forbid_vcfg_io(), mock.patch.object(INSTALLER.subprocess, 'run') as process:
            plan = json.loads(self.invoke('--legacy-bindings', '--stage-for-next-launch', '--dry-run'))
        process.assert_not_called()
        self.assertEqual(plan['mode'], 'stage-for-next-launch')
        self.assertIsNone(plan['keys'])
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def rollback_case(self, absent_config, stage=False):
        if absent_config:
            self.autoexec.unlink()
            self.binding_cfg.unlink()
        before = self.snapshot()
        original_write = Path.write_text
        target = self.binding_cfg.resolve()

        def fail_last_write(path, text, *args, **kwargs):
            if path.resolve() == target:
                path.write_bytes(b'partial interrupted write')
                raise OSError('injected failure after partial client write')
            return original_write(path, text, *args, **kwargs)

        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')), \
                mock.patch.object(Path, 'write_text', new=fail_last_write), \
                self.forbid_vcfg_io() if stage else contextlib.nullcontext():
            with self.assertRaisesRegex(OSError, 'injected failure'):
                self.invoke('--legacy-bindings', *(['--stage-for-next-launch'] if stage else []))
        self.assert_destinations(before)
        backup = self.backups()[0]
        manifest = json.loads((backup / 'manifest.json').read_text())
        self.assertEqual(sum(item['existed'] for item in manifest), (0 if absent_config else 1) if stage else (1 if absent_config else 2))
        if stage:
            self.assertEqual({Path(item['path']) for item in manifest}, {self.binding_cfg})
            self.assertFalse((backup / self.keys.name).exists())

    def test_partial_write_failure_restores_existing_files_byte_for_byte(self):
        self.rollback_case(absent_config=False)

    def test_partial_write_failure_removes_files_created_by_this_install(self):
        self.rollback_case(absent_config=True)

    def test_stage_partial_write_failure_restores_cfg_files_without_touching_vcfg(self):
        self.rollback_case(absent_config=False, stage=True)

    def test_stage_partial_write_failure_removes_new_cfg_files_without_touching_vcfg(self):
        self.rollback_case(absent_config=True, stage=True)

    def legacy_backup(self, text=INITIAL_KEYS, profile=None):
        backup = self.root / 'artifacts/client-bindings/20200101-original'
        backup.mkdir(parents=True)
        (backup / self.keys.name).write_text(text, encoding='utf-8')
        (backup / 'manifest.json').write_text(json.dumps([{'path': str(profile or self.keys), 'existed': True}]))
        return backup

    def test_normal_install_restores_original_legacy_keys_and_preserves_later_custom_bindings(self):
        self.legacy_backup(INITIAL_KEYS.replace('"z" "radio1"', '"z" "<unbound>"'))
        self.keys.write_text(INITIAL_KEYS.replace('"z" "radio1"', '"z" "css_nl_panel_up"').replace('"C" "radio3"', '"C" "custom_later"'))
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings', )
        updated = self.keys.read_text()
        self.assertIn('"z" "<unbound>"', updated)
        self.assertIn('"C" "custom_later"', updated)
        self.assertIn('"w" "+forward"', updated)
        receipt = json.loads((self.backups()[-1] / 'legacy-navigation.json').read_text())
        self.assertEqual([item['status'] for item in receipt], ['restore', 'kept_current'])

    def test_normal_install_restores_nondefault_original_binding(self):
        self.legacy_backup(INITIAL_KEYS.replace('"z" "radio1"', '"z" "+voicerecord"'))
        self.keys.write_text(INITIAL_KEYS.replace('"z" "radio1"', '"z" "css_nl_panel_up"').replace('"C" "radio3"', '"C" "css_nl_panel_down"'))
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.invoke('--legacy-bindings', )
        self.assertIn('"z" "+voicerecord"', self.keys.read_text())
        self.assertIn('"C" "radio3"', self.keys.read_text())

    def test_other_profile_backup_does_not_supply_guessed_original_keys(self):
        self.legacy_backup(profile=self.base / 'other-user' / self.keys.name)
        self.keys.write_text(INITIAL_KEYS.replace('"z" "radio1"', '"z" "css_nl_panel_up"'))
        with mock.patch.object(INSTALLER.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            output = self.invoke('--legacy-bindings', )
        self.assertIn('"z" "css_nl_panel_up"', self.keys.read_text())
        self.assertIn('WARNING', output)

    def test_early_flight_manifest_keeps_profile_attribution(self):
        backup = self.legacy_backup(INITIAL_KEYS.replace('"z" "radio1"', '"z" "<unbound>"'))
        (backup / 'manifest.json').write_text(json.dumps({'keys': str(self.keys), 'previous_v': 'old v'}))
        with self.forbid_vcfg_io():
            plan = json.loads(self.invoke('--legacy-bindings', '--stage-for-next-launch', '--legacy-current', 'z=css_nl_panel_up', '--dry-run'))
        self.assertEqual(plan['legacy_navigation'][0]['original_command'], '<unbound>')
        self.assertIn('unbind z', plan['manual_migration_cfg'])

    def test_staged_manual_migration_uses_verified_current_values_and_never_enters_autoexec(self):
        self.legacy_backup(INITIAL_KEYS.replace('"z" "radio1"', '"z" "<unbound>"'))
        before = self.keys.read_bytes()
        with self.forbid_vcfg_io(), mock.patch.object(INSTALLER.subprocess, 'run') as process:
            self.invoke('--legacy-bindings', '--stage-for-next-launch', '--legacy-current', 'z=css_nl_panel_up', '--legacy-current', 'c=my_later_choice')
        process.assert_not_called()
        migration = (self.cfg / 'nadelab_migrate_keys.cfg').read_text()
        self.assertIn('unbind z\n', migration)
        self.assertNotRegex(migration, r'(?m)^(?:un)?bind c\b')
        self.assertIn('exec nadelab_keys', migration)
        self.assertNotIn('migrate_keys', self.autoexec.read_text())
        self.assertEqual(self.keys.read_bytes(), before)

    def test_stage_migration_without_backup_refuses_before_writes(self):
        before = self.snapshot()
        with self.forbid_vcfg_io(), self.assertRaisesRegex(RuntimeError, 'No matching original'):
            self.invoke('--legacy-bindings', '--stage-for-next-launch', '--legacy-current', 'z=css_nl_panel_up')
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_partial_manual_migration_write_rolls_back_all_cfg_files(self):
        self.legacy_backup()
        migration = self.cfg / 'nadelab_migrate_keys.cfg'
        migration.write_bytes(b'// existing migration file\r\n')
        self.destinations += (migration,)
        before = self.snapshot()
        original_write = Path.write_text

        def fail_migration(path, text, *args, **kwargs):
            if path == migration:
                path.write_bytes(b'partial migration')
                raise OSError('injected migration write failure')
            return original_write(path, text, *args, **kwargs)

        with mock.patch.object(Path, 'write_text', new=fail_migration), self.forbid_vcfg_io():
            with self.assertRaisesRegex(OSError, 'migration write failure'):
                self.invoke('--legacy-bindings', '--stage-for-next-launch', '--legacy-current', 'z=css_nl_panel_up')
        self.assert_destinations(before)

    def test_never_reads_or_writes_autoexec_even_if_it_has_old_training_bindings(self):
        self.autoexec.write_bytes(b'// personal\r\nexec nadelab_keys\r\n')
        before = self.autoexec.read_bytes()
        original = Path.open

        def refuse_autoexec(path, *args, **kwargs):
            if path.resolve() == self.autoexec.resolve():
                raise AssertionError('autoexec must remain outside the installation transaction')
            return original(path, *args, **kwargs)

        with mock.patch.object(Path, 'open', new=refuse_autoexec):
            self.invoke('--legacy-bindings', '--stage-for-next-launch')
        self.assertEqual(self.autoexec.read_bytes(), before)

    def test_single_discovered_profile_still_requires_explicit_selection(self):
        before = self.snapshot()
        discovery = {'cfg_directories': [str(self.cfg)], 'keys_files': [str(self.keys)]}
        with mock.patch.object(INSTALLER, 'discover_clients', return_value=discovery), \
                mock.patch.object(sys, 'argv', ['install-client-controls.py', '--legacy-bindings']), \
                self.assertRaisesRegex(RuntimeError, 'Choose the intended Steam profile explicitly'):
            INSTALLER.main()
        self.assert_destinations(before)
        self.assertEqual(self.backups(), [])

    def test_staging_auto_discovers_single_game_without_requiring_profile(self):
        discovery = {'cfg_directories': [str(self.cfg)], 'keys_files': [str(self.keys)]}
        output = io.StringIO()
        with mock.patch.object(INSTALLER, 'ROOT', self.root), \
                mock.patch.object(INSTALLER, 'discover_clients', return_value=discovery), \
                mock.patch.object(sys, 'argv', ['install-client-controls.py', '--stage-for-next-launch', '--dry-run']), \
                contextlib.redirect_stdout(output), self.forbid_vcfg_io():
            INSTALLER.main()
        self.assertEqual(json.loads(output.getvalue())['cfg'], str(self.cfg.resolve()))
        self.assertIsNone(json.loads(output.getvalue())['keys'])

    def test_ambiguous_game_installation_refuses_before_writes(self):
        discovery = {'cfg_directories': [str(self.cfg), str(self.base / 'other')], 'keys_files': []}
        with mock.patch.object(INSTALLER, 'discover_clients', return_value=discovery), \
                mock.patch.object(sys, 'argv', ['install-client-controls.py', '--stage-for-next-launch']), \
                self.assertRaisesRegex(RuntimeError, 'Choose --cfg-dir'):
            INSTALLER.main()
        self.assertEqual(self.backups(), [])

    def test_discovery_reads_secondary_library_and_multiple_local_profiles(self):
        steam = self.base / 'steam'
        (steam / 'steamapps').mkdir(parents=True)
        library = self.base / 'secondary-library'
        cfg = library / 'steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg'
        cfg.mkdir(parents=True)
        escaped = str(library).replace('\\', '\\\\')
        (steam / 'steamapps/libraryfolders.vdf').write_text('"libraryfolders" { "1" { "path" "' + escaped + '" } }')
        profiles = []
        for account in ('111', '222'):
            profile = steam / f'userdata/{account}/730/local/cfg/cs2_user_keys_0_slot0.vcfg'
            profile.parent.mkdir(parents=True)
            profile.write_text(INITIAL_KEYS)
            profiles.append(str(profile.resolve()))
        with mock.patch.object(INSTALLER, 'steam_roots', return_value=[steam]):
            found = INSTALLER.discover_clients()
        self.assertEqual(found, {'cfg_directories': [str(cfg.resolve())], 'keys_files': profiles})

    def test_list_clients_never_selects_or_writes_a_profile(self):
        output = io.StringIO()
        found = {'cfg_directories': [str(self.cfg)], 'keys_files': [str(self.keys)]}
        before = self.snapshot()
        with mock.patch.object(INSTALLER, 'discover_clients', return_value=found), \
                mock.patch.object(sys, 'argv', ['install-client-controls.py', '--list-clients']), \
                contextlib.redirect_stdout(output):
            INSTALLER.main()
        self.assertEqual(json.loads(output.getvalue()), found)
        self.assert_destinations(before)


if __name__ == '__main__':
    unittest.main()
