-- V008: remove a legacy DML grant with no corresponding authorized
-- Perizia deletion use case. A direct DELETE could destroy a baseline
-- pre-V006 Perizia association without passing V007 UPDATE guards,
-- and would not create a perizia.delete success audit.
-- Never erase records here: this is a privilege-only forward migration.
REVOKE DELETE ON perizie FROM peritial_app;
REVOKE DELETE ON perizie FROM PUBLIC;

-- A future deletion use case needs a dedicated scope, retention/legal-hold
-- and a tenant-scoped audited repository transaction. Do not re-grant DELETE
-- to the generic application role just to bypass the workflow.
