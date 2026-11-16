import importlib.util
import pathlib
import unittest

SINK = pathlib.Path(__file__).with_name("alert_webhook_sink.py")
spec = importlib.util.spec_from_file_location("peritial_alert_sink", SINK)
sink = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sink)


class AlertWebhookSinkTests(unittest.TestCase):
    def test_firing_event_keeps_only_fixed_operational_attributes(self):
        payload = {
            "status": "firing",
            "receiver": "secret-connection-name",
            "externalURL": "https://private.internal/?token=secret",
            "alerts": [
                {
                    "status": "firing",
                    "labels": {
                        "alertname": "PeritialReadinessDependencyFailed",
                        "dependency": "clamav",
                        "severity": "warning",
                        "tenant_id": "private-tenant",
                        "user": "private-user",
                    },
                    "annotations": {"description": "private-document-contents"},
                    "generatorURL": "https://private.internal",
                }
            ],
        }
        event = sink.filter_event(payload)
        self.assertEqual(event, {
            "status": "firing",
            "alerts": [{
                "status": "firing",
                "alertname": "PeritialReadinessDependencyFailed",
                "dependency": "clamav",
            }],
        })
        self.assertNotIn("private", repr(event))
        self.assertNotIn("secret", repr(event))

    def test_resolved_event_is_recorded_without_identifiers(self):
        event = sink.filter_event({
            "status": "resolved",
            "alerts": [{
                "status": "resolved",
                "labels": {"alertname": "PeritialReadinessFailures"}
            }],
        })
        self.assertEqual(event, {
            "status": "resolved",
            "alerts": [{"status": "resolved", "alertname": "PeritialReadinessFailures"}],
        })

    def test_invalid_or_unbounded_events_fail_closed(self):
        invalid = [
            {},
            {"status": "unknown", "alerts": []},
            {"status": "firing", "alerts": []},
            {"status": "firing", "alerts": [{}]},
            {"status": "firing", "alerts": [{
                "status": "firing", "labels": {"alertname": "UntrustedExternalAlert"}
            }]},
            {"status": "firing", "alerts": [{
                "status": "firing",
                "labels": {
                    "alertname": "PeritialReadinessDependencyFailed",
                    "dependency": "unbounded-customer-name",
                },
            }]},
            {"status": "resolved", "alerts": [{
                "status": "unknown", "labels": {"alertname": "PeritialReadinessFailures"}
            }]},
            {"status": "firing", "alerts": [{
                "status": "firing", "labels": {"alertname": "PeritialReadinessFailures"}
            }] * 65},
        ]
        for payload in invalid:
            with self.subTest(payload=payload):
                with self.assertRaises(ValueError):
                    sink.filter_event(payload)


if __name__ == "__main__":
    unittest.main()
