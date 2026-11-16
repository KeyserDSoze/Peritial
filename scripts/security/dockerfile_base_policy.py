"""Fail-closed lock of application Dockerfile base images (#6).

Approved digests were recorded from BuildKit FROM resolution in a successful
Linux/amd64 Compose build (Actions run 43868851989). Updates require a new
successful build and explicit review. CI/Compose image pins are separate work.
"""
from __future__ import annotations

from pathlib import Path
import re

APPROVED_BY_DOCKERFILE = {
    "src/backend/Applications/Dockerfile": (
        "mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb",
        "mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e",
    ),
    "src/frontend/Dockerfile": (
        "node:22-alpine@sha256:1314fb30ec37592d5845e5a160d522c0e680972aa534549940b8d73bde88481a",
        "nginx:1.29-alpine@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de",
    ),
}
FROM_START = re.compile(r"^\s*FROM(?:\s|$)", re.IGNORECASE)
FULL_DIGEST = re.compile(r"[^\s@]+@sha256:[a-f0-9]{64}\Z")
IMAGE_STAGE = re.compile(
    r"^\s*FROM\s+(\S+)(?:\s+AS\s+([A-Za-z][A-Za-z0-9_.-]*))?\s*$",
    re.IGNORECASE,
)


def validate_dockerfile_text(contents: str, approved: tuple[str, ...], name: str = "<Dockerfile>") -> list[str]:
    errors: list[str] = []
    seen: list[str] = []
    stages: set[str] = set()
    for number, line in enumerate(contents.splitlines(), 1):
        if line.lstrip().startswith("#") or not FROM_START.match(line):
            continue
        location = f"{name}:{number}"
        match = IMAGE_STAGE.fullmatch(line)
        if match is None:
            errors.append(f"{location}: FROM syntax must be an explicit image@sha256:... [AS stage]")
            continue
        image, stage = match.groups()
        if not FULL_DIGEST.fullmatch(image):
            errors.append(f"{location}: Dockerfile base must contain a full lowercase SHA-256 digest")
        if stage:
            if stage.lower() in stages:
                errors.append(f"{location}: duplicate build stage name")
            stages.add(stage.lower())
        seen.append(image)
    if seen != list(approved):
        errors.append(f"{name}: FROM base images/stage order differ from reviewed allowlist")
    return errors


def validate_repository_dockerfiles(root: Path) -> list[str]:
    observed = {
        p.relative_to(root).as_posix()
        for p in root.rglob("Dockerfile*") if p.is_file()
        and ".git" not in p.relative_to(root).parts
    }
    approved = set(APPROVED_BY_DOCKERFILE)
    errors: list[str] = []
    for missing in sorted(approved - observed):
        errors.append(f"{missing}: required Dockerfile is missing")
    for new in sorted(observed - approved):
        errors.append(f"{new}: unreviewed Dockerfile; add a reviewed base-image lock")
    for path in sorted(observed & approved):
        errors.extend(validate_dockerfile_text(
            (root / path).read_text(encoding="utf-8"),
            APPROVED_BY_DOCKERFILE[path], path
        ))
    return errors
