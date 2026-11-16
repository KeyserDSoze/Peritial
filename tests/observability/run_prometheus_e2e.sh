#!/usr/bin/env bash
# E2E contract: real API -> private Collector -> OTLP Prometheus -> PromQL alerts.
# Disposable Development/CI only. No production security/notification guarantees.
set -euo pipefail

base=docker/compose.yml
collector=docker/compose.observability.yml
prom_overlay=docker/compose.prometheus.yml
stack=(docker compose -f "$base" -f "$collector" -f "$prom_overlay")
endpoint=http://127.0.0.1:19090

# Fail before modifying running services if the overlay is missing/unsafe.
"${stack[@]}" config --quiet
"${stack[@]}" config --format json | python3 -c '
import json, sys
services = json.load(sys.stdin)["services"]
prom = services["prometheus"]
otel = services["otel-collector"]
api = services["api"]
assert not otel.get("ports"), "OTLP Collector receiver must remain internal"
assert prom.get("read_only") is True, "Prometheus root filesystem must be read-only"
assert prom.get("tmpfs"), "Prometheus CI data must use disposable tmpfs"
ports = prom.get("ports", [])
assert len(ports) == 1 and ports[0].get("host_ip") == "127.0.0.1", (
    "Prometheus API must bind to loopback only")
assert api["environment"]["Observability__Otlp__Enabled"] == "true"
assert api["environment"]["Observability__Otlp__Endpoint"] == "http://otel-collector:4318/"
'

# PromQL instant-vector query: return sum of all sample values, 0 for empty.
# All queries hit the local disposable Prometheus only; no credentials used.
prom_value() {
  python3 - "$1" <<'PY'
import json, sys, urllib.parse, urllib.request
url = "http://127.0.0.1:19090/api/v1/query?" + urllib.parse.urlencode(
    {"query": sys.argv[1]})
with urllib.request.urlopen(url, timeout=5) as response:
    result = json.load(response)
assert result["status"] == "success", result
print(sum(float(item["value"][1]) for item in result["data"]["result"]))
PY
}
positive() { awk -v value="$1" 'BEGIN { exit !(value > 0) }'; }
zero() { awk -v value="$1" 'BEGIN { exit !(value == 0) }'; }

# Previous collector E2E restored the API base and stopped its receiver.
"${stack[@]}" up -d prometheus otel-collector
"${stack[@]}" up -d --no-deps --force-recreate api

# Demonstrate actual OTLP normalization: dot names -> Prometheus counter
# and histogram series, not a synthetic prometheus fixture.
ingested=0
for i in $(seq 1 75); do
  curl --fail --silent --max-time 5 http://localhost:8080/health/ready >/dev/null || true
  if ready=$(prom_value 'sum(peritial_readiness_requests_total{outcome="ready"})' 2>/dev/null) &&
     positive "$ready" &&
     histogram=$(prom_value 'sum(peritial_readiness_duration_seconds_bucket{le="+Inf"})' 2>/dev/null) &&
     positive "$histogram"; then
    ingested=1
    break
  fi
  sleep 2
done
if [ "$ingested" != 1 ]; then
  echo "Missing actual Prometheus normalized readiness counter/histogram" >&2
  docker compose -f "$base" logs --tail=40 api >&2
  "${stack[@]}" logs --tail=45 prometheus otel-collector >&2
  exit 1
fi
echo "Verified readiness counters and histograms in real Prometheus via OTLP"

# Assert no alert initially, then force actual ClamAV outage. Reuse the
# existing mandatory health dependency instead of fabricating exporter data.
initial=$(prom_value 'sum(ALERTS{alertname="PeritialReadinessFailures",alertstate="firing"})')
zero "$initial"
docker compose -f "$base" stop clamav

triggered=0
for i in $(seq 1 55); do
  code=$(curl --silent --max-time 6 -o /dev/null -w '%{http_code}' http://localhost:8080/health/ready || true)
  if [ "$code" != 503 ]; then
    echo "Expected HTTP 503 for a stopped ClamAV dependency, received $code" >&2
    exit 1
  fi
  if failures=$(prom_value 'sum(ALERTS{alertname="PeritialReadinessFailures",alertstate="firing"})' 2>/dev/null) &&
     dependency=$(prom_value 'sum(ALERTS{alertname="PeritialReadinessDependencyFailed",dependency="clamav",alertstate="firing"})' 2>/dev/null) &&
     positive "$failures" && positive "$dependency"; then
    triggered=1
    break
  fi
  sleep 2
done
if [ "$triggered" != 1 ]; then
  echo "Prometheus did not fire both real readiness failure alerts" >&2
  "${stack[@]}" logs --tail=45 prometheus otel-collector >&2
  exit 1
fi
echo "Verified both alerts firing during a real ClamAV outage"

docker compose -f "$base" start clamav
recovered=0
for i in $(seq 1 55); do
  if curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null; then
    recovered=1
    break
  fi
  sleep 2
done
test "$recovered" = "1"
echo "Verified API readiness returned to HTTP 200 after ClamAV restart"

# The live alert rule uses increase(...[2m]); recovery must NOT be asserted
# before its full lookback has elapsed. Exercise actual rule evaluation and
# prove firing series disappear without changing production thresholds.
resolved=0
for i in $(seq 1 105); do
  curl --fail --silent --max-time 6 http://localhost:8080/health/ready >/dev/null
  if failures=$(prom_value 'sum(ALERTS{alertname="PeritialReadinessFailures",alertstate="firing"})' 2>/dev/null) &&
     dependency=$(prom_value 'sum(ALERTS{alertname="PeritialReadinessDependencyFailed",dependency="clamav",alertstate="firing"})' 2>/dev/null) &&
     zero "$failures" && zero "$dependency"; then
    resolved=1
    break
  fi
  sleep 2
done
if [ "$resolved" != 1 ]; then
  echo "Readiness alerts did not resolve after actual dependency recovery" >&2
  exit 1
fi
echo "Verified Prometheus firing alerts genuinely resolve after lookback"

# Leave the ordinary Development API with telemetry disabled. None of these
# CI-only services or unauthenticated HTTP endpoints is in the base Compose.
"${stack[@]}" stop otel-collector prometheus
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
echo "Prometheus demo shut down and API restored to non-exporting default"
