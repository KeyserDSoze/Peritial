#!/usr/bin/env bash
# Disposable end-to-end test: real API -> private OTLP/HTTP collector -> debug exporter.
# No debug receiver port or credentials are exposed on the host.
set -euo pipefail

base=docker/compose.yml
overlay=docker/compose.observability.yml
compose=(docker compose -f "$base" -f "$overlay")

"${compose[@]}" config --quiet

# Validate the actual merged config: the receiver must have no host port
# bindings, and the API must explicitly opt in only in this overlay.
"${compose[@]}" config --format json | python3 -c '
import json, sys
cfg = json.load(sys.stdin)["services"]
collector = cfg["otel-collector"]
api = cfg["api"]
assert not collector.get("ports"), "OTLP receiver must not publish any host port"
assert collector.get("read_only") is True, "Collector root filesystem must be read-only"
assert api["environment"]["Observability__Otlp__Enabled"] == "true"
assert api["environment"]["Observability__Otlp__Endpoint"] == "http://otel-collector:4318/"
'

# A previous cross-service offline restore intentionally stopped all writers.
# Enable telemetry only on the API while the other services continue running.
"${compose[@]}" up -d otel-collector
"${compose[@]}" up -d --no-deps --force-recreate api

ready=0
for i in $(seq 1 50); do
  if curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null; then
    ready=1
    break
  fi
  sleep 2
done
test "$ready" = "1"

# Both data types must traverse the real Compose bridge and be decoded by a
# real Collector. Avoid relying on the API's local in-memory MeterListener.
exported=0
for i in $(seq 1 65); do
  curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null
  logs=$("${compose[@]}" logs --no-color otel-collector 2>&1)
  if grep -Fq 'peritial.readiness.requests' <<<"$logs" &&
     grep -Eq 'Name[[:space:]]*:[[:space:]]*peritial\.readiness[[:space:]]*$' <<<"$logs"; then
    exported=1
    break
  fi
  sleep 2
done

if [ "$exported" != "1" ]; then
  echo "OTLP receiver did not confirm both metric and trace delivery" >&2
  "${compose[@]}" logs --tail=100 otel-collector >&2
  exit 1
fi
echo "Verified OTLP metrics and spans delivered to isolated collector"

# Loss of observability is not a data-path failure. Existing PG, OIDC,
# ClamAV and S3 probes still decide readiness after the collector stops.
"${compose[@]}" stop otel-collector
curl --fail --silent --max-time 6 http://localhost:8080/health/live >/dev/null
curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null

# Restore the default non-exporting API before the remaining chaos gates.
docker compose -f "$base" up -d --no-deps --force-recreate api
restored=0
for i in $(seq 1 35); do
  if curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null; then
    restored=1
    break
  fi
  sleep 2
done
test "$restored" = "1"
echo "API returned to default opt-out configuration"
