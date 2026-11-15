-- DEVELOPMENT ONLY: this file must never be applied in production.
ALTER ROLE peritial_app WITH LOGIN PASSWORD 'local-only-app' NOSUPERUSER NOBYPASSRLS;

-- DEVELOPMENT ONLY: independent worker login with least-privilege metadata access.
ALTER ROLE peritial_recovery WITH LOGIN PASSWORD 'local-only-recovery' NOSUPERUSER NOBYPASSRLS;
