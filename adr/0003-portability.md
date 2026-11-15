# ADR 0003 — Portabilità

Stato: accettata — 2026-11-15

Decisione: runtime container ASP.NET Core + React; PostgreSQL come database logico comune, object storage via porta Business e adapter Azure Blob/SeaweedFS (S3). Azure Container Apps, PostgreSQL Flexible Server, Blob Storage; on-prem Docker/Compose, PostgreSQL, SeaweedFS (S3) e Keycloak.

Motivazioni: stessi contratti applicativi e script di test per entrambe le destinazioni. Evitare dipendenze da feature SQL Server.
Conseguenze: il layer Infrastructures varia per provider, non il dominio; Terraform e script on-prem devono essere revisionati separatamente.
