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
