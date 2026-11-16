"""Adversarial checks for fixed GitHub Actions CI runner and SDK versions."""
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "security"))
from ci_toolchain_policy import (  # noqa: E402
    REVIEWED_VALUES,
    validate_ci,
    validate_ci_file,
)


def fixture() -> str:
    return "jobs:\n" + "".join(
        f"    {key}: '{value}'\n" * count
        for key, (value, count) in REVIEWED_VALUES.items()
    )


class CiToolchainPinTests(unittest.TestCase):
    def test_repository_ci_has_approved_runner_and_sdk_versions(self):
        self.assertEqual([], validate_ci_file(ROOT))

    def test_baseline_exact_values_pass(self):
        self.assertEqual([], validate_ci(fixture()))

    def test_floating_or_unapproved_runner_versions_fail(self):
        for value in ("ubuntu-latest", "ubuntu-24.04", "ubuntu-26.04-arm", "self-hosted"):
            with self.subTest(value=value):
                broken = fixture().replace("ubuntu-26.04", value, 1)
                self.assertTrue(validate_ci(broken))

    def test_floating_and_changed_sdk_versions_fail(self):
        for old, new in (("22.23.4", "22"), ("22.23.4", "22.x"),
                         ("10.0.404", "10.0.x"), ("10.0.404", "10.0.405")):
            with self.subTest(new=new):
                self.assertTrue(validate_ci(fixture().replace(old, new, 1)))

    def test_expression_and_inline_mapping_are_not_whitelisted(self):
        for broken in (
            fixture().replace("runs-on: 'ubuntu-26.04'", "runs-on: [ubuntu-26.04]", 1),
            fixture().replace("runs-on: 'ubuntu-26.04'", "runs-on: " + "$" + "{{ matrix.os }}", 1),
            fixture().replace("node-version: '22.23.4'", "{ node-version: '22.23.4' }", 1),
            fixture().replace("dotnet-version: '10.0.404'", "dotnet-version: \"10.0.404\" extra", 1),
        ):
            with self.subTest(broken=broken[:90]):
                self.assertTrue(validate_ci(broken))

    def test_missing_or_duplicated_keys_fail(self):
        for needle in ("runs-on: 'ubuntu-26.04'", "node-version: '22.23.4'",
                       "dotnet-version: '10.0.404'"):
            with self.subTest(needle=needle):
                self.assertTrue(validate_ci(fixture().replace(needle, "", 1)))
                self.assertTrue(validate_ci(fixture() + "  " + needle + "\n"))

    def test_comments_do_not_satisfy_expected_counts(self):
        baseline = fixture()
        self.assertEqual([], validate_ci("# runs-on: ubuntu-latest\n" + baseline))
        self.assertTrue(validate_ci("# runs-on: ubuntu-26.04\n"))

    def test_missing_ci_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as location:
            self.assertTrue(validate_ci_file(Path(location)))


if __name__ == "__main__":
    unittest.main()
