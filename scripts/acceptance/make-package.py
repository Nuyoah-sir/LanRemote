"""Package the published self-contained build into a portable acceptance zip.

Usage:
    python scripts/acceptance/make-package.py

Produces:
    LanRemote-<version>-win-x64.zip  (in the repo root)

The zip contains the published output plus:
    START-HERE.md   - the two-machine acceptance manual (Chinese)
    check-env.ps1   - pre-flight environment check
    check-logs.ps1  - post-run log inspection / access-key leak scan
"""

import os
import re
import shutil
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PUBLISH_DIR = os.path.join(REPO_ROOT, "artifacts", "m2.1-acceptance")
SCRIPTS_DIR = os.path.join(REPO_ROOT, "scripts", "acceptance")
DOC_SOURCE = os.path.join(REPO_ROOT, "docs", "TWO_MACHINE_ACCEPTANCE.md")


def read_version() -> str:
    props = os.path.join(REPO_ROOT, "Directory.Build.props")
    with open(props, "r", encoding="utf-8") as handle:
        match = re.search(r"<LanRemoteVersion>(.*?)</LanRemoteVersion>", handle.read())
    if not match:
        raise SystemExit("LanRemoteVersion not found in Directory.Build.props")
    return match.group(1).strip()


def main() -> None:
    version = read_version()
    if not os.path.isdir(PUBLISH_DIR):
        raise SystemExit(f"publish output not found: {PUBLISH_DIR}")

    # Put the manual and the two helper scripts next to the exe.
    shutil.copyfile(DOC_SOURCE, os.path.join(PUBLISH_DIR, "START-HERE.md"))
    for name in ("check-env.ps1", "check-logs.ps1"):
        shutil.copyfile(
            os.path.join(SCRIPTS_DIR, name), os.path.join(PUBLISH_DIR, name)
        )

    zip_path = os.path.join(REPO_ROOT, f"LanRemote-{version}-win-x64.zip")
    if os.path.exists(zip_path):
        os.remove(zip_path)

    file_count = 0
    total_bytes = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, _dirs, files in os.walk(PUBLISH_DIR):
            for file_name in sorted(files):
                full_path = os.path.join(root, file_name)
                rel_path = os.path.relpath(full_path, PUBLISH_DIR)
                archive.write(full_path, rel_path)
                file_count += 1
                total_bytes += os.path.getsize(full_path)

    packed = os.path.getsize(zip_path)
    print(f"version      : {version}")
    print(f"source dir   : {PUBLISH_DIR}")
    print(f"files packed : {file_count}")
    print(f"raw size     : {total_bytes / 1024 / 1024:.1f} MiB")
    print(f"zip size     : {packed / 1024 / 1024:.1f} MiB")
    print(f"output       : {zip_path}")


if __name__ == "__main__":
    main()
