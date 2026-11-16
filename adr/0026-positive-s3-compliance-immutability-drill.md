# ADR 0026 — Prova positiva WORM COMPLIANCE con server S3 compatibile

Data: 2026-11-16. Stato: job CI indipendente e test di versione immutabile in laboratorio, requisiti #6/#18 ancora aperti.

## Problema e motivazione

L'ADR 0025 richiedeva un bucket versionato con Object Lock COMPLIANCE >=30 giorni, verificando ogni oggetto caricato. Le prove sul server SeaweedFS del demo erano solo **negative** (preflight fallito). Non dimostravano che upload e restore senza bypass funzionassero su un provider effettivamente capace di Object Lock, né che un DELETE per `VersionId` sarebbe stato davvero rifiutato.

## Decisione

1. Aggiungere job separato **`worm-recovery`** al CI di `main`, oltre ai cinque job precedenti. Lo storage testato è **PGSTY SILO**, fork comunitario compatibile S3 con Object Lock, avviato su runner Linux effimero e isolato, con immagine `docker.io/pgsty/silo` fissata a un **digest multiarch** della versione `2026-11-12`. Non cambia lo storage SeaweedFS dell'applicazione. Non installare server non mantenuti su sistemi di produzione. Il primo tentativo con immagine MinIO storica da Quay ha restituito `401 UNAUTHORIZED` ed è stato sostituito con un fork attivamente mantenuto.
2. Un test xUnit integrato `ObjectLockWormIntegrationTests` esegue API S3 reali: crea bucket **con Object Lock alla creazione**, abilita versioning e configura retention predefinita `COMPLIANCE` **32 giorni**. Poi esegue `SealedRecoveryBundle.SealAsync`, `SealedBundleOffsite.UploadAsync` e `DownloadAsync` **senza bypass**, con validazione di ciascun tag GCM, index MAC, `VersionId` e `GetObjectRetention`. Unseal in `tmpfs` privata verifica il PGDMP sintetico.
3. Invia `DeleteObject` con il `VersionId` specifico del marcatore `complete`: il provider **rifiuta** di cancellare la versione COMPLIANCE. Un bucket separato senza Object Lock ma con versioning consente la cancellazione per `VersionId`: è il **controllo positivo** che evita di confondere una DELETE malformata con una vera immutabilità. Dopo il tentativo negato la versione originale mantiene la retention.
4. Invia un DELETE **senza** VersionId: il server può creare un delete marker sulla key corrente. Verifica che `GetObject` per VersionId della versione immutabile funzioni ancora e che `DownloadAsync` ordinario **rifiuti** il prefisso non completo nello stato corrente, senza pubblicare plaintext o bundle parziali.

## Limiti aperti

Questa prova conferma il comportamento WORM di uno **storage compatibile S3** sul runner effimero, **non** una certificazione AWS S3 reale, alcuna distribuzione geografica, retention legale, immutabilità dell'intera infrastruttura o KMS. Il set di prova usa PGDMP sintetico e archivio S3 autenticato vuoto; il drill multi-tenant PostgreSQL + 4 blob reali continua nel job Compose con SeaweedFS dimostrativo senza Object Lock. L'API attuale `pull-sealed` usa solo le versioni **correnti**: un marker può nasconderle, pur essendo le versioni anteriori intatte e accessibili per ID. Occorre un comando **esplicito** di recovery storico che scelga versioni coerenti/autenticate, non una selezione automatica silenziosa. Inoltre non sono implementate policy IAM senza permessi di DELETE, recovery da versioni nascoste completo, KMS, offsite/retention automatici, backup Keycloak/Azure o snapshot coordinato DB+S3. #6 resta aperta.
