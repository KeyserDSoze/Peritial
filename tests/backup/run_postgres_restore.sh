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
BEGIN;
INSERT INTO perizie(tenant_id,id,reference,status,created_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','BACKUP-ONE',-1,now()),
 ('22222222-2222-2222-2222-222222222222','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','BACKUP-TWO',-1,now());
-- A V003 case must survive PG dump/restore with its tenant and CAS version.
INSERT INTO sinistri(tenant_id,id,external_reference,occurred_on,reported_on,created_at_utc,version)
 VALUES('11111111-1111-1111-1111-111111111111','12121212-1212-1212-1212-121212121212',
 'RESTORE-SINISTRO','2026-11-01','2026-11-03',now(),4);
-- V004: the restored perizia must retain the SAME-tenant link.
UPDATE perizie SET sinistro_id='12121212-1212-1212-1212-121212121212',
  sinistro_link_version=2
 WHERE tenant_id='11111111-1111-1111-1111-111111111111'
   AND id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc)
 VALUES('11111111-1111-1111-1111-111111111111','cccccccc-cccc-cccc-cccc-cccccccccccc',
 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','fixture.pdf','application/pdf',8,
 '11111111/cccccccc.pdf',now());
INSERT INTO security_audit(tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','dddddddd-dddd-dddd-dddd-dddddddddddd','test-actor','document.ingest','succeeded','cccccccc-cccc-cccc-cccc-cccccccccccc',now()),
 ('22222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','test-actor','perizia.create','denied',NULL,now()),
 ('11111111-1111-1111-1111-111111111111','a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1','test-actor','perizia.link-sinistro','succeeded','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',now());
-- V006: a committed version-2 association history must survive the same
-- backup/restore and retain a valid same-tenant append-only audit FK.
INSERT INTO perizia_sinistro_history
 (tenant_id,perizia_id,revision,previous_sinistro_id,new_sinistro_id,audit_id)
 VALUES ('11111111-1111-1111-1111-111111111111',
 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',2,NULL,
 '12121212-1212-1212-1212-121212121212',
 'a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1');
INSERT INTO upload_request_keys(tenant_id,subject_digest,key_digest,request_digest,document_id,status,completed_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111',decode(repeat('aa',32),'hex'),decode(repeat('bb',32),'hex'),
 decode(repeat('cc',32),'hex'),'cccccccc-cccc-cccc-cccc-cccccccccccc','completed',now());
INSERT INTO upload_intents(tenant_id,document_id,storage_key) VALUES
 ('22222222-2222-2222-2222-222222222222','ffffffff-ffff-ffff-ffff-ffffffffffff','22222222/pending.pdf');
-- V007 checks provenance when the transaction commits, never at the
-- intermediate UPDATE statement before audit/history are inserted.
COMMIT;
SQL

# Execute scripts inside pinned PG17 CLI/server image; archive is copied
# between separate clusters, not merely two databases on one server.
docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_BACKUP_FILE=/tmp/peritial.dump "$src" sh -s < scripts/db/backup.sh
docker cp "$src:/tmp/peritial.dump" "$dir/peritial.dump"
docker cp "$src:/tmp/peritial.dump.sha256" "$dir/peritial.dump.sha256"
# Refuse accidentally overwriting the previous recovery point.
if docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_BACKUP_FILE=/tmp/peritial.dump "$src" sh -s < scripts/db/backup.sh; then
  echo "Backup overwrote an existing archive" >&2; exit 1
fi
docker cp "$dir/peritial.dump" "$dst:/tmp/peritial.dump"
docker cp "$dir/peritial.dump.sha256" "$dst:/tmp/peritial.dump.sha256"

restore() {
  docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
    -e PERITIAL_RESTORE_FILE="/tmp/${1}" \
    -e PERITIAL_RESTORE_CONFIRM="${2}" "$dst" sh -s < scripts/db/restore.sh
}
# A new PostgreSQL cluster has no application roles. Refuse an incomplete restore
# before any DDL to avoid missing or incorrect grants/RLS policies.
if restore peritial.dump "EMPTY_DATABASE"; then
  echo "Restore without least-privilege roles unexpectedly succeeded" >&2; exit 1
fi

# Role names are cluster-wide and intentionally never stored in pg_dump.
docker exec -i "$dst" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
CREATE ROLE peritial_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE peritial_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS;
SQL

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
 AND (SELECT count(*) FROM sinistri
      WHERE tenant_id='11111111-1111-1111-1111-111111111111'
        AND id='12121212-1212-1212-1212-121212121212'
        AND external_reference='RESTORE-SINISTRO'
        AND occurred_on='2026-11-01'
        AND reported_on='2026-11-03' AND version=4)=1
 AND (SELECT count(*) FROM perizie
      WHERE tenant_id='11111111-1111-1111-1111-111111111111'
        AND id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
        AND sinistro_id='12121212-1212-1212-1212-121212121212'
        AND sinistro_link_version=2)=1
 AND (SELECT count(*) FROM perizie
      WHERE tenant_id='22222222-2222-2222-2222-222222222222'
        AND sinistro_id IS NULL)=1
 AND (SELECT count(*) FROM security_audit)=3
 AND (SELECT count(*) FROM perizia_sinistro_history
      WHERE tenant_id='11111111-1111-1111-1111-111111111111'
      AND perizia_id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
      AND revision=2
      AND previous_sinistro_id IS NULL
      AND new_sinistro_id='12121212-1212-1212-1212-121212121212'
      AND audit_id='a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1')=1
 AND (SELECT count(*) FROM upload_intents)=1
 AND (SELECT count(*) FROM upload_request_keys WHERE status='completed')=1
 AND (SELECT count(*) FROM schema_migrations)=11
 AND to_regclass('ix_documents_tenant_created_id') IS NOT NULL
 AND to_regclass('ix_documents_tenant_perizia_created_id') IS NOT NULL
 AND to_regclass('legacy_xml_import_keys') IS NOT NULL
 AND (SELECT count(*) FROM pg_class WHERE relname IN
    ('perizie','documents','security_audit','upload_intents','upload_request_keys',
     'sinistri','perizia_sinistro_history')
    AND relrowsecurity AND relforcerowsecurity)=7
 AND NOT has_table_privilege('peritial_app','perizie','DELETE')
 AND NOT has_table_privilege('peritial_recovery','perizie','DELETE')
 AND has_table_privilege('peritial_app','perizie','UPDATE')
 AND has_table_privilege('peritial_app','sinistri','DELETE')
 AND has_table_privilege('peritial_app','perizia_sinistro_history','INSERT')
 AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','SELECT')
 AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','UPDATE')
 AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','DELETE')
 AND NOT has_table_privilege('peritial_recovery','perizia_sinistro_history','SELECT')
 AND NOT has_table_privilege('peritial_recovery','sinistri','DELETE')
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
