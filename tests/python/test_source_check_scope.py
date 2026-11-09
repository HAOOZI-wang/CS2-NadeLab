"""Source checks may omit real UI artifacts; release checks must remain strict."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "release_scope_fixture", Path(__file__).with_name("test_release_package.py"))
PACKAGE_TESTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE_TESTS)


class SourceCheckScopeTests(unittest.TestCase):
    def run_artifact_check(self, mode):
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(PACKAGE_TESTS, "ROOT", Path(temporary)):
                with patch.dict(os.environ, {"NADELAB_SOURCE_ONLY_TESTS": mode}):
                    result = unittest.TestResult()
                    PACKAGE_TESTS.ReleaseTests("test_ui_sources_match_compiled_inventory").run(result)
                    return result

    def test_clean_source_checkout_explicitly_reports_ui_artifact_check_skipped(self):
        result = self.run_artifact_check("1")
        self.assertEqual(result.errors + result.failures, [])
        self.assertEqual(len(result.skipped), 1)
        self.assertIn("compiled UI", result.skipped[0][1])

    def test_full_checks_fail_without_compiled_ui(self):
        result = self.run_artifact_check("0")
        self.assertEqual(result.skipped, [])
        self.assertEqual(len(result.errors), 1)
        self.assertIn("FileNotFoundError", result.errors[0][1])

    def test_unknown_source_mode_does_not_silently_skip_release_validation(self):
        result = self.run_artifact_check("true")
        self.assertEqual(result.skipped, [])
        self.assertEqual(len(result.errors), 1)

    def test_release_input_policy_uses_its_own_ui_fixture_in_clean_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(PACKAGE_TESTS, "ROOT", Path(temporary)):
                result = unittest.TestResult()
                PACKAGE_TESTS.ReleaseTests("test_release_inputs_use_fixed_script_and_document_allowlists").run(result)
                self.assertEqual(result.errors + result.failures, [])
