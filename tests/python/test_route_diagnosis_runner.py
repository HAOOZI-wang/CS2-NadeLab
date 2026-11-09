"""Known-route runner must bind native evidence and preserve user input files."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('route_runner', ROOT/'scripts/diagnose-route.py')
R = importlib.util.module_from_spec(SPEC)
if Path(SPEC.origin).exists(): SPEC.loader.exec_module(R)
PHYSICS = {'Kind': 1, 'Calibrated': False, 'FuseSeconds': 1.625, 'FireGroundNormal': .8660254,
           'Gravity': 320, 'ThrowSpeed': 675, 'Restitution': .45, 'TangentialRetention': .45,
           'RestSpeed': 20, 'StepSeconds': 1/128, 'MinDetonateSeconds': 1.5, 'MaxSeconds': 12,
           'HullHalfSize': 2, 'PlayerVelocityScale': 1.25, 'HeavyImpactSpeed': 330,
           'ReleaseDelaySeconds': .1, 'PlayerGravity': 800, 'JumpImpulse': 301.99338, 'WalkSpeedRatio': .52}


class RouteRunnerTests(unittest.TestCase):
    def case(self):
        return {'Id': 'case-a', 'Kind': 'Flash', 'Map': 'de_mirage', 'Evidence': 'actual-recording', 'KnownFeasible': True,
                'WorldVersion': {'GameVersion': 'v1', 'MapBytes': 100, 'MapLastWriteUtc': 'today'},
                'Start': {'Feet': {'X': 0, 'Y': 0, 'Z': 0}, 'EyeHeight': 64},
                'Target': {'Position': {'X': 100, 'Y': 100, 'Z': 100}, 'Radius': 32, 'Airspace': True},
                'Witness': {'Aim': {'Pitch': -30, 'Yaw': 45}, 'Physics': copy.deepcopy(PHYSICS), 'ExactAction': True}}

    def test_request_rejects_nonexact_witness_and_does_not_claim_a_real_route(self):
        case = self.case(); case['Witness']['ExactAction'] = False
        with self.assertRaises(R.DiagnosisError): R.build_request(case, 'run-a')

    def test_request_preserves_input_and_never_seeds_search(self):
        case = self.case(); before = copy.deepcopy(case)
        result = R.build_request(case, 'run-a')
        self.assertEqual(result['Start'], case['Start'])
        self.assertEqual(result['Target']['Position'], case['Target']['Position'])
        self.assertEqual(result['RequestId'], 'run-a')
        self.assertNotIn('PriorHints', result)
        self.assertEqual(case, before)

    def test_request_preserves_preparation_origin_and_run_up_distance(self):
        case = self.case(); case['Start'].update(Origin=1, RunUpDistance=120)
        self.assertEqual(R.build_request(case, 'run-a')['Start'], case['Start'])

    def test_search_evidence_rejects_changed_origin_contract_or_run_up_distance(self):
        case = self.case(); case['Start'].update(Origin=1, RunUpDistance=120)
        row = {'Status': 'complete', 'Map': case['Map'], 'WorldVersion': case['WorldVersion'],
               'Start': copy.deepcopy(case['Start']), 'Target': case['Target'], 'Kind': 'Flash',
               'Physics': copy.deepcopy(PHYSICS), 'Metrics': {'Matches': 0, 'Experimental': 0}}
        baseline = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        report = {'Status': 'complete', 'ReplayRestored': True, 'Instance': baseline, 'Cases': [row]}
        self.assertEqual(len(R.search_evidence(case, report, baseline)), 2)
        for changed in ({'Origin': 0}, {'Origin': 1, 'RunUpDistance': 140}):
            row['Start'] = {**case['Start'], **changed}
            with self.subTest(changed=changed), self.assertRaises(R.DiagnosisError):
                R.search_evidence(case, report, baseline)

    def test_result_rejects_stale_build_identity_and_wrong_request_bytes(self):
        raw = {'SchemaVersion': 1, 'RequestId': 'run-a', 'Status': 'complete', 'RequestSha256': 'hash',
               'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        baseline = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        R.validate_result(raw, 'run-a', 'hash', baseline)
        raw['PluginBuild'] = 'old'
        with self.assertRaises(R.DiagnosisError): R.validate_result(raw, 'run-a', 'hash', baseline)
        raw['PluginBuild'] = 'plugin'
        with self.assertRaises(R.DiagnosisError): R.validate_result(raw, 'run-a', 'different', baseline)

    def test_restore_preserves_exact_bytes_and_absence(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'route-diagnosis-input.json'
            path.write_bytes(b'\xef\xbb\xbfprevious\r\n')
            original = path.read_bytes(); path.write_bytes(b'temporary')
            self.assertTrue(R.restore_input(path, original))
            self.assertEqual(path.read_bytes(), original)
            self.assertTrue(R.restore_input(path, None))
            self.assertFalse(path.exists())

    def test_search_evidence_requires_same_case_and_same_current_core_build(self):
        case = self.case()
        row = {'Status': 'complete', 'Map': case['Map'], 'WorldVersion': case['WorldVersion'],
               'Start': case['Start'], 'Target': case['Target'], 'Kind': 'Flash',
               'Physics': copy.deepcopy(PHYSICS), 'Metrics': {'Matches': 0, 'Experimental': 0}}
        report = {'Status': 'complete', 'ReplayRestored': True, 'Instance': {'CoreBuild': 'core', 'PluginBuild': 'plugin'}, 'Cases': [row]}
        baseline = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        evidence = R.search_evidence(case, report, baseline)
        self.assertEqual(evidence, [{'Kind': 'SearchCompleted', 'Value': True},
                                    {'Kind': 'SearchFoundRoute', 'Value': False}])
        row['Physics']['Gravity'] = 500
        with self.assertRaises(R.DiagnosisError): R.search_evidence(case, report, baseline)
        row['Physics']['Gravity'] = 320
        report['Instance']['CoreBuild'] = 'old'
        with self.assertRaises(R.DiagnosisError): R.search_evidence(case, report, baseline)
        report['Instance']['CoreBuild'] = 'core'; row['Start'] = {'Feet': {'X': 500, 'Y': 0, 'Z': 0}, 'EyeHeight': 64}
        with self.assertRaises(R.DiagnosisError): R.search_evidence(case, report, baseline)

    def test_complete_runner_binds_output_and_restores_input_without_search_commands(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); data = root/'data'; data.mkdir()
            path = data/'route-diagnosis-input.json'; path.write_bytes(b'original\r\n')
            calls = []
            case = self.case()
            state = {'SchemaVersion': 1, 'Map': case['Map'], 'WorldVersion': case['WorldVersion'],
                     'Practice': True, 'Humans': 0, 'Idle': True, 'Epoch': 1,
                     'CoreBuild': 'core', 'PluginBuild': 'plugin',
                     'BenchmarkRunId': None, 'BenchmarkCaseId': None}
            def rcon(command):
                calls.append(command)
                if command == 'css_nl_benchmarkstatus':
                    return '[NadeLab] BENCHMARKSTATUS '+json.dumps(state)
                token = command.split()[-1]
                payload = path.read_bytes()
                raw = {'SchemaVersion': 1, 'RequestId': token, 'RequestSha256': hashlib.sha256(payload).hexdigest(),
                       'CoreBuild': 'core', 'PluginBuild': 'plugin', 'Status': 'complete'}
                (data/f'route-diagnosis-{token}.json').write_text(json.dumps(raw))
                return 'ROUTEDIAGNOSIS started'
            result = R.run_diagnosis(case, data, root/'report', rcon, root/'lock')
            self.assertEqual(result['Status'], 'complete')
            self.assertEqual(path.read_bytes(), b'original\r\n')
            self.assertFalse((root/'lock').exists())
            self.assertFalse(any('replay' in cmd or 'search' in cmd for cmd in calls))


if __name__ == '__main__': unittest.main()
