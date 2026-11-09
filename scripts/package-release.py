"""Fresh allowlisted release archives. Python is needed only on the build machine."""
from __future__ import annotations
import argparse
import hashlib
import json
import re
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = "game/csgo/addons/counterstrikesharp/plugins/NadeLab"
UI_FILES = ("panorama/styles/custom_game/nadelab_panel.vcss_c",
            "panorama/layout/custom_game/nadelab_panel.vxml_c", "maps/scripts/nadelab_panel_bridge.vjs_c")
BAD_PARTS = {".tools", "server", "instance", "release-state", "data", "__pycache__", "archive", "node_modules"}
BAD_SUFFIXES = {".pdb", ".log", ".vpk", ".local", ".bak", ".pyc"}
TEXT_SUFFIXES = {".json", ".md", ".cfg", ".ps1", ".psm1", ".cmd", ".txt"}
PLAYER_DOCS = ("data-and-support.md", "install-and-recovery.md", "known-limits.md",
               "player-guide.md", "release-checklist.md", "server-guide.md")
RELEASE_SCRIPTS = ("NadeLab.ps1", "NadeLab.Common.ps1", "NadeLab.ServerProcess.ps1", "NadeLab.Input.ps1", "dependencies.lock.json")
INPUT_FILES = ("NadeLab.Input.dll", "NadeLab.Input.deps.json", "NadeLab.Input.runtimeconfig.json", "NadeLab.Core.dll")
BUILD_FILES = ("NadeLab.dll", "NadeLab.Core.dll", "NadeLab.deps.json")
PRIVATE_PATTERNS = (r"(?i)D:[\\/]+GPT[\\/]+CS2", r"(?i)C:[\\/]+Users[\\/]+Lenovo",
                    r"7656119\d{10}", r"(?i)userdata[\\/]+\d+[\\/]+730")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def safe_path(name: str) -> str:
    path = PurePosixPath(name)
    if not name or "\\" in name or ":" in name or path.is_absolute() or any(p in (".", "..", "") for p in name.split("/")):
        raise ValueError(f"Unsafe package path: {name}")
    if any(p.lower() in BAD_PARTS for p in path.parts) or path.suffix.lower() in BAD_SUFFIXES:
        raise ValueError(f"Private/development artifact in package: {name}")
    return name


def check_content(name: str, data: bytes) -> None:
    safe_path(name)
    # PE debug directories also carry path strings even when PDB files are not
    # shipped. Scan all bytes in both common encodings, not just text suffixes.
    def private_value(text: str) -> bool:
        return any(re.search(pattern, text) for pattern in PRIVATE_PATTERNS)
    if any(private_value(value.decode(encoding, errors="replace")) for value, encoding in
           ((data, "utf-8"), (data, "utf-16le"), (data[1:], "utf-16le"))):
        raise ValueError(f"Private environment value in {name}")
    if PurePosixPath(name).suffix.lower() in TEXT_SUFFIXES:
        text = data.decode("utf-8-sig")
        if re.search(r"(?im)^\s*rcon_password\s+\"?[^\s\"$][^\r\n]*", text):
            raise ValueError(f"Private environment value in {name}")
        if PurePosixPath(name).suffix.lower() == ".json":
            def inspect(value: object) -> None:
                if isinstance(value, str) and private_value(value):
                    raise ValueError(f"Private environment value in {name}")
                if isinstance(value, dict):
                    for key, item in value.items():
                        inspect(key)
                        inspect(item)
                elif isinstance(value, list):
                    for item in value:
                        inspect(item)
            inspect(json.loads(text))


def add_file(files: dict[str, bytes], name: str, path: Path) -> None:
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"Missing or linked release input: {path}")
    data = path.read_bytes()
    check_content(name, data)
    if name in files:
        raise ValueError(f"Duplicate file: {name}")
    files[name] = data


def product_version(root: Path) -> str:
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[a-z0-9.]+)?", version):
        raise ValueError("Directory.Build.props must contain a release version")
    return version


