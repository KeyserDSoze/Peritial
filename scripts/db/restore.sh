#!/bin/sh
# Restore a verified Peritial PostgreSQL logical archive into a NEW empty DB.
# Does NOT restore cluster roles, SeaweedFS/Azure Blob contents or Keycloak.
set -eu
die() { echo "Restore refused: $1" >&2; exit 1; }
[ "${PERITIAL_RESTORE_CONFIRM:-}" = "EMPTY_DATABASE" ] ||
  die "explicit PERITIAL_RESTORE_CONFIRM=EMPTY_DATABASE required"
[ -n "${PGDATABASE:-}" ] || die "PGDATABASE is required"
case "$PGDATABASE" in postgres|template0|template1) die "system database cannot be a target";; esac
archive="${PERITIAL_RESTORE_FILE:-}"
[ -n "$archive" ] || die "PERITIAL_RESTORE_FILE is required"
case "$archive" in /*) ;; *) die "restore archive must be an absolute path";; esac
[ -f "$archive" ] && [ -s "$archive" ] && [ -f "$archive.sha256" ] ||
  die "archive/checksum missing or empty"
command -v pg_restore >/dev/null 2>&1 || die "pg_restore is not installed"
command -v psql >/dev/null 2>&1 || die "psql is not installed"
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is not installed"
expected="$(cat "$archive.sha256")"
case "$expected" in
  *[!0123456789abcdef]*|"") die "invalid checksum format";;
esac
[ "${#expected}" -eq 64 ] || die "invalid checksum length"
actual="$(sha256sum "$archive" | cut -d ' ' -f 1)"
[ "$expected" = "$actual" ] || die "archive checksum mismatch"
pg_restore --list "$archive" >/dev/null || die "invalid custom archive"

# Fail closed on any preexisting non-system relation or custom schema. A
# committed versioned schema, even with no tenant rows, is NEVER a safe target.
empty="$(psql --no-password -X -qAt -v ON_ERROR_STOP=1 \
  --dbname="$PGDATABASE" -c "
SELECT NOT EXISTS (
  SELECT 1 FROM pg_class c
  JOIN pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname NOT IN ('pg_catalog','information_schema')
    AND n.nspname NOT LIKE 'pg_toast%'
    AND c.relkind IN ('r','p','v','m','f','S')
) AND NOT EXISTS (
  SELECT 1 FROM pg_namespace
  WHERE nspname NOT IN ('pg_catalog','information_schema','public')
    AND nspname NOT LIKE 'pg_toast%'
);
")" || die "cannot inspect target database"
[ "$empty" = "t" ] || die "target database is not empty"

# Roles are cluster-wide, not part of pg_dump, and must have been independently
# provisioned with restricted privileges before restoring ACLs and RLS policies.
roles="$(psql --no-password -X -qAt -v ON_ERROR_STOP=1 \
  --dbname="$PGDATABASE" -c "
SELECT count(*)=2 FROM pg_roles
WHERE rolname IN ('peritial_app','peritial_recovery')
AND NOT rolsuper AND NOT rolbypassrls;
")" || die "cannot inspect target roles"
[ "$roles" = "t" ] || die "required least-privilege PostgreSQL roles missing"

# One transaction on the destination; on any SQL error restore leaves the
# empty database unchanged. No --clean, --create, --no-acl, or data overwrite.
pg_restore --no-password --no-owner --exit-on-error --single-transaction \
  --dbname="$PGDATABASE" "$archive" ||
  die "pg_restore failed; no schema/data committed"
echo "PostgreSQL restore committed; verify RLS and data before switching traffic."
