#!/usr/bin/env python3
"""Render scoped IAM policies for encrypted Peritial S3 recovery backups.

This is a deterministic policy renderer, NOT an AWS IAM provisioning tool.
Never generate policies with wildcard bucket ARNs or destructive grants.
"""
import argparse
import json
import os
import pathlib
import re
import sys

BUCKET_RE = re.compile(r"[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\Z", re.ASCII)
BASIC_BUCKET_READ = [
    "s3:GetBucketVersioning",
    "s3:GetBucketObjectLockConfiguration",
]
BUCKET_LIST = ["s3:ListBucket", "s3:ListBucketVersions"]
OBJECT_READ = ["s3:GetObject", "s3:GetObjectVersion", "s3:GetObjectRetention"]
OBJECT_WRITE = ["s3:PutObject"]


def policy(bucket: str, *, writer: bool) -> dict:
    if not BUCKET_RE.fullmatch(bucket) or ".." in bucket or bucket.replace(".", "").isdigit():
        raise ValueError("Invalid explicit S3 bucket name")
    bucket_arn = "arn:aws:s3:::" + bucket
    return {
        "Version": "2012-10-17",
        "Statement": [
            {
                "Sid": "ReadOnlyBucketSecurityConfiguration",
                "Effect": "Allow",
                "Action": BASIC_BUCKET_READ,
                "Resource": bucket_arn,
            },
            {
                "Sid": "ListOnlyEncryptedRecoveryPrefix",
                "Effect": "Allow",
                "Action": BUCKET_LIST,
                "Resource": bucket_arn,
                "Condition": {"StringLike": {"s3:prefix": ["sealed-v1/*"]}},
            },
            {
                "Sid": "ReadOrUploadOnlyEncryptedBundleObjects",
                "Effect": "Allow",
                "Action": sorted(OBJECT_READ + (OBJECT_WRITE if writer else [])),
                "Resource": bucket_arn + "/sealed-v1/*",
            },
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bucket", required=True)
    parser.add_argument("--output", required=True, help="New private output directory")
    args = parser.parse_args()
    try:
        writer = policy(args.bucket, writer=True)
        reader = policy(args.bucket, writer=False)
        dest = pathlib.Path(args.output)
        if not dest.is_absolute() or dest.exists() or dest.is_symlink():
            raise ValueError("Policy output must be a new absolute private directory")
        dest.mkdir(mode=0o700, parents=False, exist_ok=False)
        for name, value in (("writer.json", writer), ("reader.json", reader)):
            path = dest / name
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as out:
                json.dump(value, out, indent=2, sort_keys=True)
                out.write("\n")
        return 0
    except (ValueError, OSError) as exc:
        print("Refusing insecure IAM policy rendering: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
