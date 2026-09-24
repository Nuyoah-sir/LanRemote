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
    START-HERE.*        - 所选里程碑的两机验收手册；不附带改网脚本。

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

set-lab-ip.ps1 仅保留为仓库历史材料，不得随新包交付。
验收器只读检查现有网络，管理员权限或 UAC 确认不豁免不改网约束。
"""

import argparse
import os
import re
import subprocess
import tempfile
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROJECT = os.path.join(REPO_ROOT, "tools", "LanRemote.Acceptance", "LanRemote.Acceptance.csproj")
EXCLUDED_SCRIPT_EXTENSIONS = frozenset({".ps1", ".cmd", ".bat"})


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


def publish(publish_dir: str) -> None:
    dotnet = find_dotnet()
    if not os.path.isfile(dotnet):
        raise SystemExit(f"dotnet.exe not found (looked at {dotnet}); source scripts/env.sh first")

    print(f"publishing   : {os.path.relpath(PROJECT, REPO_ROOT)}")
    subprocess.run(
        [
            dotnet,
            "publish",
            PROJECT,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "true",
            "-o", publish_dir,
        ],
        check=True,
        cwd=REPO_ROOT,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the self-contained WPF acceptance package.")
    parser.add_argument("--milestone", choices=("m4",), default="m4")
    parser.add_argument("--manual", help="Explicit acceptance manual for the selected milestone")
    parser.add_argument("--output-dir", default=REPO_ROOT)
    args = parser.parse_args()
    if not args.manual:
        parser.error("m4 requires --manual; never ship the historical M3 EOF criteria as M4 instructions")
    if os.environ.get("LANREMOTE_M3_SKIP_PUBLISH") == "1":
        parser.error("m4 must publish fresh binaries; skipping publish is not supported")
    doc_source = os.path.abspath(args.manual)
    output_dir = os.path.abspath(args.output_dir)
    if not os.path.isdir(output_dir):
        parser.error("output directory must already exist")
    if not os.path.isfile(doc_source):
        parser.error("acceptance manual not found")
    manual_extension = os.path.splitext(doc_source)[1]
    if manual_extension.casefold() in EXCLUDED_SCRIPT_EXTENSIONS:
        parser.error("acceptance manual must not be a script")
    manual_name = "START-HERE" + manual_extension
    version = read_version()

    # 每次发布到独立新目录；不复用或删除任何旧发布产物，失败目录也保留。
    artifacts_dir = os.path.join(REPO_ROOT, "artifacts")
    os.makedirs(artifacts_dir, exist_ok=True)
    publish_dir = tempfile.mkdtemp(prefix=args.milestone + "-acceptance-", dir=artifacts_dir)
    publish(publish_dir)

    zip_path = os.path.join(output_dir, f"LanRemote-{version}-{args.milestone}-acceptance-win-x64.zip")

    file_count = 0
    total_bytes = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, _dirs, files in os.walk(publish_dir):
            for file_name in sorted(files):
                # 防御性过滤所有层级、大小写的脚本与旧手册，包括嵌套同名手册。
                name = file_name.casefold()
                if (os.path.splitext(name)[1] in EXCLUDED_SCRIPT_EXTENSIONS
                        or name == "start-here" or name.startswith("start-here.")):
                    continue
                full_path = os.path.join(root, file_name)
                rel_path = os.path.relpath(full_path, publish_dir)
                archive.write(full_path, rel_path)
                file_count += 1
                total_bytes += os.path.getsize(full_path)
        # 只从明确选择的源路径装入一份手册，不以文件名放行发布目录中的任何副本。
        archive.write(doc_source, manual_name)
        file_count += 1
        total_bytes += os.path.getsize(doc_source)

    packed = os.path.getsize(zip_path)
    print(f"version      : {version}")
    print(f"source dir   : {publish_dir}")
    print(f"files packed : {file_count}")
    print(f"raw size     : {total_bytes / 1024 / 1024:.1f} MiB")
    print(f"zip size     : {packed / 1024 / 1024:.1f} MiB")
    print(f"output       : {zip_path}")


if __name__ == "__main__":
    main()
