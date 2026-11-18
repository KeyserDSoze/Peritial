-- V005 (forward-only): grant the app role DELETE only in the new, opt-in
-- audited CAS use case. V004's composite FK ON DELETE RESTRICT remains
-- unchanged and forbids removing any Sinistro linked to a Perizia.
-- No records are deleted during this migration. Recovery has no access.
GRANT DELETE ON sinistri TO peritial_app;
