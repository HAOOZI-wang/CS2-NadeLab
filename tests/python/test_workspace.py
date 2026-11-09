"""Windows workbench tests. All mutations are confined to temporary fixtures."""
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/workspace.ps1'
POWERSHELL = Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe'


def ps_string(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(POWERSHELL.is_file(), 'Windows PowerShell 5.1 required')
class WorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nadelab workspace ')
        self.addCleanup(self.temp.cleanup)
        self.fixture = Path(self.temp.name).resolve()
        self.project = self.fixture / 'project with spaces'
        self.player = self.fixture / 'player with spaces'
        self.project.mkdir()
        self.player.mkdir()

    def run_ps(self, body, expected=0):
        command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + body
        encoded = base64.b64encode(command.encode('utf-16le')).decode('ascii')
        result = subprocess.run([str(POWERSHELL), '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
                                cwd=self.fixture, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace', timeout=45)
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        return result.stdout.strip()

    def helpers(self):
        return f". {ps_string(SCRIPT)}; $ctx=New-NlwContext {ps_string(self.project)} {ps_string(self.player)}; "

    def fixture_script(self):
        destination = self.project / 'scripts/workspace.ps1'
        destination.parent.mkdir(exist_ok=True)
        shutil.copyfile(SCRIPT, destination)
        return destination

    def test_windows_powershell_51_parses_script_and_cmd_is_ascii(self):
        output = self.run_ps(f"$tokens=$null; $errors=$null; $null=[Management.Automation.Language.Parser]::ParseFile({ps_string(SCRIPT)},[ref]$tokens,[ref]$errors); if($errors.Count) {{ throw ($errors.Message -join '; ') }}; $PSVersionTable.PSVersion.ToString()")
        self.assertTrue(output.startswith('5.1.'), output)
        self.assertTrue(SCRIPT.read_bytes().startswith(b'\xef\xbb\xbf'), 'PowerShell 5.1 needs a UTF-8 BOM for Chinese literals')
        (ROOT / '工作台.cmd').read_bytes().decode('ascii')

    def test_plan_from_unrelated_working_directory_creates_nothing(self):
        script = self.fixture_script()
        before = sorted(str(p.relative_to(self.fixture)) for p in self.fixture.rglob('*'))
        output = self.run_ps(f"& {ps_string(script)} -Plan -Json -InstalledRoot {ps_string(self.player)} -Map de_dust2")
        plans = json.loads(output)
        self.assertEqual(len(plans), 11)
        start = next(p for p in plans if p['Action'] == 'StartDev')
        self.assertEqual(Path(start['WorkingDirectory']), self.project)
        self.assertIn(str(self.project / 'scripts/local-server.py'), start['Arguments'])
        self.assertEqual(start['Arguments'][-2:], ['--map', 'de_dust2'])
        build = next(p for p in plans if p['Action'] == 'Build')
        self.assertIn('-NoPackage', build['Arguments'])
        installed = next(p for p in plans if p['Action'] == 'InstallPlugin')
        self.assertEqual(Path(installed['Arguments'][-1]), self.project / 'server')
        self.assertEqual(before, sorted(str(p.relative_to(self.fixture)) for p in self.fixture.rglob('*')))

    def test_acceptance_plan_is_offline_and_reads_only_selected_project(self):
        plan = json.loads(self.run_ps(self.helpers() + "New-NlwActionPlan $ctx 'Acceptance' 'de_mirage' | ConvertTo-Json -Depth 5"))
        self.assertEqual(plan['Program'], 'python')
        self.assertEqual(plan['Arguments'], ['-s', str(self.project/'scripts/acceptance.py')])
        self.assertEqual(Path(plan['WorkingDirectory']), self.project)
        self.assertEqual(plan['Kind'], 'Command')

    def write_metadata(self, project_overrides=None, player_overrides=None):
        project_config = {'GameDirectory': str(self.project / 'server/game/csgo'), 'Executable': str(self.project / 'server/game/bin/win64/cs2.exe'), 'Port': 29025, 'rconPassword': 'DO-NOT-PRINT-THIS'}
        player_config = {'instance': str(self.player / 'instance'), 'executable': str(self.player / 'instance/game/bin/win64/cs2.exe'), 'port': 29026, 'secret': 'DO-NOT-PRINT-THIS'}
        project_config.update(project_overrides or {})
        player_config.update(player_overrides or {})
        (self.project / 'server').mkdir(exist_ok=True)
        (self.player / 'release-state').mkdir(exist_ok=True)
        (self.project / 'server/instance.json').write_text(json.dumps(project_config), encoding='utf-8')
        (self.player / 'release-state/installation.json').write_text(json.dumps(player_config), encoding='utf-8')

    def test_valid_instance_metadata_confirms_paths_without_exposing_credentials(self):
        game = self.project / 'server/game/csgo'
        installed = self.player / 'instance'
        self.write_metadata()
        output = self.run_ps(self.helpers() + 'Get-NlwStatus $ctx @() @() | ConvertTo-Json -Depth 6')
        status = json.loads(output)
        self.assertEqual(status['ProjectPort'], 29025)
        self.assertEqual(status['PlayerPort'], 29026)
        self.assertEqual(Path(status['PlayerLogs']), installed / 'logs')
        self.assertEqual(Path(status['ProjectData']), game / 'addons/counterstrikesharp/plugins/NadeLab/data')
        self.assertEqual(status['Warnings'], [])
        self.assertNotIn('DO-NOT-PRINT-THIS', output)

    def test_external_metadata_paths_are_rejected_and_do_not_change_instance_identity(self):
        self.write_metadata(
            {'GameDirectory': str(self.fixture / 'elsewhere/game/csgo'), 'Executable': str(self.fixture / 'elsewhere/game/bin/win64/cs2.exe')},
            {'instance': str(self.fixture / 'elsewhere'), 'executable': str(self.fixture / 'elsewhere/game/bin/win64/cs2.exe')})
        output = self.run_ps(self.helpers() + """
            $rejected=$false;
            try { Assert-NlwStartOwnership $ctx @() @() } catch { $rejected=$true }
            if(-not $rejected) { throw 'Wrong metadata was accepted' }
            Get-NlwStatus $ctx @() @() | ConvertTo-Json -Depth 6
        """)
        status = json.loads(output)
        self.assertEqual(len(status['Warnings']), 4)
        self.assertEqual(Path(status['ProjectExecutable']), self.project / 'server/game/bin/win64/cs2.exe')
        self.assertEqual(Path(status['PlayerExecutable']), self.player / 'instance/game/bin/win64/cs2.exe')
        self.assertEqual(Path(status['ProjectData']), self.project / 'server/game/csgo/addons/counterstrikesharp/plugins/NadeLab/data')
        self.assertEqual(Path(status['PlayerLogs']), self.player / 'instance/logs')

    def test_copied_player_executable_cannot_alias_project_owner(self):
        player_exe = self.player / 'instance/game/bin/win64/cs2.exe'
        self.write_metadata({'Executable': str(player_exe), 'Port': 29026})
        output = self.run_ps(self.helpers() + """
            $processes=@([pscustomobject]@{Id=51;Path=$ctx.PlayerExecutable;Name='cs2.exe'});
            $ports=@([pscustomobject]@{Protocol='UDP';Port=$ctx.ProjectPort;Address='0.0.0.0';ProcessId=51});
            $rejected=$false;
            try { Assert-NlwStartOwnership $ctx $processes $ports } catch { $rejected=$true }
            if(-not $rejected) { throw 'Player instance was accepted as project owner' }
            Get-NlwStatus $ctx $processes $ports | ConvertTo-Json -Depth 6
        """)
        status = json.loads(output)
        self.assertNotEqual(status['ProjectExecutable'], status['PlayerExecutable'])
        self.assertEqual(status['Ports'][0]['Owner'], 'Player')
        self.assertEqual(status['ProjectProcessIds'], [])
        self.assertEqual(status['PlayerProcessIds'], [51])
        self.assertTrue(status['Warnings'])

    def test_copied_project_instance_cannot_alias_player_owner(self):
        project_exe = self.project / 'server/game/bin/win64/cs2.exe'
        self.write_metadata(player_overrides={'instance': str(self.project / 'server'), 'executable': str(project_exe)})
        output = self.run_ps(self.helpers() + """
            $processes=@([pscustomobject]@{Id=51;Path=$ctx.ProjectExecutable;Name='cs2.exe'});
            $ports=@([pscustomobject]@{Protocol='UDP';Port=$ctx.ProjectPort;Address='0.0.0.0';ProcessId=51});
            $rejected=$false;
            try { Assert-NlwStartOwnership $ctx $processes $ports } catch { $rejected=$true }
            if(-not $rejected) { throw 'Mismatched player metadata allowed start' }
            Get-NlwStatus $ctx $processes $ports | ConvertTo-Json -Depth 6
        """)
        status = json.loads(output)
        self.assertEqual(status['Ports'][0]['Owner'], 'Project')
        self.assertEqual(status['PlayerProcessIds'], [])
        self.assertEqual(status['ProjectProcessIds'], [51])
        self.assertEqual(len(status['Warnings']), 2)

    def test_ports_require_json_integer_in_supported_range(self):
        self.write_metadata()
        output = self.run_ps(self.helpers() + """
            $accepted=@(foreach($value in 1024,27025,65535) { Get-NlwMetadataPort $value 'test' });
            $rejected=0;
            foreach($value in @($null,0,1023,65536,-1,'27025',27025.0,$true,9999999999999999)) {
                $failed=$false;
                try { $null=Get-NlwMetadataPort $value 'test' } catch { $failed=$true }
                if(-not $failed) { throw 'Non-integer or invalid-range port accepted' }
                $rejected++;
            }
            [pscustomobject]@{ Accepted=$accepted; Rejected=$rejected } | ConvertTo-Json
        """)
        result = json.loads(output)
        self.assertEqual(result['Accepted'], [1024, 27025, 65535])
        self.assertEqual(result['Rejected'], 9)

    def test_invalid_metadata_port_prevents_start_even_without_listener(self):
        self.write_metadata({'Port': '27025'}, {'port': 70000})
        output = self.run_ps(self.helpers() + """
            $rejected=$false;
            try { Assert-NlwStartOwnership $ctx @() @() } catch { $rejected=$true }
            if(-not $rejected) { throw 'Invalid metadata port was accepted' }
            Get-NlwStatus $ctx @() @() | ConvertTo-Json -Depth 6
        """)
        status = json.loads(output)
        self.assertEqual(len(status['Warnings']), 2)
        self.assertEqual(status['ProjectPort'], 27025)
        self.assertEqual(status['PlayerPort'], 27025)

    def test_missing_debug_log_explains_next_step_without_creating_directory(self):
        script = self.fixture_script()
        output = self.run_ps(f'& {ps_string(script)} -Action DebugLogs -InstalledRoot {ps_string(self.player)}')
        self.assertIn('尚无工作台构建 / 安装记录', output)
        self.assertIn('4 或 5', output)
        self.assertFalse((self.project / 'artifacts').exists())

    def test_empty_debug_directory_explains_next_step_instead_of_opening_it(self):
        script = self.fixture_script()
        debug = self.project / 'artifacts/debug-sessions'
        debug.mkdir(parents=True)
        output = self.run_ps(f'& {ps_string(script)} -Action DebugLogs -InstalledRoot {ps_string(self.player)}')
        self.assertIn('尚无工作台构建 / 安装记录', output)
        self.assertEqual(list(debug.iterdir()), [])

    def test_same_port_different_instance_is_not_reported_as_project(self):
        output = self.run_ps(self.helpers() + """
            $processes=@([pscustomobject]@{Id=41;Path=$ctx.PlayerExecutable;Name='cs2.exe'},[pscustomobject]@{Id=42;Path='C:\\other\\cs2.exe';Name='cs2.exe'},[pscustomobject]@{Id=43;Path='';Name='cs2.exe'});
            $ports=@(foreach($id in 41,42,43,44){[pscustomobject]@{Protocol='UDP';Port=27025;Address='0.0.0.0';ProcessId=$id}});
            Get-NlwStatus $ctx $processes $ports | ConvertTo-Json -Depth 6
        """)
        status = json.loads(output)
        self.assertEqual(status['ProjectProcessIds'], [])
        self.assertEqual(status['PlayerProcessIds'], [41])
        self.assertEqual([p['Owner'] for p in status['Ports']], ['Player', 'Other', 'Unknown', 'Unknown'])

    def test_start_gate_rejects_other_and_unknown_owners_for_udp_and_tcp(self):
        output = self.run_ps(self.helpers() + """
            $count=0;
            foreach($protocol in 'UDP','TCP') {
                foreach($path in $ctx.PlayerExecutable,'C:\\other\\cs2.exe','') {
                    $processes=@([pscustomobject]@{Id=51;Path=$path;Name='cs2.exe'});
                    $ports=@([pscustomobject]@{Protocol=$protocol;Port=$ctx.ProjectPort;Address='0.0.0.0';ProcessId=51});
                    $rejected=$false;
                    try { Assert-NlwStartOwnership $ctx $processes $ports } catch { $rejected=$true }
                    if(-not $rejected) { throw 'An unrelated or unknown port owner was accepted' }
                    $count++;
                }
            }
            $count
        """)
        self.assertEqual(output, '6')

    def test_start_gate_accepts_own_instance_and_no_listener_without_actions(self):
        output = self.run_ps(self.helpers() + """
            Assert-NlwStartOwnership $ctx @() @();
            $processes=@([pscustomobject]@{Id=61;Path=$ctx.ProjectExecutable;Name='cs2.exe'});
            Assert-NlwStartOwnership $ctx $processes @([pscustomobject]@{Protocol='UDP';Port=$ctx.ProjectPort;Address='0.0.0.0';ProcessId=61});
            'PASS'
        """)
        self.assertEqual(output, 'PASS')
        self.assertEqual(list(self.project.iterdir()), [])

    def test_start_gate_does_not_treat_unreadable_cs2_process_as_free(self):
        output = self.run_ps(self.helpers() + """
            $processes=@([pscustomobject]@{Id=61;Path='';Name='cs2.exe'});
            $rejected=$false;
            try { Assert-NlwStartOwnership $ctx $processes @() } catch { $rejected=$true }
            if(-not $rejected) { throw 'Unreadable CS2 was treated as a free instance' }
            'PASS'
        """)
        self.assertEqual(output, 'PASS')

    def test_netstat_fallback_handles_udp_ipv6_and_ignores_remote_or_established_ports(self):
        output = self.run_ps(self.helpers() + """
            $lines=@(
                '  TCP    0.0.0.0:27025     0.0.0.0:0          LISTENING       10',
                '  TCP    [::]:27025       [::]:0             LISTENING       11',
                '  UDP    0.0.0.0:27025    *:*                                 12',
                '  UDP    [::1]:27025      *:*                                 13',
                '  TCP    127.0.0.1:27025  127.0.0.1:50000    ESTABLISHED     14',
                '  TCP    127.0.0.1:50000  127.0.0.1:27025    ESTABLISHED     15',
                '  UDP    0.0.0.0:27026    *:*                                 16'
            );
            @(ConvertFrom-NlwNetstat $lines @(27025)) | ConvertTo-Json
        """)
        ports = json.loads(output)
        self.assertEqual([p['ProcessId'] for p in ports], [10, 11, 12, 13])
        self.assertEqual([p['Protocol'] for p in ports], ['TCP', 'TCP', 'UDP', 'UDP'])

    def test_hash_comparison_distinguishes_missing_and_changed_files(self):
        built = self.project / 'build'; installed = self.project / 'installed'
        built.mkdir(); installed.mkdir()
        for name in ('NadeLab.dll', 'NadeLab.Core.dll', 'NadeLab.deps.json'):
            (built / name).write_bytes(b'new')
        (installed / 'NadeLab.dll').write_bytes(b'new')
        (installed / 'NadeLab.Core.dll').write_bytes(b'old')
        output = self.run_ps(self.helpers() + f'Get-NlwPluginComparison {ps_string(built)} {ps_string(installed)} | ConvertTo-Json')
        self.assertEqual([item['Result'] for item in json.loads(output)], ['Match', 'Different', 'MissingInstalled'])

    def test_missing_player_installation_is_clear_and_does_not_create_it(self):
        script = self.fixture_script()
        output = self.run_ps(f'& {ps_string(script)} -Action Player -InstalledRoot {ps_string(self.player)}', expected=1)
        self.assertIn('入口脚本不存在', output)
        self.assertEqual(list(self.player.iterdir()), [])

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 required for build dispatch fixture')
    def test_failed_build_preserves_log_and_exit_code_using_only_fixture(self):
        script = self.fixture_script()
        (self.project / 'scripts/build.ps1').write_text("param([switch]$NoPackage)\nif(-not $NoPackage){throw 'Packaging not disabled'}\nWrite-Output ('fixture build at '+(Get-Location).Path)\nexit 7\n", encoding='utf-8-sig')
        output = self.run_ps(f'& {ps_string(script)} -Action Build -InstalledRoot {ps_string(self.player)}', expected=1)
        self.assertIn('退出码 7', output)
        sessions = list((self.project / 'artifacts/debug-sessions').iterdir())
        self.assertEqual(len(sessions), 1)
        report = json.loads((sessions[0] / 'result.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(report['ExitCode'], 7)
        log = (sessions[0] / 'build.log').read_bytes()
        self.assertIn('fixture build at', log.decode('utf-16' if log.startswith(b'\xff\xfe') else 'utf-8-sig'))
        self.assertFalse((self.project / 'server').exists())

    def test_failed_plugin_install_preserves_log_without_stopping_any_process(self):
        script = self.fixture_script()
        (self.project / 'scripts/install-plugin.ps1').write_text(
            "param([string]$ServerRoot)\n"
            "if($ServerRoot -ne (Join-Path (Split-Path -Parent $PSScriptRoot) 'server')){throw 'Wrong instance'}\n"
            "Write-Output 'fixture: target server is running; refusing installation'\nexit 3\n", encoding='utf-8-sig')
        output = self.run_ps(f'& {ps_string(script)} -Action InstallPlugin -InstalledRoot {ps_string(self.player)}', expected=1)
        self.assertIn('退出码 3', output)
        sessions = list((self.project / 'artifacts/debug-sessions').iterdir())
        self.assertEqual(len(sessions), 1)
        report = json.loads((sessions[0] / 'result.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(report['Action'], 'InstallPlugin')
        self.assertEqual(report['ExitCode'], 3)
        log = (sessions[0] / 'install.log').read_bytes()
        self.assertIn('refusing installation', log.decode('utf-16' if log.startswith(b'\xff\xfe') else 'utf-8-sig'))
        self.assertFalse((self.project / 'server').exists())


if __name__ == '__main__':
    unittest.main()
