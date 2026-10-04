#!/usr/bin/env python3
"""Reject new ad-hoc viewport breakpoints; retain documented legacy exceptions.

Container queries are deliberately independent of the viewport tiers.
Remove exceptions as existing components migrate to the standard tiers.
"""
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
allowed = {"576px", "575.98px", "768px", "767.98px", "1024px", "1023.98px",
           "1440px", "1439.98px", "1920px", "1919.98px"}
exceptions = json.loads((root / "scripts/responsive-breakpoint-exceptions.json").read_text())
failures = []
for path in sorted((root / "src/GwsBusinessSuite.Web").rglob("*.css")):
    if any(part in {"bin", "obj", "assets", "lib"} for part in path.parts):
        continue
    relative = str(path.relative_to(root))
    accepted = allowed | set(exceptions.get(relative, []))
    for match in re.finditer(r"@media\s+([^{}]+)\{", path.read_text()):
        for width in re.findall(r"(?:min|max)-width\s*:\s*([\d.]+(?:px|em|rem))", match[1]):
            if width not in accepted:
                line = path.read_text()[:match.start()].count("\n") + 1
                failures.append(f"{relative}:{line}: nonstandard viewport breakpoint {width}")
if failures:
    print("\n".join(failures), file=sys.stderr)
    sys.exit(1)
print("Responsive breakpoint lint passed (legacy exceptions retained).")
