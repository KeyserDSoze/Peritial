"""Lock selected GitHub Actions runner and SDK versions to verified CI values.

The runner family is pinned, but the GitHub-hosted VM image build remains
mutable. The SDK versions correspond to completed CI job logs; this is not a
complete lock of package restore, Docker tools or upstream apt dependencies.
"""
from __future__ import annotations

from pathlib import Path
import re

REVIEWED_VALUES = {
    "runs-on": ("ubuntu-26.04", 9),
    "node-version": ("22.23.4", 2),
    "dotnet-version": ("10.0.404", 3),
}

SENSITIVE_KEY = re.compile(r"(?:^|[\s{,])(runs-on|node-version|dotnet-version)\s*:")
KEY_VALUE = re.compile(
    r"^\s*(runs-on|node-version|dotnet-version)\s*:\s*(?:(['\"])([^'\"]+)\2|([^\s#\"']+))\s*$"
)


def validate_ci(contents: str, location: str = ".github/workflows/ci.yml") -> list[str]:
    """Fail closed on missing/drifted jobs and unapproved runner/toolchain values."""
    errors: list[str] = []
    counts: dict[str, int] = {key: 0 for key in REVIEWED_VALUES}
    for number, source in enumerate(contents.splitlines(), 1):
        stripped = source.lstrip()
        if stripped.startswith("#"):
            continue
        # Comments are allowed only after a complete unambiguous YAML scalar.
        line = source.split("#", 1)[0].rstrip()
        if not SENSITIVE_KEY.search(line):
            continue
        match = KEY_VALUE.fullmatch(line)
        if match is None:
            errors.append(f"{location}:{number}: ambiguous runner/SDK YAML expression")
            continue
        key = match.group(1)
        value = match.group(3) if match.group(2) else match.group(4)
        counts[key] += 1
        expected, _ = REVIEWED_VALUES[key]
        if value != expected:
            errors.append(f"{location}:{number}: {key} {value!r} is not approved ({expected!r})")
    for key, (_, count) in REVIEWED_VALUES.items():
        if counts[key] != count:
            errors.append(f"{location}: expected exactly {count} {key} entries; found {counts[key]}")
    return errors


def validate_ci_file(root: Path) -> list[str]:
    path = root / ".github/workflows/ci.yml"
    if not path.is_file():
        return [f"{path}: required CI definition missing"]
    return validate_ci(path.read_text(encoding="utf-8"), path.as_posix())
