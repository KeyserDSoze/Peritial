-- V014: reject all NEW document rows without an admitted SHA-256 digest,
-- including raw SQL writers bypassing the application repository.
-- The V013 CHECK already enforces exactly 32 bytes when the digest exists.
--
-- NOT VALID is deliberate: V001–V013 historical records may have NULL.
-- PostgreSQL enforces this CHECK on every INSERT and UPDATE from now on,
-- without scanning, changing, deleting or inventing checksums for old rows.
-- Do not VALIDATE this constraint while legacy NULL records remain.
-- This is transactional, forward-only and journal-checksummed by migrator.
ALTER TABLE documents
    ADD CONSTRAINT ck_documents_sha256_required_new
    CHECK (content_sha256 IS NOT NULL) NOT VALID;

-- Keep FORCE RLS, existing app INSERT/SELECT-only permissions,
-- V013 digest immutability trigger and V009 revoked UPDATE/DELETE.
-- Validating/restoring legacy blobs requires separate approved provenance,
-- scan, storage integrity and an explicitly designed backfill path (#27).
