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
