import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
for name in ("check-env.ps1", "check-logs.ps1", "set-lab-ip.ps1", "run-acceptance.ps1"):
    path = os.path.join(here, name)
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        print(f"{name}: BOM already present")
        continue
    with open(path, "wb") as handle:
        handle.write(b"\xef\xbb\xbf" + raw)
    print(f"{name}: BOM added ({len(raw)} bytes)")
