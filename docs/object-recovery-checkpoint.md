# Checkpoint offline DB ↔ SeaweedFS S3 — integrità, **non backup** (#6/#21)

**Stato:** verificatore read-only S3/PostgreSQL testato su servizi reali. Nessun export dei contenuti, nessun ripristino di oggetti S3/Azure Blob. Non eseguire su un ambiente con API, MCP o worker in scrittura. Manifest e credenziali contengono dati riservati; non condividerli né committarli.

## A cosa serve

Il comando `capture` costruisce un inventario ordinato dei documenti referenziati da `documents` in PostgreSQL, per **tutti i tenant**. Per ogni documento legge l'oggetto SeaweedFS S3, verifica che la dimensione reale sia uguale a `documents.length_bytes` e ne calcola SHA-256. Il manifest JSON versione 1 registra tenant UUID, documento UUID, chiave S3, dimensione e checksum. Se manca un blob, la dimensione differisce, una chiave globale è riutilizzata, l'utente DB può essere filtrato da FORCE RLS o esiste un upload/una chiave idempotente ancora in stato pending, l'operazione si interrompe.

`verify` confronta un manifest precedente con **tutti** i riferimenti del database destinazione e rilegge ogni blob S3 per confrontare dimensioni e SHA-256. Rifiuta differenze di cardinalità, tenant/ID/chiave/metadati, bytes alterati anche a dimensione invariata e manifest versionati diversamente. Il numero di documenti validati è l'unica informazione stampata: mai nomi, tenant, chiavi o dati.

**Importante:** non c'è una transazione comune tra S3 e PostgreSQL. Il lock `SHARE NOWAIT` su `documents` e i registri di upload limita corse durante l'inventario DB, ma è rilasciato prima della lettura dei blob. La conferma `WRITERS_STOPPED` **non rileva da sola** processi ancora attivi: l'operatore deve fermarli realmente. Il manifest non contiene i byte degli oggetti, non li salva e non prova l'autenticità del dump o la consistenza temporale con il bucket.

## Prerequisiti operatore

- .NET 10 e accesso al repository `main` alla stessa release del DB; PostgreSQL 17+ e bucket S3 SeaweedFS compatibile. Per Azure Blob serve un implementazione distinta.
- Finestra di manutenzione: **fermare ogni writer** REST/MCP, worker di recovery, job schedulati e processi che scrivono al bucket; verificare che non ci siano operazioni in volo. Conservare separatamente il backup PostgreSQL `pg_dump -Fc` e **una copia durabile, cifrata e verificabile di tutti gli oggetti del bucket** a un punto concordato.
- Credenziale PostgreSQL con SELECT su `documents`, `upload_intents` e `upload_request_keys`, LOCK su quelle tabelle e lettura di tutti i tenant senza filtro FORCE RLS (ruolo audit dedicato `BYPASSRLS` e senza DML, creato dall'amministratore e disabilitato dopo la manutenzione; in CI si usa il superuser demo). **Non** usare il ruolo applicativo `peritial_app`. Credenziale S3 di **sola lettura** per il bucket/oggetti.
- Directory del manifest privata e già esistente; il comando crea il file temporaneo con permessi **0600 fin dall'apertura** su Unix, poi usa rename senza overwrite. Non inserirlo in Git, nei log o nel bucket pubblico. Sono presenti chiavi e identificativi riservati.

Impostare nel processo operatore (secret manager o ambiente protetto):

```bash
export PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED
export PERITIAL_CHECKPOINT_POSTGRES='(connection string audit PostgreSQL)'
export PERITIAL_CHECKPOINT_S3_ENDPOINT='https://s3.example.invalid'
export PERITIAL_CHECKPOINT_S3_BUCKET='(bucket dei documenti)'
export PERITIAL_CHECKPOINT_S3_ACCESS_KEY='(secret manager)'
export PERITIAL_CHECKPOINT_S3_SECRET_KEY='(secret manager)'
export PERITIAL_CHECKPOINT_FILE=/secure/backups/peritial-documents-checkpoint.json
```

Durante la manutenzione, **dopo** aver fermato i writer e coordinato la copia dei blob e `pg_dump`:

```bash
dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- capture
```

Su un nuovo ambiente di prova, **dopo** avere ripristinato autonomamente il database e **tutti** gli oggetti nella destinazione S3, configurare le variabili per i servizi destinazione e conservare il manifest originale in posizione privata. Il file non deve essere modificabile da processi non autorizzati.

```bash
dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- verify
```

Solo dopo esito positivo controllare anche i record di audit, versioni schema, RLS/grants, storage ACL, ruoli Keycloak, code/documenti non referenziati e consentire ripartenza dei writer secondo il change plan. Oggetti orfani fuori da `documents` non vengono enumerati: richiedono inventario integrale del bucket e processo di riconciliazione separato. Il checksum non rileva una sostituzione malevola simultanea di oggetto e manifest: occorrono firma, immutabilità e archiviazione offsite.

## Evidenze di test e limiti

La suite `ObjectRecoveryCheckpointContractTests` usa PostgreSQL + SeaweedFS reali in Compose, con schema DB e chiavi S3 isolati, e verifica:
- manifest generato per **due tenant distinti** e validazione dei byte originali;
- oggetto mancante rifiutato;
- checksum diverso **a dimensione identica** rifiutato;
- documento rimosso dal database rispetto al manifest rifiutato;
- upload intent e chiave di idempotenza pendenti rifiutati;
- riutilizzo della stessa chiave S3 tra tenant rifiutato;
- credenziali applicative filtrate da FORCE RLS rifiutate;
- versione manifest sconosciuta rifiutata.

La CI backend compila anche il CLI ma non esegue un export di byte S3. Mancano: snapshot/restore realmente coordinati PostgreSQL+SeaweedFS/Azure, copie dei blob, test S3→S3 di migrazione, autenticazione del manifest, ripristino Keycloak, backup offsite, retention e recovery con failover. Per questi motivi #6/#18/#21 **restano aperte**.
