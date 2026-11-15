# ADR 0005 — Provider S3 on-prem

Stato: accettata — 2026-11-15.

Il runtime on-prem passa a SeaweedFS, sistema open-source con API S3 e immagini pubbliche disponibili; l'adapter .NET continua a usare l'SDK S3 e contratti Business indipendenti dal prodotto. Il nome interno dell'adapter MinioObjectStore sarà generalizzato a S3ObjectStore nel refactoring successivo. MinIO non è richiesto.

Motivazione: il pull di minio/minio dal registry Docker Hub è fallito nella prima CI; evitare dipendenze da registry non più disponibile. Per produzione: pin digest verificato, backup e replica storage.
