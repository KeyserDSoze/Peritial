-- V013: private, tenant-scoped checksum of ClamAV-admitted document bytes.
-- Nullable only for already-existing documents: never fabricate a digest
-- during migration. Download for a NULL checksum is fail-closed pending an
-- explicitly approved and re-scanned legacy backfill (#27).
-- Forward-only DDL. schema_migrations checksums this entire file.
ALTER TABLE documents
    ADD COLUMN IF NOT EXISTS content_sha256 bytea;

ALTER TABLE documents
    ADD CONSTRAINT ck_documents_content_sha256_length
    CHECK (content_sha256 IS NULL OR octet_length(content_sha256) = 32);

-- Do not allow a later metadata update to rewrite a previously recorded
-- digest. App role already has no UPDATE or DELETE rights (V009);
-- the trigger also protects against accidental owner-side UPDATEs.
CREATE FUNCTION reject_document_digest_rewrite()
RETURNS trigger LANGUAGE plpgsql AS $function$
BEGIN
    IF OLD.content_sha256 IS DISTINCT FROM NEW.content_sha256 THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            CONSTRAINT = 'ck_documents_digest_immutable',
            MESSAGE = 'Document checksum cannot be changed after insertion.';
    END IF;
    RETURN NEW;
END;
$function$;

REVOKE ALL ON FUNCTION reject_document_digest_rewrite() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reject_document_digest_rewrite() TO peritial_app;
CREATE TRIGGER trg_reject_document_digest_rewrite
    BEFORE UPDATE OF content_sha256 ON documents
    FOR EACH ROW EXECUTE FUNCTION reject_document_digest_rewrite();

-- Public metadata DTOs intentionally exclude content_sha256.
-- Recovery role's column-scoped SELECT on documents is unaffected.
