-- DEVELOPMENT ONLY: this file must never be applied in production.
ALTER ROLE peritial_app WITH LOGIN PASSWORD 'local-only-app' NOSUPERUSER NOBYPASSRLS;
