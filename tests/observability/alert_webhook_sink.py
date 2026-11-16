#!/usr/bin/env python3
"""Disposable Alertmanager webhook receiver for CI only.

No request details, labels outside the allowlist, customer identifiers,
timestamps, annotations, external URLs, or authorization data are persisted.
Never deploy this listener outside the private Development Compose network.
"""

import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

EVENT_FILE = Path("/events/events.jsonl")
MAX_CONTENT_LENGTH = 65536
MAX_ALERTS_PER_EVENT = 64
ALLOWED_ALERT_NAMES = frozenset({
    "PeritialReadinessFailures",
    "PeritialReadinessDependencyFailed",
    "PeritialReadinessSlow",
    "PeritialReadinessTelemetryMissing",
})
ALLOWED_DEPENDENCIES = frozenset({"postgres", "clamav", "oidc", "storage"})
ALLOWED_STATUSES = frozenset({"firing", "resolved"})


def filter_event(payload):
    """Reduce a webhook to fixed, low-cardinality alert attributes."""
    if not isinstance(payload, dict) or payload.get("status") not in ALLOWED_STATUSES:
        raise ValueError("Invalid group status")
    alerts = payload.get("alerts")
    if not isinstance(alerts, list) or not 1 <= len(alerts) <= MAX_ALERTS_PER_EVENT:
        raise ValueError("Invalid number of alerts")

    filtered = []
    for alert in alerts:
        if not isinstance(alert, dict) or alert.get("status") not in ALLOWED_STATUSES:
            raise ValueError("Invalid alert status")
        labels = alert.get("labels")
        if not isinstance(labels, dict):
            raise ValueError("Missing labels")
        name = labels.get("alertname")
        if name not in ALLOWED_ALERT_NAMES:
            raise ValueError("Unrecognized alert")
        dependency = labels.get("dependency")
        if name == "PeritialReadinessDependencyFailed":
            if dependency not in ALLOWED_DEPENDENCIES:
                raise ValueError("Invalid dependency")
        elif dependency is not None:
            raise ValueError("Unexpected dependency")

        event = {"status": alert["status"], "alertname": name}
        if dependency is not None:
            event["dependency"] = dependency
        filtered.append(event)
    return {"status": payload["status"], "alerts": filtered}


class SinkHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/alerts" or not self.headers.get("Content-Type", "").startswith("application/json"):
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "-1"))
            if length < 1 or length > MAX_CONTENT_LENGTH:
                raise ValueError("Invalid body size")
            data = self.rfile.read(length)
            if len(data) != length:
                raise ValueError("Truncated body")
            event = filter_event(json.loads(data))
        except (ValueError, TypeError, UnicodeDecodeError, json.JSONDecodeError):
            self.send_response(400)
            self.end_headers()
            return
        with EVENT_FILE.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(event, separators=(",", ":"), sort_keys=True) + "\n")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"accepted":true}')

    def log_message(self, _format, *_args):
        # Never log remote addresses, external URLs, payloads or credentials.
        pass


if __name__ == "__main__":
    HTTPServer(("0.0.0.0", 8088), SinkHandler).serve_forever()
