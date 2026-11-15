> **Parzialmente superata da ADR 0011:** la compensazione sincrona è ora limitata ai PUT falliti (in cui nessun commit metadata è stato avviato). Un esito di commit PostgreSQL ambiguo non autorizza DELETE immediato: la pulizia deve essere affidata al worker con intento persistente.

# ADR 0009 — Compensazione sincrona dello storage durante ingestion

Stato: adottata come misura **best-effort**, non idonea a garantire integrità cross-system dopo crash (2026-11-15).

## Contesto
Il commit di documents e security_audit è atomico sotto PostgreSQL RLS (ADR 0008), ma l'upload oggetto S3/Azure Blob avviene prima della transazione. Errore del PUT dopo invio parziale, commit DB fallito o richiesta HTTP annullata possono lasciare file orfani.

## Decisione
- IObjectStore espone PutAsync e DeleteAsync. MinioObjectStore usa S3 DeleteObjectAsync; AzureBlobObjectStore usa DeleteIfExistsAsync con snapshots inclusi. Cancellare una chiave inesistente non deve essere errore.
- La FileIngestionService genera una chiave UUID+tenant dopo i controlli di input, tenta PUT, poi chiama IDocumentRepository.AddAuditedAsync. Solo quando entrambi hanno successo ritorna un risultato.
- In caso di errore da PUT (anche parziale) o metadata/audit, tenta DeleteAsync sulla chiave generata. Il cleanup usa un token separato con limite temporale di 15 secondi, per proseguire anche se il client si disconnette.
- Se la cancellazione fallisce, segnalare entrambi gli errori in un AggregateException: non occultare la causa principale né asserire un recupero mai avvenuto.
- Test fake dimostrano cleanup su errore storage/DB, failure di cleanup osservabile, gestione cancellazione client e retry dopo compensazione, senza memorizzare payload nell'audit.

## Limiti e seguito obbligatorio
Questa è compensazione sincrona, **non** una transazione distribuita o un protocollo di staging. La morte del processo dopo PUT, la cancellazione fallita senza worker e l'incertezza dell'esito commit PostgreSQL non vengono risolte. Un recovery worker deve distinguere oggetti ancora referenziati da upload pendenti e gestire retention/idempotency key prima di cancellare. Ogni file deve essere privato e sottoposto a scanning/quarantena prima del consumo. Restano aperte #5 e #21, collegate a #6 per migrazioni/backup e #19 per alerting.
