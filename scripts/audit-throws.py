"""Summarize recorded throws without fitting physics or treating repeats as holdouts."""
import argparse
import json
import math
import statistics
from collections import defaultdict
from datetime import datetime
from pathlib import Path


def report(rows):
    rows = sorted(rows, key=lambda r: datetime.fromisoformat(r['Utc']).timestamp())
    groups = defaultdict(list)
    for row in rows:
        predicted = row.get('Predicted')
        if not predicted:
            continue
        aim, start = predicted['Aim'], row['Start']
        feet = start['Feet']
        # Hold out whole nearby-origin/action groups; repeated throws at one
        # setup must not appear on both sides of a calibration evaluation.
        key = (aim.get('EffectiveAction', 0), aim['Mode'],
               *(round(feet[axis] / 32) for axis in ('X', 'Y', 'Z')))
        groups[key].append(row)
    ordered = sorted(groups, key=lambda key: datetime.fromisoformat(groups[key][-1]['Utc']).timestamp())
    held = set(ordered[-max(1, math.ceil(len(ordered) * .2)):]) if ordered else set()
    strata = defaultdict(list)
    partitions = {'calibration': [], 'holdout': []}
    for key, records in groups.items():
        partition = 'holdout' if key in held else 'calibration'
        partitions[partition].extend(r['Utc'] for r in records)
        for r in records:
            eye = r['Start']['EyeHeight']
            strata[(key[0], key[1], 'below60' if eye < 60 else '60plus', partition)].append(r)
    summaries = []
    for (action, mode, eye, partition), records in sorted(strata.items()):
        matched = [r for r in records if r.get('Matched')]
        errors = [r['ModelError'] for r in matched if isinstance(r.get('ModelError'), (int, float))]
        eye_errors = [abs(r['ActualThrow']['EyeHeight'] - r['Start']['EyeHeight']) for r in records
                      if r.get('ActualThrow') and not r['Predicted']['Aim'].get('Airborne')]
        summaries.append({'Action': action, 'Mode': mode, 'RecordedEyeBand': eye, 'Partition': partition,
                          'Throws': len(records), 'Matched': len(matched),
                          'MatchedTargetHits': sum(bool(r.get('TargetHit')) for r in matched),
                          'MedianMatchedModelError': statistics.median(errors) if errors else None,
                          'MaxMatchedModelError': max(errors, default=None),
                          'MaxObservedEyeDifference': max(eye_errors, default=None)})
    return {'SchemaVersion': 1, 'Throws': len(rows), 'OriginActionGroups': len(groups),
            'Strata': summaries, 'Partitions': partitions,
            'Limits': ['No physics parameters changed.', 'Eye band does not identify stairs or wall proximity.',
                       'Holdout is a future evaluation partition, not untouched evidence: earlier development used these records.',
                       'Unmatched execution excluded from model-error statistics.',
                       'TargetHit is endpoint validity; full smoke coverage still requires observation.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('input', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.input.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    args.output.write_text(json.dumps(report(rows), ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Audited {len(rows)} throws -> {args.output}')
