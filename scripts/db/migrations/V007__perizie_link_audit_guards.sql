-- V007: database-level enforcement that V006 link revisions cannot be
-- silently rewritten without a matching successful audit/provenance row.
-- FORWARD ONLY; never rewrite historical V001-V006/checksums or backfill.
--
-- The BEFORE trigger rejects unversioned raw writes immediately. A deferred
-- constraint trigger verifies the exact source/target/revision and matching
-- perizia action in append-only audit/history at COMMIT. This permits the
-- Business port's UPDATE -> audit INSERT -> history INSERT transaction order.
--
-- SECURITY DEFINER is needed because peritial_app has INSERT-only access
-- to the audit/history tables. Only the trusted migration owner creates it.
-- Dynamic identifiers are quoted and derived from TG_TABLE_SCHEMA; this
-- also supports isolated test schemas without trusting caller search_path.
CREATE FUNCTION peritial_guard_sinistro_link_revision()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $guard$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.sinistro_id IS NOT NULL OR NEW.sinistro_link_version <> 1 THEN
            RAISE EXCEPTION 'Association must start unlinked at revision 1'
                USING ERRCODE = 'P0001';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.sinistro_id IS DISTINCT FROM NEW.sinistro_id THEN
        IF NEW.sinistro_link_version <> OLD.sinistro_link_version + 1 THEN
            RAISE EXCEPTION 'Association revision must increase exactly once'
                USING ERRCODE = 'P0001';
        END IF;
    ELSIF NEW.sinistro_link_version IS DISTINCT FROM OLD.sinistro_link_version THEN
        RAISE EXCEPTION 'Association revision cannot change without link change'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$guard$;

CREATE TRIGGER peritial_guard_sinistro_link_revision
BEFORE INSERT OR UPDATE ON perizie
FOR EACH ROW EXECUTE FUNCTION peritial_guard_sinistro_link_revision();

CREATE FUNCTION peritial_verify_sinistro_link_history()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $verify$
DECLARE
    matching_event boolean;
    expected_action text;
BEGIN
    expected_action := CASE
        WHEN OLD.sinistro_id IS NULL THEN 'perizia.link-sinistro'
        WHEN NEW.sinistro_id IS NULL THEN 'perizia.unlink-sinistro'
        ELSE 'perizia.reparent-sinistro'
    END;

    EXECUTE format(
        'SELECT EXISTS (' ||
        ' SELECT 1 FROM %I.perizia_sinistro_history h ' ||
        ' JOIN %I.security_audit a ON a.tenant_id=h.tenant_id AND a.id=h.audit_id ' ||
        ' WHERE h.tenant_id=$1 AND h.perizia_id=$2 AND h.revision=$3 ' ||
        ' AND h.previous_sinistro_id IS NOT DISTINCT FROM $4 ' ||
        ' AND h.new_sinistro_id IS NOT DISTINCT FROM $5 ' ||
        ' AND a.resource_id=$2 AND a.action=$6 AND a.outcome=''succeeded'')',
        TG_TABLE_SCHEMA, TG_TABLE_SCHEMA)
        INTO matching_event
        USING NEW.tenant_id, NEW.id, NEW.sinistro_link_version,
              OLD.sinistro_id, NEW.sinistro_id, expected_action;

    IF NOT matching_event THEN
        RAISE EXCEPTION 'Association change requires committed provenance'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NULL;
END
$verify$;

CREATE CONSTRAINT TRIGGER peritial_link_history_required
AFTER UPDATE ON perizie
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW
WHEN (OLD.sinistro_id IS DISTINCT FROM NEW.sinistro_id)
EXECUTE FUNCTION peritial_verify_sinistro_link_history();

-- Do not expose SECURITY DEFINER trigger helpers to public invocation.
REVOKE ALL ON FUNCTION peritial_guard_sinistro_link_revision() FROM PUBLIC;
REVOKE ALL ON FUNCTION peritial_verify_sinistro_link_history() FROM PUBLIC;
