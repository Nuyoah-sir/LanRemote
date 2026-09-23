"""Package the current M4 WPF acceptance harness into a portable zip.

The historical script filename is retained; current binaries must NOT be
labelled M3 or shipped with the old fast-EOF success criteria.

Usage:
    python scripts/acceptance/make-m3-package.py --manual <M4-manual.txt> --output-dir <existing-dir>

Produces:
    LanRemote-<version>-m4-acceptance-win-x64.zip

The rationale below describes the original M3 tool; M4 now exercises complete
authentication, approval, and session hold/release as well.

WHY THIS IS A SEPARATE SCRIPT FROM make-package.py
    make-package.py publishes LanRemote.App (the WPF client). That package is
    useless for M3 acceptance, because LanRemote.App only *references*
    LanRemote.Transport - it never calls TransportHost / TlsClientConnector /
    ControlPreAuthSession. Running it on two machines would only re-prove that
    discovery still works.

    This script publishes tools/LanRemote.Acceptance instead, which is the GUI
    that actually exercises TLS + pinning + channel_hello.

The zip contains the published self-contained output plus:
    START-HERE.md       - the M3 two-machine acceptance manual (Chinese)
    set-lab-ip.ps1      - put both machines on a private 192.168.1.0/24 lab net

THE ENTRY POINT IS AN EXE, NOT A SCRIPT
    LanRemote.Acceptance is a WinExe + WPF app: double-clicking
    LanRemote.Acceptance.exe opens a window, and the two-machine acceptance is
    driven entirely from that window. There is deliberately no START.cmd and no
    run-acceptance.ps1 any more.

    An earlier revision of this script shipped a console exe plus a .cmd/.ps1
    pair. That was wrong for this project and was removed, because:
      * M2's two-machine acceptance was run from the WPF UIs on both machines
        (HANDOFF 9.1: "用户在 B 机界面确认", "B 点刷新");
      * the product-form principle recorded in HANDOFF 13 / ADR-024/025/026 is
        "终端用户永远不需要打开 PowerShell";
      * a console exe double-clicked with no arguments prints usage and exits,
        so the user sees a flash and concludes "there is no exe in the package".
    Lesson: don't ship a console tool for a step the project intends to be
    driven from a window.

set-lab-ip.ps1 stays a script on purpose: it needs elevation and it touches the
machine's network configuration, which must never happen silently.
"""

import argparse
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

HELPERS = ("set-lab-ip.ps1",)


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

    # Published output is overwritten in place by default. Wiping the directory
    # first deletes 200+ files in one go, which trips bulk-delete safety prompts,
    # and nothing needs it: `dotnet publish -o` overwrites every file it emits.
    # Set LANREMOTE_M3_CLEAN=1 only when you want a from-scratch directory.
    if os.environ.get("LANREMOTE_M3_CLEAN") == "1" and os.path.isdir(PUBLISH_DIR):
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
    global PUBLISH_DIR, DOC_SOURCE
    parser = argparse.ArgumentParser(description="Build the self-contained WPF acceptance package.")
    parser.add_argument("--milestone", choices=("m4",), default="m4")
    parser.add_argument("--manual", help="Explicit acceptance manual for the selected milestone")
    parser.add_argument("--output-dir", default=REPO_ROOT)
    args = parser.parse_args()
    if args.milestone == "m4" and not args.manual:
        parser.error("m4 requires --manual; never ship the historical M3 EOF criteria as M4 instructions")
    if args.milestone == "m4" and os.environ.get("LANREMOTE_M3_SKIP_PUBLISH") == "1":
        parser.error("m4 must publish fresh binaries; skipping publish is only for historical M3 repacking")
    if args.milestone != "m3":
        PUBLISH_DIR = os.path.join(REPO_ROOT, "artifacts", args.milestone + "-acceptance")
    if args.manual:
        DOC_SOURCE = os.path.abspath(args.manual)
    output_dir = os.path.abspath(args.output_dir)
    if not os.path.isdir(output_dir):
        parser.error("output directory must already exist")
    if not os.path.isfile(DOC_SOURCE):
        parser.error("acceptance manual not found")
    version = read_version()

    if os.environ.get("LANREMOTE_M3_SKIP_PUBLISH") != "1":
        publish()

    if not os.path.isdir(PUBLISH_DIR):
        raise SystemExit(f"publish output not found: {PUBLISH_DIR}")

    if not os.path.isfile(DOC_SOURCE):
        raise SystemExit(f"manual not found: {DOC_SOURCE}")

    manual_name = "START-HERE" + os.path.splitext(DOC_SOURCE)[1]
    shutil.copyfile(DOC_SOURCE, os.path.join(PUBLISH_DIR, manual_name))
    for name in HELPERS:
        source = os.path.join(SCRIPTS_DIR, name)
        if not os.path.isfile(source):
            raise SystemExit(f"helper script not found: {source}")
        shutil.copyfile(source, os.path.join(PUBLISH_DIR, name))

    # PowerShell 5.1 decodes .ps1 as ANSI unless a BOM says otherwise, which
    # garbles every Chinese character in the console output.
    # (No .cmd ships any more - and if one ever does, it must stay pure ASCII:
    # cmd.exe decodes it with the console code page.)
    for name in HELPERS:
        if not name.lower().endswith(".ps1"):
            continue
        target = os.path.join(PUBLISH_DIR, name)
        with open(target, "rb") as handle:
            raw = handle.read()
        if not raw.startswith(b"\xef\xbb\xbf"):
            with open(target, "wb") as handle:
                handle.write(b"\xef\xbb\xbf" + raw)

    zip_path = os.path.join(output_dir, f"LanRemote-{version}-{args.milestone}-acceptance-win-x64.zip")

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
