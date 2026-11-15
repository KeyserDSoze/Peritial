-- PostgreSQL 17+, repeatable bootstrap for initial environment only.
-- For production use controlled versioned migrations and a restricted app DB role.
CREATE TABLE IF NOT EXISTS perizie (
    tenant_id uuid NOT NULL,
    id uuid NOT NULL,
    reference varchar(160) NOT NULL,
    status integer NOT NULL,
    assigned_to text NULL,
    created_at_utc timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    CONSTRAINT uq_perizie_reference UNIQUE (tenant_id, reference),
    CONSTRAINT ck_perizie_status CHECK (status BETWEEN -1 AND 7)
);
CREATE INDEX IF NOT EXISTS ix_perizie_tenant_status ON perizie (tenant_id, status);

CREATE TABLE IF NOT EXISTS documents (
    tenant_id uuid NOT NULL,
    id uuid NOT NULL,
    perizia_id uuid NULL,
    file_name varchar(255) NOT NULL,
    content_type varchar(128) NOT NULL,
    length_bytes bigint NOT NULL CHECK (length_bytes > 0),
    storage_key text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, perizia_id) REFERENCES perizie (tenant_id, id),
    UNIQUE (tenant_id, storage_key)
);
CREATE INDEX IF NOT EXISTS ix_documents_perizia ON documents (tenant_id, perizia_id);


-- The application role is separate from the privileged schema/migration owner.
-- Production must provision its credentials securely; never store them here.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peritial_app') THEN
    CREATE ROLE peritial_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
END $$;
GRANT USAGE ON SCHEMA public TO peritial_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON perizie, documents TO peritial_app;

ALTER TABLE perizie ENABLE ROW LEVEL SECURITY;
ALTER TABLE perizie FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_perizie ON perizie;
CREATE POLICY tenant_isolation_perizie ON perizie
  FOR ALL TO peritial_app
  USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);

ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_documents ON documents;
CREATE POLICY tenant_isolation_documents ON documents
  FOR ALL TO peritial_app
  USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);


-- Append-only security audit: no grant for SELECT/UPDATE/DELETE to the app role.
-- Only the privileged migrator can query audit records for retention/investigation.
CREATE TABLE IF NOT EXISTS security_audit (
    tenant_id uuid NOT NULL,
    id uuid NOT NULL,
    subject varchar(256) NOT NULL CHECK (length(subject) > 0),
    action varchar(64) NOT NULL CHECK (length(action) > 0),
    outcome varchar(24) NOT NULL CHECK (outcome IN ('succeeded','denied')),
    resource_id uuid NULL,
    occurred_at_utc timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX IF NOT EXISTS ix_security_audit_tenant_time
    ON security_audit (tenant_id, occurred_at_utc DESC);
REVOKE ALL ON security_audit FROM PUBLIC;
GRANT INSERT ON security_audit TO peritial_app;
ALTER TABLE security_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_audit FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_append_audit ON security_audit;
CREATE POLICY tenant_append_audit ON security_audit FOR INSERT TO peritial_app
    WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);


-- Durable upload intent written before object-store PUT, removed transactionally
-- with documents+audit. No file contents, filename, access tokens, or PII.
CREATE TABLE IF NOT EXISTS upload_intents (
    tenant_id uuid NOT NULL,
    document_id uuid NOT NULL,
    storage_key text NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    lease_expires_at_utc timestamptz NOT NULL DEFAULT (now() + interval '2 minutes'),
    PRIMARY KEY (tenant_id, document_id),
    UNIQUE (tenant_id, storage_key)
);
CREATE INDEX IF NOT EXISTS ix_upload_intents_created ON upload_intents (created_at_utc);
-- Re-runnable bootstrap for an existing local database with older intents.
ALTER TABLE upload_intents ADD COLUMN IF NOT EXISTS lease_expires_at_utc
    timestamptz NOT NULL DEFAULT (now() + interval '2 minutes');
CREATE INDEX IF NOT EXISTS ix_upload_intents_lease
    ON upload_intents (created_at_utc, lease_expires_at_utc);


