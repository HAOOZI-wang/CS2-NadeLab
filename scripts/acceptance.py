"""Offline, version-bound training evidence audit. Never starts or controls CS2.

Default invocation writes a new acceptance report, corpus, and replay cases.
Optional before/after benchmark reports and client evidence are independently gated.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import re
from pathlib import Path
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def load_module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


BENCH = load_module('acceptance_benchmark', 'benchmark-search.py')
UI_FILES = {
    'panorama/styles/custom_game/nadelab_panel.vcss_c': 'panorama/styles/custom_game/nadelab_panel.css',
    'panorama/layout/custom_game/nadelab_panel.vxml_c': 'panorama/layout/custom_game/nadelab_panel.xml',
    'maps/scripts/nadelab_panel_bridge.vjs_c': 'maps/scripts/nadelab_panel_bridge.js',
}
JOURNEYS = (
    ('smoke', '选烟目标→记站位→搜索→就位→手投，并观察实际封烟'),
    ('keep-target', '保留目标换站位→重搜，核对实际出手点'),
    ('flash-objectives', '放置双假人→设置闪/不闪→搜索→手投→核对双方效果'),
    ('dummy-layout', '保存双假人组合→删除→加载→覆盖取消→核对位置和要求'),
    ('dummy-status', '伤害/致盲多个假人→实时状态→指定复位/删除'),
    ('restart-ui', '重启客户端→核对资源版本→菜单四选项/滚轮/方向键/WASD/V'),
)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def ui_identity(root):
    root = Path(root); folder = root/'artifacts/panel-ui'
    try:
        manifest = read_json(folder/'manifest.json')
        if manifest.get('schema') != 1 or not manifest.get('clientResourceRevision') or not manifest.get('files'):
            raise ValueError('missing UI identity')
        entries = {e['path']: e['source'] for e in manifest['files']}
        if entries != UI_FILES or len(manifest['files']) != len(UI_FILES):
            raise ValueError('incomplete UI manifest')
        xml = (root/'ui/panorama/layout/custom_game/nadelab_panel.xml').read_text(encoding='utf-8-sig')
        badge = ET.fromstring(xml).find(".//Label[@id='NadeLabResourceRevision']")
        if badge is None or badge.get('text') != 'UI '+manifest['clientResourceRevision']:
            raise ValueError('UI revision is not baked into layout')
        for entry in manifest['files']:
            compiled = (folder/entry['path']).resolve()
            source = (root/'ui'/entry['source']).resolve()
            if not compiled.is_relative_to(folder.resolve()) or not source.is_relative_to((root/'ui').resolve()):
                raise ValueError('UI path escaped')
            if sha256(compiled).lower() != entry['sha256'].lower() or sha256(source).lower() != entry['sourceSha256'].lower():
                raise ValueError('UI source or binary changed')
        return {'Status': 'matched', 'Revision': manifest['clientResourceRevision'],
                'ManifestSha256': sha256(folder/'manifest.json')}
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError):
        return {'Status': 'unverified', 'Revision': None, 'ManifestSha256': None}


def release_feet(start, predicted, physics):
    """Resolve the recorded origin contract as ThrowExecution.Resolve does."""
    start = BENCH.start(start)
    action = 1 if predicted['Action'] == 0 and predicted['ForwardSpeed'] > 0 else predicted['Action']
    airborne = action in (4, 5, 7, 8)
    offset = predicted['ForwardOffset']
    if start.get('Origin', 0) == 1 and action in (1, 2, 5):
        offset += start['RunUpDistance']
        if not airborne:
            offset += predicted['ForwardSpeed']*BENCH.number(physics['ReleaseDelaySeconds'])
    yaw = math.radians(predicted['Yaw'])
    return (start['Feet']['X']+math.cos(yaw)*offset,
            start['Feet']['Y']+math.sin(yaw)*offset,
            start['Feet']['Z']+predicted['ReleaseHeight'])


def exact_launch(start, predicted, actual, physics, kind):
    """Diagnostic eligibility within explicit network quantization bounds, not a human-skill claim."""
    try:
        BENCH.physics_profile(physics, kind)
        observed = actual['Aim']
        if not {'Pitch', 'Yaw', 'Mode'} <= observed.keys():
            return False
        pitch = BENCH.number(observed['Pitch']); yaw = BENCH.number(observed['Yaw'])
        airborne = predicted['Action'] in (4, 5, 7, 8)
        if (actual.get('OnGround') is not (not airborne) or actual.get('JumpThrow') is not airborne
                or observed['Mode'] != predicted['Mode']):
            return False
        direction = (math.cos(math.radians(predicted['Yaw'])), math.sin(math.radians(predicted['Yaw'])))
        feet = BENCH.vector(actual['Feet']); velocity = BENCH.vector(actual['Velocity'])
        release = release_feet(start, predicted, physics)
        speed = (direction[0]*predicted['ForwardSpeed'], direction[1]*predicted['ForwardSpeed'], predicted['VerticalSpeed'])
        eye = 46 if predicted['Action'] == 6 else 64 if airborne else start['EyeHeight']
        return (abs(pitch-predicted['Pitch']) <= .002 and abs((yaw-predicted['Yaw']+180)%360-180) <= .002
                and math.dist(release, [feet[k] for k in ('X', 'Y', 'Z')]) <= .02
                and math.dist(speed, [velocity[k] for k in ('X', 'Y', 'Z')]) <= .1
                and abs(BENCH.number(actual['EyeHeight'])-eye) <= .02
                and abs(BENCH.number(actual['Strength'])-(1, .5, 0)[int(predicted['Mode'])]) <= .001)
    except (ValueError, KeyError, TypeError, BENCH.BenchmarkError):
        return False


def world_inventory(game):
    game = Path(game)
    version = (game/'steam.inf').read_bytes().decode('utf-8-sig')
    worlds = {}
    for path in sorted((game/'maps').glob('de_*.vpk')):
        if path.stem.endswith('_vanity'):
            continue
        info = path.stat()
        seconds, nanos = divmod(info.st_mtime_ns, 1_000_000_000)
        stamp = datetime.fromtimestamp(seconds, timezone.utc).strftime('%Y-%m-%dT%H:%M:%S')
        worlds[path.stem] = {'GameVersion': version, 'MapBytes': info.st_size,
                             'MapLastWriteUtc': f'{stamp}.{nanos//100:07d}Z'}
    return worlds


def inside_target(point, target):
    """Mirror target geometry only for audit; explicit event hit also required."""
    def box(center, half):
        delta = [abs(point[k]-center[k]) for k in ('X', 'Y', 'Z')]
        sizes = [half[k] for k in ('X', 'Y', 'Z')]
        if any(type(v) not in (int, float) or not math.isfinite(v) or v <= 0 for v in sizes):
            return False
        return delta[2] <= sizes[2] and sum((delta[i]/sizes[i])**2 for i in (0, 1)) <= 1
    if target['AreaHalfSize'] is None:
        return math.dist([point[k] for k in ('X','Y','Z')], [target['Position'][k] for k in ('X','Y','Z')]) <= target['Radius']
    if any(box(r['Center'], r['HalfSize']) for r in target['Exclusions']):
        return False
    if target['AreaHalfSize'] is not None:
        primary = box(target['Position'], target['AreaHalfSize'])
    else:
        primary = math.dist(list(point.values()), [target['Position'][k] for k in point]) <= target['Radius']
    return primary or any(box(r['Center'], r['HalfSize']) for r in target['ExtraRegions'])


def audit_records(rows, worlds, required=12):
    cases, rejected, seen = [], Counter(), set()
    total = 0
    for row in rows:
        total += 1
        try:
            name = row['Map']; world = BENCH.world(row['WorldVersion'], name)
            if name not in worlds or world != BENCH.world(worlds[name], name):
                rejected['stale-world'] += 1; continue
            if row.get('PlayerIsBot') is not False:
                rejected['actor-not-confirmed-player'] += 1; continue
            matched = row.get('ExecutionMatched', row.get('Matched'))
            if matched is not True:
                rejected['execution-not-matched'] += 1; continue
            start = BENCH.start(row['Start']); endpoint = BENCH.vector(row['Actual'])
            actual = row['ActualThrow']
            predicted = BENCH.aim(row['Predicted']['Aim'])
            if predicted is None or predicted['Mode'] not in (0, 1, 2) or predicted['Action'] not in range(9) or not -89 <= predicted['Pitch'] <= 89:
                raise ValueError('invalid launch')
            kind = row.get('Kind', 'Smoke')
            if kind not in BENCH.KINDS:
                raise ValueError('invalid kind')
            # Launch evidence must actually exist, even when an older event says Matched.
            actual_feet = BENCH.vector(actual['Feet']); BENCH.number(actual['EyeHeight'])
            release = release_feet(start, predicted, row.get('Physics'))
            if math.dist(release, [actual_feet[k] for k in ('X','Y','Z')]) > .02:
                rejected['release-origin-mismatch'] += 1; continue
            raw_target = row.get('Target')
            if raw_target is not None:
                target = BENCH.target(raw_target)
                if row.get('PositionHit', row.get('TargetHit', row.get('LandingSuccess'))) is not True or not inside_target(endpoint, target):
                    rejected['target-not-confirmed'] += 1; continue
                target_source = 'recorded-target'
            else:
                # This is a NEW explicitly endpoint-derived test, not a relaxed failed target.
                if row.get('Outcome') not in ('detonated', 'ignited', 'rested'):
                    rejected['missing-terminal-event'] += 1; continue
                target = BENCH.target({'Position': endpoint, 'Radius': 32,
                                       'Airspace': kind in ('Flash', 'HighExplosive')})
                target_source = 'observed-endpoint'
            key = BENCH.fingerprint({'Map': name, 'World': world, 'Start': start, 'Aim': predicted, 'Target': target})
            if key in seen:
                rejected['duplicate-case'] += 1; continue
            seen.add(key)
            exact = exact_launch(start, predicted, actual, row.get('Physics'), kind)
            case = {'Id': 'throw-'+key[:16], 'Map': name, 'WorldVersion': world, 'Evidence': 'actual-recording',
                    'KnownFeasible': True, 'Kind': kind, 'Start': start, 'Target': target,
                    'PreferredView': None, 'TargetSource': target_source,
                    'EffectVerified': row.get('EffectVerified', row.get('CoverageVerified')) is True,
                    'Witness': {'Aim': predicted, 'ObservedEndpoint': endpoint, 'ExactAction': exact,
                                'SourceRecordSha256': BENCH.fingerprint(row), 'Utc': row.get('Utc'),
                                'Physics': row.get('Physics')},
                    'Interpretation': 'Real player-pawn endpoint reachability; operator identity and manual repeatability are not established.'}
            cases.append(case)
        except (ValueError, TypeError, KeyError, IndexError, OverflowError, BENCH.BenchmarkError):
            rejected['incomplete-or-invalid-record'] += 1
    coverage = {'Smoke': any(c['Kind'] == 'Smoke' for c in cases),
                'Flash': any(c['Kind'] == 'Flash' for c in cases),
                'StationaryJump': any(c['Witness']['Aim']['Action'] == 4 for c in cases),
                'Moving': any(c['Witness']['Aim']['Action'] in (1, 2, 3, 5, 7, 8) for c in cases)}
    gaps = [name for name, present in coverage.items() if not present]
    return {'SchemaVersion': 1, 'RecordsRead': total, 'KnownFeasible': len(cases), 'RequiredCases': required,
            'MissingCases': max(0, required-len(cases)), 'CoverageGaps': gaps,
            'CollisionCoverage': 'needs-native-and-observed-route-review',
            'Status': 'ready' if len(cases) >= required and not gaps else 'incomplete',
            'Rejected': dict(rejected), 'Cases': cases,
            'Kinds': dict(Counter(c['Kind'] for c in cases)),
            'Limits': ['File world identity only; running server identity is checked separately by benchmark.',
                       'Existing records are regression evidence, not untouched calibration holdouts.',
                       'PlayerIsBot=false identifies a player pawn, not whether a human or UI automation operated it.',
                       'Count alone does not establish smoke/jump/bank coverage.']}


def distribution(values, expected):
    values = sorted(v for v in values if type(v) in (int, float) and math.isfinite(v) and v >= 0)
    return {'Samples': len(values), 'Missing': expected-len(values),
            'P50Seconds': statistics.median(values) if values else None,
            'P95Seconds': values[max(0, math.ceil(len(values)*.95)-1)] if values else None}


def performance_summary(rows, stage=None):
    selected = [(r, r.get(stage, {}) if stage else r) for r in rows]
    complete = [(r, s) for r, s in selected if s.get('Status') == 'complete']
    known = [(r, s) for r, s in complete if r.get('KnownFeasible') is True]
    return {'Completed': len(complete), 'Unfinished': len(rows)-len(complete),
            'KnownFeasible': len(known),
            'KnownFeasibleFound': sum(s['Metrics']['Matches']+s['Metrics'].get('Experimental', 0) > 0 for _, s in known),
            'KnownFeasibleRecommended': sum(s['Metrics']['Matches'] > 0 for _, s in known),
            'FirstUsable': distribution([s['Metrics'].get('FirstUsableSeconds') for _, s in complete], len(complete)),
            'FirstRecommended': distribution([s['Metrics'].get('FirstRecommendationSeconds') for _, s in complete], len(complete)),
            'NativeTraces': sum(s.get('Diagnostics', {}).get('NativeTraces', 0) for _, s in complete),
            'PeakTickMs': max((s.get('Diagnostics', {}).get('PeakTickMs', 0) for _, s in complete), default=None)}


def compare_gate(before, after):
    if any(r.get('Status') != 'complete' or r.get('ReplayRestored') is not True for r in (before, after)):
        return {'Status': 'incomplete', 'Reason': 'run-incomplete-or-replay-not-restored'}
    try:
        comparison = BENCH.compare_reports(before, after)
    except (ValueError, TypeError, KeyError, BENCH.BenchmarkError):
        return {'Status': 'incompatible', 'Reason': 'benchmark-inputs-or-protocol-invalid'}
    comparable = comparison['Cases'] and all(c['Comparable'] for c in comparison['Cases'])
    stages = ('Cold', 'Warm') if before['Mode'] == BENCH.REPEAT_MODE else (None,)
    summaries = {stage or 'Cold': {'Before': performance_summary(before['Cases'], stage),
                                 'After': performance_summary(after['Cases'], stage)} for stage in stages}
    regressions = []
    for case in comparison['Cases']:
        if not case['Comparable']:
            continue
        for stage in stages:
            part = case[stage] if stage else case
            a, b = part['Before'], part['After']
            if a['Matches']+a['Experimental'] > 0 and b['Matches']+b['Experimental'] == 0:
                regressions.append({'CaseId': case['CaseId'], 'Stage': stage or 'Cold', 'Reason': 'lost-all-candidates'})
            elif a['Matches'] > 0 and b['Matches'] == 0:
                regressions.append({'CaseId': case['CaseId'], 'Stage': stage or 'Cold', 'Reason': 'lost-recommended-candidate'})
    return {'Status': 'incompatible' if not comparable else 'regressed' if regressions else 'compared',
            'CoverageRegressions': regressions, 'Summary': summaries, 'Comparison': comparison,
            'Limits': 'P95 uses nearest rank on this finite corpus; no FPS or manual-success claim.'}


def case_fingerprint(row):
    return BENCH.fingerprint({'Evidence': row['Evidence'], 'KnownFeasible': row['KnownFeasible'],
        'Map': row['Map'], 'WorldVersion': BENCH.world(row['WorldVersion'], row['Map']), 'Kind': row['Kind'],
        'Start': BENCH.start(row['Start']), 'Target': BENCH.target(row['Target']),
        'PreferredView': BENCH.aim(row.get('PreferredView'))})


def validate_performance_report(report):
    """Recompute identity from inputs; a supplied hash is never the authority."""
    ids, fingerprints = set(), set()
    if not report.get('Cases'):
        raise ValueError('empty report')
    instance = report['Instance']
    for row in report['Cases']:
        value = case_fingerprint(row)
        if (row['CaseId'] in ids or value in fingerprints or row['Fingerprint'] != value
                or row['Map'] != instance['Map']
                or BENCH.world(row['WorldVersion'], row['Map']) != BENCH.world(instance['WorldVersion'], instance['Map'])):
            raise ValueError('invalid report identity')
        ids.add(row['CaseId']); fingerprints.add(value)
        parts = [row]+[row[s] for s in ('Cold', 'Warm')] if report['Mode'] == BENCH.REPEAT_MODE else [row]
        for part in parts:
            metrics = part['Metrics']
            for key in ('Matches', 'Experimental', 'Evaluated'):
                if type(metrics[key]) is not int or metrics[key] < 0:
                    raise ValueError('invalid count')
            if BENCH.number(metrics['Seconds']) < 0:
                raise ValueError('invalid duration')
            for key in ('FirstUsableSeconds', 'FirstRecommendationSeconds'):
                if metrics.get(key) is not None and BENCH.number(metrics[key]) < 0:
                    raise ValueError('invalid first result')
            for value in part.get('Diagnostics', {}).values():
                if type(value) in (float, int) and (not math.isfinite(value) or value < 0):
                    raise ValueError('invalid diagnostics')
    return fingerprints


def performance_gate(pairs, cases, current_build):
    if not pairs:
        return {'Status': 'pending', 'Reason': 'no-benchmark-pairs'}
    if not current_build:
        return {'Status': 'incomplete', 'Reason': 'current-binary-identity-unverified'}
    expected = {case_fingerprint(c) for c in cases}; covered = set(); comparisons = []
    try:
        for before, after in pairs:
            if any(r.get('Status') != 'complete' or r.get('ReplayRestored') is not True for r in (before, after)):
                return {'Status': 'incomplete', 'Reason': 'run-incomplete-or-replay-not-restored'}
            for key in ('CoreBuild', 'PluginBuild'):
                if after['Instance'][key] != current_build[key]:
                    raise ValueError('after build is stale')
            old = validate_performance_report(before); new = validate_performance_report(after)
            if old != new or not new <= expected or covered & new:
                raise ValueError('changed unrelated or duplicate cases')
            covered |= new
            comparison = compare_gate(before, after)
            comparisons.append({'Map': after['Instance']['Map'], **comparison})
        status = ('incompatible' if any(c['Status'] == 'incompatible' for c in comparisons)
                  else 'regressed' if any(c['Status'] == 'regressed' for c in comparisons)
                  else 'incomplete' if covered != expected or not expected else 'compared')
        return {'Status': status, 'ExpectedCases': len(expected), 'CoveredCases': len(covered),
                'MissingFingerprints': sorted(expected-covered), 'CurrentBuild': current_build, 'Pairs': comparisons}
    except (ValueError, TypeError, KeyError, BENCH.BenchmarkError):
        return {'Status': 'incompatible', 'Reason': 'current-build-corpus-or-report-integrity-mismatch'}


def binary_identity(root):
    # Metadata only: never load or execute the game plugin.
    result = subprocess.run(['pwsh', '-NoProfile', '-File', str(Path(root)/'scripts/read-build-identity.ps1'),
                             '-Root', str(root)], capture_output=True, text=True, timeout=20, check=True)
    identity = json.loads(result.stdout)
    if set(identity) != {'CoreBuild', 'PluginBuild'} or any(not re.fullmatch(r'[0-9a-f-]{36}', v) for v in identity.values()):
        raise ValueError('invalid assembly metadata')
    return identity


def client_journeys(evidence, base, source, revision):
    rows = evidence.get('Journeys', [])
    result = []
    for identity, instruction in JOURNEYS:
        matches = [r for r in rows if r.get('Id') == identity]
        status = 'pending'; reason = None
        if len(matches) > 1:
            status = 'invalid-evidence'; reason = 'duplicate-journey'
        elif matches:
            row = matches[0]
            status = row.get('Status', 'pending')
            if status not in ('pending', 'passed', 'failed'):
                status = 'invalid-evidence'
            elif status in ('passed', 'failed'):
                if not source or not revision or evidence.get('SourceFingerprint') != source or evidence.get('ClientResourceRevision') != revision:
                    status = 'stale'
                else:
                    try:
                        files = row.get('Evidence', [])
                        if not files or not row.get('Observer'):
                            raise ValueError('missing observation')
                        for item in files:
                            path = (Path(base)/item['Path']).resolve()
                            if not path.is_relative_to(Path(base).resolve()) or sha256(path) != item['Sha256']:
                                raise ValueError('invalid observation')
                    except (OSError, ValueError, TypeError, KeyError):
                        status = 'invalid-evidence'
        result.append({'Id': identity, 'Instruction': instruction, 'Status': status, 'Reason': reason})
    return result


def write_audit(data, worlds, output):
    output = Path(output); data = Path(data)
    if output.resolve().is_relative_to(data.resolve()) or data.resolve().is_relative_to(output.resolve()):
        raise ValueError('output overlaps player data')
    if output.exists():
        raise FileExistsError('output already exists')
    rows = []; malformed = 0
    for name in ('throws.jsonl', 'grenade-throws.jsonl'):
        path = data/name
        if not path.exists():
            continue
        for line in path.read_text(encoding='utf-8-sig').splitlines():
            if not line.strip():
                continue
            try:
                value = json.loads(line)
                if not isinstance(value, dict):
                    raise ValueError('not record')
                rows.append(value)
            except ValueError:
                malformed += 1
    corpus = audit_records(rows, worlds)
    corpus['MalformedLines'] = malformed
    output.mkdir(parents=True)
    save(output/'corpus.json', corpus)
    for name in sorted({c['Map'] for c in corpus['Cases']}):
        save(output/f'cases-{name}.json', {'SchemaVersion': 1, 'Map': name, 'WorldVersion': worlds[name],
                                         'Cases': [c for c in corpus['Cases'] if c['Map'] == name]})
    return corpus


def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2), encoding='utf-8')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=ROOT/'server/game/csgo/addons/counterstrikesharp/plugins/NadeLab/data')
    parser.add_argument('--game', type=Path, default=ROOT/'server/game/csgo')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--before', type=Path)
    parser.add_argument('--after', type=Path)
    parser.add_argument('--pair', type=Path, nargs=2, action='append', default=[], metavar=('BEFORE', 'AFTER'),
                        help='Repeat once per map; the union must cover the entire audited corpus.')
    parser.add_argument('--client-evidence', type=Path)
    parser.add_argument('--require-complete', action='store_true')
    args = parser.parse_args(argv)
    try:
        if bool(args.before) != bool(args.after):
            raise ValueError('provide both before and after')
        output = args.output or ROOT/'artifacts/acceptance'/datetime.now().strftime('%Y%m%d-%H%M%S-%f')
        corpus = write_audit(args.data, world_inventory(args.game), output)
        packaging = load_module('acceptance_packaging', 'package-release.py')
        source = packaging.source_fingerprint(ROOT)
        ui = ui_identity(ROOT)
        revision = ui['Revision']
        evidence = read_json(args.client_evidence) if args.client_evidence else {}
        journeys = client_journeys(evidence, args.client_evidence.parent if args.client_evidence else output, source, revision)
        # Build evidence belongs to a verified source fingerprint; no test-count inference.
        build_path = ROOT/'artifacts/release-build.json'
        try:
            build = packaging.validate_build_record(ROOT)
        except (OSError, ValueError, KeyError):
            build = {}
        identity = None
        if build:
            try:
                identity = binary_identity(ROOT)
                # Reject a source/binary race during metadata inspection.
                if packaging.validate_build_record(ROOT) != build:
                    identity = None
            except (OSError, ValueError, subprocess.SubprocessError):
                identity = None
        paths = args.pair+([(args.before, args.after)] if args.before else [])
        comparison = performance_gate([(read_json(a), read_json(b)) for a, b in paths], corpus['Cases'], identity)
        report = {'SchemaVersion': 1, 'Utc': datetime.now(timezone.utc).isoformat(),
                  'SourceFingerprint': source, 'ClientResourceRevision': revision, 'UiResources': ui,
                  'Build': {'Status': 'matched' if build.get('sourceFingerprint') == source else 'unverified',
                            'BinaryIdentity': identity,
                            'RecordSha256': sha256(build_path) if build_path.exists() else None},
                  'Corpus': {k: v for k, v in corpus.items() if k != 'Cases'},
                  'Performance': comparison, 'ClientJourneys': journeys,
                  'Status': 'incomplete',
                  'Limits': ['Offline audit does not connect to a server or verify pixels.',
                             'Human evidence attestations remain separate from automated checks.']}
        if (corpus['Status'] == 'ready' and report['Build']['Status'] == 'matched' and ui['Status'] == 'matched' and comparison['Status'] == 'compared'
                and all(j['Status'] == 'passed' for j in journeys)):
            report['Status'] = 'complete'
        save(output/'report.json', report)
        save(output/'client-evidence-template.json', {
            'SourceFingerprint': source, 'ClientResourceRevision': revision,
            'Journeys': [{'Id': identity, 'Status': 'pending', 'Observer': '', 'Evidence': [],
                          'Instruction': instruction} for identity, instruction in JOURNEYS]})
        print(json.dumps({'Status': report['Status'], 'Report': str(output/'report.json'),
                          'KnownFeasible': corpus['KnownFeasible'], 'MissingCases': corpus['MissingCases']}, ensure_ascii=False))
        return 2 if args.require_complete and report['Status'] != 'complete' else 0
    except (OSError, ValueError, TypeError, KeyError, BENCH.BenchmarkError) as exc:
        print(json.dumps({'Status': 'failed', 'Error': type(exc).__name__}))
        return 1


if __name__ == '__main__':
    sys.exit(main())
