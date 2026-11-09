"""Estimate launch speed/gravity from live captures; never declare a profile calibrated."""
import argparse
import json
import math
import statistics
from datetime import datetime, timezone
from pathlib import Path


def normalise(value):
    if isinstance(value, dict):
        return {key.lower(): normalise(val) for key, val in value.items()}
    if isinstance(value, list):
        return [normalise(val) for val in value]
    return value


def quadratic(times, values):
    """Least-squares a + bt + ct² with pivoting, no external dependencies."""
    sums = [sum(t ** k for t in times) for k in range(5)]
    a = [[sums[i+j] for j in range(3)] + [sum(y * t ** i for t, y in zip(times, values))] for i in range(3)]
    for col in range(3):
        pivot = max(range(col, 3), key=lambda row: abs(a[row][col]))
        a[col], a[pivot] = a[pivot], a[col]
        if abs(a[col][col]) < 1e-12:
            raise ValueError("Singular sample times")
        scale = a[col][col]
        a[col] = [v / scale for v in a[col]]
        for row in range(3):
            if row == col:
                continue
            factor = a[row][col]
            a[row] = [v - factor * p for v, p in zip(a[row], a[col])]
    coeff = [a[i][3] for i in range(3)]
    rms = math.sqrt(sum((y - (coeff[0] + coeff[1]*t + coeff[2]*t*t))**2 for t, y in zip(times, values))/len(times))
    return coeff, rms


def estimate(records):
    speeds, gravities, accepted = [], [], 0
    versions = set()
    for raw in records:
        r = normalise(raw)
        if not r.get("matched") or r.get("map") != "de_mirage":
            continue
        actual = r.get("actualthrow") or {}
        velocity = actual.get("velocity", {})
        if not actual.get("standing") or sum(velocity.get(k, 999)**2 for k in "xyz") > 1:
            continue
        versions.add(json.dumps(r.get("worldversion"), sort_keys=True))
        strength = actual.get("strength")
        initial = r.get("initialvelocity")
        if not isinstance(strength, (float, int)) or not 0 <= strength <= 1 or not initial:
            continue
        speed = math.sqrt(sum(initial[k]**2 for k in "xyz"))/(.3+.7*strength)
        if not math.isfinite(speed) or not 200 <= speed <= 1500:
            continue
        speeds.append(speed)
        accepted += 1
        samples = [s for s in r.get("samples", []) if 0 <= s.get("time", 999) <= .3]
        # Reject gaps/duplicate times and require enough free-flight samples for curvature.
        if len(samples) < 10:
            continue
        ts = [s["time"] for s in samples]
        if any(not .001 <= b-a <= .04 for a, b in zip(ts, ts[1:])):
            continue
        try:
            cx, rx = quadratic(ts, [s["position"]["x"] for s in samples])
            cy, ry = quadratic(ts, [s["position"]["y"] for s in samples])
            cz, rz = quadratic(ts, [s["position"]["z"] for s in samples])
        except (ValueError, KeyError, TypeError):
            continue
        gravity = -2*cz[2]
        # Early collision, rolling, or irregular sampling should not tune gravity.
        if 100 <= gravity <= 800 and max(rx, ry, rz) < .35 and abs(cx[2]) < 5 and abs(cy[2]) < 5:
            gravities.append(gravity)
    if len(versions) > 1:
        raise ValueError("Records mix map/game versions; calibrate each version separately.")
    if len(speeds) < 5 or len(gravities) < 3:
        raise ValueError(f"Insufficient clean samples: {len(speeds)} launch speeds, {len(gravities)} free-flight segments; need at least 5 and 3.")
    return statistics.median(speeds), statistics.median(gravities), accepted, len(gravities)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    records = []
    skipped = 0
    for line in args.captures.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError:
            skipped += 1
    try:
        speed, gravity, n, ng = estimate(records)
    except ValueError as error:
        parser.exit(2, f"No profile written: {error}\n")
    profile = json.loads((Path(__file__).resolve().parents[1]/"configs/physics.example.json").read_text(encoding="utf-8"))
    profile.update(Id="experimental-"+datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"), Calibrated=False,
                   ThrowSpeed=round(speed, 4), Gravity=round(gravity, 4))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(profile, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(f"Proposed profile: {args.output}; {n} launch records, {ng} free-flight fits; {skipped} incomplete lines skipped.")
    print("Calibrated=false. Bounce/friction/detonation timing still require tuning and held-out validation.")


if __name__ == "__main__":
    main()
