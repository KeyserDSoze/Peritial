#!/usr/bin/env bash
# Offline drill against two ACTUALLY independent PostgreSQL and SeaweedFS
# servers. CI only: all credentials are disposable Compose demo credentials.
set -euo pipefail
cd "$(dirname "$0")/../.."
# Host tmpfs is bind-mounted into the SDK container. Neither PGDMP nor the
# S3 blob archive nor the decrypted restore stage may touch the runner disk.
dir="$(mktemp -d /dev/shm/peritial-recovery.XXXXXXXX)"
test "$(stat -f -c %T "$dir")" = "tmpfs" || { echo "Scratch must be tmpfs" >&2; exit 1; }
test "$(wc -l </proc/swaps)" -eq 1 || { echo "Host swap must be disabled" >&2; exit 1; }
umask 077
# The isolated SDK container owns the unsealed directories as root with 0700.
# GitHub-hosted Ubuntu runners require sudo to read/remove those private paths.
# Never relax the directory ACLs to make a test pass.
cleanup() { sudo rm -rf -- "$dir"; }
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
# Before restoring ANY PostgreSQL data or writing to the second SeaweedFS,
# seal the complete source PostgreSQL dump plus S3 byte archive with AES-GCM.
# Both keys are generated ephemerally in CI and remain OUTSIDE the encrypted
# bundle. The decrypted data is private and automatically removed at teardown.
docker compose -f docker/compose.yml --profile test --profile recovery-test run -T --rm \
  -v "$dir:/recovery" \
  -e PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED \
  -e PERITIAL_CHECKPOINT_S3_BUCKET=peritial \
  -e PERITIAL_CHECKPOINT_S3_ACCESS_KEY=localminio \
  -e PERITIAL_CHECKPOINT_S3_SECRET_KEY=local-only-minio-secret \
  -e PERITIAL_ARCHIVE_DIR=/recovery/source-objects \
  -e PERITIAL_BUNDLE_POSTGRES_DUMP=/recovery/peritial.dump \
  -e PERITIAL_BUNDLE_DIR=/recovery/sealed \
  -e PERITIAL_BUNDLE_UNSEAL_DIR=/recovery/unsealed \
  tests sh -ec '
    dd if=/dev/urandom of=/recovery/s3-hmac.key bs=32 count=1 status=none
    dd if=/dev/urandom of=/recovery/aes-gcm.key bs=32 count=1 status=none
    chmod 600 /recovery/s3-hmac.key /recovery/aes-gcm.key

    export PERITIAL_CHECKPOINT_POSTGRES="Host=postgres;Port=5432;Database=peritial;Username=peritial;Password=local-only-postgres"
    export PERITIAL_CHECKPOINT_S3_ENDPOINT=http://seaweedfs:8333
    export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/recovery/s3-hmac.key

    # The existing S3 archive also refuses missing/weak signing keys.
    unset PERITIAL_ARCHIVE_HMAC_KEY_FILE
    if dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- export; then
      echo "Unsigned S3 export unexpectedly succeeded" >&2; exit 1
    fi
    export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/recovery/s3-hmac.key
    chmod 644 "$PERITIAL_ARCHIVE_HMAC_KEY_FILE"
    if dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- export; then
      echo "Export accepted group-readable signing key" >&2; exit 1
    fi
    chmod 600 "$PERITIAL_ARCHIVE_HMAC_KEY_FILE"
    dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- export

    export PERITIAL_BUNDLE_CONFIRM=WRITERS_STOPPED
    # The new full-bundle encryption key is separately mandatory/private.
    unset PERITIAL_BUNDLE_AES_KEY_FILE
    if dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal; then
      echo "Unencrypted multi-service backup unexpectedly succeeded" >&2; exit 1
    fi
    export PERITIAL_BUNDLE_AES_KEY_FILE=/recovery/aes-gcm.key
    chmod 644 "$PERITIAL_BUNDLE_AES_KEY_FILE"
    if dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal; then
      echo "Sealer accepted group-readable encryption key" >&2; exit 1
    fi
    chmod 600 "$PERITIAL_BUNDLE_AES_KEY_FILE"
    dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal
    test ! -e /recovery/sealed/postgres/peritial.dump
    test ! -e /recovery/sealed/s3/manifest.json

    export PERITIAL_BUNDLE_CONFIRM=ISOLATED_RESTORE
    dd if=/dev/urandom of=/recovery/wrong-gcm.key bs=32 count=1 status=none
    chmod 600 /recovery/wrong-gcm.key
    export PERITIAL_BUNDLE_AES_KEY_FILE=/recovery/wrong-gcm.key
    if dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- unseal; then
      echo "Sealed recovery accepted wrong AES-GCM key" >&2; exit 1
    fi
    test ! -e /recovery/unsealed
    export PERITIAL_BUNDLE_AES_KEY_FILE=/recovery/aes-gcm.key
    dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- unseal
    cmp /recovery/peritial.dump /recovery/unsealed/postgres/peritial.dump
    cmp /recovery/peritial.dump.sha256 /recovery/unsealed/postgres/peritial.dump.sha256
    test -s /recovery/unsealed/s3/manifest.hmac
    echo "PASS: authenticated AES-256-GCM bundle of PGDMP and S3 objects"
  '

