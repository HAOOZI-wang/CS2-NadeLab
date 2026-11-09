"""Inspect the exact Git index, not untracked local data. No secret values are printed.

This catches common accidental inclusions; it is not a comprehensive secret audit.
Run after git add, and independently review unfamiliar files before publishing.
"""
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {
    ".gitignore", ".gitattributes", "LICENSE", "README.md", "CONTRIBUTING.md", "SECURITY.md",
    "THIRD_PARTY_NOTICES.md", "CHANGELOG.md", "Directory.Build.props", "NadeLab.slnx", "NuGet.Config",
    "NadeLab.cmd", "Start-NadeLab.cmd", "Restore-CS2-Settings.cmd", "Start-CS2-Performance.cmd", "工作台.cmd",
}
PREFIXES = ("src/", "tests/", "scripts/", "configs/", "ui/", ".github/", "docs/release/")
DOCS = {"docs/development-guide.md", "docs/license-history.md", "docs/训练与调试.txt"}
BLOCKED_PARTS = {"bin", "obj", ".tools", "artifacts", "server", "instance", "release-state", "data",
                 "__pycache__", "node_modules", ".vs", ".idea", ".git"}
BLOCKED_SUFFIXES = {".user", ".suo", ".pyc", ".log", ".local", ".bak", ".tmp", ".dmp", ".pdb",
                    ".dll", ".exe", ".vpk", ".dem", ".pem", ".pfx", ".key"}


def check_entry(name, data, mode):
    path = PurePosixPath(name)
    issues = []
    if (path.is_absolute() or "\\" in name or any(p in ("", ".", "..") for p in name.split("/"))
            or not (name in ROOT_FILES or name in DOCS or name.startswith(PREFIXES))
            or name == "scripts/bind-flight.py"):
        issues.append("outside public source boundary")
    if (any(p.lower() in BLOCKED_PARTS for p in path.parts) or path.suffix.lower() in BLOCKED_SUFFIXES
            or path.name.lower() == ".env" or path.name.lower().startswith(".env.")):
        issues.append("private or generated file")
    if mode not in ("100644", "100755"):
        issues.append("link, submodule or unsupported Git mode")
    if len(data) > 5 * 1024 * 1024:
        issues.append("file exceeds source review size limit")
    text = data.decode("utf-8-sig", errors="replace")
    if re.search(r"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})", text):
        issues.append("possible GitHub credential")
    if re.search(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", text):
        issues.append("private key material")
    if re.search(r'(?im)^\s*rcon_password\s+["\']?(?!["\'$\s])[^\s"\']+', text):
        issues.append("literal RCON password")
    return issues


def main():
    listing = subprocess.run(["git", "ls-files", "--stage", "-z"], cwd=ROOT, check=True, stdout=subprocess.PIPE).stdout
    failures = 0
    count = 0
    for raw in listing.split(b"\0"):
        if not raw:
            continue
        meta, raw_name = raw.split(b"\t", 1)
        mode, oid, stage = meta.decode("ascii").split()
        name = raw_name.decode("utf-8")
        count += 1
        if stage != "0":
            issues = ["unresolved Git merge entry"]
        elif mode not in ("100644", "100755"):
            issues = check_entry(name, b"", mode)
        else:
            data = subprocess.run(["git", "cat-file", "blob", oid], cwd=ROOT, check=True, stdout=subprocess.PIPE).stdout
            issues = check_entry(name, data, mode)
        if issues:
            failures += 1
            print(f"REJECT {name}: {'; '.join(issues)}")
    if count == 0:
        print("REJECT: Git index is empty; stage the intended source files first.")
        return 1
    print(f"Public source index: {count} files checked; {failures} rejected. Manual content review is still required.")
    return int(failures != 0)


if __name__ == "__main__":
    sys.exit(main())
