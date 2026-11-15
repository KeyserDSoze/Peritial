#!/usr/bin/env bash
# Integration contract: a verified PostgreSQL dump can be restored on a NEW
# server with journal, tenant data and least-privilege RLS intact. No S3 backup.
set -euo pipefail
cd "$(dirname "$0")/../.."
src="peritial-backup-src-${GITHUB_RUN_ID:-$$}"
dst="peritial-backup-dst-${GITHUB_RUN_ID:-$$}"
dir="$(mktemp -d)"
cleanup() {
  docker rm -f "$src" "$dst" >/dev/null 2>&1 || true
  rm -rf "$dir"
}
trap cleanup EXIT
docker run -d --name "$src" -p 127.0.0.1:55432:5432 \
  -e POSTGRES_USER=peritial -e POSTGRES_PASSWORD=local-only-postgres \
  -e POSTGRES_DB=peritial postgres:17-alpine >/dev/null
docker run -d --name "$dst" \
  -e POSTGRES_USER=peritial -e POSTGRES_PASSWORD=local-only-postgres \
  -e POSTGRES_DB=peritial postgres:17-alpine >/dev/null
for name in "$src" "$dst"; do
  ready=false
  for i in $(seq 1 60); do
    if docker exec "$name" pg_isready -U peritial -d peritial >/dev/null 2>&1; then
      ready=true; break
    fi
    sleep 1
  done
  if [ "$ready" != true ]; then echo "PostgreSQL did not start" >&2; exit 1; fi
done

# Run the actual versioned V001/V002 migrator on the source.
PERITIAL_MIGRATOR_POSTGRES="Host=127.0.0.1;Port=55432;Database=peritial;Username=peritial;Password=local-only-postgres" \
  dotnet run --project scripts/db/Peritial.DbMigrator -- apply

# Synthetic fixtures from two tenants: perizie, document, audit, completed
# idempotency key, and an unrelated pending recovery intent.
docker exec -i "$src" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
INSERT INTO perizie(tenant_id,id,reference,status,created_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','BACKUP-ONE',-1,now()),
 ('22222222-2222-2222-2222-222222222222','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','BACKUP-TWO',-1,now());
INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc)
 VALUES('11111111-1111-1111-1111-111111111111','cccccccc-cccc-cccc-cccc-cccccccccccc',
 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','fixture.pdf','application/pdf',8,
 '11111111/cccccccc.pdf',now());
INSERT INTO security_audit(tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','dddddddd-dddd-dddd-dddd-dddddddddddd','test-actor','document.ingest','succeeded','cccccccc-cccc-cccc-cccc-cccccccccccc',now()),
 ('22222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','test-actor','perizia.create','denied',NULL,now());
INSERT INTO upload_request_keys(tenant_id,subject_digest,key_digest,request_digest,document_id,status,completed_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111',decode(repeat('aa',32),'hex'),decode(repeat('bb',32),'hex'),
 decode(repeat('cc',32),'hex'),'cccccccc-cccc-cccc-cccc-cccccccccccc','completed',now());
INSERT INTO upload_intents(tenant_id,document_id,storage_key) VALUES
 ('22222222-2222-2222-2222-222222222222','ffffffff-ffff-ffff-ffff-ffffffffffff','22222222/pending.pdf');
SQL

# Execute scripts inside pinned PG17 CLI/server image; archive is copied
# between separate clusters, not merely two databases on one server.
docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_BACKUP_FILE=/tmp/peritial.dump "$src" sh -s < scripts/db/backup.sh
docker cp "$src:/tmp/peritial.dump" "$dir/peritial.dump"
docker cp "$src:/tmp/peritial.dump.sha256" "$dir/peritial.dump.sha256"
docker cp "$dir/peritial.dump" "$dst:/tmp/peritial.dump"
docker cp "$dir/peritial.dump.sha256" "$dst:/tmp/peritial.dump.sha256"

# Role names are cluster-wide and intentionally never stored in pg_dump.
docker exec -i "$dst" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
CREATE ROLE peritial_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE peritial_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS;
SQL
restore() {
  docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
    -e PERITIAL_RESTORE_FILE="/tmp/${1}" \
    -e PERITIAL_RESTORE_CONFIRM="${2}" "$dst" sh -s < scripts/db/restore.sh
}

# Must fail closed without explicit confirmation.
if restore peritial.dump ""; then
  echo "Unconfirmed restore unexpectedly succeeded" >&2; exit 1
fi
# A tampered archive must be denied before any database modification.
docker exec "$dst" sh -c 'cp /tmp/peritial.dump /tmp/tampered.dump && cp /tmp/peritial.dump.sha256 /tmp/tampered.dump.sha256 && printf x >> /tmp/tampered.dump'
if restore tampered.dump "EMPTY_DATABASE"; then
  echo "Tampered archive unexpectedly restored" >&2; exit 1
fi

restore peritial.dump "EMPTY_DATABASE"

# Full verification: data + durable idempotency/recovery state + journal +
# forced RLS policies + restricted privileges, not only successful pg_restore.
ok="$(docker exec "$dst" psql -X -qAt -U peritial -d peritial -v ON_ERROR_STOP=1 -c "
SELECT
 (SELECT count(*) FROM perizie)=2
 AND (SELECT count(DISTINCT tenant_id) FROM perizie)=2
 AND (SELECT count(*) FROM documents)=1
 AND (SELECT count(*) FROM security_audit)=2
 AND (SELECT count(*) FROM upload_intents)=1
 AND (SELECT count(*) FROM upload_request_keys WHERE status='completed')=1
 AND (SELECT count(*) FROM schema_migrations)=2
 AND (SELECT count(*) FROM pg_class WHERE relname IN
    ('perizie','documents','security_audit','upload_intents','upload_request_keys')
    AND relrowsecurity AND relforcerowsecurity)=5
 AND NOT has_table_privilege('peritial_app','security_audit','SELECT')
 AND NOT has_column_privilege('peritial_recovery','upload_request_keys','subject_digest','SELECT');
")"
if [ "$ok" != "t" ]; then
  echo "Restored data, journal, RLS or grants mismatch" >&2; exit 1
fi
# The app sees no tenant data when its signed tenant context is absent.
visible="$(docker exec "$dst" psql -X -qAt -U peritial -d peritial -v ON_ERROR_STOP=1 \
  -c 'BEGIN; SET LOCAL ROLE peritial_app; SELECT count(*) FROM perizie; ROLLBACK;' )"
if [ "$visible" != "0" ]; then
  echo "Missing-tenant session did not fail closed" >&2; exit 1
fi

# A second restore must be refused and must not modify the original rows.
if restore peritial.dump "EMPTY_DATABASE"; then
  echo "Restore into nonempty database unexpectedly succeeded" >&2; exit 1
fi
echo "PASS: fresh-cluster PostgreSQL dump+checksum+restore and safety guards"