# Only NOW use the independently decrypted PostgreSQL backup to provision
# the new database. The archive bytes are not recovered from source S3 again.
sudo docker compose -f docker/compose.yml --profile recovery-test cp \
  "$dir/unsealed/postgres/peritial.dump" postgres-restore:/tmp/peritial.dump
sudo docker compose -f docker/compose.yml --profile recovery-test cp \
  "$dir/unsealed/postgres/peritial.dump.sha256" postgres-restore:/tmp/peritial.dump.sha256

# pg_dump contains the ACLs but not cluster-global roles. No app login.
docker compose -f docker/compose.yml --profile recovery-test exec -T postgres-restore \
  psql -X -U peritial -d peritial -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
DO $roles$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='peritial_app') THEN
    CREATE ROLE peritial_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='peritial_recovery') THEN
    CREATE ROLE peritial_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS;
  END IF;
END
$roles$;
DO $safety$
BEGIN
  IF (SELECT count(*) FROM pg_roles
      WHERE rolname IN ('peritial_app','peritial_recovery')
        AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole) <> 2 THEN
    RAISE EXCEPTION 'Required roles are missing or overprivileged';
  END IF;
END
$safety$;
SQL

docker compose -f docker/compose.yml --profile recovery-test exec -T \
  -e PGDATABASE=peritial -e PGUSER=peritial \
  -e PERITIAL_RESTORE_FILE=/tmp/peritial.dump \
  -e PERITIAL_RESTORE_CONFIRM=EMPTY_DATABASE \
  postgres-restore sh -s < scripts/db/restore.sh

# The destination S3 bucket must be empty; import only the S3 bytes
# decrypted from the SAME sealed recovery bundle as the restored PGDMP.
docker compose -f docker/compose.yml --profile test --profile recovery-test run -T --rm \
  -v "$dir:/recovery" \
  -e PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED \
  -e PERITIAL_CHECKPOINT_S3_BUCKET=peritial \
  -e PERITIAL_CHECKPOINT_S3_ACCESS_KEY=localminio \
  -e PERITIAL_CHECKPOINT_S3_SECRET_KEY=local-only-minio-secret \
  -e PERITIAL_ARCHIVE_DIR=/recovery/unsealed/s3 \
  -e PERITIAL_ARCHIVE_HMAC_KEY_FILE=/recovery/s3-hmac.key \
  tests sh -ec '
    export PERITIAL_CHECKPOINT_POSTGRES="Host=postgres-restore;Port=5432;Database=peritial;Username=peritial;Password=local-only-postgres"
    export PERITIAL_CHECKPOINT_S3_ENDPOINT=http://seaweedfs-restore:8333
    export PERITIAL_RESTORE_OBJECTS_CONFIRM=EMPTY_DESTINATION_BUCKET
    dd if=/dev/urandom of=/recovery/wrong-hmac.key bs=32 count=1 status=none
    chmod 600 /recovery/wrong-hmac.key
    export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/recovery/wrong-hmac.key
    if dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects; then
      echo "Restore accepted wrong independent S3 HMAC key" >&2; exit 1
    fi
    export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/recovery/s3-hmac.key
    dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects

    if dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects; then
      echo "Unexpected overwrite of restored S3 bucket" >&2; exit 1
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
