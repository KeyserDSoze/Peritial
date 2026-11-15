# ADR 0004 — Portabilità Azure PaaS

Stato: accettata, 2026-11-15.

Azure usa Container Apps per API/MCP e PWA, PostgreSQL Flexible Server su subnet privata, Blob Storage con Managed Identity. Il dominio rimane indipendente dall'hosting. Terraform descrive solo l'infrastruttura: immagini, migrazioni, accesso OIDC e hardening sono processi separati.

Conseguenze: provisioning Azure e on-prem possono differire senza cambiare i casi d'uso; non considerare l'app production-ready finché i gate security e deployment non sono automatizzati.
