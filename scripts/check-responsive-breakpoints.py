#!/usr/bin/env python3
"""Reject viewport breakpoints outside the standard tiers.

Container queries are deliberately independent of the viewport tiers: admin components size
to the content area with @container gws-main (...), which allows any width.
responsive-breakpoint-exceptions.json can list a per-stylesheet exception; it is empty now
that every legacy width has migrated (2026-10-04), so keep it that way.
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
retained = sum(len(widths) for widths in exceptions.values())
print("Responsive breakpoint lint passed" + (f" ({retained} listed exceptions)." if retained else "."))
