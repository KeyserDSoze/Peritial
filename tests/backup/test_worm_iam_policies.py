"""Tests for least-privilege IAM policies rendered for the WORM recovery job."""
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "recovery" / "render_worm_iam.py"
spec = importlib.util.spec_from_file_location("worm_iam", SCRIPT)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class WormIamPolicyTests(unittest.TestCase):
    def test_writer_and_reader_are_scoped_and_never_get_delete_or_bucket_admin(self):
        bucket = "peritial-worm-ci"
        for writer in (True, False):
            with self.subTest(writer=writer):
                value = mod.policy(bucket, writer=writer)
                self.assertEqual("2012-10-17", value["Version"])
                statements = value["Statement"]
                self.assertEqual(3, len(statements))
                self.assertEqual(
                    {"arn:aws:s3:::" + bucket, "arn:aws:s3:::" + bucket + "/sealed-v1/*"},
                    {x["Resource"] for x in statements},
                )
                self.assertTrue(all(x["Effect"] == "Allow" for x in statements))
                actions = set(sum((x["Action"] for x in statements), []))
                self.assertIn("s3:GetObjectRetention", actions)
                self.assertIn("s3:GetObjectVersion", actions)
                self.assertIn("s3:GetBucketObjectLockConfiguration", actions)
                self.assertIn("s3:ListBucketVersions", actions)
                self.assertEqual(writer, "s3:PutObject" in actions)
                self.assertFalse(any(
                    a == "s3:*" or "Delete" in a or "PutBucket" in a or
                    "PutObjectRetention" in a or "PutObjectLegalHold" in a
                    for a in actions))
                bucket_list = next(x for x in statements if "s3:ListBucket" in x["Action"])
                self.assertEqual(
                    {"StringLike": {"s3:prefix": ["sealed-v1/*"]}},
                    bucket_list["Condition"],
                )

    def test_invalid_bucket_cannot_expand_scope(self):
        for bucket in ("*", "", "../other", "bucket/objects", "192.168.1.1",
                       "UPPERCASE", "a", "a..b", "x" * 65):
            with self.subTest(bucket=bucket):
                with self.assertRaises(ValueError):
                    mod.policy(bucket, writer=True)

    def test_cli_outputs_private_files_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = pathlib.Path(tmp) / "policies"
            command = [sys.executable, str(SCRIPT), "--bucket", "peritial-worm-ci",
                       "--output", str(output)]
            result = subprocess.run(command, check=False, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(0o700, output.stat().st_mode & 0o777)
            for name in ("writer.json", "reader.json"):
                path = output / name
                self.assertEqual(0o600, path.stat().st_mode & 0o777)
                self.assertEqual("2012-10-17", json.loads(path.read_text())["Version"])
            again = subprocess.run(command, check=False, capture_output=True, text=True)
            self.assertNotEqual(0, again.returncode)
            self.assertEqual({"writer.json", "reader.json"},
                             {p.name for p in output.iterdir()})
            symlink = pathlib.Path(tmp) / "linked"
            symlink.symlink_to(output, target_is_directory=True)
            denied = subprocess.run(command[:-1] + [str(symlink)],
                                    check=False, capture_output=True, text=True)
            self.assertNotEqual(0, denied.returncode)


if __name__ == "__main__":
    unittest.main()
