# ADR 0119 — Preflight capacità effettiva tmpfs del recovery

- Data: 24 novembre 2026
- Stato: implementato su main; CI da attestare
- Issue: #6, #27

## Decisione

Le quote ADR 0113–0118 sono limiti logici e non garantiscono spazio
libero. Riutilizzare il resolver del mount Linux più specifico,
verificato tmpfs privato swap-free, e leggere AvailableFreeSpace
sul mount effettivo tramite DriveInfo. Per ogni file positivo
calcolare ceil(length / Environment.SystemPageSize) con aritmetica
senza overflow, sottraendo le pagine dal budget disponibile.
Nessuna scrittura o allocazione di prova.

Unseal controlla le lunghezze dell'indice HMAC autenticato e già
validato prima di leggere i ciphertext o creare staging. Export
controlla tutti i blob più almeno una pagina per manifest e HMAC
dopo il checkpoint, prima della seconda serie di GET; una seconda
verifica usa la lunghezza effettiva del JSON prima di scriverlo.
Il primo passaggio checkpoint continua a leggere S3 per verificare
i digest; non scrive plaintext. Restano invariati i limiti logici,
formato v1, ACL, HMAC/GCM e SHA-256.

## Test

Arrotondamento di file piccoli, soglie, overflow e valori invalidi;
test mount reale su /dev/shm con domanda impossibile senza allocare
byte; unseal con indice ri-autenticato oltre capacità del mount
quando la dimensione consente il caso, senza staging pubblicato.
Controprova di restore ordinario già presente.

## Limiti

Controllo puntuale, non prenotazione di spazio. Non misura inode,
directory metadata, cgroup/RAM, quote specifiche o consumo concorrente.
Non controlla ancora capacità dello staging cifrato o del dump
PostgreSQL prima di pg_dump. L'operatore deve mantenere isolamento,
swap disattivato e headroom. Nessuna modifica a death.
