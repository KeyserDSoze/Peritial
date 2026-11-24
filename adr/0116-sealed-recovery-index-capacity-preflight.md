# ADR 0116 — Preflight dell'indice cifrato e directory bounded

- Data: 24 novembre 2026
- Stato: implementazione e test su main, CI da attestare
- Issue: #6, #27

## Problema

ADR 0113 e 0114 hanno introdotto i limiti cumulativi per l'archivio S3.
Unseal chiamava il validatore dell'archivio interno dopo avere decifrato
tutti i file. Un indice esterno autenticato ma eccedente i limiti interni
poteva consumare tmpfs inutilmente prima di essere rifiutato. Anche il
controllo delle directory cifrate usava Count() su tutte le entry.

## Decisione

Dopo HMAC e prima di leggere i payload o creare staging, ValidateEntries
controlla i percorsi canonici e le lunghezze dei quattro file tecnici
(PGDMP >= 5 byte, sidecar SHA-256 64–128 byte, manifest S3 1–50.000.000
byte e HMAC 32 byte). I soli blob S3 sono soggetti allo stesso budget
default già imposto da ReadAndValidateAsync: 10.000 oggetti, 512 MiB
complessivi, lunghezze positive e somma overflow-safe.
Seal usa lo stesso validatore prima della cifratura, Unseal e
ValidateEncryptedAsync lo usano dopo autenticazione. Root e payload
usano il conteggio streaming N+1 di ADR 0115.

Nessuna modifica al formato v1, agli algoritmi, alla persistenza o a death.

## Verifica

Estendere il contratto xUnit con root/payload eccedenti, ciphertext
mancante, indice ri-autenticato con blob sopra quota, blob zero,
sidecar HMAC di 33 byte e manifest sopra 50 MB. Entrambe le operazioni
rifiutano prima della pubblicazione; la verifica dei casi di budget
controlla anche che non esista staging residuo. Ripristinare l'indice
originale e verificare il bundle valido, poi proseguire con il restore.

## Limiti

Il PGDMP non è compreso nel budget S3. Serve ancora un budget
complessivo di capacità per il bundle, con policy esplicita e prove
su ambienti reali. I limiti default sono invariati: eventuali override
del CLI checkpoint non sono propagati al bundle sigillato. Restano
necessari isolamento, tmpfs privato, writer arrestati e policy offsite.