def json_bytes(obj: object) -> bytes:
    return (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def source_fingerprint(root: Path) -> str:
    paths = [root / name for name in ("Directory.Build.props", "NadeLab.slnx", "NuGet.Config")]
    for name in ("NadeLab.Core", "NadeLab.Plugin", "NadeLab.Input"):
        paths.extend(path for path in (root / "src" / name).rglob("*")
                     if path.is_file() and not any(part in ("bin", "obj") for part in path.relative_to(root / "src" / name).parts))
    for path in root.glob("Directory.Build.targets"):
        paths.append(path)
    inventory = []
    for path in sorted(set(paths)):
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"Missing or linked build source: {path}")
        inventory.append({"path": path.relative_to(root).as_posix(), "sha256": digest(path.read_bytes())})
    return digest(json_bytes(inventory))


def binary_inventory(root: Path) -> list[dict]:
    result = []
    for name in BUILD_FILES:
        path = root / "src/NadeLab.Plugin/bin/Release/net10.0" / name
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"Missing or linked build artifact: {path}")
        data = path.read_bytes()
        check_content(name, data)
        result.append({"path": name, "bytes": len(data), "sha256": digest(data)})
    return result


def write_build_record(root: Path, expected_source: str, verified_version: str) -> dict:
    version = product_version(root)
    current = source_fingerprint(root)
    if verified_version != version or current != expected_source:
        raise ValueError("Version or source changed during the build; rebuild before packaging")
    record = {"schema": 1, "version": version, "verifiedProductVersion": verified_version,
              "sourceFingerprint": current, "files": binary_inventory(root)}
    destination = root / "artifacts/release-build.json"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(json_bytes(record))
    return record


def validate_build_record(root: Path) -> dict:
    path = root / "artifacts/release-build.json"
    if not path.is_file():
        raise ValueError("Verified release build record missing; run scripts/build.ps1")
    record = json.loads(path.read_text(encoding="utf-8"))
    version = product_version(root)
    if record.get("schema") != 1 or record.get("version") != version or record.get("verifiedProductVersion") != version:
        raise ValueError("Release build version is stale; run scripts/build.ps1")
    if record.get("sourceFingerprint") != source_fingerprint(root) or record.get("files") != binary_inventory(root):
        raise ValueError("Release source or binaries changed after verification; run scripts/build.ps1")
    return record


