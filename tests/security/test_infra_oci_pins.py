"""Regression tests for selected verified infrastructure image references (#6)."""
import sys
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "security"))
from infra_oci_policy import (  # noqa: E402
    POSTGRES,
    PROMETHEUS,
    detect_images,
    validate_repository,
    validate_sources,
)


def good_sources() -> dict[str, str]:
    return {
        ".github/workflows/ci.yml": (
            f"        image: {POSTGRES}\n"
            f"            --entrypoint /bin/promtool {PROMETHEUS} \\\n"
            f"            --entrypoint /bin/promtool {PROMETHEUS} \\\n"
        ),
        "docker/compose.yml": (
            f"    image: {POSTGRES}\n"
            f"    image: {POSTGRES}\n"
        ),
        "docker/compose.prometheus.yml": f"    image: {PROMETHEUS}\n",
    }


class InfrastructureOciPolicyTests(unittest.TestCase):
    def test_real_sources_match_reviewed_images(self):
        self.assertEqual([], validate_repository(ROOT))

    def test_baseline_passes_for_all_three_paths(self):
        self.assertEqual([], validate_sources(good_sources()))

    def test_mutable_and_wrong_postgres_digests_fail(self):
        for other in ("postgres:17-alpine", POSTGRES[:-1] + "0", "postgres@sha256:1234"):
            with self.subTest(other=other):
                sources = good_sources()
                sources["docker/compose.yml"] = sources["docker/compose.yml"].replace(POSTGRES, other, 1)
                self.assertTrue(validate_sources(sources))

    def test_mutable_and_wrong_prometheus_digests_fail(self):
        for other in ("prom/prometheus:v3.5.0", PROMETHEUS[:-1] + "0", "prom/prometheus:v4.0"):
            with self.subTest(other=other):
                sources = good_sources()
                sources[".github/workflows/ci.yml"] = sources[".github/workflows/ci.yml"].replace(PROMETHEUS, other, 1)
                self.assertTrue(validate_sources(sources))

    def test_missing_or_duplicate_references_fail(self):
        sources = good_sources()
        del sources["docker/compose.prometheus.yml"]
        self.assertTrue(validate_sources(sources))
        sources = good_sources()
        sources["docker/compose.yml"] += f"    image: {POSTGRES}\n"
        self.assertTrue(validate_sources(sources))

    def test_comment_cannot_satisfy_image_requirements(self):
        sources = good_sources()
        sources["docker/compose.yml"] = "# image: " + POSTGRES + "\n" + f"    image: {POSTGRES}\n"
        self.assertTrue(validate_sources(sources))

    def test_cli_shell_continuation_is_not_part_of_digest(self):
        sources = good_sources()
        images = detect_images(sources[".github/workflows/ci.yml"], ".github/workflows/ci.yml")
        self.assertEqual([PROMETHEUS, PROMETHEUS], images["prometheus"])


if __name__ == "__main__":
    unittest.main()
