# ADR 0112 — Limite di lettura S3 per checkpoint e archivio offline

- Data: 23 novembre 2026
- Stato: implementato su `main`; attestazione Compose del nuovo HEAD ancora richiesta
- Issue: #6 (backup/restore), #27 (integrità dei byte documentali), #21 (recovery)
- `death`: solo archivio storico, nessun commit

## Problema

Dal journal V013/V014 il documento ha una dimensione persistita
`documents.length_bytes` e un digest SHA-256 privato dei byte
ammessi. `ObjectRecoveryCheckpoint` rilegge il blob da SeaweedFS
per verificare il digest, mentre `ObjectRecoveryArchive.ExportAsync`
trasferisce i byte in una directory privata `tmpfs` prima di
sigillare il bundle.

I reader di S3 usavano finora il metodo `ReadAsync(buffer)`
fino a EOF e soltanto **alla fine** confrontavano la lunghezza con
PostgreSQL/manifest. Un oggetto sovrascritto con payload arbitrariamente
lungo, oppure uno stream malevolo senza EOF, poteva quindi consumare
tempo di rete, CPU e spazio `tmpfs` prima del rifiuto. Un mismatch
finale non è sufficiente come limite di risorse.

## Decisione

Legare ogni lettura al numero di byte già attestato nei metadati
di PostgreSQL o nel manifest previamente verificato:

- `ObjectRecoveryCheckpoint.CaptureAsync` passa
  `row.LengthBytes` al reader e `VerifyAsync` usa la dimensione
  dello stesso documento nel manifest, verificata prima via DB.
- `ObjectRecoveryArchive.ExportAsync` passa
  `item.LengthBytes` al copiatore S3→`tmpfs`; `ImportAsync`
  usa `saved.LengthBytes` nel controllo dei file di archive prima
  di effettuare qualsiasi S3 PUT.
- Ogni loop legge al massimo `min(64 KiB, byteRimanenti+1)`:
  quell'unico byte oltre la lunghezza ammessa rileva il payload
  eccedente senza proseguire il download o la copia. Troncamenti,
  digest diversi e dimensioni discordanti restano fail-closed.
- Rimangono i confronti SHA-256 con il digest persistito in
  V013/V014 e, per gli archive, il controllo HMAC della
  manifest con chiave segregata. Non fidarsi dell'ETag S3.
- Nessuna modifica al modello dei DTO, al journal migrazioni,
  a FORCE RLS, alla validazione dei file originali né ai diritti
  sui bucket.

## Prove

- [Test SeaweedFS reale f92da0e](https://github.com/KeyserDSoze/Peritial/commit/f92da0e7bb1e3ff6b68b08a397b06dfaa50e6178):
  upload di un blob molto più grande della dimensione registrata,
  pur mantenendo i byte PDF in testa, checkpoint rifiutato; dopo
  il ripristino dell'oggetto originale, il capture torna valido.
- [Fix checkpoint 6565a7b](https://github.com/KeyserDSoze/Peritial/commit/6565a7be00fd7c674f900311b6930db572dec3da):
  limite streaming e rifiuto esplicito di eccesso/troncamento.
- [Fix archive a6c3c66](https://github.com/KeyserDSoze/Peritial/commit/a6c3c66d61c2164a60e9fe09b6c251c132679cbc):
  stesso budget su export e verifica pre-import.
- Backend unit+integration sul commit test:
  [CI #45223346566](https://github.com/KeyserDSoze/Peritial/actions/runs/45223346566)
  **512/512 PASS** (23 Domain, 283 Business, 206 Infrastructure);
  le prove S3 reali avvengono nel job Compose e non sono
  implicitamente coperte da questo risultato backend.
- CI full-stack del nuovo HEAD: **verificare lo stato terminale**,
  non dichiarare 9/9 dai soli job indipendenti.

## Limiti

Il bound per-oggetto non rappresenta un budget cumulativo di
tutti gli oggetti, non fornisce rate limit, timeout globale,
snapshot atomico fra PostgreSQL e S3, retention o firma
offsite. Il checkpoint di un record legacy con digest NULL
rimane soltanto una fotografia di quei byte, non una prova
dell'ammissione storica. Prima del recovery produttivo servono
finestra con writer arrestati, capacity planning per il set
intero, protezione dell'archive, restore su nuova destinazione
e test di rollback/failover Azure/on-prem. #6 e #27 restano aperte.
