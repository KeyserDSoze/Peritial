-- V003: first persistent Sinistro slice. Forward-only and additive.
-- DateOnly facts are stored as PostgreSQL DATE; legacy datetime/timezone
-- migration is deliberately excluded until characterization (#7).
-- No premature uniqueness constraint on external_reference: historical
-- YearlyId / insurer duplicate semantics still need real data study.
CREATE TABLE sinistri (
    tenant_id uuid NOT NULL,
    id uuid NOT NULL,
    external_reference varchar(100) NOT NULL
        CHECK (length(btrim(external_reference)) > 0),
    occurred_on date NOT NULL,
    reported_on date NULL,
    created_at_utc timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (tenant_id, id)
);

CREATE INDEX ix_sinistri_tenant_occurred
    ON sinistri (tenant_id, occurred_on DESC, id);
CREATE INDEX ix_sinistri_tenant_reference
    ON sinistri (tenant_id, external_reference);

-- Do not allow unscoped readers or the recovery worker to inspect cases.
-- DELETE is reserved for a future audited domain use case.
REVOKE ALL ON sinistri FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON sinistri TO peritial_app;
ALTER TABLE sinistri ENABLE ROW LEVEL SECURITY;
ALTER TABLE sinistri FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_sinistri ON sinistri
    FOR ALL TO peritial_app
    USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);
