#!/usr/bin/env python3
"""Offline CI gate for the opt-in FreshClam overlay.

Uses Docker Compose JSON merge as source of truth; no live ClamAV mirror or
daemon is contacted. This is NOT evidence of successful signature downloads.
"""
import copy
import json
import pathlib
import subprocess
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
BASE = "docker/compose.yml"
OVERLAY = "docker/compose.clamav-updates.yml"


def compose_config(*files):
    command = ["docker", "compose"]
    for path in files:
        command.extend(("-f", path))
    command.extend(("config", "--format", "json"))
    proc = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    if proc.returncode:
        # Never print the merged environment: demo stack includes credentials.
        raise RuntimeError("Docker Compose overlay failed to merge")
    return json.loads(proc.stdout)


def validate_update_overlay(config):
    services = config.get("services", {})
    if not isinstance(services, dict) or "clamav" not in services or "api" not in services:
        raise ValueError("Expected ClamAV and API")
    clamav, api = services["clamav"], services["api"]
    env, api_env = clamav.get("environment", {}), api.get("environment", {})
    if not isinstance(env, dict) or not isinstance(api_env, dict):
        raise ValueError("Missing environment")
    if clamav.get("image") != "clamav/clamav:1.5":
        raise ValueError("Uncontrolled ClamAV version")
    if clamav.get("ports"):
        raise ValueError("ClamAV unauthenticated TCP must stay internal")
    if env.get("CLAMAV_NO_FRESHCLAMD") != "false":
        raise ValueError("FreshClam must be running")
    if env.get("TZ") != "UTC":
        raise ValueError("ClamAV timezone must be UTC")
    freq = str(env.get("FRESHCLAM_CHECKS", ""))
    if not freq.isdigit() or int(freq) not in range(1, 25):
        raise ValueError("FreshClam daily check count must be bounded")

    db_mounts = [m for m in clamav.get("volumes", [])
                 if m.get("target") == "/var/lib/clamav"]
    if (len(db_mounts) != 1 or
            db_mounts[0].get("type") != "volume" or
            db_mounts[0].get("source") != "clamav-signatures" or
            db_mounts[0].get("read_only", False) or
            "clamav-signatures" not in config.get("volumes", {})):
        raise ValueError("Writable, named persistent signature volume required")

    if api_env.get("ASPNETCORE_ENVIRONMENT") != "Development":
        raise ValueError("This overlay is local demo only, not production settings")
    max_age = str(api_env.get("Malware__Clamd__MaxSignatureAgeHours", ""))
    if not max_age.isdigit() or not 1 <= int(max_age) <= 168:
        raise ValueError("API signature-age admission must be active")
    if api_env.get("Malware__Clamd__Host") != "clamav":
        raise ValueError("API must use the same private clamd service")
    if api.get("depends_on", {}).get("clamav", {}).get("condition") != "service_healthy":
        raise ValueError("API must await ClamAV health")


class FreshClamOverlayContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.base = compose_config(BASE)
        cls.merged = compose_config(BASE, OVERLAY)

    def test_base_stays_offline_and_opt_in_overlay_is_strict(self):
        self.assertEqual(
            self.base["services"]["clamav"]["environment"]["CLAMAV_NO_FRESHCLAMD"],
            "true",
        )
        self.assertNotIn("Malware__Clamd__MaxSignatureAgeHours",
                         self.base["services"]["api"]["environment"])
        validate_update_overlay(self.merged)

    def test_accidental_disable_exposure_or_non_persistent_db_fails_closed(self):
        changes = [
            lambda v: v["services"]["clamav"]["environment"].update(
                CLAMAV_NO_FRESHCLAMD="true"),
            lambda v: v["services"]["clamav"].update(
                ports=[{"published": "3310", "target": 3310}]),
            lambda v: v["services"]["clamav"]["volumes"][0].update(
                type="bind", source="./unsafe"),
            lambda v: v["services"]["clamav"]["volumes"].clear(),
            lambda v: v["services"]["clamav"]["environment"].update(TZ="Europe/Rome"),
            lambda v: v["services"]["clamav"]["environment"].update(FRESHCLAM_CHECKS="0"),
            lambda v: v["services"]["api"]["environment"].update(
                Malware__Clamd__MaxSignatureAgeHours=""),
            lambda v: v["services"]["api"]["environment"].update(
                Malware__Clamd__MaxSignatureAgeHours="999"),
            lambda v: v["services"]["api"]["environment"].update(
                ASPNETCORE_ENVIRONMENT="Production"),
            lambda v: v["services"]["api"]["depends_on"]["clamav"].update(
                condition="service_started"),
        ]
        for change in changes:
            with self.subTest(change=changes.index(change)):
                candidate = copy.deepcopy(self.merged)
                change(candidate)
                with self.assertRaises(ValueError):
                    validate_update_overlay(candidate)


if __name__ == "__main__":
    unittest.main()
