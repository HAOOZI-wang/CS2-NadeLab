"""Distribution regressions: stale files, tampering, private values, unsafe paths."""
import importlib.util
import json
import os
import tempfile
import unittest
from unittest import mock
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_release", ROOT / "scripts/package-release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_rejects_private_and_traversal_paths(self):
        for name in ("../secret", "a/../secret", "C:/secret", "a\\secret", "/secret", "a//b",
                     "server/logs/stdout.log", ".tools/a", "plugin/data/lineups.json", "a.pdb", "maps/a.vpk"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.safe_path(name)

    def test_rejects_private_values(self):
        for text in ("D:\\GPT\\CS2\\scripts", "C:/Users/Lenovo/a", "76561198012345678", 'rcon_password "example-secret"'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                release.check_content("README.md", text.encode())

    def test_rejects_private_paths_in_binary_debug_records_both_utf16_alignments(self):
        value = r'D:\GPT\CS2\src\Plugin\obj\Release\Plugin.pdb'
        for prefix, encoding in ((b'MZ\0', 'utf-8'), (b'MZ', 'utf-16le'), (b'MZ\0', 'utf-16le')):
            with self.subTest(encoding=encoding, prefix=prefix), self.assertRaises(ValueError):
                release.check_content('NadeLab.dll', prefix + value.encode(encoding))

    def test_rejects_json_escaped_private_paths_and_local_account_paths(self):
        values = [json.dumps({'path': r'D:\GPT\CS2\anything'}).encode(),
                  b'{"path":"D\\u003a\\u005cGPT\\u005cCS2"}',
                  json.dumps({'profile': r'E:\Steam\userdata\1234567\730\local'}).encode()]
        for value in values:
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.check_content('config.json', value)

    def test_safe_code_and_documentation(self):
        release.check_content("README.md", b'connect 127.0.0.1:27025\nrcon_password $secret')
        release.check_content("scripts/release/start.ps1", b"$server = Join-Path $Root 'instance'")

    def test_roundtrip_and_deterministic_archive(self):
        files = {"README.md": b"hello", "game/csgo/cfg/nadelab_keys.cfg": b"echo ready"}
        files["release-manifest.json"] = release.manifest_for(files, "0.2.0-rc.1", "windows")
        with tempfile.TemporaryDirectory() as temporary:
            first, second = Path(temporary)/"one.zip", Path(temporary)/"two.zip"
            release.write_zip(first, files)
            release.write_zip(second, files)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            self.assertEqual(release.validate_zip(first)["files"], 3)

    def test_manifest_rejects_extra_or_modified_file(self):
        for extra in (False, True):
            files = {"README.md": b"correct"}
            files["release-manifest.json"] = release.manifest_for(files, "0.2.0-rc.1", "windows")
            files["extra.json" if extra else "README.md"] = b'"changed"'
            with tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary)/"bad.zip"
                release.write_zip(path, files)
                with self.assertRaises(ValueError):
                    release.validate_zip(path)

    def test_duplicate_casefold_names_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)/"bad.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("a.txt", "a")
                archive.writestr("A.txt", "b")
            with self.assertRaises(ValueError):
                release.validate_zip(path)

    def test_ui_sources_match_compiled_inventory(self):
        if os.environ.get("NADELAB_SOURCE_ONLY_TESTS") == "1":
            self.skipTest("Source-only checks: real compiled UI requires CS2 Workshop Tools; full release build still verifies it")
        panel = ROOT / "artifacts/panel-ui"
        manifest = json.loads((panel / "manifest.json").read_text(encoding="utf-8-sig"))
        for item in manifest["files"]:
            self.assertEqual(release.digest((ROOT/"ui"/item["source"]).read_bytes()), item["sourceSha256"].lower())
            self.assertEqual(release.digest((panel/item["path"]).read_bytes()), item["sha256"].lower())

    def test_dependency_urls_and_hashes_are_pinned(self):
        lock = json.loads((ROOT / "scripts/release/dependencies.lock.json").read_text(encoding="utf-8-sig"))
        self.assertEqual({item["name"] for item in lock["packages"]}, {"metamod", "css-runtime"})
        for item in lock["packages"]:
            self.assertTrue(item["url"].startswith("https://"))
            self.assertNotIn("/latest/", item["url"])
            self.assertRegex(item["sha256"], r"^[a-f0-9]{64}$")

    def fake_build(self, root):
        (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><Version>0.2.0-rc.1</Version></PropertyGroup></Project>')
        (root / 'NadeLab.slnx').write_text('<Solution />')
        (root / 'NuGet.Config').write_text('<configuration />')
        for name in ('NadeLab.Core', 'NadeLab.Plugin'):
            source = root / 'src' / name
            source.mkdir(parents=True)
            (source / 'Example.cs').write_text('// source')
        binaries = root / 'src/NadeLab.Plugin/bin/Release/net10.0'
        binaries.mkdir(parents=True)
        for name in release.BUILD_FILES:
            (binaries / name).write_bytes(b'{}' if name.endswith('.json') else b'MZ fake test binary')
        return binaries

    def test_build_record_detects_changed_source_binary_or_version(self):
        for changed in ('source', 'binary', 'version'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                binaries = self.fake_build(root)
                fingerprint = release.source_fingerprint(root)
                release.write_build_record(root, fingerprint, '0.2.0-rc.1')
                self.assertEqual(release.validate_build_record(root)['sourceFingerprint'], fingerprint)
                if changed == 'source':
                    (root / 'src/NadeLab.Plugin/Example.cs').write_text('// changed source')
                elif changed == 'binary':
                    (binaries / 'NadeLab.dll').write_bytes(b'MZ stale binary')
                else:
                    (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><Version>0.3.0</Version></PropertyGroup></Project>')
                with self.assertRaises(ValueError):
                    release.validate_build_record(root)

    def test_build_record_requires_unchanged_prebuild_source_and_verified_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fake_build(root)
            fingerprint = release.source_fingerprint(root)
            with self.assertRaises(ValueError):
                release.write_build_record(root, fingerprint, '0.1.0')
            (root / 'src/NadeLab.Core/Example.cs').write_text('// source changed during build')
            with self.assertRaises(ValueError):
                release.write_build_record(root, fingerprint, '0.2.0-rc.1')
            self.assertFalse((root / 'artifacts/release-build.json').exists())

    def test_generated_obj_and_bin_files_do_not_invalidate_source_fingerprint(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fake_build(root)
            before = release.source_fingerprint(root)
            generated = root / 'src/NadeLab.Plugin/obj/Release/generated.cs'
            generated.parent.mkdir(parents=True)
            generated.write_text('// compiler generated')
            self.assertEqual(release.source_fingerprint(root), before)

    def test_release_inputs_use_fixed_script_and_document_allowlists(self):
        added = []
        def capture(files, name, path):
            added.append(name)
            files[name] = b'allowed'
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            panel = root / 'artifacts/panel-ui'
            entries = []
            for index, compiled in enumerate(release.UI_FILES):
                source = f'fixture/source{index}.txt'
                source_data, compiled_data = b'fixture UI source', b'fixture compiled resource'
                src, dst = root / 'ui' / source, panel / compiled
                src.parent.mkdir(parents=True, exist_ok=True)
                dst.parent.mkdir(parents=True, exist_ok=True)
                src.write_bytes(source_data)
                dst.write_bytes(compiled_data)
                entries.append({'path': compiled, 'source': source, 'sha256': release.digest(compiled_data),
                                'sourceSha256': release.digest(source_data)})
            (panel / 'manifest.json').write_text(json.dumps({'schema': 1, 'files': entries}))
            with mock.patch.object(release, 'add_file', side_effect=capture):
                release.inputs(root, 'windows')
        self.assertEqual({name for name in added if name.startswith('docs/release/')},
                         {'docs/release/' + name for name in release.PLAYER_DOCS})
        self.assertEqual({name for name in added if name.startswith('scripts/release/')},
                         {'scripts/release/' + name for name in release.RELEASE_SCRIPTS})
        self.assertNotIn('scripts/release/Test-ReleaseTools.ps1', added)
        self.assertTrue({'docs/license-history.md', 'docs/development-guide.md', 'docs/训练与调试.txt',
                         'CONTRIBUTING.md', 'SECURITY.md'}.issubset(added))


if __name__ == "__main__":
    unittest.main()
