#!/usr/bin/env bash
# Offline drill against two ACTUALLY independent PostgreSQL and SeaweedFS
# servers. CI only: all credentials are disposable Compose demo credentials.
set -euo pipefail
cd "$(dirname "$0")/../.."
dir="$(mktemp -d)"
cleanup() { rm -rf "$dir"; }
trap cleanup EXIT

# The live API/recovery worker must not mutate DB or S3 during the backup.
docker compose -f docker/compose.yml stop api
docker compose -f docker/compose.yml --profile recovery-test up -d postgres-restore seaweedfs-restore
ready=false
for i in $(seq 1 60); do
  if docker compose -f docker/compose.yml --profile recovery-test exec -T postgres-restore \
    pg_isready -h 127.0.0.1 -U peritial -d peritial >/dev/null 2>&1; then
    ready=true; break
  fi
  sleep 2
done
if [ "$ready" != true ]; then echo "Fresh restore PostgreSQL unavailable" >&2; exit 1; fi

# The PG source has real E2E-created tenants, documents, audit,
# idempotency and recovery history from the full stack tests.
docker compose -f docker/compose.yml exec -T postgres \
  pg_dump -U peritial -d peritial -Fc --no-owner > "$dir/peritial.dump"
(cd "$dir" && sha256sum peritial.dump | cut -d ' ' -f 1 > peritial.dump.sha256)
docker compose -f docker/compose.yml --profile recovery-test cp \
  "$dir/peritial.dump" postgres-restore:/tmp/peritial.dump
docker compose -f docker/compose.yml --profile recovery-test cp \
  "$dir/peritial.dump.sha256" postgres-restore:/tmp/peritial.dump.sha256

# pg_dump contains the ACLs but not cluster-global roles. No app login.
docker compose -f docker/compose.yml --profile recovery-test exec -T postgres-restore \
  psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
CREATE ROLE peritial_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE peritial_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS;
SQL

docker compose -f docker/compose.yml --profile recovery-test exec -T \
  -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_RESTORE_FILE=/tmp/peritial.dump \
  -e PERITIAL_RESTORE_CONFIRM=EMPTY_DATABASE \
  postgres-restore sh -s < scripts/db/restore.sh

# The SDK container is ephemeral: the archive has the real object bytes but
# is not stored in a CI artifact or permanent backup destination.
docker compose -f docker/compose.yml --profile test --profile recovery-test run -T --rm \
  -e PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED \
  -e PERITIAL_CHECKPOINT_S3_BUCKET=peritial \
  -e PERITIAL_CHECKPOINT_S3_ACCESS_KEY=localminio \
  -e PERITIAL_CHECKPOINT_S3_SECRET_KEY=local-only-minio-secret \
  -e PERITIAL_ARCHIVE_DIR=/tmp/peritial-offline-objects \
  tests sh -ec '
    export PERITIAL_CHECKPOINT_POSTGRES="Host=postgres;Port=5432;Database=peritial;Username=peritial;Password=local-only-postgres"
    export PERITIAL_CHECKPOINT_S3_ENDPOINT=http://seaweedfs:8333
    dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- export

    export PERITIAL_CHECKPOINT_POSTGRES="Host=postgres-restore;Port=5432;Database=peritial;Username=peritial;Password=local-only-postgres"
    export PERITIAL_CHECKPOINT_S3_ENDPOINT=http://seaweedfs-restore:8333
    export PERITIAL_RESTORE_OBJECTS_CONFIRM=EMPTY_DESTINATION_BUCKET
    dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects

    # Repeating import must refuse any preexisting byte, not overwrite.
    if dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects; then
      echo "Unexpected overwrite of restored S3 bucket" >&2
      exit 1
    fi
  '

# Verify RLS and durable metadata/audit still exist in the destination PG.
verified="$(docker compose -f docker/compose.yml --profile recovery-test exec -T \
  postgres-restore psql -X -qAt -U peritial -d peritial -v ON_ERROR_STOP=1 -c "
    SELECT (SELECT count(*) FROM documents) >= 2
       AND (SELECT count(*) FROM perizie) >= 2
       AND (SELECT count(*) FROM security_audit WHERE outcome='succeeded') >= 2
       AND (SELECT count(*) FROM pg_class WHERE relname IN
            ('perizie','documents','security_audit','upload_intents','upload_request_keys')
            AND relrowsecurity AND relforcerowsecurity)=5
       AND (SELECT count(*) FROM upload_intents)=0;
  " | tr -d '\r')"
if [ "$verified" != "t" ]; then
  echo "Restored DB metadata, audit, tenant rows or FORCE RLS mismatch" >&2
  exit 1
fi
echo "PASS: actual PostgreSQL pg_dump/pg_restore plus S3 byte export and verified restore to independent servers"
