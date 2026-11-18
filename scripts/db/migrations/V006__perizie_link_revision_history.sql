-- V006: forward-only monotone Perizia/Sinistro link revision + append-only
-- technical provenance. Existing rows receive revision 1 as a baseline:
-- V001-V005 associations predating this migration cannot be reconstructed.
-- No historical migration or existing link is rewritten.
ALTER TABLE perizie
  ADD COLUMN sinistro_link_version bigint NOT NULL DEFAULT 1;
ALTER TABLE perizie ADD CONSTRAINT ck_perizie_link_version_positive
  CHECK (sinistro_link_version >= 1);

CREATE TABLE perizia_sinistro_history (
    tenant_id uuid NOT NULL,
    perizia_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision > 1),
    previous_sinistro_id uuid NULL,
    new_sinistro_id uuid NULL,
    audit_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, perizia_id, revision),
    CONSTRAINT fk_link_history_perizia FOREIGN KEY (tenant_id,perizia_id)
        REFERENCES perizie(tenant_id,id) ON DELETE RESTRICT,
    CONSTRAINT fk_link_history_audit FOREIGN KEY (tenant_id,audit_id)
        REFERENCES security_audit(tenant_id,id) ON DELETE RESTRICT,
    CONSTRAINT ck_link_history_changed CHECK (
        previous_sinistro_id IS DISTINCT FROM new_sinistro_id)
);

-- The API can INSERT provenance only; no SELECT, UPDATE or DELETE.
-- The privileged migration/recovery runbook controls archival retention.
REVOKE ALL ON perizia_sinistro_history FROM PUBLIC;
GRANT INSERT ON perizia_sinistro_history TO peritial_app;
ALTER TABLE perizia_sinistro_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE perizia_sinistro_history FORCE ROW LEVEL SECURITY;
CREATE POLICY append_link_history ON perizia_sinistro_history
  FOR INSERT TO peritial_app
  WITH CHECK (tenant_id = NULLIF(current_setting('peritial.tenant_id', true), '')::uuid);
