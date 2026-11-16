"""Review-locked infrastructure images with verified pulls from green GitHub Actions.

Scope intentionally limited to PostgreSQL in two Compose services and the CI
service, and Prometheus in demo Compose and both CI promtool commands.
Do not infer a full Compose supply-chain guarantee from this policy.
"""
from pathlib import Path
import re

POSTGRES = "postgres:17-alpine@sha256:a77aa70e641402a21411b92b721c58d10f23b50ae5fdd2333efd22d166e1e5f7"
PROMETHEUS = "prom/prometheus:v3.5.0@sha256:63805ebb8d2b3920190daf1cb14a60871b16fd38bed42b857a3182bc621f4996"

# Counts are deliberately explicit so a deletion or duplication is visible.
EXPECTED: dict[str, dict[str, tuple[str, int]]] = {
    ".github/workflows/ci.yml": {"postgres": (POSTGRES, 1), "prometheus": (PROMETHEUS, 2)},
    "docker/compose.yml": {"postgres": (POSTGRES, 2)},
    "docker/compose.prometheus.yml": {"prometheus": (PROMETHEUS, 1)},
}

IMAGE_LINE = re.compile(r"^\s*image\s*:\s*(\S+)\s*$")
PROMTOOL_LINE = re.compile(r"--entrypoint\s+/bin/promtool\s+(\S+)\s*\\?\s*$")


def detect_images(content: str, path: str) -> dict[str, list[str]]:
    images = {"postgres": [], "prometheus": []}
    for line in content.splitlines():
        if line.lstrip().startswith("#"):
            continue
        # CI runs promtool via docker, unlike Compose service image fields.
        if path.startswith(".github/"):
            command = PROMTOOL_LINE.search(line)
            if command:
                images["prometheus"].append(command.group(1))
        match = IMAGE_LINE.fullmatch(line)
        if match:
            image = match.group(1)
            if "postgres" in image:
                images["postgres"].append(image)
            if "prometheus" in image:
                images["prometheus"].append(image)
    return images


def validate_sources(sources: dict[str, str]) -> list[str]:
    errors: list[str] = []
    for path, expected in EXPECTED.items():
        if path not in sources:
            errors.append(f"{path}: required image inventory source missing")
            continue
        found = detect_images(sources[path], path)
        for kind, (approved, count) in expected.items():
            if found[kind] != [approved] * count:
                errors.append(
                    f"{path}: expected {count} immutable reviewed {kind} image references; "
                    "actual references differ or are missing"
                )
    return errors


def validate_repository(root: Path) -> list[str]:
    sources = {
        path: (root / path).read_text(encoding="utf-8")
        for path in EXPECTED if (root / path).is_file()
    }
    return validate_sources(sources)
