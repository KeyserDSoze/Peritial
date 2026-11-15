#!/usr/bin/env bash
set -euo pipefail
# CI-only: kill the API after a real signed S3 HTTP PUT has forwarded body
# bytes to SeaweedFS; no mock of PutObjectAsync or seed-after-kill shortcut.
compose() { docker compose -f docker/compose.yml "$@"; }
chaos() { docker compose -f docker/compose.yml -f docker/compose.chaos.yml --profile chaos "$@"; }
temp="$(mktemp -d)"
client_pid=""
cleanup() {
  if [ -n "$client_pid" ]; then kill "$client_pid" 2>/dev/null || true; fi
  rm -rf "$temp"
}
trap cleanup EXIT

chaos up -d --no-deps --force-recreate s3-chaos-proxy api
ready=0
for i in $(seq 1 50); do
  if curl --fail --silent --max-time 2 http://localhost:8080/health/live >/dev/null; then
    ready=1
    break
  fi
  sleep 2
done
test "$ready" = 1 || { echo "Chaos API readiness failed"; exit 1; }

# Test-only Keycloak client; production PWA still uses Authorization Code + PKCE.
token="$(curl --fail --silent --show-error --max-time 20 \
  -d grant_type=password -d client_id=peritial-test-write \
  -d username=localuser -d password=local-only-user \
  http://localhost:8081/realms/peritial/protocol/openid-connect/token \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')"
test -n "$token"

python3 - "$temp/chaos.pdf" <<'PY'
import sys
with open(sys.argv[1], "wb") as file:
    file.write(b"%PDF-1.7\n1 0 obj\n<<>>\nendobj\n")
    file.write(b"clean fault-injection test data\n" * 300000)
    file.write(b"%%EOF\n")
PY

curl --silent --show-error --max-time 180 \
  -H "Authorization: Bearer $token" \
  -H "Idempotency-Key: chaos-mid-put-00000001" \
  -F "file=@$temp/chaos.pdf;type=application/pdf" \
  http://localhost:8080/api/files >"$temp/response" 2>"$temp/error" &
client_pid="$!"
unset token

forwarded=0
for i in $(seq 1 90); do
  if chaos exec -T s3-chaos-proxy test -f /signals/put-body-forwarded; then
    forwarded=1
    break
  fi
  if ! kill -0 "$client_pid" 2>/dev/null; then
    echo "Upload returned before S3 request body was forwarded"
    chaos logs --tail=40 s3-chaos-proxy
    exit 1
  fi
  sleep 2
done
test "$forwarded" = 1 || {
  echo "No S3 PUT bytes reached SeaweedFS via chaos gate"
  chaos logs --tail=40 s3-chaos-proxy
  exit 1
}
kill -0 "$client_pid" 2>/dev/null || { echo "Upload finished before kill"; exit 1; }

# No other uploads are pending after the normal Playwright suite. Ensure
# this request actually has an intent, which is needed for crash recovery.
intent="$(compose exec -T postgres psql -U peritial -d peritial -Atqc \
  "SELECT document_id::text || '|' || storage_key FROM upload_intents WHERE tenant_id='11111111-1111-1111-1111-111111111111'")"
rows="$(printf '%s\n' "$intent" | grep -c '|' || true)"
test "$rows" = 1 || { echo "Expected one durable in-flight upload intent"; exit 1; }
document_id="$(printf '%s' "$intent" | cut -d'|' -f1)"
storage_key="$(printf '%s' "$intent" | cut -d'|' -f2)"

compose kill --signal SIGKILL api
kill "$client_pid" 2>/dev/null || true
wait "$client_pid" 2>/dev/null || true
client_pid=""

# Only after SIGKILL, expire the dead uploader lease and grace period. A live
# writer has no chance to renew. Restart the original API WITHOUT chaos proxy.
updated="$(compose exec -T postgres psql -U peritial -d peritial -Atqc \
  "WITH aged AS (UPDATE upload_intents SET created_at_utc=now()-interval '2 hours', lease_expires_at_utc=now()-interval '1 minute' WHERE tenant_id='11111111-1111-1111-1111-111111111111' AND document_id='$document_id' RETURNING 1) SELECT count(*) FROM aged")"
test "$updated" = 1 || { echo "Could not expire killed upload intent"; exit 1; }

compose up -d --no-deps --force-recreate api
ready=0
for i in $(seq 1 50); do
  if curl --fail --silent --max-time 2 http://localhost:8080/health/live >/dev/null; then
    ready=1
    break
  fi
  sleep 2
done
test "$ready" = 1 || { echo "Restarted API not ready"; exit 1; }

recovered=0
for i in $(seq 1 70); do
  remaining="$(compose exec -T postgres psql -U peritial -d peritial -Atqc \
    "SELECT count(*) FROM upload_intents WHERE tenant_id='11111111-1111-1111-1111-111111111111' AND document_id='$document_id'")"
  if [ "$remaining" = 0 ]; then
    recovered=1
    break
  fi
  sleep 2
done
test "$recovered" = 1 || { echo "Mid-PUT crash recovery timed out"; exit 1; }

# Signed SDK GET distinguishes 404 from unauthorized, unlike plain curl.
compose --profile test run -T --rm \
  -e PERITIAL_TEST_MID_PUT=1 \
  -e PERITIAL_TEST_CHAOS_ID="$document_id" \
  -e PERITIAL_TEST_CHAOS_KEY="$storage_key" \
  -e PERITIAL_TEST_S3_ENDPOINT=http://seaweedfs:8333 \
  tests dotnet test tests/backend/Peritial.Infrastructures.Tests/Peritial.Infrastructures.Tests.csproj \
  --filter FullyQualifiedName~UploadMidPutContractTests \
  --logger "console;verbosity=normal"

echo "S3_MID_PUT_KILL_RECOVERED"
