"""Public index checks reject common private/runtime entries before publication."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("public_source", ROOT / "scripts/check-public-source.py")
PUBLIC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLIC)


class PublicSourceTests(unittest.TestCase):
    def test_authored_code_and_public_docs_are_allowed(self):
        for path in ("src/NadeLab.Core/Search.cs", "docs/release/player-guide.md", ".github/workflows/source-checks.yml", "LICENSE"):
            self.assertEqual(PUBLIC.check_entry(path, b"ordinary source", "100644"), [])

    def test_private_roots_internal_notes_and_legacy_account_script_are_rejected(self):
        for path in ("server/rcon.local", "artifacts/report.json", ".competition-build/report.py", "参赛材料/report.pdf",
                     "docs/work-in-progress.txt", "scripts/bind-flight.py", "docs/archive/notes.md"):
            self.assertTrue(PUBLIC.check_entry(path, b"anything", "100644"), path)

    def test_nested_runtime_and_secret_files_are_rejected(self):
        for path in ("src/Plugin/bin/NadeLab.dll", "tests/data/player.json", "configs/.env", "configs/key.PEM", "scripts/debug.log"):
            self.assertTrue(PUBLIC.check_entry(path, b"anything", "100644"), path)

    def test_links_and_submodules_are_not_implicitly_published(self):
        for mode in ("120000", "160000"):
            self.assertTrue(PUBLIC.check_entry("src/link", b"../../server", mode))

    def test_token_and_private_key_report_contains_no_secret_value(self):
        token = "gh" + "p_" + "x" * 36
        for value in (token, "-----BEGIN " + "PRIVATE KEY-----"):
            issues = PUBLIC.check_entry("configs/example.json", value.encode(), "100644")
            self.assertTrue(issues)
            self.assertNotIn(value, " ".join(issues))

    def test_rcon_literal_rejected_but_variable_reference_allowed(self):
        self.assertTrue(PUBLIC.check_entry("configs/example.cfg", b'rcon_password "do-not-publish"', "100644"))
        self.assertEqual(PUBLIC.check_entry("configs/example.cfg", b'rcon_password "$secret"', "100644"), [])

    def test_large_file_requires_deliberate_review(self):
        self.assertTrue(PUBLIC.check_entry("src/large.txt", b"x" * (5 * 1024 * 1024 + 1), "100644"))
