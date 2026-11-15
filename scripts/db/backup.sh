#!/bin/sh
# Peritial PostgreSQL logical backup (PG17+).
# Run with a read-capable backup role via PGHOST/PGPORT/PGUSER/PGPASSWORD/
# PGDATABASE or a secure pgpass service. Never store connection strings in Git.
set -eu
umask 077
die() { echo "Backup refused: $1" >&2; exit 1; }
[ -n "${PGDATABASE:-}" ] || die "PGDATABASE is required"
destination="${PERITIAL_BACKUP_FILE:-}"
[ -n "$destination" ] || die "PERITIAL_BACKUP_FILE is required"
case "$destination" in /*) ;; *) die "backup destination must be an absolute path";; esac
[ ! -e "$destination" ] && [ ! -e "$destination.sha256" ] ||
  die "destination or checksum already exists; choose a new unique backup name"
command -v pg_dump >/dev/null 2>&1 || die "pg_dump is not installed"
command -v pg_restore >/dev/null 2>&1 || die "pg_restore is not installed"
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is not installed"
partial="$(mktemp "${destination}.part.XXXXXXXX")" || die "cannot create temporary dump"
hash_partial="$(mktemp "${destination}.sha256.part.XXXXXXXX")" || {
  rm -f "$partial"; die "cannot create temporary digest"
}
cleanup() { rm -f "$partial" "$hash_partial"; }
trap cleanup EXIT HUP INT TERM

# One consistent PostgreSQL snapshot including schema, journal, audit and RLS
# metadata. ACLs ARE included (no --no-acl). Ownership is assigned to the
# intended DDL administrator on restore; cluster roles must exist separately.
if ! pg_dump --no-password --dbname="$PGDATABASE" --format=custom \
  --no-owner --file="$partial"; then
  die "pg_dump failed; partial archive discarded"
fi
if ! pg_restore --list "$partial" >/dev/null; then
  die "archive validation failed; partial archive discarded"
fi
digest="$(sha256sum "$partial" | cut -d ' ' -f 1)"
printf '%s\n' "$digest" > "$hash_partial"
# Never silently overwrite a previous point-in-time recovery checkpoint.
[ ! -e "$destination" ] && [ ! -e "$destination.sha256" ] ||
  die "destination appeared during backup"
mv "$partial" "$destination"
mv "$hash_partial" "$destination.sha256"
echo "Backup created (PostgreSQL custom archive + SHA-256)."
