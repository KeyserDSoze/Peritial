# ADR 0021 — Autenticazione HMAC-SHA256 del bundle documentale S3

Data: 2026-11-15. Stato: implementazione e prova CI; DR completo resta aperto (#6/#18/#21).

## Contesto

ADR 0020 ha introdotto export/import reale di byte SeaweedFS in un archive locale. La sola SHA-256 di ogni blob non autentica il manifest: un avversario con accesso in scrittura all'archivio potrebbe cambiare contemporaneamente key, identificativi, hash e byte, lasciando checksum apparentemente validi. Il dump PostgreSQL e gli oggetti S3 non hanno uno snapshot transazionale condiviso.

## Decisione

- Ogni `ObjectRecoveryArchive.ExportAsync` richiede una chiave esterna **binaria di 32 byte**, segreta, casuale, non inclusa nell'archivio. Il CLI la legge esclusivamente da `PERITIAL_ARCHIVE_HMAC_KEY_FILE`, un percorso assoluto verso file regolare owner-only (`0600` o più restrittivo su Unix, senza symlink); non la stampa, non la passa su command line e ne azzera il buffer dopo l'operazione. `capture` e `verify` rimangono comandi di checkpoint non autenticati.
- Scrivere `manifest.hmac` (32 byte) in 0600, accanto a `manifest.json`, sotto la directory temporanea 0700. Calcolo HMAC-SHA256 con prefisso di dominio `Peritial.S3Archive.Manifest.HMACSHA256.v1\\n` e i byte JSON raw del manifest. Il MAC copre tutta la lista documenti ordinata, chiavi S3, hash/length e Content-Type. I blob non sono firmati direttamente ma sono vincolati dagli hash nel manifest autenticato.
- `ImportAsync` e `ReadAndValidateAsync` rifiutano chiave assente/corta, sidecar mancante o alterato, JSON alterato, directory incomplete; verificano il MAC in constant-time **prima di parsare JSON, leggere dati tenant o eseguire qualsiasi PUT**. Dopo, restano obbligatori hash di ogni blob, metadata PostgreSQL completo, bucket vuoto e PUT `If-None-Match: *`.
- Test TDD su SeaweedFS+PostgreSQL reali, tra due server: chiave diversa, sidecar mancante/manomesso, manifest falsificato, byte alterati a pari lunghezza. Il drill CI genera una chiave CSPRNG all'interno del container effimero, prova che senza chiave o con permessi troppo ampi non è possibile esportare, prova che una chiave diversa non permette import, quindi restaura il set di documenti.

## Limiti e rischi

HMAC simmetrico **non è** una firma asimmetrica con non ripudio, non cifra byte né manifest ed è falsificabile da chi possiede la chiave. La chiave va tenuta e ruotata in KMS con accesso separato e backup; la sua perdita impedisce il restore. Il MAC copre l'archive S3, **non** il dump PostgreSQL, né dimostra che siano stati generati nello stesso istante. Rimangono aperti cifratura/firma dell'intero bundle multi-servizio, immutabilità e retention offsite, catalogo di versioni/orfani/multipart S3, Azure, Keycloak, restore e failover produttivo. Non chiudere #6/#18/#21 per questa sola tranche.