def inputs(root: Path, kind: str) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    for name in ("NadeLab.dll", "NadeLab.deps.json", "NadeLab.Core.dll"):
        add_file(files, f"{PLUGIN}/{name}", root / "src/NadeLab.Plugin/bin/Release/net10.0" / name)
    add_file(files, f"{PLUGIN}/physics.example.json", root / "configs/physics.example.json")
    for name in ("nadelab_practice.cfg", "nadelab_keys.cfg"):
        add_file(files, f"game/csgo/cfg/{name}", root / "configs" / name)
    for name in ("README.md", "CHANGELOG.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "CONTRIBUTING.md", "SECURITY.md",
                 "docs/license-history.md", "docs/development-guide.md", "docs/训练与调试.txt"):
        add_file(files, name, root / name)
    for name in PLAYER_DOCS:
        path = root / "docs/release" / name
        add_file(files, path.relative_to(root).as_posix(), path)
    panel = root / "artifacts/panel-ui"
    manifest = json.loads((panel / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest.get("schema") != 1:
        raise ValueError("Unsupported compiled UI manifest")
    entries = {e["path"]: e for e in manifest["files"]}
    if len(entries) != len(manifest["files"]) or set(entries) != set(UI_FILES):
        raise ValueError("UI manifest must contain exactly the three NadeLab resources")
    for name in UI_FILES:
        item = entries[name]
        source = safe_path(item["source"])
        if digest((panel / name).read_bytes()) != item["sha256"].lower():
            raise ValueError(f"Compiled UI changed: {name}")
        if digest((root / "ui" / source).read_bytes()) != item["sourceSha256"].lower():
            raise ValueError(f"Rebuild UI after source change: {source}")
        add_file(files, f"game/csgo/{name}", panel / name)
    files["panel-ui-manifest.json"] = json_bytes({"schema": 1, "files": [
        {"path": e["path"], "sha256": e["sha256"].lower(), "sourceSha256": e["sourceSha256"].lower()}
        for e in manifest["files"]]})
    if kind == "windows":
        for name in INPUT_FILES:
            add_file(files, f"tools/input/{name}", root / "src/NadeLab.Input/bin/Release/net10.0" / name)
        add_file(files, "NadeLab.cmd", root / "NadeLab.cmd")
        for name in RELEASE_SCRIPTS:
            path = root / "scripts/release" / name
            add_file(files, path.relative_to(root).as_posix(), path)
        if "scripts/release/NadeLab.ps1" not in files or "scripts/release/dependencies.lock.json" not in files:
            raise ValueError("Portable launcher or dependency lock missing")
    return files


def manifest_for(files: dict[str, bytes], version: str, kind: str) -> bytes:
    return json_bytes({"schema": 1, "product": "NadeLab", "version": version, "channel": "release-candidate",
                       "package": kind, "files": [{"path": name, "bytes": len(data), "sha256": digest(data)}
                                                   for name, data in sorted(files.items())]})


def write_zip(path: Path, files: dict[str, bytes]) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            check_content(name, data)
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)


def validate_zip(path: Path) -> dict:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(n.casefold() for n in names)):
            raise ValueError("Duplicate/case-colliding archive names")
        for name in names:
            check_content(name, archive.read(name))
        manifest = json.loads(archive.read("release-manifest.json"))
        expected = {e["path"]: e for e in manifest["files"]}
        if len(expected) != len(manifest["files"]) or set(names) != set(expected) | {"release-manifest.json"}:
            raise ValueError("Archive inventory does not match its manifest")
        for name, entry in expected.items():
            data = archive.read(name)
            if digest(data) != entry["sha256"] or len(data) != entry["bytes"]:
                raise ValueError(f"Archive integrity failure: {name}")
        return {"version": manifest["version"], "package": manifest["package"], "files": len(names),
                "sha256": digest(path.read_bytes()), "bytes": path.stat().st_size}


def build(root: Path, output: Path) -> dict:
    version = product_version(root)
    validate_build_record(root)
    output.mkdir(parents=True, exist_ok=True)
    reports = {}
    with tempfile.TemporaryDirectory(prefix="nadelab-release-", dir=output) as temporary:
        stage = Path(temporary)
        for kind in ("windows", "server-plugin"):
            files = inputs(root, kind)
            files["release-manifest.json"] = manifest_for(files, version, kind)
            name = f"NadeLab-{version}-{kind}.zip"
            archive = stage / name
            write_zip(archive, files)
            reports[name] = validate_zip(archive)
            archive.replace(output / name)
        sums = "".join(f"{r['sha256']}  {n}\n" for n, r in sorted(reports.items()))
        (output / "SHA256SUMS.txt").write_text(sums, encoding="ascii")
        (output / "package-validation.json").write_bytes(json_bytes({"schema": 1, "packages": reports}))
    return reports


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify", type=Path)
    parser.add_argument("--fingerprint", action="store_true")
    parser.add_argument("--record-build", action="store_true")
    parser.add_argument("--expected-source-fingerprint")
    parser.add_argument("--verified-version")
    args = parser.parse_args()
    if args.fingerprint:
        print(source_fingerprint(ROOT))
        raise SystemExit(0)
    if args.record_build:
        if not args.expected_source_fingerprint or not args.verified_version:
            parser.error("--record-build requires the pre-build fingerprint and verified DLL ProductVersion")
        print(json.dumps(write_build_record(ROOT, args.expected_source_fingerprint, args.verified_version), ensure_ascii=True, indent=2))
        raise SystemExit(0)
    result = validate_zip(args.verify) if args.verify else build(ROOT, args.output or ROOT / "artifacts/releases" / product_version(ROOT))
    print(json.dumps(result, ensure_ascii=True, indent=2))
