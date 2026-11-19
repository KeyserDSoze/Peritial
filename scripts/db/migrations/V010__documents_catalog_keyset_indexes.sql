-- V010: ordered B-tree paths for the read-only document catalog.
-- These cover global tenant pages and tenant+Perizia filtered pages
-- by the exact DESC timestamp/UUID keyset order.
-- Forward-only DDL; no DML or mutation grants.
-- Plain CREATE INDEX runs transactionally under the DDL migrator's journal
-- lock, NOT CONCURRENTLY. On populated installations it may block writes:
-- run only under the reviewed maintenance/backup/recovery procedure.
-- No IF NOT EXISTS: a conflicting/preexisting name fails closed and must
-- be investigated instead of silently certifying the wrong index.
CREATE INDEX ix_documents_tenant_created_id
    ON documents (tenant_id, created_at_utc DESC, id DESC);
CREATE INDEX ix_documents_tenant_perizia_created_id
    ON documents (tenant_id, perizia_id, created_at_utc DESC, id DESC);

-- Keep legacy ix_documents_perizia for compatibility with existing queries.
-- RLS, all metadata, audit, storage bytes and role privileges are unchanged.
