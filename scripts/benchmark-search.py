"""Serial, anonymous search benchmarks against an already-running empty lab server.

No server lifecycle actions. --dry-run and --compare never connect to RCON.
The native SchemaVersion=1 benchmark protocol is required; no legacy fallback.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import sys
import time
import uuid
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = {'actual-recording', 'historical-search', 'synthetic'}
KINDS = ('Smoke', 'Flash', 'HighExplosive', 'Molotov', 'Incendiary', 'Decoy')
TOKEN = re.compile(r'^[A-Za-z0-9_-]{1,80}$')
MODE = 'cold-auto'
REPEAT_MODE = 'repeat-auto'
# Keep in step with PhysicsProfile. Missing or newly introduced fields must not
# silently produce a supposedly comparable partial model fingerprint.
PHYSICS_NUMBERS = ('FuseSeconds', 'FireGroundNormal', 'Gravity', 'ThrowSpeed',
                   'Restitution', 'TangentialRetention', 'RestSpeed', 'StepSeconds',
                   'MinDetonateSeconds', 'MaxSeconds', 'HullHalfSize', 'PlayerVelocityScale',
                   'HeavyImpactSpeed', 'ReleaseDelaySeconds', 'PlayerGravity', 'JumpImpulse', 'WalkSpeedRatio')


class BenchmarkError(RuntimeError):
    """Messages are fixed safe codes, never raw server responses or source data."""


def require(condition, code):
    if not condition:
        raise BenchmarkError(code)


def encode(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False,
                      separators=(',', ':')).encode('utf-8')


def fingerprint(value):
    return hashlib.sha256(encode(value)).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def number(value):
    require(type(value) in (int, float) and math.isfinite(value), 'invalid-number')
    return value


def vector(value):
    require(isinstance(value, dict), 'invalid-vector')
    return {key: number(value[key]) for key in ('X', 'Y', 'Z')}


def aim(value):
    if value is None:
        return None
    require(isinstance(value, dict), 'invalid-aim')
    defaults = {'Pitch': 0, 'Yaw': 0, 'Mode': 0, 'ForwardSpeed': 0, 'Action': 0,
                'ReleaseHeight': 0, 'VerticalSpeed': 0, 'ForwardOffset': 0}
    return {key: number(value.get(key, default)) for key, default in defaults.items()}


def start(value):
    result = {'Feet': vector(value['Feet']), 'EyeHeight': number(value.get('EyeHeight', 64))}
    origin = value.get('Origin', 0)
    require(type(origin) is int and origin in (0, 1), 'invalid-throw-origin')
    if origin == 1:
        distance = number(value.get('RunUpDistance', 100))
        require(60 <= distance <= 160, 'invalid-run-up-distance')
        result.update(Origin=1, RunUpDistance=float(distance))
    # Old records omit Origin. Release-reference starts still have that exact
    # canonical shape; their unused run-up distance cannot change replay identity.
    return result


def target(value):
    result = {'Position': vector(value['Position']), 'Radius': number(value.get('Radius', 32)),
              'AreaHalfSize': vector(value['AreaHalfSize']) if value.get('AreaHalfSize') is not None else None,
              'Airspace': value.get('Airspace', False), 'ExtraRegions': [], 'Exclusions': [], 'Objective': None}
    require(type(result['Airspace']) is bool, 'invalid-airspace')
    require(1 <= result['Radius'] <= 256, 'invalid-radius')
    for key in ('ExtraRegions', 'Exclusions'):
        regions = value.get(key) or []
        require(isinstance(regions, list) and len(regions) <= 2, 'invalid-region-count')
        result[key] = [{'Center': vector(r['Center']), 'HalfSize': vector(r['HalfSize'])} for r in regions]
    if value.get('Objective') is not None:
        obj = value['Objective']
        result['Objective'] = {'Center': vector(obj['Center']), 'Normal': vector(obj['Normal']),
                               'HalfWidth': number(obj['HalfWidth']), 'HalfHeight': number(obj['HalfHeight']),
                               'ObserverEye': vector(obj['ObserverEye']) if obj.get('ObserverEye') is not None else None,
                               'ObserverView': aim(obj.get('ObserverView'))}
    # Display labels and computed getters never affect the replay or leak to reports.
    return result


def world(value, map_name=None):
    fields = {'GameVersion', 'MapBytes', 'MapLastWriteUtc'}
    require(isinstance(value, dict) and set(value) in (fields, fields | {'MapName'}), 'invalid-world-version')
    if 'MapName' in value:
        require(isinstance(value['MapName'], str) and value['MapName'] == map_name, 'world-map-name-mismatch')
    require(isinstance(value['GameVersion'], str) and value['GameVersion'] not in ('', 'unknown'), 'missing-game-version')
    require(type(value['MapBytes']) is int and value['MapBytes'] > 0, 'invalid-map-size')
    require(isinstance(value['MapLastWriteUtc'], str) and value['MapLastWriteUtc'], 'missing-map-time')
    # Schema 1 already binds Map separately; normalize the optional newer
    # duplicate only after verifying it agrees, preserving frozen fingerprints.
    return {key: value[key] for key in ('GameVersion', 'MapBytes', 'MapLastWriteUtc')}


def load_cases(path):
    try:
        doc = read_json(path)
        require(doc.get('SchemaVersion') == 1, 'unsupported-case-schema')
        require(isinstance(doc.get('Cases'), list) and 1 <= len(doc['Cases']) <= 100, 'invalid-case-count')
        cases, ids = [], set()
        for index, raw in enumerate(doc['Cases'], 1):
            identity = raw.get('Id')
            require(isinstance(identity, str) and TOKEN.fullmatch(identity) and identity not in ids, 'invalid-case-id')
            ids.add(identity)
            require(raw.get('Evidence') in EVIDENCE, 'missing-evidence-class')
            require(raw.get('Kind') in KINDS, 'invalid-grenade-kind')
            require(raw.get('KnownFeasible') is None or type(raw['KnownFeasible']) is bool, 'invalid-known-feasible')
            item = {'CaseId': f'c{index:03d}', 'Evidence': raw['Evidence'], 'KnownFeasible': raw.get('KnownFeasible'),
                    'Map': raw.get('Map', doc.get('Map')), 'WorldVersion': world(raw.get('WorldVersion', doc.get('WorldVersion')), raw.get('Map', doc.get('Map'))),
                    'Kind': raw['Kind'], 'Start': start(raw['Start']), 'Target': target(raw['Target']),
                    'PreferredView': aim(raw.get('PreferredView'))}
            require(isinstance(item['Map'], str) and re.fullmatch(r'[A-Za-z0-9_/-]+', item['Map']), 'invalid-map')
            item['Fingerprint'] = fingerprint({key: value for key, value in item.items() if key != 'CaseId'})
            cases.append(item)
        return cases
    except BenchmarkError:
        raise
    except (OSError, ValueError, TypeError, KeyError):
        raise BenchmarkError('invalid-case-file') from None


def parse_status(response):
    prefix = '[NadeLab] BENCHMARKSTATUS '
    lines = [line.split(prefix, 1)[1] for line in response.splitlines() if prefix in line]
    require(len(lines) == 1, 'benchmark-status-unavailable')
    try:
        raw = json.loads(lines[0])
        require(type(raw['SchemaVersion']) is int and raw['SchemaVersion'] == 1, 'unsupported-status-schema')
        require(type(raw['Humans']) is int and raw['Humans'] >= 0, 'invalid-human-count')
        require(type(raw['Idle']) is bool and type(raw['Practice']) is bool, 'invalid-status')
        require(type(raw['Epoch']) is int, 'invalid-epoch')
        checked_world = world(raw['WorldVersion'], raw['Map'])
        for key in ('CoreBuild', 'PluginBuild'):
            require(isinstance(raw[key], str) and TOKEN.fullmatch(raw[key]), 'missing-build-identity')
        status = {key: raw[key] for key in ('SchemaVersion', 'Map', 'WorldVersion', 'Practice', 'Humans', 'Idle',
                                          'Epoch', 'CoreBuild', 'PluginBuild', 'BenchmarkRunId', 'BenchmarkCaseId')}
        status['WorldVersion'] = checked_world
        for key in ('BenchmarkCacheIsolated', 'BenchmarkPassId'):
            if key in raw:
                status[key] = raw[key]
        return status
    except BenchmarkError:
        raise
    except (ValueError, TypeError, KeyError):
        raise BenchmarkError('invalid-status') from None


def safe_status(rcon, baseline=None, *, idle=False, run_id=None, case_id=None, isolated=False, pass_id=None):
    status = parse_status(rcon('css_nl_benchmarkstatus'))
    require(status['Practice'], 'practice-disabled')
    require(status['Humans'] == 0, 'human-connected')
    if isolated:
        require(status.get('BenchmarkCacheIsolated') is True, 'isolated-benchmark-cache-required')
    if baseline is not None:
        for key in ('Map', 'WorldVersion', 'Epoch', 'CoreBuild', 'PluginBuild'):
            require(status[key] == baseline[key], 'instance-changed')
    if idle:
        require(status['Idle'] and status['BenchmarkRunId'] is None and status['BenchmarkCaseId'] is None, 'server-not-idle')
    elif status['BenchmarkRunId'] is not None or status['BenchmarkCaseId'] is not None:
        require(status['BenchmarkRunId'] == run_id and status['BenchmarkCaseId'] == case_id, 'foreign-job')
        if pass_id is not None and 'BenchmarkPassId' in status:
            require(status['BenchmarkPassId'] == pass_id, 'foreign-pass')
    return status


def same_numbers(actual, expected):
    if type(expected) in (int, float) and type(actual) in (int, float):
        return math.isfinite(actual) and math.isclose(actual, expected, rel_tol=1e-7, abs_tol=1e-5)
    if isinstance(expected, dict):
        return isinstance(actual, dict) and actual.keys() == expected.keys() and all(same_numbers(actual[k], v) for k, v in expected.items())
    if isinstance(expected, list):
        return isinstance(actual, list) and len(actual) == len(expected) and all(same_numbers(a, b) for a, b in zip(actual, expected))
    return type(actual) is type(expected) and actual == expected


def numeric_fields(source, keys):
    return {key: number(source[key]) for key in keys if key in source and source[key] is not None}


def physics_profile(source, kind):
    require(isinstance(source, dict) and kind in KINDS, 'invalid-physics')
    required = set(PHYSICS_NUMBERS) | {'Kind', 'Calibrated'}
    require(required <= source.keys() <= required | {'Id'}, 'incomplete-or-unknown-physics')
    require(source['Kind'] in (kind, KINDS.index(kind)), 'result-physics-kind-mismatch')
    require(type(source['Calibrated']) is bool, 'invalid-calibration-flag')
    return {**{key: number(source[key]) for key in PHYSICS_NUMBERS},
            'Kind': kind, 'Calibrated': source['Calibrated']}


def candidate_summary(raw):
    result = {'Aim': aim(raw['Aim']), 'Endpoint': vector(raw['Trajectory']['Endpoint'])}
    result.update(numeric_fields(raw, ('Error', 'Reliability', 'RobustHits', 'RobustTrials', 'ExecutionHits', 'ExecutionTrials',
                                     'ModelHits', 'ModelTrials', 'SourceStressHits', 'SourceStressTrials')))
    result['Trajectory'] = numeric_fields(raw['Trajectory'], ('Status', 'Time', 'Bounces', 'WallBounces'))
    return result


def validate_result(raw, case, run_id, pass_id=None):
    try:
        require(raw['BenchmarkRunId'] == run_id and raw['BenchmarkCaseId'] == case['CaseId'], 'result-identity-mismatch')
        if pass_id is not None:
            require(raw.get('BenchmarkPassId') == pass_id, 'result-pass-mismatch')
        require(raw['Map'] == case['Map'] and world(raw['WorldVersion'], raw['Map']) == case['WorldVersion'], 'result-world-mismatch')
        require(raw['Kind'] == case['Kind'], 'result-kind-mismatch')
        require(same_numbers(start(raw['Start']), case['Start']) and same_numbers(start(raw['RequestedStart']), case['Start']), 'result-origin-mismatch')
        require(same_numbers(target(raw['Target']), case['Target']), 'result-target-mismatch')
        require(isinstance(raw['Utc'], str) and raw['Utc'], 'missing-result-time')
        # Explicit whitelist: no source identifiers, instructions, names or raw logs.
        profile = physics_profile(raw['Physics'], case['Kind'])
        result = raw['Result']
        require(isinstance(result['Matches'], list) and isinstance(result.get('Experimental', []), list), 'invalid-result-candidates')
        metrics = {'Seconds': number(raw['Seconds']), 'Matches': len(result['Matches']),
                   'Experimental': len(result.get('Experimental') or []), 'Evaluated': number(result['Evaluated']),
                   'FirstRecommendationSeconds': raw.get('FirstRecommendationSeconds'),
                   'FirstUsableSeconds': raw.get('FirstUsableSeconds'),
                   'ResumePasses': result.get('ResumePasses', 0), 'Truncated': result['Truncated'] is True}
        if pass_id is not None or 'PriorHintCount' in raw:
            count = raw.get('PriorHintCount')
            require(type(count) is int and 0 <= count <= 12, 'invalid-prior-hint-count')
            require(pass_id != 'cold' or count == 0, 'cold-pass-used-prior-hints')
            metrics['PriorHintCount'] = count
        for key in ('FirstRecommendationSeconds', 'FirstUsableSeconds'):
            if metrics[key] is not None:
                require(number(metrics[key]) >= 0, 'invalid-result-time')
        diagnostics = numeric_fields(raw['Diagnostics'], ('NativeTraces', 'CollisionHits', 'SolidStarts', 'WorkMs', 'PeakTickMs'))
        result = {'Status': 'complete', 'Metrics': metrics, 'Physics': profile, 'PhysicsFingerprint': fingerprint(profile),
                'Diagnostics': diagnostics, 'Matches': [candidate_summary(c) for c in result['Matches']],
                'Experimental': [candidate_summary(c) for c in result.get('Experimental') or []]}
        if pass_id is not None:
            result['PassId'] = pass_id
        return result
    except BenchmarkError:
        raise
    except (ValueError, TypeError, KeyError):
        raise BenchmarkError('invalid-result') from None


def summarize(rows):
    groups = {}
    for evidence in sorted(EVIDENCE):
        selected = [r for r in rows if r['Evidence'] == evidence and r.get('Status') == 'complete']
        groups[evidence] = {'Completed': len(selected), 'WithMatches': sum(r['Metrics']['Matches'] > 0 for r in selected),
                            'WithAnyCandidate': sum(r['Metrics']['Matches'] + r['Metrics']['Experimental'] > 0 for r in selected),
                            'KnownFeasibleCases': sum(r['KnownFeasible'] is True for r in selected)}
    return groups


def atomic_write(path, data):
    temporary = path.with_name(path.name + '.benchmark-tmp')
    try:
        temporary.write_bytes(data)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def run_benchmark(cases, data_dir, output, rcon, *, lock_path, timeout=180, poll=.5,
                  stale_policy='error', repeat=False, clock=time.monotonic, sleep=time.sleep):
    data_dir, output, lock_path = Path(data_dir), Path(output), Path(lock_path)
    require(not output.resolve().is_relative_to(data_dir.resolve()), 'output-overlaps-plugin-data')
    require(not (output / 'report.json').exists(), 'output-already-exists')
    run_id = uuid.uuid4().hex
    report = {'SchemaVersion': 1, 'BenchmarkRunId': run_id, 'Mode': REPEAT_MODE if repeat else MODE,
              'Utc': datetime.now(timezone.utc).isoformat(), 'Status': 'running',
              'Interpretation': 'Search candidates are simulated; this run does not verify a human throw.',
              'Cases': [], 'ReplayRestored': None}
    original, modified, locked, active = None, False, False, False
    replay = data_dir / 'replay-recent.json'
    try:
        require(data_dir.is_dir(), 'plugin-data-missing')
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            with lock_path.open('x', encoding='ascii') as stream:
                stream.write(run_id)
            locked = True
        except FileExistsError:
            raise BenchmarkError('benchmark-already-locked') from None
        baseline = safe_status(rcon, idle=True, isolated=repeat)
        report['Instance'] = {key: baseline[key] for key in ('Map', 'WorldVersion', 'Epoch', 'CoreBuild', 'PluginBuild')}
        if repeat:
            report['Instance']['BenchmarkCacheIsolated'] = True
        accepted = []
        for case in cases:
            row = dict(case)
            if case['Map'] != baseline['Map'] or case['WorldVersion'] != baseline['WorldVersion']:
                require(stale_policy == 'mark', 'stale-case-world')
                row['Status'] = 'stale'
            else:
                row['Status'] = 'pending'
                accepted.append(case)
            report['Cases'].append(row)
        if accepted:
            original = replay.read_bytes() if replay.exists() else None
            report['OriginalReplaySha256'] = hashlib.sha256(original).hexdigest() if original is not None else None
            payload = {'Cases': [{**{k: c[k] for k in ('Map', 'WorldVersion', 'Kind', 'Start', 'Target', 'PreferredView')},
                                  'BenchmarkCaseId': c['CaseId'], 'BenchmarkRunId': run_id} for c in accepted]}
            if repeat:
                for item in payload['Cases']:
                    item['BenchmarkPassId'] = 'cold'
            # In-memory exact-byte backup is never exported: legacy files may contain accounts.
            modified = True
            atomic_write(replay, encode(payload))
            last = data_dir / 'replay-last.json'
            for index, case in enumerate(accepted, 1):
                row = next(r for r in report['Cases'] if r['CaseId'] == case['CaseId'])
                for stage in ('cold', 'warm') if repeat else ('cold',):
                    if repeat:
                        row['Status'] = 'running'
                        row['ActivePass'] = stage
                        row[stage.title()] = {'Status': 'pending', 'PassId': stage}
                    safe_status(rcon, baseline, idle=True, isolated=repeat)
                    if stage == 'warm':
                        payload['Cases'][index-1]['BenchmarkPassId'] = stage
                        atomic_write(replay, encode(payload))
                    previous = last.read_bytes() if last.exists() else None
                    active = True  # Includes uncertain RCON delivery failures.
                    row['Status'] = 'running'
                    if repeat:
                        row['ActivePass'] = stage
                        row[stage.title()] = {'Status': 'running', 'PassId': stage}
                    command_mode = 'auto' if stage == 'warm' else MODE
                    reply = rcon(f'css_nl_replay recent{index}-{command_mode}')
                    require('REPLAY search started;' in reply, 'replay-not-started')
                    deadline = clock() + timeout
                    while True:
                        status = safe_status(rcon, baseline, run_id=run_id, case_id=case['CaseId'],
                                             isolated=repeat, pass_id=stage if repeat else None)
                        current = last.read_bytes() if last.exists() else None
                        if current is not None and current != previous:
                            try:
                                raw = json.loads(current)
                            except (ValueError, UnicodeDecodeError):
                                raw = None  # Writer may not have completed its file yet.
                            if raw is not None:
                                result = validate_result(raw, case, run_id, stage if repeat else None)
                                if status['Idle']:
                                    safe_status(rcon, baseline, idle=True, isolated=repeat)
                                    if repeat:
                                        row[stage.title()] = result
                                    if not repeat or stage == 'warm':
                                        row.update(result)
                                        row.pop('ActivePass', None)
                                    active = False
                                    break
                        require(clock() < deadline, 'case-timeout')
                        sleep(poll)
        report['Status'] = 'complete'
    except (Exception, KeyboardInterrupt) as exc:
        report['Status'] = 'failed'
        report['Error'] = str(exc) if isinstance(exc, BenchmarkError) else 'interrupted' if isinstance(exc, KeyboardInterrupt) else 'io-or-protocol-failure'
        for row in report['Cases']:
            if row['Status'] == 'running':
                row['Status'] = 'failed'
                row['Error'] = report['Error']
                if repeat and row.get('ActivePass'):
                    stage = row['ActivePass']
                    if row.get(stage.title(), {}).get('Status') != 'complete':
                        row[stage.title()] = {'Status': 'failed', 'PassId': stage, 'Error': report['Error']}
        if active:
            try:
                reply = rcon('css_nl_benchmarkcancel ' + run_id)
                report['Cancellation'] = 'cancelled' if 'BENCHMARK cancelled.' in reply else 'no-matching-run'
            except Exception:
                report['Cancellation'] = 'unconfirmed'
    finally:
        if modified:
            try:
                if original is None:
                    replay.unlink(missing_ok=True)
                else:
                    atomic_write(replay, original)
                report['ReplayRestored'] = (replay.read_bytes() == original) if original is not None else not replay.exists()
            except OSError:
                report['ReplayRestored'] = False
                report['Status'] = 'failed'
                report['Error'] = 'replay-restore-failed'
        if locked:
            # Keep a lock after uncertain cancellation/restoration so another run cannot proceed.
            if report.get('Cancellation') != 'unconfirmed' and report['ReplayRestored'] is not False:
                lock_path.unlink(missing_ok=True)
        report['Summary'] = summarize(report['Cases'])
        output.mkdir(parents=True, exist_ok=True)
        atomic_write(output / 'report.json', encode(report))
    return report


def compare_reports(before, after):
    require(before.get('SchemaVersion') == after.get('SchemaVersion') == 1, 'unsupported-report-schema')
    require(before.get('Mode') == after.get('Mode') and before.get('Mode') in (MODE, REPEAT_MODE), 'incompatible-benchmark-mode')
    repeat = before['Mode'] == REPEAT_MODE
    if repeat:
        require(before.get('Instance', {}).get('BenchmarkCacheIsolated') is True and
                after.get('Instance', {}).get('BenchmarkCacheIsolated') is True, 'isolated-benchmark-cache-required')
    rows = []
    old = {c['CaseId']: c for c in before['Cases']}
    new = {c['CaseId']: c for c in after['Cases']}
    def valid_physics(row):
        try:
            profile = physics_profile(row['Physics'], row['Kind'])
            return fingerprint(profile) == row['PhysicsFingerprint']
        except (BenchmarkError, KeyError, TypeError):
            return False
    def delta(a, b):
        result = {'Before': a['Metrics'], 'After': b['Metrics'],
                  'Delta': {k: b['Metrics'][k] - a['Metrics'][k] for k in ('Seconds', 'Matches', 'Experimental', 'Evaluated')},
                  'NativeTracesDelta': b['Diagnostics'].get('NativeTraces', 0) - a['Diagnostics'].get('NativeTraces', 0)}
        for key in ('FirstRecommendationSeconds', 'FirstUsableSeconds'):
            av, bv = a['Metrics'].get(key), b['Metrics'].get(key)
            result['Delta'][key] = bv - av if av is not None and bv is not None else None
        if 'PriorHintCount' in a['Metrics'] and 'PriorHintCount' in b['Metrics']:
            result['Delta']['PriorHintCount'] = b['Metrics']['PriorHintCount'] - a['Metrics']['PriorHintCount']
        return result
    def valid_passes(row):
        return all(isinstance(row.get(stage), dict) and row[stage].get('Status') == 'complete' and
                   row[stage].get('PassId') == stage.lower() and valid_physics({**row[stage], 'Kind': row['Kind']})
                   for stage in ('Cold', 'Warm')) and row['Cold']['Metrics'].get('PriorHintCount') == 0 and \
            type(row['Warm']['Metrics'].get('PriorHintCount')) is int and 0 <= row['Warm']['Metrics']['PriorHintCount'] <= 12 and \
            row['Warm']['Metrics'] == row['Metrics'] and row['Warm']['PhysicsFingerprint'] == row['PhysicsFingerprint']
    for identity in sorted(old.keys() | new.keys()):
        a, b = old.get(identity), new.get(identity)
        row = {'CaseId': identity, 'Comparable': False}
        if a is None or b is None:
            row['Reason'] = 'missing-case'
        elif a['Fingerprint'] != b['Fingerprint']:
            row['Reason'] = 'case-or-world-changed'
        elif a['Status'] != 'complete' or b['Status'] != 'complete':
            row['Reason'] = 'incomplete-case'
        elif repeat and (not valid_passes(a) or not valid_passes(b)):
            row['Reason'] = 'incomplete-or-invalid-pass'
        elif not valid_physics(a) or not valid_physics(b):
            row['Reason'] = 'incomplete-or-invalid-physics'
        elif a['PhysicsFingerprint'] != b['PhysicsFingerprint']:
            row['Reason'] = 'physics-changed'
        elif repeat and any(a[s]['PhysicsFingerprint'] != b[s]['PhysicsFingerprint'] for s in ('Cold', 'Warm')):
            row['Reason'] = 'physics-changed'
        else:
            row.update({'Comparable': True, 'Evidence': a['Evidence'], **delta(a, b)})
            if repeat:
                row.update({stage: delta(a[stage], b[stage]) for stage in ('Cold', 'Warm')})
        rows.append(row)
    return {'SchemaVersion': 1, 'Mode': before['Mode'], 'Cases': rows,
            'Interpretation': 'After minus before; unmatched/changed inputs are not comparable. No live throws were validated.'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--cases', type=Path)
    mode.add_argument('--compare', type=Path, nargs=2, metavar=('BEFORE', 'AFTER'))
    parser.add_argument('--output', type=Path)
    parser.add_argument('--dry-run', action='store_true', help='No writes, RCON or current-world claim.')
    parser.add_argument('--repeat', action='store_true', help='Cold then warm per case, requiring an isolated per-run server cache.')
    parser.add_argument('--stale-policy', choices=('error', 'mark'), default='error')
    parser.add_argument('--timeout', type=float, default=180)
    parser.add_argument('--poll', type=float, default=.5)
    args = parser.parse_args(argv)
    try:
        require(math.isfinite(args.timeout) and args.timeout > 0 and math.isfinite(args.poll) and args.poll >= .1, 'invalid-timing')
        if args.compare:
            require(not args.repeat, 'repeat-only-with-cases')
            result = compare_reports(*(read_json(p) for p in args.compare))
            if not args.dry_run:
                require(args.output is not None, 'output-required')
                require(not (args.output / 'comparison.json').exists(), 'output-already-exists')
                args.output.mkdir(parents=True, exist_ok=True)
                atomic_write(args.output / 'comparison.json', encode(result))
        else:
            cases = load_cases(args.cases)
            if args.dry_run:
                result = {'DryRun': True, 'CurrentInstanceVerified': False, 'Mode': REPEAT_MODE if args.repeat else MODE, 'Cases': cases}
            else:
                require(args.output is not None, 'output-required')
                config = read_json(ROOT / 'server/instance.json')
                data = Path(config['GameDirectory']) / 'addons/counterstrikesharp/plugins/NadeLab/data'
                spec = importlib.util.spec_from_file_location('nadelab_local_server', ROOT / 'scripts/local-server.py')
                lab = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(lab)
                result = run_benchmark(cases, data, args.output, lab.rcon,
                                       lock_path=ROOT / 'artifacts/benchmark-search.lock', timeout=args.timeout,
                                       poll=args.poll, stale_policy=args.stale_policy, repeat=args.repeat)
        print(json.dumps(result, ensure_ascii=False, allow_nan=False))
        return int(result.get('Status') == 'failed')
    except BenchmarkError as exc:
        print(json.dumps({'Status': 'failed', 'Error': str(exc)}))
        return 1
    except (OSError, ValueError, TypeError, KeyError):
        print(json.dumps({'Status': 'failed', 'Error': 'io-or-protocol-failure'}))
        return 1


if __name__ == '__main__':
    sys.exit(main())
