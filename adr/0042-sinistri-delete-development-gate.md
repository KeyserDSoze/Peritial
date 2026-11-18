# ADR 0042 — Gate fail-closed per hard-delete Sinistri, scope dedicato solo Development

- Data: 18 novembre 2026
- Stato: implementato; verifica CI sul commit finale necessaria
- Issue: #22 (P0), #7, #4, #12, #21
- Branch: solo `main`; `death` rimane storico intatto.

## Problema

L'ADR 0041 ha introdotto DELETE V005 con CAS, RLS, FK RESTRICT e audit
atomico. Il semplice scope `peritial.write` consentiva però
l'hard-delete a ogni client con scrittura anche **senza** una
decisione sulle policy retention, legal hold, doppia approvazione
e ripristino. Togliere il pulsante PWA da solo non è sufficiente:
REST e MCP rimanevano attivi.

## Decisione

1. Il Business `SinistroService.DeleteAsync` richiede
   `peritial.write` **e** `peritial.sinistri.delete`,
   entrambi estratti dal JWT firmato, oltre a un gate
   `SinistroDeletionGate.Enabled`. Le tre verifiche sono
   effettuate **prima di qualunque GET, controllo versione o DELETE**
   per impedire inferenze e data leakage. Per ogni diniego
   si aggiunge `sinistro.delete/denied` nell'audit tenant-scoped
   con `resourceId=null`; nessun successo fittizio.
2. `SinistroDeletionGate.ForEnvironment(isDevelopment,enabled)`
   è una policy immutabile: `Disabled` è il default anche per
   test/unitari e Development; se `enabled=true` fuori
   Development **rifiuta l'avvio**. La singola impostazione
   `Sinistri:EnableDevelopmentDelete` non può pertanto abilitare
   cancellazioni su un deployment non-Development.
   **Non esiste un ramo di codice per abilitarla in produzione**.
3. Realm Keycloak **demo locale**: scope OAuth separato
   `peritial.sinistri.delete` e client
   `peritial-test-delete` con default scopes read/write/delete
   solo per fixture, con claim tenant+subject e audience esistenti.
   Il client `peritial-web` e `peritial-test-write`
   continuano con read/write **senza delete**. La Compose
   effimera Development imposta esplicitamente
   `Sinistri__EnableDevelopmentDelete=true` per esercitare
   la cancellazione V005 nei test. In produzione non copiare
   questi client demo né tale env.
4. REST e MCP invocano lo stesso Business, quindi il gate non è
   aggirabile da un tool o una route erroneamente pubblicati.
   `/api/sinistri/{id}` mantiene `peritial.write` a livello
   endpoint; il servizio applica anche lo scope dedicato.
   `delete_sinistro` segue la stessa regola; OAuth RFC 9728
   documenta lo scope dedicato, senza concederlo automaticamente.
5. Quando **entrambe** le autorizzazioni sono presenti e il gate
   Development è attivo, rimangono obbligatori
   `expectedVersion`, SQL CAS, PostgreSQL FORCE RLS,
   FK V004 RESTRICT e audit atomico dell'ADR 0041.
   Nessun nuovo schema, privilegi SQL o dato distruttivo
   viene introdotto da questa tranche.

## Verifica

Test unitari prima dell'implementazione: policy default-deny
anche in Development e fuori, rifiuto startup per
`enabled=true` non-Development, writer senza scope
dedicato, scoped user con gate spento e audit negato senza UUID;
con gate demo+scope corretto, resta la suite V005 di CRUD
CAS/FK/audit. Playwright OAuth PKCE per client web,
password grant **solo** per client di test isolato con scope
dedicato, denial REST/MCP, successo consentito in Development
e risultati senza leakage.

## Limiti bloccanti #22

**NON è un'autorizzazione di hard-delete produttivo.** Resta da
definire la durata obbligatoria di conservazione, il legal hold,
l'eventuale soft-delete, approvals/segregation of duties,
audit dell'azione autorizzativa, restore e procedure operative
verificate su Azure e on-prem. Fino a quel momento il gate
produttivo **non potrà essere aperto** e la PWA non propone
alcuna cancellazione. Nessuna issue #22/#7 viene chiusa.
