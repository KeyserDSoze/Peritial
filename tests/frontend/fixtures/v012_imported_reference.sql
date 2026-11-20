-- Synthetic CI-only provenance row. NEVER apply to a persistent database.
-- The real import writer is internal; this allows testing signed REST/MCP
-- attempts against a genuine V011 ledger-backed case in a disposable schema.
-- Only signed tenant A (the Keycloak local test realm) may read this case.
-- Use psql --single-transaction; does not belong in schema_migrations.
INSERT INTO sinistri
    (tenant_id, id, external_reference, occurred_on, reported_on,
     created_at_utc, version)
VALUES (
    '11111111-1111-1111-1111-111111111111'::uuid,
    'a12b0000-0000-4000-8000-000000000012'::uuid,
    'E2E-V012-IMPORTED-GUARD',
    DATE '2026-11-20', NULL, now(), 1
);
INSERT INTO legacy_xml_import_keys
    (tenant_id, subject_digest, key_digest, request_digest, sinistro_id)
VALUES (
    '11111111-1111-1111-1111-111111111111'::uuid,
    decode(repeat('7a', 32),'hex'),
    decode(repeat('8b', 32),'hex'),
    decode(repeat('9c', 32),'hex'),
    'a12b0000-0000-4000-8000-000000000012'::uuid
);
INSERT INTO security_audit
    (tenant_id, id, subject, action, outcome, resource_id, occurred_at_utc)
VALUES (
    '11111111-1111-1111-1111-111111111111'::uuid,
    'a12b0000-0000-4000-8000-000000000013'::uuid,
    'ci-fixture',
    'sinistro.import-xml', 'succeeded',
    'a12b0000-0000-4000-8000-000000000012'::uuid,
    now()
);
