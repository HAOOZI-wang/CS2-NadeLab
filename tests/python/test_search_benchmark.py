"""Benchmark protocol regressions; fake RCON and isolated temporary files only."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

PROJECT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('nadelab_benchmark', PROJECT / 'scripts/benchmark-search.py')
BENCH = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BENCH)
WORLD = {'GameVersion': 'ServerVersion=123\n', 'MapBytes': 1234, 'MapLastWriteUtc': '2026-10-13T00:00:00Z'}
PHYSICS = {'Kind': 1, 'Calibrated': False, 'Id': 'private-profile-name',
           'FuseSeconds': 1.625, 'FireGroundNormal': .8660254, 'Gravity': 320, 'ThrowSpeed': 675,
           'Restitution': .45, 'TangentialRetention': .45, 'RestSpeed': 20, 'StepSeconds': 1/128,
           'MinDetonateSeconds': 1.5, 'MaxSeconds': 12, 'HullHalfSize': 2, 'PlayerVelocityScale': 1.25,
           'HeavyImpactSpeed': 330, 'ReleaseDelaySeconds': .1, 'PlayerGravity': 800,
           'JumpImpulse': 301.99338, 'WalkSpeedRatio': .52}


def source_case(identity='secret-player-id', evidence='actual-recording'):
    return {'Id': identity, 'Evidence': evidence, 'KnownFeasible': True,
            'Kind': 'Flash', 'Start': {'Feet': {'X': 10.1234567, 'Y': 20, 'Z': 30}, 'EyeHeight': 64},
            'Target': {'Position': {'X': 40, 'Y': 50, 'Z': 60}, 'Radius': 32,
                       'Airspace': True, 'Label': 'private target label'},
            'Source': {'SteamId': '12345678901234567', 'Name': 'Private Player', 'File': 'private/path'}}


class FakeServer:
    def __init__(self, data):
        self.data = data
        self.calls = []
        self.status = {'SchemaVersion': 1, 'Map': 'de_mirage', 'WorldVersion': WORLD,
                       'Practice': True, 'Humans': 0, 'Idle': True, 'Epoch': 5,
                       'BenchmarkRunId': None, 'BenchmarkCaseId': None,
                       'BenchmarkCacheIsolated': True, 'BenchmarkPassId': None,
                       'CoreBuild': 'core-123', 'PluginBuild': 'plugin-123'}
        self.mode = 'success'
        self.on_result = lambda result: result
        self.on_status = lambda status: status
        self.cancel_error = False

    def __call__(self, command):
        self.calls.append(command)
        if command == 'css_nl_benchmarkstatus':
            return '[NadeLab] BENCHMARKSTATUS ' + json.dumps(self.on_status(copy.deepcopy(self.status)))
        if command.startswith('css_nl_benchmarkcancel '):
            if self.cancel_error:
                raise ConnectionError('PRIVATE CONNECTION DETAILS')
            self.status.update(Idle=True, BenchmarkRunId=None, BenchmarkCaseId=None, BenchmarkPassId=None)
            return '[NadeLab] BENCHMARK cancelled.'
        if command.startswith('css_nl_replay recent'):
            n = int(command.split('recent')[1].split('-')[0])
            case = BENCH.read_json(self.data / 'replay-recent.json')['Cases'][n - 1]
            self.status.update(Idle=False, BenchmarkRunId=case['BenchmarkRunId'], BenchmarkCaseId=case['BenchmarkCaseId'], BenchmarkPassId=case.get('BenchmarkPassId'))
            if self.mode == 'rejected':
                return 'REPLAY failed: private-username'
            if self.mode == 'disconnect':
                raise ConnectionError('private-password')
            if self.mode == 'human':
                self.status['Humans'] = 1
            if self.mode == 'epoch':
                self.status['Epoch'] += 1
            if self.mode in ('wait', 'human', 'epoch', 'partial'):
                if self.mode == 'partial':
                    (self.data / 'replay-last.json').write_text('{', encoding='utf-8')
                return 'REPLAY search started; captured coordinates only, no player movement.'
            result = {key: copy.deepcopy(case[key]) for key in ('BenchmarkRunId', 'BenchmarkCaseId', 'Map', 'WorldVersion', 'Kind', 'Start', 'Target')}
            if 'BenchmarkPassId' in case:
                result['BenchmarkPassId'] = case['BenchmarkPassId']
                result['PriorHintCount'] = 0 if case['BenchmarkPassId'] == 'cold' else 2
            result.update(RequestedStart=copy.deepcopy(case['Start']), Utc='2026-10-13T00:00:00Z',
                          Physics=copy.deepcopy(PHYSICS),
                          Seconds=2.5, Result={'Evaluated': 300, 'Truncated': False, 'Matches': [], 'Experimental': []},
                          Diagnostics={'NativeTraces': 12345, 'WorkMs': 200, 'PeakTickMs': 2},
                          FirstRecommendationSeconds=None, FirstUsableSeconds=None,
                          SteamId='private-extra-field')
            (self.data / 'replay-last.json').write_bytes(BENCH.encode(self.on_result(result)))
            self.status.update(Idle=True, BenchmarkRunId=None, BenchmarkCaseId=None, BenchmarkPassId=None)
            return 'REPLAY search started; captured coordinates only, no player movement.'
        raise AssertionError('Unexpected command')


class SearchBenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nadelab-benchmark-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / 'data'
        self.data.mkdir()
        self.original = b'\xef\xbb\xbf{ "Cases": [], "SteamId": "private-original" }\r\n'
        (self.data / 'replay-recent.json').write_bytes(self.original)
        self.case_file = self.root / 'cases.json'
        self.write_cases([source_case()])
        self.server = FakeServer(self.data)
        self.lock = self.root / 'artifacts/benchmark.lock'
        self.now = 0

    def write_cases(self, cases):
        self.case_file.write_bytes(BENCH.encode({'SchemaVersion': 1, 'Map': 'de_mirage', 'WorldVersion': WORLD, 'Cases': cases}))

    def tick(self, amount):
        self.now += amount

    def run_suite(self, **kwargs):
        return BENCH.run_benchmark(BENCH.load_cases(self.case_file), self.data, self.root / 'output', self.server,
                                   lock_path=self.lock, clock=lambda: self.now, sleep=self.tick,
                                   timeout=1, poll=.2, **kwargs)

    def assert_restored(self, result):
        self.assertTrue(result['ReplayRestored'])
        self.assertEqual((self.data / 'replay-recent.json').read_bytes(), self.original)

    def test_success_anonymous_and_exact_byte_restoration(self):
        result = self.run_suite()
        self.assertEqual(result['Status'], 'complete')
        self.assert_restored(result)
        self.assertFalse(self.lock.exists())
        self.assertEqual(result['Cases'][0]['CaseId'], 'c001')
        self.assertEqual(result['Cases'][0]['Metrics']['Matches'], 0)
        text = json.dumps(result)
        for private in ('private', 'secret-player', '12345678901234567', 'Source', 'SteamId', 'Label'):
            self.assertNotIn(private, text)

    def test_new_embedded_map_identity_preserves_legacy_frozen_case(self):
        def add_map(raw):
            raw['WorldVersion'] = {**raw['WorldVersion'], 'MapName': raw['Map']}
            return raw
        self.server.on_status = add_map
        self.server.on_result = add_map
        result = self.run_suite()
        self.assertEqual(result['Status'], 'complete')
        self.assert_restored(result)
        self.assertEqual(result['Cases'][0]['WorldVersion'], WORLD)

    def test_contradictory_embedded_map_is_rejected_before_search(self):
        self.server.on_status = lambda raw: {**raw, 'WorldVersion': {**WORLD, 'MapName': 'de_nuke'}}
        result = self.run_suite()
        self.assertEqual(result['Status'], 'failed')
        self.assertEqual(result['Error'], 'world-map-name-mismatch')
        self.assertFalse(any(call.startswith('css_nl_replay ') for call in self.server.calls))

    def test_dry_run_no_writes_no_server_import(self):
        before = {p.relative_to(self.root): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}
        with mock.patch.object(BENCH.importlib.util, 'spec_from_file_location', side_effect=AssertionError('RCON import')), contextlib.redirect_stdout(io.StringIO()) as stream:
            code = BENCH.main(['--cases', str(self.case_file), '--dry-run', '--output', str(self.root/'absent')])
        self.assertEqual(code, 0)
        self.assertFalse(json.loads(stream.getvalue())['CurrentInstanceVerified'])
        self.assertEqual(before, {p.relative_to(self.root): p.read_bytes() for p in self.root.rglob('*') if p.is_file()})

    def test_requires_explicit_evidence_and_unique_ids(self):
        for modification in ('missing', 'invented', 'duplicate'):
            with self.subTest(modification=modification):
                case = source_case()
                if modification == 'missing':
                    del case['Evidence']
                else:
                    case['Evidence'] = 'human' if modification == 'invented' else 'synthetic'
                self.write_cases([case, copy.deepcopy(case)] if modification == 'duplicate' else [case])
                with self.assertRaises(BENCH.BenchmarkError):
                    BENCH.load_cases(self.case_file)

    def test_initial_human_refused_without_replay_mutation(self):
        self.server.status['Humans'] = 1
        result = self.run_suite()
        self.assertEqual(result['Error'], 'human-connected')
        self.assertEqual(self.server.calls, ['css_nl_benchmarkstatus'])
        self.assertEqual((self.data/'replay-recent.json').read_bytes(), self.original)

    def test_initial_busy_refused(self):
        self.server.status['Idle'] = False
        self.assertEqual(self.run_suite()['Error'], 'server-not-idle')

    def test_unknown_protocol_refused_without_legacy_fallback(self):
        self.server.on_status = lambda s: {**s, 'SchemaVersion': 0}
        self.assertEqual(self.run_suite()['Error'], 'unsupported-status-schema')

    def test_stale_rejected(self):
        self.server.status['WorldVersion'] = {**WORLD, 'MapBytes': 999}
        result = self.run_suite()
        self.assertEqual(result['Error'], 'stale-case-world')
        self.assertFalse(any('css_nl_replay ' in x for x in self.server.calls))

    def test_stale_mark_does_not_replay(self):
        self.server.status['Map'] = 'de_nuke'
        result = self.run_suite(stale_policy='mark')
        self.assertEqual(result['Status'], 'complete')
        self.assertEqual(result['Cases'][0]['Status'], 'stale')
        self.assertIsNone(result['ReplayRestored'])
        self.assertEqual(self.server.calls, ['css_nl_benchmarkstatus'])

    def test_mixed_stale_cases_use_injected_index_not_public_id(self):
        a, b = source_case('one'), source_case('two')
        a['Map'] = 'de_nuke'
        self.write_cases([a, b])
        result = self.run_suite(stale_policy='mark')
        self.assertEqual([r['Status'] for r in result['Cases']], ['stale', 'complete'])
        self.assertIn('css_nl_replay recent1-cold-auto', self.server.calls)
        self.assertEqual(result['Cases'][1]['CaseId'], 'c002')

    def test_human_join_cancels_and_restores(self):
        self.server.mode = 'human'
        result = self.run_suite()
        self.assertEqual(result['Error'], 'human-connected')
        self.assertEqual(result['Cancellation'], 'cancelled')
        self.assert_restored(result)
        self.assertTrue(any(x.startswith('css_nl_benchmarkcancel ') for x in self.server.calls))

    def test_epoch_change_cancels(self):
        self.server.mode = 'epoch'
        result = self.run_suite()
        self.assertEqual(result['Error'], 'instance-changed')
        self.assert_restored(result)

    def test_timeout_allows_busy_own_run_then_cancels(self):
        self.server.mode = 'wait'
        result = self.run_suite()
        self.assertEqual(result['Error'], 'case-timeout')
        self.assert_restored(result)
        self.assertGreater(self.server.calls.count('css_nl_benchmarkstatus'), 2)

    def test_partial_json_times_out_instead_of_using_old_result(self):
        self.server.mode = 'partial'
        result = self.run_suite()
        self.assertEqual(result['Error'], 'case-timeout')
        self.assert_restored(result)

    def test_uncertain_cancel_retains_lock_and_redacts_errors(self):
        self.server.mode, self.server.cancel_error = 'disconnect', True
        result = self.run_suite()
        self.assertEqual(result['Cancellation'], 'unconfirmed')
        self.assertTrue(self.lock.exists())
        self.assert_restored(result)
        self.assertNotIn('private', json.dumps(result).lower())

    def test_preexisting_lock_blocks_without_removing_foreign_lock(self):
        self.lock.parent.mkdir()
        self.lock.write_text('other-run')
        result = self.run_suite()
        self.assertEqual(result['Error'], 'benchmark-already-locked')
        self.assertEqual(self.lock.read_text(), 'other-run')
        self.assertEqual(self.server.calls, [])

    def test_original_missing_removed_after_success(self):
        (self.data/'replay-recent.json').unlink()
        result = self.run_suite()
        self.assertTrue(result['ReplayRestored'])
        self.assertFalse((self.data/'replay-recent.json').exists())

    def test_result_wrong_run_rejected(self):
        self.server.on_result = lambda result: {**result, 'BenchmarkRunId': 'stale-run'}
        result = self.run_suite()
        self.assertEqual(result['Error'], 'result-identity-mismatch')
        self.assert_restored(result)

    def test_result_changed_coordinates_rejected(self):
        def change(result):
            result['Start']['Feet']['X'] += 1
            return result
        self.server.on_result = change
        result = self.run_suite()
        self.assertEqual(result['Error'], 'result-origin-mismatch')
        self.assert_restored(result)

    def test_preparation_origin_survives_case_generation_replay_and_report(self):
        case = source_case()
        case['Start'].update(Origin=1, RunUpDistance=120)
        self.write_cases([case])
        replayed = []
        self.server.on_result = lambda row: replayed.append(copy.deepcopy(row['Start'])) or row
        result = self.run_suite()
        self.assertEqual(result['Status'], 'complete')
        self.assertEqual(replayed, [case['Start']])
        self.assertEqual(result['Cases'][0]['Start'], case['Start'])
        self.assert_restored(result)

    def test_result_changed_origin_contract_is_rejected(self):
        def change(row):
            row['Start']['Origin'] = 1
            return row
        self.server.on_result = change
        result = self.run_suite()
        self.assertEqual(result.get('Error'), 'result-origin-mismatch')
        self.assert_restored(result)

    def test_result_changed_requested_run_up_distance_is_rejected(self):
        case = source_case()
        case['Start'].update(Origin=1, RunUpDistance=120)
        self.write_cases([case])
        def change(row):
            row['RequestedStart']['RunUpDistance'] = 140
            return row
        self.server.on_result = change
        result = self.run_suite()
        self.assertEqual(result.get('Error'), 'result-origin-mismatch')
        self.assert_restored(result)

    def test_origin_canonicalization_preserves_legacy_identity_and_preparation_defaults(self):
        legacy = source_case()['Start']
        self.assertEqual(BENCH.start({**legacy, 'Origin': 0, 'RunUpDistance': 160}), legacy)
        self.assertEqual(BENCH.fingerprint(BENCH.start(legacy)), BENCH.fingerprint(legacy))
        preparation = {**legacy, 'Origin': 1}
        expected = {**preparation, 'RunUpDistance': 100}
        self.assertEqual(BENCH.start(preparation), expected)
        self.assertEqual(BENCH.fingerprint(BENCH.start(preparation)),
                         BENCH.fingerprint(BENCH.start({**preparation, 'RunUpDistance': 100.0})))
        self.assertNotEqual(BENCH.fingerprint(BENCH.start(preparation)), BENCH.fingerprint(BENCH.start(legacy)))
        self.assertNotEqual(BENCH.fingerprint(BENCH.start(preparation)),
                            BENCH.fingerprint(BENCH.start({**preparation, 'RunUpDistance': 120})))

    def test_unknown_origin_and_invalid_preparation_distance_are_rejected(self):
        base = source_case()['Start']
        for origin in (-1, 2, True, 1.0, 'Preparation'):
            with self.subTest(origin=origin), self.assertRaises(BENCH.BenchmarkError):
                BENCH.start({**base, 'Origin': origin})
        for distance in (59, 161, float('nan'), float('inf'), True, '100'):
            with self.subTest(distance=distance), self.assertRaises(BENCH.BenchmarkError):
                BENCH.start({**base, 'Origin': 1, 'RunUpDistance': distance})
        for distance in (60, 160):
            self.assertEqual(BENCH.start({**base, 'Origin': 1, 'RunUpDistance': distance})['RunUpDistance'], distance)

    def test_result_changed_target_rejected(self):
        self.server.on_result = lambda r: {**r, 'Target': {**r['Target'], 'Radius': 64}}
        self.assertEqual(self.run_suite()['Error'], 'result-target-mismatch')

    def test_result_requested_origin_must_match_even_when_actual_origin_matches(self):
        def change(result):
            result['RequestedStart']['Feet']['Z'] += 4
            return result
        self.server.on_result = change
        self.assertEqual(self.run_suite()['Error'], 'result-origin-mismatch')

    def test_foreign_job_does_not_get_accepted_as_own_search(self):
        self.server.mode = 'wait'
        def status(s):
            if not s['Idle']:
                s['BenchmarkRunId'] = 'foreign-run'
            return s
        self.server.on_status = status
        result = self.run_suite()
        self.assertEqual(result['Error'], 'foreign-job')
        self.assertNotIn('css_nl_benchmarkcancel foreign-run', self.server.calls)
        self.assert_restored(result)

    def test_float32_round_trip_is_allowed_but_not_actual_relocation(self):
        def rounded(result):
            result['Start']['Feet']['X'] = 10.123457
            result['RequestedStart']['Feet']['X'] = 10.123457
            result['Target']['ExtraRegions'] = None
            result['Target']['Exclusions'] = None
            return result
        self.server.on_result = rounded
        self.assertEqual(self.run_suite()['Status'], 'complete')

    def test_write_failure_restores_original_and_keeps_safe_error(self):
        original_write = BENCH.atomic_write
        calls = 0
        def failed_write(path, contents):
            nonlocal calls
            calls += 1
            if calls == 1:
                path.write_bytes(b'partial payload')
                raise OSError('private filesystem message')
            return original_write(path, contents)
        with mock.patch.object(BENCH, 'atomic_write', failed_write):
            result = self.run_suite()
        self.assertEqual(result['Error'], 'io-or-protocol-failure')
        self.assert_restored(result)

    def test_string_enum_physics_supported(self):
        self.server.on_result = lambda r: {**r, 'Physics': {**r['Physics'], 'Kind': 'Flash'}}
        self.assertEqual(self.run_suite()['Status'], 'complete')

    def test_compare_offline_dry_run_does_not_write(self):
        report = self.run_suite()
        with contextlib.redirect_stdout(io.StringIO()) as stream:
            code = BENCH.main(['--compare', str(self.root/'output/report.json'), str(self.root/'output/report.json'),
                               '--output', str(self.root/'comparison'), '--dry-run'])
        self.assertEqual(code, 0)
        self.assertFalse((self.root/'comparison').exists())
        self.assertTrue(json.loads(stream.getvalue())['Cases'][0]['Comparable'])

    def test_checks_idle_and_humans_before_every_case(self):
        self.write_cases([source_case('one'), source_case('two')])
        def status(s):
            # First case has completed (4 status calls), then admission before second.
            if self.server.calls.count('css_nl_benchmarkstatus') == 5:
                s['Humans'] = 1
            return s
        self.server.on_status = status
        result = self.run_suite()
        self.assertEqual(result['Error'], 'human-connected')
        self.assertEqual([r['Status'] for r in result['Cases']], ['complete', 'pending'])
        self.assertEqual(sum(x.startswith('css_nl_replay ') for x in self.server.calls), 1)
        self.assert_restored(result)

    def test_evidence_groups_do_not_claim_real_validation(self):
        self.write_cases([source_case('one', 'actual-recording'), source_case('two', 'historical-search'), source_case('three', 'synthetic')])
        result = self.run_suite()
        self.assertEqual(result['Status'], 'complete')
        self.assertEqual({k: v['Completed'] for k, v in result['Summary'].items()}, {k: 1 for k in BENCH.EVIDENCE})
        self.assertNotIn('Validated', result['Summary'])

    def test_comparison_requires_same_case_world_and_physics(self):
        before = self.run_suite()
        after = copy.deepcopy(before)
        after['Cases'][0]['Metrics']['Seconds'] = 1.5
        compared = BENCH.compare_reports(before, after)
        self.assertTrue(compared['Cases'][0]['Comparable'])
        self.assertEqual(compared['Cases'][0]['Delta']['Seconds'], -1)
        self.assertIsNone(compared['Cases'][0]['Delta']['FirstUsableSeconds'])
        after['Cases'][0]['Physics']['Gravity'] = 321
        after['Cases'][0]['PhysicsFingerprint'] = BENCH.fingerprint(after['Cases'][0]['Physics'])
        self.assertEqual(BENCH.compare_reports(before, after)['Cases'][0]['Reason'], 'physics-changed')
        after['Cases'][0]['Fingerprint'] = 'other'
        self.assertEqual(BENCH.compare_reports(before, after)['Cases'][0]['Reason'], 'case-or-world-changed')

    def test_every_physics_parameter_change_is_non_comparable(self):
        before = self.run_suite()
        for key in BENCH.PHYSICS_NUMBERS:
            with self.subTest(parameter=key):
                after = copy.deepcopy(before)
                profile = copy.deepcopy(PHYSICS)
                profile[key] += .125
                profile = BENCH.physics_profile(profile, 'Flash')
                after['Cases'][0].update(Physics=profile, PhysicsFingerprint=BENCH.fingerprint(profile))
                compared = BENCH.compare_reports(before, after)['Cases'][0]
                self.assertFalse(compared['Comparable'])
                self.assertEqual(compared['Reason'], 'physics-changed')

    def test_incomplete_or_new_physics_schema_cannot_silently_compare(self):
        before = self.run_suite()
        for missing in (True, False):
            with self.subTest(missing=missing):
                after = copy.deepcopy(before)
                profile = after['Cases'][0]['Physics']
                if missing:
                    del profile['HullHalfSize']
                else:
                    profile['NewFuturePhysicsParameter'] = 1
                after['Cases'][0]['PhysicsFingerprint'] = BENCH.fingerprint(profile)
                compared = BENCH.compare_reports(before, after)['Cases'][0]
                self.assertFalse(compared['Comparable'])
                self.assertEqual(compared['Reason'], 'incomplete-or-invalid-physics')

    def test_physics_id_is_not_part_of_the_model(self):
        changed = {**PHYSICS, 'Id': 'another-build-label'}
        self.assertEqual(BENCH.physics_profile(PHYSICS, 'Flash'), BENCH.physics_profile(changed, 'Flash'))

    def test_repeat_keeps_cold_warm_results_separate_and_serial(self):
        self.write_cases([source_case('one'), source_case('two')])
        self.server.on_result = lambda r: {**r, 'Seconds': 10 if r['BenchmarkPassId'] == 'cold' else 2}
        result = self.run_suite(repeat=True)
        self.assertEqual(result['Status'], 'complete')
        self.assertEqual(result['Mode'], 'repeat-auto')
        self.assertEqual([c for c in self.server.calls if c.startswith('css_nl_replay ')],
                         ['css_nl_replay recent1-cold-auto', 'css_nl_replay recent1-auto',
                          'css_nl_replay recent2-cold-auto', 'css_nl_replay recent2-auto'])
        for row in result['Cases']:
            self.assertEqual(row['Cold']['Metrics']['Seconds'], 10)
            self.assertEqual(row['Warm']['Metrics']['Seconds'], 2)
            self.assertEqual(row['Metrics'], row['Warm']['Metrics'])
            self.assertEqual(row['Cold']['Metrics']['PriorHintCount'], 0)
            self.assertEqual(row['Warm']['Metrics']['PriorHintCount'], 2)
        self.assert_restored(result)
        compared = BENCH.compare_reports(result, copy.deepcopy(result))
        self.assertTrue(compared['Cases'][0]['Comparable'])
        self.assertEqual(compared['Cases'][0]['Cold']['Delta']['Seconds'], 0)
        self.assertEqual(compared['Cases'][0]['Warm']['Delta']['Seconds'], 0)
        self.assertEqual(compared['Mode'], 'repeat-auto')

    def test_repeat_requires_isolated_cache_before_mutating_replay(self):
        del self.server.status['BenchmarkCacheIsolated']
        result = self.run_suite(repeat=True)
        self.assertEqual(result['Error'], 'isolated-benchmark-cache-required')
        self.assertEqual((self.data/'replay-recent.json').read_bytes(), self.original)
        self.assertFalse(any(c.startswith('css_nl_replay ') for c in self.server.calls))

    def test_repeat_warm_human_join_keeps_cold_result_cancels_and_restores(self):
        def after_cold(result):
            self.server.mode = 'human'
            return result
        self.server.on_result = after_cold
        result = self.run_suite(repeat=True)
        row = result['Cases'][0]
        self.assertEqual(row['Cold']['Status'], 'complete')
        self.assertEqual(row['Warm']['Status'], 'failed')
        self.assertEqual(row['Warm']['Error'], 'human-connected')
        self.assertNotIn('Metrics', row)
        self.assertEqual(result['Cancellation'], 'cancelled')
        self.assert_restored(result)

    def test_repeat_wrong_pass_cannot_be_accepted_as_warm_completion(self):
        self.server.on_result = lambda r: {**r, 'BenchmarkPassId': 'cold'}
        result = self.run_suite(repeat=True)
        self.assertEqual(result['Error'], 'result-pass-mismatch')
        self.assertEqual(result['Cases'][0]['Cold']['Status'], 'complete')
        self.assertEqual(result['Cases'][0]['Warm']['Status'], 'failed')
        self.assert_restored(result)

    def test_repeat_cold_must_not_read_prior_cache(self):
        self.server.on_result = lambda r: {**r, 'PriorHintCount': 1}
        result = self.run_suite(repeat=True)
        self.assertEqual(result['Error'], 'cold-pass-used-prior-hints')
        self.assert_restored(result)

    def test_repeat_cache_guarantee_must_remain_true_and_warm_can_have_no_hints(self):
        self.server.on_result = lambda r: {**r, 'PriorHintCount': 0}
        result = self.run_suite(repeat=True)
        self.assertEqual(result['Status'], 'complete')
        self.assertEqual(result['Cases'][0]['Warm']['Metrics']['PriorHintCount'], 0)
        self.assert_restored(result)
        def status(s):
            if self.server.calls.count('css_nl_benchmarkstatus') >= 3:
                s['BenchmarkCacheIsolated'] = False
            return s
        self.server.on_status = status
        with self.assertRaisesRegex(BENCH.BenchmarkError, 'isolated-benchmark-cache-required'):
            BENCH.safe_status(self.server, isolated=True)

    def test_repeat_comparison_rejects_cold_only_or_incomplete_stage(self):
        repeat = self.run_suite(repeat=True)
        cold = copy.deepcopy(repeat)
        cold['Mode'] = 'cold-auto'
        with self.assertRaisesRegex(BENCH.BenchmarkError, 'incompatible-benchmark-mode'):
            BENCH.compare_reports(cold, repeat)
        broken = copy.deepcopy(repeat)
        del broken['Cases'][0]['Cold']['Physics']['HullHalfSize']
        self.assertEqual(BENCH.compare_reports(repeat, broken)['Cases'][0]['Reason'], 'incomplete-or-invalid-pass')

    def test_repeat_dry_run_never_connects_or_writes(self):
        with mock.patch.object(BENCH.importlib.util, 'spec_from_file_location', side_effect=AssertionError('RCON import')), contextlib.redirect_stdout(io.StringIO()) as stream:
            self.assertEqual(BENCH.main(['--cases', str(self.case_file), '--repeat', '--dry-run', '--output', str(self.root/'absent')]), 0)
        self.assertEqual(json.loads(stream.getvalue())['Mode'], 'repeat-auto')
        self.assertFalse((self.root/'absent').exists())


if __name__ == '__main__':
    unittest.main()
