import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("calibrate", Path(__file__).resolve().parents[2]/"scripts/calibrate.py")
calibrate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(calibrate)


def captures():
    records = []
    for strength in [0, .5, 1, 0, .5, 1]:
        v = 690 * (.3 + .7 * strength)
        records.append({"Map": "de_mirage", "WorldVersion": {"GameVersion": "synthetic-test-only"}, "Matched": True,
                        "ActualThrow": {"Standing": True, "Strength": strength, "Velocity": {"X": 0, "Y": 0, "Z": 0}},
                        "InitialVelocity": {"X": v, "Y": 0, "Z": 0},
                        "Samples": [{"Time": i / 64, "Position": {"X": v*i/64, "Y": 0, "Z": 64-.5*315*(i/64)**2}} for i in range(1, 20)]})
    return records


class CalibrationTests(unittest.TestCase):
    def test_known_ballistic_parameters(self):
        speed, gravity, count, fits = calibrate.estimate(captures())
        self.assertAlmostEqual(speed, 690, places=4)
        self.assertAlmostEqual(gravity, 315, places=4)
        self.assertEqual((count, fits), (6, 6))

    def test_mismatched_throws_cannot_tune_physics(self):
        data = captures()
        for record in data:
            record["Matched"] = False
        with self.assertRaisesRegex(ValueError, "Insufficient"):
            calibrate.estimate(data)

    def test_mixed_game_versions_are_rejected(self):
        data = captures()
        data[-1]["WorldVersion"]["GameVersion"] = "different-build"
        with self.assertRaisesRegex(ValueError, "mix"):
            calibrate.estimate(data)

    def test_collision_contaminated_segment_is_rejected(self):
        data = captures()
        for record in data:
            for sample in record["Samples"][10:]:
                sample["Position"]["Z"] += 10
        with self.assertRaisesRegex(ValueError, "Insufficient"):
            calibrate.estimate(data)


if __name__ == "__main__":
    unittest.main()
