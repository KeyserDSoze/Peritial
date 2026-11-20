-- V011: internal-only case-only XML import idempotency and provenance.
-- Never enable a public commit route before ALL Sinistro writers share a
-- database-enforced uniqueness/concurrency gate. V003 reference is not unique.
-- No existing rows are modified. Keep V001-V010 checksums unchanged.
CREATE TABLE legacy_xml_import_keys (
    tenant_id uuid NOT NULL,
    subject_digest bytea NOT NULL CHECK (octet_length(subject_digest) = 32),
    key_digest bytea NOT NULL CHECK (octet_length(key_digest) = 32),
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    sinistro_id uuid NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, subject_digest, key_digest),
    UNIQUE (tenant_id, sinistro_id),
    -- A provisional ledger row is inserted before the case, in the SAME
    -- transaction; this FK must be checked at COMMIT, after case + audit.
    -- An imported Sinistro is protected from direct hard delete while its
    -- durable idempotency proof exists; retention/erasure needs a new policy.
    CONSTRAINT fk_legacy_import_sinistro FOREIGN KEY (tenant_id, sinistro_id)
        REFERENCES sinistri (tenant_id, id) DEFERRABLE INITIALLY DEFERRED
);

-- Never allow an application-side mutation of a committed idempotency proof.
REVOKE ALL ON legacy_xml_import_keys FROM PUBLIC;
GRANT SELECT, INSERT ON legacy_xml_import_keys TO peritial_app;
ALTER TABLE legacy_xml_import_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE legacy_xml_import_keys FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_legacy_xml_import_keys ON legacy_xml_import_keys
    FOR ALL TO peritial_app
    USING (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);

-- No recovery-worker grant and no raw JWT subject, opaque key, XML or PII.
