-- V002: accelerate tenant-scoped audit investigations without exposing audit data to the app role.
-- This is a forward-only, additive index. Rollback requires a reviewed restore.
CREATE INDEX ix_security_audit_tenant_action_time
    ON security_audit (tenant_id, action, occurred_at_utc DESC);
