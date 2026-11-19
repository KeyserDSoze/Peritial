-- V009: the application ingests immutable document metadata and reads its catalog.
-- It does not implement an authorized metadata UPDATE or DELETE use case.
-- Legacy V001 grants permit silently changing storage_key/file_name or
-- removing an object reference without the ingestion audit or recovery protocol.
-- Forward-only, privilege-only: no existing rows/objects are touched.
REVOKE UPDATE, DELETE ON documents FROM peritial_app;
REVOKE UPDATE, DELETE ON documents FROM PUBLIC;

-- INSERT and SELECT remain available for tenant-scoped ingestion/catalog.
-- The recovery role's narrow SELECT (tenant_id, storage_key) is unchanged.
-- Future metadata versioning/erasure requires audited tenant-scoped APIs,
-- retention/legal hold, approval and coordinated object-store recovery.
