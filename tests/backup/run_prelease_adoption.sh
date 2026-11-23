#!/usr/bin/env bash
# TDD/CI drill: adopt the narrowly recognized historical pre-lease variant using a real PG17
# backup. Never runs against any external/prod database or persistent volume.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="peritial-prelease-adopt-${GITHUB_RUN_ID:-$$}"
dir="$(mktemp -d)"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$dir"
}
trap cleanup EXIT

docker run -d --name "$name" -p 127.0.0.1:55434:5432 \
  -e POSTGRES_USER=peritial -e POSTGRES_PASSWORD=local-only-postgres \
  -e POSTGRES_DB=peritial postgres:17-alpine >/dev/null
ready=false
for i in $(seq 1 60); do
  if docker exec "$name" pg_isready -h 127.0.0.1 -U peritial -d peritial >/dev/null 2>&1; then
    ready=true; break
  fi
  sleep 1
done
if [ "$ready" != true ]; then echo "Isolated PostgreSQL not ready" >&2; exit 1; fi

docker exec -i "$name" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 \
  < scripts/db/001_init.sql >/dev/null
docker exec -i "$name" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 \
  < scripts/db/002_dev_app_role.sql >/dev/null
# Simulate precisely the supported unversioned historical shape: missing
# lease_expires_at_utc and its dependent index/column grant. No other drift.
docker exec "$name" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 \
  -c 'ALTER TABLE upload_intents DROP COLUMN lease_expires_at_utc' >/dev/null
# Two tenant records and one persistent audited document: the V002 upgrade
# must preserve every row and its FK, while setting only journal and index.
docker exec -i "$name" psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
INSERT INTO perizie(tenant_id,id,reference,status,created_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','ADOPT-ONE',-1,now()),
 ('22222222-2222-2222-2222-222222222222','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','ADOPT-TWO',-1,now());
INSERT INTO documents(tenant_id,id,perizia_id,file_name,content_type,length_bytes,storage_key,created_at_utc)
 VALUES('11111111-1111-1111-1111-111111111111','cccccccc-cccc-cccc-cccc-cccccccccccc',
 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','legacy.pdf','application/pdf',8,
 '11111111/cccccccc.pdf',now());
INSERT INTO security_audit(tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) VALUES
 ('11111111-1111-1111-1111-111111111111','dddddddd-dddd-dddd-dddd-dddddddddddd','actor-legacy',
 'document.ingest','succeeded','cccccccc-cccc-cccc-cccc-cccccccccccc',now());
INSERT INTO upload_intents(tenant_id,document_id,storage_key) VALUES
 ('22222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','22222222/legacy-pending.pdf');
SQL

# The PostgreSQL-only archive is a prerequisite, not a substitute for the
# missing application object-store snapshots.
docker exec -i -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_BACKUP_FILE=/tmp/legacy-v001.dump "$name" sh -s < scripts/db/backup.sh
docker cp "$name:/tmp/legacy-v001.dump" "$dir/legacy-v001.dump"
docker cp "$name:/tmp/legacy-v001.dump.sha256" "$dir/legacy-v001.dump.sha256"

export PERITIAL_MIGRATOR_POSTGRES="Host=127.0.0.1;Port=55434;Database=peritial;Username=peritial;Password=local-only-postgres"
export PERITIAL_LEGACY_BACKUP_FILE="$dir/legacy-v001.dump"
export PERITIAL_LEGACY_CONFIRM="OFFLINE_BACKUP_VERIFIED"

# Ordinary apply remains fail-closed for untracked legacy installations.
if dotnet run --project scripts/db/Peritial.DbMigrator -- apply; then
  echo "Untracked legacy database was automatically migrated" >&2; exit 1
fi
# The original strict V001 command must not silently repair a known variant.
if dotnet run --project scripts/db/Peritial.DbMigrator -- adopt; then
  echo "Strict V001 adoption accepted pre-lease schema" >&2; exit 1
fi
# Refuse absent explicit operator consent, even when the archive is present.
unset PERITIAL_LEGACY_CONFIRM
if dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease; then
  echo "Adoption without consent was allowed" >&2; exit 1
fi
export PERITIAL_LEGACY_CONFIRM="OFFLINE_BACKUP_VERIFIED"
# Even valid PostgreSQL custom archives are rejected if their manifest does
# not match. The original proof must remain unchanged.
cp "$dir/legacy-v001.dump.sha256" "$dir/digest.good"
printf '0000000000000000000000000000000000000000000000000000000000000000\n' \
  > "$dir/legacy-v001.dump.sha256"
if dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease; then
  echo "Adoption with altered backup proof was allowed" >&2; exit 1
fi
mv "$dir/digest.good" "$dir/legacy-v001.dump.sha256"

dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease
dotnet run --project scripts/db/Peritial.DbMigrator -- apply
# Adopting twice is forbidden: a recorded version cannot be silently re-based.
if dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease; then
  echo "A journaled schema was re-adopted" >&2; exit 1
fi

result="$(docker exec "$name" psql -X -qAt -v ON_ERROR_STOP=1 -U peritial -d peritial -c "
 SELECT
   (SELECT count(*) FROM perizie)=2
   AND (SELECT count(DISTINCT tenant_id) FROM perizie)=2
   AND (SELECT count(*) FROM documents WHERE file_name='legacy.pdf')=1
   AND (SELECT count(*) FROM security_audit WHERE action='document.ingest')=1
   AND (SELECT count(*) FROM upload_intents
        WHERE tenant_id='22222222-2222-2222-2222-222222222222'
        AND document_id='eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
        AND storage_key='22222222/legacy-pending.pdf'
        AND lease_expires_at_utc > created_at_utc)=1
   AND to_regclass('ix_upload_intents_lease') IS NOT NULL
   AND (SELECT count(*) FROM schema_migrations)=14
   -- A V001 document has no admitted checksum: migration must not forge one.
   AND (SELECT count(*) FROM documents
        WHERE file_name='legacy.pdf' AND content_sha256 IS NULL)=1
   AND to_regclass('ix_documents_tenant_created_id') IS NOT NULL
   AND to_regclass('ix_documents_tenant_perizia_created_id') IS NOT NULL
   AND to_regclass('legacy_xml_import_keys') IS NOT NULL
   AND to_regprocedure('guard_imported_sinistro_reference()') IS NOT NULL
   AND (SELECT count(*) FROM pg_class
     WHERE relname IN ('perizie','documents','security_audit','upload_intents','upload_request_keys','sinistri')
       AND relrowsecurity AND relforcerowsecurity)=6
   AND to_regclass('ix_security_audit_tenant_action_time') IS NOT NULL;
")"
if [ "$result" != "t" ]; then
  echo "Adoption changed preexisting tenant data, audit or FORCE RLS" >&2; exit 1
fi
echo "PASS: pre-lease variant repaired and adopted atomically with verified backup and tenant data"
