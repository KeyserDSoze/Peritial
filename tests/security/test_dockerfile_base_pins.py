"""Adversarial OCI base-image regression checks; no registry calls needed."""
from pathlib import Path
from tempfile import TemporaryDirectory
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "security"))
from dockerfile_base_policy import (  # noqa: E402
    APPROVED_BY_DOCKERFILE,
    validate_dockerfile_text,
    validate_repository_dockerfiles,
)


def fixture(path: str) -> str:
    first, second = APPROVED_BY_DOCKERFILE[path]
    return f"FROM {first} AS build\nRUN echo ok\nFROM {second}\n"


class DockerfileBasePinTests(unittest.TestCase):
    def test_application_dockerfiles_match_approved_digests(self):
        self.assertEqual([], validate_repository_dockerfiles(ROOT))

    def test_reviewed_images_pass(self):
        for name, approved in APPROVED_BY_DOCKERFILE.items():
            with self.subTest(name=name):
                self.assertEqual([], validate_dockerfile_text(fixture(name), approved, name))

    def test_mutable_unknown_and_partial_refs_fail(self):
        name = "src/frontend/Dockerfile"
        first, second = APPROVED_BY_DOCKERFILE[name]
        for changed in (
            first.split("@")[0],
            first.replace("@sha256:", "@sha256:" + "a" * 4)[:50],
            first.split("@")[0] + "@sha256:" + "0" * 64,
            first.split("@")[0] + "@sha256:" + "A" * 64,
            "${NODE_IMAGE}",
            "unknown.vendor/image:stable@sha256:" + "a" * 64,
        ):
            with self.subTest(image=changed):
                source = f"FROM {changed} AS build\nFROM {second}\n"
                self.assertTrue(validate_dockerfile_text(source, (first, second), name))

    def test_multiline_platform_ambiguous_and_duplicate_stage_fail(self):
        name = "src/frontend/Dockerfile"
        first, second = APPROVED_BY_DOCKERFILE[name]
        cases = (
            f"FROM --platform=linux/amd64 {first} AS build\nFROM {second}\n",
            f"FROM {first} \\\n AS build\nFROM {second}\n",
            f"FROM {first} AS build\nFROM {second} AS build\n",
            f"FROM {first} AS build # comment\nFROM {second}\n",
            f"FROM {second}\nFROM {first} AS build\n",
            "RUN echo no base\n",
        )
        for source in cases:
            with self.subTest(source=source):
                self.assertTrue(validate_dockerfile_text(source, (first, second), name))

    def test_unapproved_new_dockerfile_and_missing_expected_are_rejected(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertTrue(validate_repository_dockerfiles(root))
            for name in APPROVED_BY_DOCKERFILE:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(fixture(name), encoding="utf-8")
            self.assertEqual([], validate_repository_dockerfiles(root))
            rogue = root / "infra" / "Dockerfile.deploy"
            rogue.parent.mkdir(parents=True)
            rogue.write_text("FROM alpine:latest\n", encoding="utf-8")
            self.assertTrue(validate_repository_dockerfiles(root))

    def test_comments_are_ignored_not_parsed_as_from(self):
        name, approved = next(iter(APPROVED_BY_DOCKERFILE.items()))
        self.assertEqual(
            [],
            validate_dockerfile_text("# FROM alpine:latest\n" + fixture(name), approved, name),
        )


if __name__ == "__main__":
    unittest.main()
