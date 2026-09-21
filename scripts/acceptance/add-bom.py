import os

here = os.path.dirname(os.path.abspath(__file__))

# Windows PowerShell 5.1 decodes .ps1 as ANSI unless a BOM says otherwise,
# which garbles Chinese output.
#
# NOTE: run-acceptance.ps1 used to be listed here. It is gone on purpose --
# the M3 acceptance harness is now a WPF window program (LanRemote.Acceptance.exe),
# so there is no PowerShell driver any more. Do not re-add it.
# set-lab-ip.ps1 stays: it needs elevation and touches the NIC configuration,
# so it must remain an explicitly-triggered script, never a silent step.
FORCED_BOM = ("check-env.ps1", "check-logs.ps1", "set-lab-ip.ps1")

for name in FORCED_BOM:
    path = os.path.join(here, name)
    if not os.path.isfile(path):
        print(f"{name}: MISSING (skipped)")
        continue
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        print(f"{name}: BOM already present")
        continue
    with open(path, "wb") as handle:
        handle.write(b"\xef\xbb\xbf" + raw)
    print(f"{name}: BOM added ({len(raw)} bytes)")