-- Dedicated, non-BYPASSRLS worker: globally sees only pending upload keys and
-- the minimum document columns needed to decide whether deletion is safe.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peritial_recovery') THEN
    CREATE ROLE peritial_recovery NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
END $$;
GRANT USAGE ON SCHEMA public TO peritial_recovery;
REVOKE ALL ON upload_intents FROM PUBLIC;
GRANT SELECT, INSERT, DELETE ON upload_intents TO peritial_app;
GRANT SELECT, UPDATE, DELETE ON upload_intents TO peritial_recovery;
GRANT SELECT (tenant_id, storage_key) ON documents TO peritial_recovery;

ALTER TABLE upload_intents ENABLE ROW LEVEL SECURITY;
ALTER TABLE upload_intents FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_upload_intents ON upload_intents;
CREATE POLICY tenant_upload_intents ON upload_intents FOR ALL TO peritial_app
    USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);
DROP POLICY IF EXISTS recovery_intents_select ON upload_intents;
CREATE POLICY recovery_intents_select ON upload_intents FOR SELECT TO peritial_recovery USING (true);
DROP POLICY IF EXISTS recovery_intents_update ON upload_intents;
CREATE POLICY recovery_intents_update ON upload_intents FOR UPDATE TO peritial_recovery
    USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS recovery_intents_delete ON upload_intents;
CREATE POLICY recovery_intents_delete ON upload_intents FOR DELETE TO peritial_recovery USING (true);
DROP POLICY IF EXISTS recovery_document_keys ON documents;
CREATE POLICY recovery_document_keys ON documents FOR SELECT TO peritial_recovery USING (true);


-- Tenant/actor-scoped idempotency. Store only SHA-256 digests of caller subject,
-- client idempotency key and request fingerprint; never raw key, filename or body.
-- Completed claims remain until an explicit, policy-controlled retention job.
CREATE TABLE IF NOT EXISTS upload_request_keys (
    tenant_id uuid NOT NULL,
    subject_digest bytea NOT NULL CHECK (octet_length(subject_digest) = 32),
    key_digest bytea NOT NULL CHECK (octet_length(key_digest) = 32),
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    document_id uuid NOT NULL,
    status varchar(16) NOT NULL CHECK (status IN ('pending','completed')),
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    completed_at_utc timestamptz NULL,
    PRIMARY KEY (tenant_id,subject_digest,key_digest),
    UNIQUE (tenant_id,document_id)
);
CREATE INDEX IF NOT EXISTS ix_upload_requests_document ON upload_request_keys (tenant_id,document_id);
REVOKE ALL ON upload_request_keys FROM PUBLIC;
GRANT SELECT, INSERT, DELETE ON upload_request_keys TO peritial_app;
GRANT UPDATE (status,completed_at_utc) ON upload_request_keys TO peritial_app;
-- Recovery needs only tenant, document id and status, not digests of actor/key/body.
GRANT SELECT (tenant_id,document_id,status) ON upload_request_keys TO peritial_recovery;
GRANT DELETE ON upload_request_keys TO peritial_recovery;
GRANT UPDATE (status,completed_at_utc) ON upload_request_keys TO peritial_recovery;
ALTER TABLE upload_request_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE upload_request_keys FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_upload_requests ON upload_request_keys;
CREATE POLICY tenant_upload_requests ON upload_request_keys FOR ALL TO peritial_app
    USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);
DROP POLICY IF EXISTS recovery_requests_select ON upload_request_keys;
CREATE POLICY recovery_requests_select ON upload_request_keys FOR SELECT TO peritial_recovery USING (true);
DROP POLICY IF EXISTS recovery_requests_update ON upload_request_keys;
CREATE POLICY recovery_requests_update ON upload_request_keys FOR UPDATE TO peritial_recovery
    USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS recovery_requests_delete ON upload_request_keys;
CREATE POLICY recovery_requests_delete ON upload_request_keys FOR DELETE TO peritial_recovery USING (true);
