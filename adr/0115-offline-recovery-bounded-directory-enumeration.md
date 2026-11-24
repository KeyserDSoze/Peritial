# ADR 0115 — Enumerazione bounded delle directory di restore S3 offline

- Data: 24 novembre 2026
- Stato: implementazione e regressioni su main; CI da attestare
- Issue: #6, correlata #27

## Contesto

ADR 0114 limita il numero di documenti nel manifest autenticato e nell'inventario SQL.
Il restore materializzava però tutte le entry delle directory locali con ToArray(),
incluse quelle non previste dal manifest. Un manifest valido non autentica la
popolazione della directory sul filesystem. Un surplus di file o directory
poteva quindi amplificare memoria e lavoro prima del rifiuto.

## Decisione

- Radice: enumerare al massimo quattro entry, conservando la verifica ordinale
  dei tre nomi obbligatori.
- Directory objects: verificare in streaming il numero esatto di entry,
  interrompendosi alla prima eccedente (N+1). Nessuna lista di filename.
  Il numero atteso è già ammesso dal budget ADR 0114.
- Conservare verifica HMAC, presenza e lunghezza dei singoli blob, rifiuto dei
  link, verifica PostgreSQL e digest di tutti i byte prima dei PUT.
- Nessuna variazione al formato archivio, schema DB, ACL o API REST/MCP.

## Verifica e limiti

Test unitari su directory vuota, conteggi esatti, mancanti ed eccedenti;
enumeratore sentinella che fallisce se letto oltre N+1, anche al cap di 100000.
Contratto Compose PostgreSQL/SeaweedFS: manifest HMAC invariato con entry
root estranea, entry object estranea e blob mancante; bucket di destinazione
ancora vuoto e successivo restore regolare.

Queste guardie non rendono sicuro un filesystem modificato concorrentemente:
restano obbligatori staging privato tmpfs swap-free, writer arrestati,
isolamento operativo, capacità e prove Azure/on-prem. Non sono un limite
globale del tempo I/O del filesystem. Nessun intervento su death.
