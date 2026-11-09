"""Acceptance gates exercise records and reports, never source-text assertions."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('acceptance', ROOT/'scripts/acceptance.py')
A = importlib.util.module_from_spec(SPEC)
if SPEC.loader and SPEC.origin and Path(SPEC.origin).exists():
    SPEC.loader.exec_module(A)
WORLD = {'GameVersion': 'ServerVersion=100\r\n', 'MapBytes': 100, 'MapLastWriteUtc': '2026-10-27T00:00:00.0000000Z'}
PHYSICS = {'Kind': 1, 'Calibrated': False, 'FuseSeconds': 1.625, 'FireGroundNormal': .8660254,
           'Gravity': 320, 'ThrowSpeed': 675, 'Restitution': .45, 'TangentialRetention': .45,
           'RestSpeed': 20, 'StepSeconds': 1/128, 'MinDetonateSeconds': 1.5, 'MaxSeconds': 12,
           'HullHalfSize': 2, 'PlayerVelocityScale': 1.25, 'HeavyImpactSpeed': 330,
           'ReleaseDelaySeconds': .1, 'PlayerGravity': 800, 'JumpImpulse': 301.99338, 'WalkSpeedRatio': .52}


def recorded():
    return {'Map': 'de_mirage', 'WorldVersion': copy.deepcopy(WORLD), 'Kind': 'Flash',
            'PlayerIsBot': False, 'ExecutionMatched': True, 'Outcome': 'detonated',
            'Utc': '2026-10-27T01:00:00Z', 'ThrowId': 'private-identifier',
            'PlayerSteamId': 'secret-account',
            'Start': {'Feet': {'X': 1, 'Y': 2, 'Z': 3}, 'EyeHeight': 64},
            'ActualThrow': {'Feet': {'X': 1, 'Y': 2, 'Z': 3}, 'EyeHeight': 64, 'Strength': 1,
                            'Aim': {'Pitch': -30, 'Yaw': 40, 'Mode': 0},
                            'Velocity': {'X': 0, 'Y': 0, 'Z': 0}, 'OnGround': True, 'JumpThrow': False},
            'Predicted': {'Aim': {'Pitch': -30, 'Yaw': 40, 'Mode': 0, 'Action': 0}},
            'Actual': {'X': 100, 'Y': 200, 'Z': 50}, 'Target': None,
            'Physics': copy.deepcopy(PHYSICS), 'EffectVerified': False}


def air_forward_recorded():
    row = recorded()
    row.update(Kind='Smoke', Outcome='rested')
    row['Physics']['Kind'] = 0
    row['Start'].update(EyeHeight=58, Origin=1, RunUpDistance=100)
    row['Predicted']['Aim'].update(Yaw=90, Action=8, ForwardSpeed=30,
                                   ForwardOffset=3, ReleaseHeight=26, VerticalSpeed=219)
    row['ActualThrow']['Aim']['Yaw'] = 90
    row['ActualThrow'].update(Feet={'X': 1, 'Y': 5, 'Z': 29}, EyeHeight=64,
                              Velocity={'X': 0, 'Y': 30, 'Z': 219}, OnGround=False, JumpThrow=True)
    return row


class AcceptanceTests(unittest.TestCase):
    def corpus(self, rows):
        return A.audit_records(rows, {'de_mirage': WORLD})

    def test_matching_human_capture_exports_endpoint_witness_without_effect_claim(self):
        result = self.corpus([recorded()])
        self.assertEqual(result['KnownFeasible'], 1)
        case = result['Cases'][0]
        self.assertTrue(case['KnownFeasible'])
        self.assertEqual(case['Target']['Position'], {'X': 100, 'Y': 200, 'Z': 50})
        self.assertEqual(case['Target']['Radius'], 32)
        self.assertEqual(case['TargetSource'], 'observed-endpoint')
        self.assertFalse(case['EffectVerified'])
        self.assertNotIn('secret-account', json.dumps(result))
        self.assertNotIn('private-identifier', json.dumps(result))

    def test_stale_capture_is_not_relabelled_or_counted(self):
        row = recorded(); row['WorldVersion']['GameVersion'] = 'old'
        original = copy.deepcopy(row)
        result = self.corpus([row])
        self.assertEqual(result['KnownFeasible'], 0)
        self.assertEqual(result['Rejected']['stale-world'], 1)
        self.assertEqual(row, original)

    def test_unknown_actor_bot_unmatched_and_nonfinite_are_excluded(self):
        rows = []
        for changes in ({'PlayerIsBot': None}, {'PlayerIsBot': True}, {'ExecutionMatched': False},
                        {'Actual': {'X': float('nan'), 'Y': 0, 'Z': 0}}):
            row = recorded(); row.update(changes); rows.append(row)
        self.assertEqual(self.corpus(rows)['KnownFeasible'], 0)

    def test_original_missed_target_is_not_replaced_by_observed_endpoint(self):
        row = recorded(); row['Target'] = {'Position': {'X': 900, 'Y': 900, 'Z': 0}, 'Radius': 16}
        row['PositionHit'] = False
        result = self.corpus([row])
        self.assertEqual(result['KnownFeasible'], 0)
        self.assertEqual(result['Rejected']['target-not-confirmed'], 1)

    def test_duplicate_capture_does_not_fill_twelve_case_goal(self):
        result = self.corpus([recorded() for _ in range(12)])
        self.assertEqual(result['KnownFeasible'], 1)
        self.assertEqual(result['RequiredCases'], 12)
        self.assertEqual(result['MissingCases'], 11)
        self.assertEqual(result['Status'], 'incomplete')

    def test_noncanonical_strength_retains_real_feasibility_but_not_exact_model_witness(self):
        row = recorded(); row['ActualThrow']['Strength'] = .54; row['Predicted']['Aim']['Mode'] = 1
        row['ActualThrow']['Aim']['Mode'] = 1
        result = self.corpus([row])
        self.assertEqual(result['KnownFeasible'], 1)
        self.assertFalse(result['Cases'][0]['Witness']['ExactAction'])

    def test_missing_or_approximate_observed_launch_cannot_diagnose_physics(self):
        for change in ('missing-aim', 'velocity', 'angle', 'eye', 'ground', 'physics'):
            row = recorded()
            if change == 'missing-aim': row['ActualThrow'].pop('Aim')
            elif change == 'velocity': row['ActualThrow']['Velocity']['X'] = 4
            elif change == 'angle': row['ActualThrow']['Aim']['Pitch'] += .4
            elif change == 'eye': row['ActualThrow']['EyeHeight'] -= .5
            elif change == 'ground': row['ActualThrow'].pop('OnGround')
            elif change == 'physics': row['Physics'] = {'Kind': 1}
            with self.subTest(change=change):
                result = self.corpus([row])
                self.assertEqual(result['KnownFeasible'], 1)
                self.assertFalse(result['Cases'][0]['Witness']['ExactAction'])

    def test_network_angle_quantization_is_allowed_but_strength_mismatch_is_not(self):
        row = recorded(); row['ActualThrow']['Aim']['Pitch'] += .0002
        self.assertTrue(self.corpus([row])['Cases'][0]['Witness']['ExactAction'])
        row['ActualThrow']['Strength'] = .99
        self.assertFalse(self.corpus([row])['Cases'][0]['Witness']['ExactAction'])

    def test_declared_hit_outside_recorded_geometry_is_not_accepted(self):
        row = recorded(); row['Target'] = {'Position': {'X': 900, 'Y': 900, 'Z': 0}, 'Radius': 16}
        row['PositionHit'] = True
        self.assertEqual(self.corpus([row])['KnownFeasible'], 0)

    def test_airspace_areas_use_engine_model_ellipse_not_box_corners(self):
        row = recorded(); row['Actual'] = {'X': 9, 'Y': 9, 'Z': 0}
        row['Target'] = {'Position': {'X': 0, 'Y': 0, 'Z': 0}, 'Radius': 32,
                         'AreaHalfSize': {'X': 10, 'Y': 10, 'Z': 10}, 'Airspace': True}
        row['PositionHit'] = True
        self.assertEqual(self.corpus([row])['KnownFeasible'], 0)

    def test_actual_release_far_from_requested_start_is_not_a_witness(self):
        row = recorded(); row['ActualThrow']['Feet']['X'] = 900
        self.assertEqual(self.corpus([row])['KnownFeasible'], 0)

    def test_preparation_origin_exports_exact_moving_witness_with_the_actual_release_geometry(self):
        # Start is (1,2,3), run-up is120, delay is.1. Only Run/Walk add
        # their release-delay travel; RunJump adds its existing airborne offset.
        cases = (
            (1, 250, 0, 0, 0, {'X': 146, 'Y': 2, 'Z': 3}),
            (2, 130, 0, 0, 0, {'X': 134, 'Y': 2, 'Z': 3}),
            (5, 250, 26, 26, 219, {'X': 147, 'Y': 2, 'Z': 29}),
            (3, 125, 12.5, 0, 0, {'X': 13.5, 'Y': 2, 'Z': 3}),
            (0, 250, 0, 0, 0, {'X': 146, 'Y': 2, 'Z': 3}),
        )
        for action, speed, offset, height, vertical, feet in cases:
            with self.subTest(action=action):
                row = recorded(); row['Start'].update(Origin=1, RunUpDistance=120)
                row['Predicted']['Aim'].update(Yaw=0, Action=action, ForwardSpeed=speed,
                                               ForwardOffset=offset, ReleaseHeight=height, VerticalSpeed=vertical)
                row['ActualThrow']['Aim']['Yaw'] = 0
                row['ActualThrow'].update(Feet=feet, Velocity={'X': speed, 'Y': 0, 'Z': vertical},
                                          OnGround=action != 5, JumpThrow=action == 5)
                before = copy.deepcopy(row)
                result = self.corpus([row])
                self.assertEqual(result['KnownFeasible'], 1, result['Rejected'])
                self.assertTrue(result['Cases'][0]['Witness']['ExactAction'])
                self.assertEqual(result['Cases'][0]['Start'], row['Start'])
                self.assertEqual(row, before)

    def test_preparation_origin_cannot_validate_the_obsolete_unshifted_release_point(self):
        row = recorded(); row['Start'].update(Origin=1, RunUpDistance=100)
        row['Predicted']['Aim'].update(Yaw=0, Action=1, ForwardSpeed=250)
        row['ActualThrow']['Aim']['Yaw'] = 0
        row['ActualThrow']['Velocity']['X'] = 250
        result = self.corpus([row])
        self.assertEqual(result['KnownFeasible'], 0)
        self.assertEqual(result['Rejected'].get('release-origin-mismatch'), 1)

    def test_air_forward_jump_exports_three_unit_release_without_a_run_up(self):
        for origin, distance in ((0, 100), (1, 100), (1, 160)):
            with self.subTest(origin=origin, distance=distance):
                row = air_forward_recorded()
                row['Start'].update(Origin=origin, RunUpDistance=distance)
                before = copy.deepcopy(row)
                release = A.release_feet(row['Start'], row['Predicted']['Aim'], row['Physics'])
                for actual, expected in zip(release, (1, 5, 29)):
                    self.assertAlmostEqual(actual, expected)
                result = self.corpus([row])
                self.assertEqual(result['KnownFeasible'], 1, result['Rejected'])
                witness = result['Cases'][0]['Witness']
                self.assertTrue(witness['ExactAction'])
                self.assertEqual(witness['Aim']['Action'], 8)
                self.assertEqual(witness['Aim']['ForwardOffset'], 3)
                self.assertNotIn('Moving', result['CoverageGaps'])
                self.assertIn('StationaryJump', result['CoverageGaps'])
                self.assertEqual(row, before)

    def test_air_forward_exact_launch_requires_airborne_motion_and_latched_source(self):
        row = air_forward_recorded()
        predicted = A.BENCH.aim(row['Predicted']['Aim'])
        self.assertTrue(A.exact_launch(row['Start'], predicted, row['ActualThrow'], row['Physics'], 'Smoke'))
        for change in ('grounded', 'unlatched', 'ground-speed', 'prestep', 'run-up', 'eye'):
            with self.subTest(change=change):
                actual = copy.deepcopy(row['ActualThrow'])
                if change == 'grounded': actual['OnGround'] = True
                elif change == 'unlatched': actual['JumpThrow'] = False
                elif change == 'ground-speed': actual['Velocity']['Y'] = 60
                elif change == 'prestep': actual['Feet']['Y'] += 5
                elif change == 'run-up': actual['Feet']['Y'] += 100
                elif change == 'eye': actual['EyeHeight'] = 58
                self.assertFalse(A.exact_launch(row['Start'], predicted, actual, row['Physics'], 'Smoke'))

    def test_action_after_air_forward_enum_is_still_rejected(self):
        row = air_forward_recorded(); row['Predicted']['Aim']['Action'] = 9
        result = self.corpus([row])
        self.assertEqual(result['KnownFeasible'], 0)
        self.assertEqual(result['Rejected'].get('incomplete-or-invalid-record'), 1)

    def test_twelve_flashes_do_not_satisfy_smoke_jump_and_bank_coverage(self):
        rows = []
        for i in range(12):
            row = recorded(); row['Actual']['X'] += i*100; rows.append(row)
        result = self.corpus(rows)
        self.assertEqual(result['MissingCases'], 0)
        self.assertEqual(result['Status'], 'incomplete')
        self.assertIn('Smoke', result['CoverageGaps'])

    def test_first_result_summary_keeps_missing_results_and_does_not_treat_them_as_zero(self):
        rows = [{'Status': 'complete', 'KnownFeasible': True,
                 'Metrics': {'Matches': 1, 'Experimental': 0, 'FirstUsableSeconds': x,
                             'FirstRecommendationSeconds': x}, 'Diagnostics': {'NativeTraces': 10}}
                for x in (1, 2, None, 9)]
        result = A.performance_summary(rows)
        self.assertEqual(result['FirstUsable']['Samples'], 3)
        self.assertEqual(result['FirstUsable']['Missing'], 1)
        self.assertEqual(result['FirstUsable']['P50Seconds'], 2)
        self.assertEqual(result['FirstUsable']['P95Seconds'], 9)

    def test_repeat_summary_does_not_substitute_warm_time_for_cold(self):
        row = {'Status': 'complete', 'KnownFeasible': True,
               'Metrics': {'Matches': 1, 'Experimental': 0, 'FirstUsableSeconds': 1},
               'Cold': {'Status': 'complete', 'Metrics': {'Matches': 1, 'Experimental': 0, 'FirstUsableSeconds': 8}},
               'Warm': {'Status': 'complete', 'Metrics': {'Matches': 1, 'Experimental': 0, 'FirstUsableSeconds': 1}}}
        self.assertEqual(A.performance_summary([row], 'Cold')['FirstUsable']['P50Seconds'], 8)
        self.assertEqual(A.performance_summary([row], 'Warm')['FirstUsable']['P50Seconds'], 1)

    def test_client_pass_requires_current_identity_and_existing_hashed_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root/'screen.txt').write_text('observed UI')
            evidence = {'SourceFingerprint': 'build', 'ClientResourceRevision': 'ui1',
                        'Journeys': [{'Id': 'smoke', 'Status': 'passed', 'Observer': 'operator',
                                      'Evidence': [{'Path': 'screen.txt', 'Sha256': A.sha256(root/'screen.txt')}]}]}
            result = A.client_journeys(evidence, root, 'build', 'ui1')
            self.assertEqual(result[0]['Status'], 'passed')
            self.assertEqual(len(result), 6)
            self.assertTrue(all(r['Status'] == 'pending' for r in result[1:]))
            (root/'screen.txt').write_text('changed')
            self.assertEqual(A.client_journeys(evidence, root, 'build', 'ui1')[0]['Status'], 'invalid-evidence')
            evidence['SourceFingerprint'] = 'old'
            self.assertEqual(A.client_journeys(evidence, root, 'build', 'ui1')[0]['Status'], 'stale')

    def test_native_checks_cannot_implicitly_pass_client_journeys(self):
        result = A.client_journeys({'NativeChecksPassed': 999}, Path('.'), 'build', 'ui1')
        self.assertTrue(all(r['Status'] == 'pending' for r in result))

    def test_failed_or_partially_restored_runs_are_not_compared_as_successful(self):
        before = {'Status': 'failed', 'ReplayRestored': True}
        after = {'Status': 'complete', 'ReplayRestored': True}
        self.assertEqual(A.compare_gate(before, after)['Status'], 'incomplete')
        after.pop('ReplayRestored')
        self.assertEqual(A.compare_gate(before, after)['Status'], 'incomplete')
        before['Status'] = 'complete'; after['ReplayRestored'] = False
        self.assertEqual(A.compare_gate(before, after)['Status'], 'incomplete')

    def reports(self, cases):
        rows = []
        for i, case in enumerate(cases):
            row = {k: copy.deepcopy(case[k]) for k in ('Evidence', 'KnownFeasible', 'Map', 'WorldVersion', 'Kind', 'Start', 'Target', 'PreferredView')}
            row['Fingerprint'] = A.BENCH.fingerprint(row)
            physics = A.BENCH.physics_profile(PHYSICS, 'Flash')
            row.update(CaseId=f'c{i+1:03d}', Status='complete', Physics=physics,
                       PhysicsFingerprint=A.BENCH.fingerprint(physics), Diagnostics={'NativeTraces': 3, 'PeakTickMs': 1},
                       Metrics={'Seconds': 1, 'Matches': 1, 'Experimental': 0, 'Evaluated': 10,
                                'FirstUsableSeconds': .1, 'FirstRecommendationSeconds': .2})
            rows.append(row)
        report = {'SchemaVersion': 1, 'Status': 'complete', 'ReplayRestored': True, 'Mode': A.BENCH.MODE,
                  'Instance': {'CoreBuild': 'core', 'PluginBuild': 'plugin', 'Map': cases[0]['Map'], 'WorldVersion': cases[0]['WorldVersion']},
                  'Cases': rows}
        return copy.deepcopy(report), report

    def test_performance_gate_requires_current_binaries_and_entire_corpus(self):
        row = recorded(); other = recorded(); other['Actual']['X'] += 100
        cases = self.corpus([row, other])['Cases']; current = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        pair = self.reports(cases)
        self.assertEqual(A.performance_gate([pair], cases, current)['Status'], 'compared')
        self.assertEqual(A.performance_gate([pair], cases, None)['Status'], 'incomplete')
        pair[1]['Instance']['CoreBuild'] = 'old'
        self.assertEqual(A.performance_gate([pair], cases, current)['Status'], 'incompatible')
        self.assertEqual(A.performance_gate([self.reports(cases[:1])], cases, current)['Status'], 'incomplete')

    def test_performance_rejects_forged_fingerprint_duplicate_and_nonfinite_metric(self):
        cases = self.corpus([recorded()])['Cases']; current = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        for change in ('changed-input', 'duplicate', 'nan', 'world', 'negative'):
            pair = self.reports(cases)
            if change == 'changed-input': pair[1]['Cases'][0]['Start']['Feet']['X'] += 1
            elif change == 'duplicate': pair[1]['Cases'].append(copy.deepcopy(pair[1]['Cases'][0]))
            elif change == 'nan': pair[1]['Cases'][0]['Metrics']['Seconds'] = float('nan')
            elif change == 'negative': pair[1]['Cases'][0]['Metrics']['Matches'] = -1
            else: pair[1]['Instance']['WorldVersion']['MapBytes'] += 1
            with self.subTest(change=change):
                self.assertEqual(A.performance_gate([pair], cases, current)['Status'], 'incompatible')

    def test_multi_map_pairs_must_cover_once_and_reject_unrelated_cases(self):
        cases = self.corpus([recorded()])['Cases']; second = copy.deepcopy(cases[0]); second['Map'] = 'de_dust2'
        current = {'CoreBuild': 'core', 'PluginBuild': 'plugin'}
        pairs = [self.reports(cases), self.reports([second])]
        self.assertEqual(A.performance_gate(pairs, cases+[second], current)['Status'], 'compared')
        self.assertEqual(A.performance_gate(pairs, cases, current)['Status'], 'incompatible')
        self.assertEqual(A.performance_gate(pairs+pairs[:1], cases+[second], current)['Status'], 'incompatible')

    def test_workbench_audit_does_not_connect_or_modify_record_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); data = root/'data'; data.mkdir(); output = root/'report'
            (data/'grenade-throws.jsonl').write_text(json.dumps(recorded())+'\n')
            raw = (data/'grenade-throws.jsonl').read_bytes()
            A.write_audit(data, {'de_mirage': WORLD}, output)
            self.assertEqual((data/'grenade-throws.jsonl').read_bytes(), raw)
            self.assertTrue((output/'corpus.json').is_file())
            self.assertTrue((output/'cases-de_mirage.json').is_file())
            with self.assertRaises(FileExistsError):
                A.write_audit(data, {'de_mirage': WORLD}, output)

    def test_ui_identity_rejects_sources_changed_after_resource_compilation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root/'ui').mkdir(); (root/'artifacts/panel-ui').mkdir(parents=True)
            entries = []
            for compiled, source in A.UI_FILES.items():
                src = root/'ui'/source; dst = root/'artifacts/panel-ui'/compiled
                src.parent.mkdir(parents=True, exist_ok=True); dst.parent.mkdir(parents=True, exist_ok=True)
                src.write_text('<root><Label id="NadeLabResourceRevision" text="UI ui1" /></root>')
                dst.write_bytes(b'compiled')
                entries.append({'path': compiled, 'source': source, 'sha256': A.sha256(dst), 'sourceSha256': A.sha256(src)})
            manifest = {'schema': 1, 'clientResourceRevision': 'ui1', 'files': entries}
            (root/'artifacts/panel-ui/manifest.json').write_text(json.dumps(manifest))
            self.assertEqual(A.ui_identity(root)['Status'], 'matched')
            manifest['files'] = entries[:1]
            (root/'artifacts/panel-ui/manifest.json').write_text(json.dumps(manifest))
            self.assertEqual(A.ui_identity(root)['Status'], 'unverified')
            manifest['files'] = entries; manifest['clientResourceRevision'] = 'other'
            (root/'artifacts/panel-ui/manifest.json').write_text(json.dumps(manifest))
            self.assertEqual(A.ui_identity(root)['Status'], 'unverified')
            manifest['clientResourceRevision'] = 'ui1'
            (root/'artifacts/panel-ui/manifest.json').write_text(json.dumps(manifest))
            src.write_text('edited without recompilation')
            self.assertEqual(A.ui_identity(root)['Status'], 'unverified')


if __name__ == '__main__':
    unittest.main()
