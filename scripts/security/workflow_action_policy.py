"""Fail-closed allowlist of immutable GitHub Actions references used in CI.

The SHA values were resolved from official upstream release-tag refs when the
policy was adopted. A future upgrade must review the upstream commit and update
the allowlist and workflow together. This does not pin Docker images or tools.
"""

from __future__ import annotations

from pathlib import Path
import re

APPROVED_ACTIONS: dict[str, str] = {
    "actions/checkout": "11d5960a326750d5838078e36cf38b85af677262",  # v4
    "actions/setup-node": "49933ea5288caeca8642d1e84afbd3f7d6820020",  # v4
    "actions/setup-dotnet": "67a3573c9a986a3f9c594539f4ab511d57bb3ce9",  # v4
    "hashicorp/setup-terraform": "b9cd54a3c349d3f38e8881555d616ced269862dd",  # v3
}

USE_KEY = re.compile(r"(?:^|[\s{,])uses\s*:")
USE_LINE = re.compile(r"^\s*(?:-\s*)?uses\s*:\s*(\S+)\s*$")
FULL_SHA = re.compile(r"[0-9a-f]{40}\Z")


def validate_workflow_text(contents: str, path: str = "<workflow>") -> list[str]:
    """Return actionable errors; deliberately disallow unreviewed action sources."""
    errors: list[str] = []
    for line_number, original in enumerate(contents.splitlines(), start=1):
        # Workflow comments are not executable references.
        line = original.split("#", 1)[0]
        if not USE_KEY.search(line):
            continue
        location = f"{path}:{line_number}"
        match = USE_LINE.fullmatch(line)
        if not match:
            errors.append(f"{location}: uses must be a single unquoted action@40-hex-SHA reference")
            continue
        ref = match.group(1)
        if "@" not in ref:
            errors.append(f"{location}: action must include a commit SHA")
            continue
        action, sha = ref.rsplit("@", 1)
        expected = APPROVED_ACTIONS.get(action)
        if expected is None:
            errors.append(f"{location}: unreviewed action {action!r}; update allowlist explicitly")
        elif FULL_SHA.fullmatch(sha) is None:
            errors.append(f"{location}: {action} must use a full lowercase 40-hex commit SHA")
        elif sha != expected:
            errors.append(f"{location}: {action} commit differs from approved SHA")
    return errors


def validate_workflow_directory(directory: Path) -> list[str]:
    paths = sorted(path for path in directory.rglob("*")
                   if path.is_file() and path.suffix in {".yml", ".yaml"})
    if not paths:
        return [f"{directory}: no workflows found; CI pinning gate cannot run"]
    errors: list[str] = []
    for path in paths:
        errors.extend(validate_workflow_text(path.read_text(encoding="utf-8"), str(path)))
    return errors
