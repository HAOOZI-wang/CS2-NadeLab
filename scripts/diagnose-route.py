"""Replay one recorded, exact supported throw through native collision diagnostics.

Run only on an already-running empty practice server. Does not start/stop CS2,
move players, seed the search library, or change a case's origin/target/physics.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('route_benchmark', ROOT/'scripts/benchmark-search.py')
BENCH = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(BENCH)


class DiagnosisError(RuntimeError): pass


def require(condition, code):
    if not condition: raise DiagnosisError(code)


def build_request(case, request_id):
    witness = case.get('Witness') or {}
    require(case.get('Evidence') == 'actual-recording' and case.get('KnownFeasible') is True
            and witness.get('ExactAction') is True, 'exact-real-witness-required')
    require(BENCH.TOKEN.fullmatch(request_id) and BENCH.TOKEN.fullmatch(case['Id']), 'invalid-identity')
    return {'SchemaVersion': 1, 'RequestId': request_id, 'CaseId': case['Id'],
            'Map': case['Map'], 'WorldVersion': BENCH.world(case['WorldVersion'], case['Map']),
            'Start': BENCH.start(case['Start']), 'Target': BENCH.target(case['Target']),
            'Aim': BENCH.aim(witness['Aim']), 'Physics': witness['Physics'],
            'Evidence': [{'Kind': 'MatchedRealRoute', 'Value': True}]}


def validate_result(raw, request_id, request_sha, baseline):
    require(raw.get('SchemaVersion') == 1 and raw.get('RequestId') == request_id, 'result-identity-mismatch')
    require(raw.get('RequestSha256', '').lower() == request_sha.lower(), 'request-content-mismatch')
    require(all(raw.get(k) == baseline[k] for k in ('CoreBuild', 'PluginBuild')), 'result-build-changed')
    require(raw.get('Status') in ('complete', 'completed'), 'native-diagnosis-incomplete')


def search_evidence(case, report, baseline):
    require(report.get('Status') == 'complete' and report.get('ReplayRestored') is True, 'search-incomplete')
    require(all(report.get('Instance', {}).get(k) == baseline[k] for k in ('CoreBuild', 'PluginBuild')), 'search-build-changed')
    matches = []
    for row in report.get('Cases', []):
        if (row.get('Status') == 'complete' and row.get('Map') == case['Map'] and row.get('Kind') == case['Kind']
                and BENCH.world(row['WorldVersion'], row['Map']) == BENCH.world(case['WorldVersion'], case['Map'])
                and BENCH.same_numbers(BENCH.start(row['Start']), BENCH.start(case['Start']))
                and BENCH.same_numbers(BENCH.target(row['Target']), BENCH.target(case['Target']))):
            matches.append(row)
    require(len(matches) == 1, 'search-case-not-unique-or-mismatched')
    try:
        expected = BENCH.physics_profile(case['Witness']['Physics'], case['Kind'])
        found = BENCH.physics_profile(matches[0]['Physics'], case['Kind'])
        require(BENCH.same_numbers(expected, found), 'search-physics-changed')
    except BENCH.BenchmarkError:
        raise DiagnosisError('search-physics-incomplete') from None
    metrics = matches[0]['Metrics']
    require(all(type(metrics.get(k)) is int and metrics[k] >= 0 for k in ('Matches', 'Experimental')), 'invalid-search-metrics')
    return [{'Kind': 'SearchCompleted', 'Value': True},
            {'Kind': 'SearchFoundRoute', 'Value': metrics['Matches']+metrics['Experimental'] > 0}]


def restore_input(path, original):
    if original is None:
        path.unlink(missing_ok=True)
        return not path.exists()
    BENCH.atomic_write(path, original)
    return path.read_bytes() == original


def run_diagnosis(case, data, output, rcon, lock_path, timeout=30, clock=time.monotonic, sleep=time.sleep, search_report=None):
    data, output, lock_path = Path(data), Path(output), Path(lock_path)
    require(data.is_dir(), 'plugin-data-missing')
    require(not output.exists(), 'output-exists')
    require(not output.resolve().is_relative_to(data.resolve()) and not data.resolve().is_relative_to(output.resolve()), 'output-overlaps-data')
    request_id = uuid.uuid4().hex
    request = build_request(case, request_id)
    input_path = data/'route-diagnosis-input.json'
    result_path = data/f'route-diagnosis-{request_id}.json'
    report = {'SchemaVersion': 1, 'RequestId': request_id, 'Status': 'failed', 'InputRestored': None}
    locked = modified = active = False; original = None
    try:
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        with lock_path.open('x', encoding='ascii') as stream: stream.write(request_id)
        locked = True
        baseline = BENCH.safe_status(rcon, idle=True)
        require(case['Map'] == baseline['Map'] and request['WorldVersion'] == baseline['WorldVersion'], 'stale-case-world')
        if search_report is not None:
            request['Evidence'].extend(search_evidence(case, search_report, baseline))
        payload = BENCH.encode(request); request_sha = hashlib.sha256(payload).hexdigest()
        require(not result_path.exists(), 'result-already-exists')
        original = input_path.read_bytes() if input_path.exists() else None
        modified = True; BENCH.atomic_write(input_path, payload)
        active = True
        reply = rcon('css_nl_routediagnose '+request_id)
        require('ROUTEDIAGNOSIS started' in reply, 'native-diagnosis-not-started')
        deadline = clock()+timeout
        while True:
            BENCH.safe_status(rcon, baseline)
            if result_path.exists():
                try: raw = BENCH.read_json(result_path)
                except (OSError, ValueError): raw = None
                if raw is not None:
                    validate_result(raw, request_id, request_sha, baseline)
                    BENCH.safe_status(rcon, baseline, idle=True)
                    report['Native'] = raw; report['Status'] = 'complete'; active = False
                    break
            require(clock() < deadline, 'diagnosis-timeout')
            sleep(.1)
    except (Exception, KeyboardInterrupt) as exc:
        report['Error'] = str(exc) if isinstance(exc, (DiagnosisError, BENCH.BenchmarkError)) else 'interrupted' if isinstance(exc, KeyboardInterrupt) else 'io-or-protocol-failure'
        if active:
            try:
                response = rcon('css_nl_routediagnosiscancel '+request_id)
                report['Cancellation'] = 'confirmed' if 'cancelled' in response.lower() or 'no matching' in response.lower() else 'unconfirmed'
            except Exception: report['Cancellation'] = 'unconfirmed'
    finally:
        if modified:
            try: report['InputRestored'] = restore_input(input_path, original)
            except OSError: report['InputRestored'] = False
        if report['InputRestored'] is False:
            report['Status'] = 'failed'; report['Error'] = 'input-restore-failed'
        if locked and report.get('Cancellation') != 'unconfirmed' and report['InputRestored'] is not False:
            lock_path.unlink(missing_ok=True)
        output.mkdir(parents=True)
        (output/'diagnosis.json').write_text(json.dumps(report, ensure_ascii=False, allow_nan=False, indent=2), encoding='utf-8')
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cases', type=Path, required=True)
    parser.add_argument('--case', type=int, default=1, help='One-based case index in the frozen corpus file.')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--benchmark', type=Path, help='Same-build completed search report, to distinguish search coverage from model mismatch.')
    args = parser.parse_args(argv)
    try:
        cases = BENCH.read_json(args.cases)['Cases']
        require(1 <= args.case <= len(cases), 'invalid-case-index')
        case = cases[args.case-1]
        if args.dry_run:
            build_request(case, 'dry-run')
            print(json.dumps({'Status': 'valid-input', 'CurrentInstanceVerified': False})); return 0
        config = BENCH.read_json(ROOT/'server/instance.json')
        spec = importlib.util.spec_from_file_location('route_lab', ROOT/'scripts/local-server.py')
        lab = importlib.util.module_from_spec(spec); spec.loader.exec_module(lab)
        data = Path(config['GameDirectory'])/'addons/counterstrikesharp/plugins/NadeLab/data'
        report = run_diagnosis(case, data, args.output, lab.rcon, ROOT/'artifacts/benchmark-search.lock',
                               search_report=BENCH.read_json(args.benchmark) if args.benchmark else None)
        print(json.dumps({'Status': report['Status'], 'Report': str(args.output/'diagnosis.json'), 'Error': report.get('Error')}))
        return int(report['Status'] != 'complete')
    except (OSError, ValueError, TypeError, KeyError, DiagnosisError, BENCH.BenchmarkError) as exc:
        print(json.dumps({'Status': 'failed', 'Error': str(exc) if isinstance(exc, (DiagnosisError, BENCH.BenchmarkError)) else type(exc).__name__}))
        return 1


if __name__ == '__main__': sys.exit(main())
