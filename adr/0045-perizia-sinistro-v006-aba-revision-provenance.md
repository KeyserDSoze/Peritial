# ADR 0045 — V006: revisione monotona anti-ABA e storia correlata all'audit

- Data: 18 novembre 2026
- Stato: codice implementato; verifica CI/upgrade e policy compliance in corso
- Issue: #23, #7, #9, #12, #13, #21, #6
- Branch: `main` solamente. `death` è documentazione storica intatta.

## Contesto

Gli ADR 0043/0044 proteggono link/unlink/reparent con
CAS su `sinistro_id`: la corrispondenza del solo UUID
non identifica una revisione precisa. Un ciclo A→B→A
consentirebbe a un client con snapshot antico di agire
sulla nuova A. La riga audit esistente include azione
e Perizia ma non source/target, riducendo la ricostruibilità
degli eventi successivi alla migrazione.

## Decisione

1. V006 forward-only introduce
   `perizie.sinistro_link_version bigint NOT NULL DEFAULT 1`,
   con `CHECK >=1`. Questa **baseline** è assegnata a
   tutte le Perizie esistenti, collegate o no, e non
   attesta l'assenza di eventi storici: nessun backfill
   di associazioni precedenti. Migrazioni V001–V005
   e checksum invariati. `SchemaMigrator` registra
   la V006 con checksum, transazione DDL e advisory lock.
2. GET `/api/perizie/{id}/sinistro` e MCP lettura
   restituiscono `{periziaId,sinistroId,linkVersion}`.
   Le mutazioni POST link, PUT reparent e DELETE unlink
   esigono tutte `expectedLinkVersion` positivo in
   aggiunta agli ID già richiesti. Il campo va letto
   prima di confermare ogni operazione; 400/errore MCP
   per payload vecchio privo di versione, 409 per
   mismatch, **anche se il collegamento è tornato allo
   stesso UUID**. Niente fallback che cerchi la
   revisione più recente al posto dell'utente.
3. Ogni mutazione usa un singolo
   `UPDATE perizie SET sinistro_id=<target/null>,
   sinistro_link_version=sinistro_link_version+1
   WHERE tenant_id=@tenant AND id=@perizia AND
   sinistro_id=<expected/IS NULL> AND
   sinistro_link_version=@expectedVersion
   RETURNING sinistro_link_version`.
   Il reparent mantiene il guard SQL per stati
   `Billed,Duplicated,Cancelled`. FORCE RLS e FK
   V004 restano in vigore: target e Perizia foreign
   restano indistinguibili da missing (404).
4. Dopo il CAS, nella **stessa transazione**,
   `security_audit` registra l'esito `succeeded`
   e `perizia_sinistro_history` registra la revisione,
   source/target UUID e `audit_id`. La FK composta
   a `security_audit(tenant_id,id)` impedisce eventi
   senza audit corrispondente; un errore audit/history
   rollbacka l'intera modifica e la revisione.
   Una richiesta conflittuale non genera successo.
   La tabella history ha PK
   `(tenant_id,perizia_id,revision)`, FK alla Perizia,
   policy append-only FORCE RLS e unicamente INSERT
   concesso a `peritial_app`. Recovery non può
   leggere/modificare. Non contiene payload HTTP,
   riferimenti esterni, token, nomi o PII descrittiva.
5. Il picker PWA mantiene `linkVersion` firmato,
   lo trasmette su tutte e tre le mutazioni e lo
   aggiorna solo dopo risposta server; un valore
   mancante, non intero o non positivo disabilita la
   mutazione. Il 409 invalida lo snapshot e richiede
   un GET esplicito senza auto-retry. Il messaggio
   di revisione è solo informativo, non un secondo
   live status che interferisce con il messaggio
   di esito accessibile.
6. Compose demo non applica V006 in automatico.
   La CI disposable applica V003→V004→V005→V006
   e i runbook migrator/adoption/restore registrano
   6 versioni. Il deploy reale richiede maintenance,
   backup e applicazione privilegiata prima
   della nuova API. Il gate hard-delete #22
   **non è modificato**.

## Limiti e accettazione

Il modello consente tracciabilità tecnica **solo
dall'upgrade V006 in poi**. Non sostituisce requisiti
formali di retention/legal hold, accessi ai log, protezione
WORM, archivio firma, verifiche legali o approvazioni
di reparent in fasi sensibili. Il ruolo DDL/amministrativo
deve gestire accesso ai log storici secondo governance
da definire, senza esporli via REST/MCP. I test Business,
PostgreSQL (rollback, concorrenza, FK, ACL), OAuth
REST/MCP e PWA, idempotenza migrazioni e restore
devono passare su `main` prima di chiudere la parte
tecnica; #23 rimane aperta per policy/compliance
e staging upgrade con dati pregressi.
