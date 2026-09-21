"""Package the M3 two-machine acceptance harness into a portable zip.

Usage:
    python scripts/acceptance/make-m3-package.py

Produces:
    LanRemote-<version>-m3-acceptance-win-x64.zip   (in the repo root)

WHY THIS IS A SEPARATE SCRIPT FROM make-package.py
    make-package.py publishes LanRemote.App (the WPF client). That package is
    useless for M3 acceptance, because LanRemote.App only *references*
    LanRemote.Transport - it never calls TransportHost / TlsClientConnector /
    ControlPreAuthSession. Running it on two machines would only re-prove that
    discovery still works.

    This script publishes tools/LanRemote.Acceptance instead, which is the CLI
    that actually exercises TLS + pinning + channel_hello.

The zip contains the published self-contained output plus:
    START-HERE.md       - the M3 two-machine acceptance manual (Chinese)
    run-acceptance.ps1  - driver that runs the three mandatory scenarios
    set-lab-ip.ps1      - put both machines on a private 192.168.1.0/24 lab net
"""

import os
import re
import shutil
import subprocess
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROJECT = os.path.join(REPO_ROOT, "tools", "LanRemote.Acceptance", "LanRemote.Acceptance.csproj")
PUBLISH_DIR = os.path.join(REPO_ROOT, "artifacts", "m3-acceptance")
SCRIPTS_DIR = os.path.join(REPO_ROOT, "scripts", "acceptance")
DOC_SOURCE = os.path.join(REPO_ROOT, "docs", "M3_TWO_MACHINE_ACCEPTANCE.md")

HELPERS = ("run-acceptance.ps1", "set-lab-ip.ps1")


def read_version() -> str:
    props = os.path.join(REPO_ROOT, "Directory.Build.props")
    with open(props, "r", encoding="utf-8") as handle:
        match = re.search(r"<LanRemoteVersion>(.*?)</LanRemoteVersion>", handle.read())
    if not match:
        raise SystemExit("LanRemoteVersion not found in Directory.Build.props")
    return match.group(1).strip()


def find_dotnet() -> str:
    """Same rule as scripts/env.sh: the SDK lives at the user level, not in PATH."""
    override = os.environ.get("DOTNET_ROOT")
    if override:
        candidate = os.path.join(override, "dotnet.exe")
        if os.path.isfile(candidate):
            return candidate
    return os.path.join(os.path.expanduser("~"), ".dotnet", "dotnet.exe")


def publish() -> None:
    dotnet = find_dotnet()
    if not os.path.isfile(dotnet):
        raise SystemExit(f"dotnet.exe not found (looked at {dotnet}); source scripts/env.sh first")

    if os.path.isdir(PUBLISH_DIR):
        shutil.rmtree(PUBLISH_DIR)

    print(f"publishing   : {os.path.relpath(PROJECT, REPO_ROOT)}")
    subprocess.run(
        [
            dotnet,
            "publish",
            PROJECT,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "true",
            "-o", PUBLISH_DIR,
        ],
        check=True,
        cwd=REPO_ROOT,
    )


def main() -> None:
    version = read_version()

    if os.environ.get("LANREMOTE_M3_SKIP_PUBLISH") != "1":
        publish()

    if not os.path.isdir(PUBLISH_DIR):
        raise SystemExit(f"publish output not found: {PUBLISH_DIR}")

    if not os.path.isfile(DOC_SOURCE):
        raise SystemExit(f"manual not found: {DOC_SOURCE}")

    shutil.copyfile(DOC_SOURCE, os.path.join(PUBLISH_DIR, "START-HERE.md"))
    for name in HELPERS:
        source = os.path.join(SCRIPTS_DIR, name)
        if not os.path.isfile(source):
            raise SystemExit(f"helper script not found: {source}")
        shutil.copyfile(source, os.path.join(PUBLISH_DIR, name))

    # PowerShell 5.1 decodes .ps1 as ANSI unless a BOM says otherwise, which
    # garbles every Chinese character in the console output.
    for name in HELPERS:
        target = os.path.join(PUBLISH_DIR, name)
        with open(target, "rb") as handle:
            raw = handle.read()
        if not raw.startswith(b"\xef\xbb\xbf"):
            with open(target, "wb") as handle:
                handle.write(b"\xef\xbb\xbf" + raw)

    zip_path = os.path.join(REPO_ROOT, f"LanRemote-{version}-m3-acceptance-win-x64.zip")
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
