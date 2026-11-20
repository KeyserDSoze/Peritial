-- V012: global SQL guard for the references of already imported Sinistri.
-- This is NOT a blanket uniqueness rule for legacy case references.
-- Preserve legitimate historical duplicates when no import ledger exists.
-- Backed-up maintenance migration; operators must stop all writers first.
--
-- Lock DML while validating preexisting imported-reference collisions.
-- This migration must fail closed, not silently bless a V011 race.
LOCK TABLE sinistri IN SHARE ROW EXCLUSIVE MODE;
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM legacy_xml_import_keys k
        JOIN sinistri imported
          ON imported.tenant_id=k.tenant_id AND imported.id=k.sinistro_id
        JOIN sinistri other_case
          ON other_case.tenant_id=imported.tenant_id
         AND other_case.external_reference=imported.external_reference
         AND other_case.id<>imported.id
    ) THEN
        RAISE EXCEPTION 'Imported Sinistro reference collision requires manual reconciliation';
    END IF;
END;
$$;

-- SECURITY INVOKER (the default): peritial_app must keep least-privilege
-- SELECT on legacy_xml_import_keys. No bypass-RLS or SECURITY DEFINER here.
CREATE FUNCTION guard_imported_sinistro_reference()
RETURNS trigger LANGUAGE plpgsql AS $guard$
BEGIN
    -- All INSERT/UPDATE paths take the same per-tenant/reference mutex,
    -- including raw SQL using the least-privileged application role.
    -- hashtextextended is a transient PostgreSQL advisory-lock hash, not
    -- a persisted identity; different refs may collide (only serialization).
    PERFORM pg_advisory_xact_lock(
        hashtextextended(NEW.tenant_id::text || chr(10) || NEW.external_reference, 0)
    );

    -- Imported rows are immutable until source-aware reconciliation/retention
    -- exists. The ledger FK already prevents hard deletion.
    IF TG_OP = 'UPDATE' AND EXISTS (
        SELECT 1 FROM legacy_xml_import_keys k
        WHERE k.tenant_id=OLD.tenant_id AND k.sinistro_id=OLD.id
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE='23505',
            CONSTRAINT='ck_imported_sinistro_reference_guard',
            MESSAGE='Imported Sinistro must not be modified.';
    END IF;

    -- Blocks a direct ordinary case INSERT or UPDATE targeting a reference
    -- already owned by a durable XML import. An import's OWN deferred ledger
    -- has no joined Sinistro until its INSERT, so its first INSERT is allowed.
    IF EXISTS (
        SELECT 1
        FROM legacy_xml_import_keys k
        JOIN sinistri imported
          ON imported.tenant_id=k.tenant_id AND imported.id=k.sinistro_id
        WHERE k.tenant_id=NEW.tenant_id
          AND imported.external_reference=NEW.external_reference
          AND imported.id<>NEW.id
        LIMIT 1
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE='23505',
            CONSTRAINT='ck_imported_sinistro_reference_guard',
            MESSAGE='Imported Sinistro reference is reserved.';
    END IF;
    RETURN NEW;
END;
$guard$;

REVOKE ALL ON FUNCTION guard_imported_sinistro_reference() FROM PUBLIC;
-- The trigger executes under the invoker, not via a privileged override.
GRANT EXECUTE ON FUNCTION guard_imported_sinistro_reference() TO peritial_app;
CREATE TRIGGER trg_guard_imported_sinistro_reference
    BEFORE INSERT OR UPDATE ON sinistri
    FOR EACH ROW EXECUTE FUNCTION guard_imported_sinistro_reference();
