"""Regression tests for immutable and explicitly reviewed GitHub Actions refs."""

from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "security"))
from workflow_action_policy import (  # noqa: E402
    APPROVED_ACTIONS,
    validate_workflow_directory,
    validate_workflow_text,
)


def sample(ref: str) -> str:
    return f"name: example\njobs:\n  check:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: {ref}\n"


class WorkflowActionPinTests(unittest.TestCase):
    def test_all_repository_workflows_are_pinned_to_approved_commits(self) -> None:
        self.assertEqual(
            [],
            validate_workflow_directory(ROOT / ".github" / "workflows"),
            "Workflow actions must be reviewed and pinned to approved commits",
        )

    def test_all_approved_action_refs_pass(self) -> None:
        for action, sha in APPROVED_ACTIONS.items():
            with self.subTest(action=action):
                self.assertEqual([], validate_workflow_text(sample(f"{action}@{sha} # reviewed tag")))

    def test_mobile_tag_partial_sha_and_wrong_commit_are_rejected(self) -> None:
        for ref in (
            "actions/checkout@v4",
            "actions/checkout@main",
            "actions/checkout@11d5960a",
            "actions/checkout@" + "0" * 40,
            "actions/checkout@" + APPROVED_ACTIONS["actions/checkout"].upper(),
            "actions/checkout",
        ):
            with self.subTest(ref=ref):
                self.assertTrue(validate_workflow_text(sample(ref)))

    def test_unknown_and_local_actions_are_rejected_even_if_pinned(self) -> None:
        for ref in (
            "attacker/action@" + "a" * 40,
            "docker://alpine:latest",
            "./.github/actions/local",
        ):
            with self.subTest(ref=ref):
                self.assertTrue(validate_workflow_text(sample(ref)))

    def test_unusual_or_multiline_uses_syntax_fails_closed(self) -> None:
        for contents in (
            "steps:\n  - { uses: actions/checkout@v4 }\n",
            "steps:\n  - uses:\n      actions/checkout@v4\n",
            'steps:\n  - uses: "actions/checkout@v4"\n',
        ):
            with self.subTest(contents=contents):
                self.assertTrue(validate_workflow_text(contents))

    def test_comments_and_arbitrary_shell_steps_without_uses_are_allowed(self) -> None:
        self.assertEqual([], validate_workflow_text(
            "# uses: actions/checkout@v4\nsteps:\n  - run: echo ready\n"
        ))

    def test_empty_workflow_directory_is_rejected(self) -> None:
        from tempfile import TemporaryDirectory
        with TemporaryDirectory() as directory:
            self.assertTrue(validate_workflow_directory(Path(directory)))


if __name__ == "__main__":
    unittest.main()
