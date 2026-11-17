-- V004 (forward-only): optional tenant-verified Sinistro <-> Perizia relationship.
-- A Sinistro may have many Perizie; a Perizia is initially unlinked and can
-- acquire one Sinistro. Reassignment/unlink are deliberately separate workflows.
-- Never rewrite V001/V002/V003 or import legacy IDs here.
ALTER TABLE perizie
    ADD COLUMN sinistro_id uuid NULL;

ALTER TABLE perizie
    ADD CONSTRAINT fk_perizie_sinistro_same_tenant
    FOREIGN KEY (tenant_id, sinistro_id)
    REFERENCES sinistri (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE RESTRICT;

-- Supports the tenant-scoped read path; avoids indexing unlinked historical
-- perizie. Existing FORCE RLS and privileges on perizie remain unchanged.
CREATE INDEX ix_perizie_tenant_sinistro
    ON perizie (tenant_id, sinistro_id, id)
    WHERE sinistro_id IS NOT NULL;
