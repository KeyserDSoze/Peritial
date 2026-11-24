# ADR 0117 — Budget complessivo del bundle e stream remoti bounded

- Data: 24 novembre 2026
- Stato: implementato su main; CI da attestare
- Issue: #6, #27

## Decisione

ADR 0116 limitava solo i blob S3. Aggiungere un cap fisso di 1 GiB
alla somma dei file in chiaro, compreso PGDMP e file tecnici, con
aritmetica overflow-safe. È una soglia conservativa di laboratorio,
non una stima di capacità del deployment. Seal rifiuta il PGDMP
oltre quota prima di hash e controlla la somma prima di leggere
i byte dei blob; Unseal e ValidateEncrypted applicano il limite
all'indice dopo autenticazione e prima della decifratura.

L'inventario di trasferimento remoto ammette fino a 10.006 file e
1 GiB + 64 MiB per index esterno e overhead GCM. I marker e il
transfer.json hanno limiti separati già esistenti. Applicare il
preflight sia prima delle PUT sia dopo HMAC e prima dello staging
di pull-sealed/pull-historic. Riutilizzare il reader bounded di
ADR 0112 per verifica GET post-PUT e download remoto: al massimo
N+1 byte letti, mai scritto il byte eccedente. Conservare controllo
SHA-256, GCM, Object Lock, versioni esplicite e cleanup.

## Test

Casi esatto limite, superamento cumulativo, overflow e count remoto;
indice HMAC valido con PGDMP enorme o somma sopra soglia;
contratto SeaweedFS con ciphertext remoto più lungo o più corto,
nessuna destinazione pubblicata, successivo restore corretto.
I test non richiedono allocazioni da 1 GiB.

## Conseguenze

Nessun cambiamento al formato v1, DB, ACL o death.
Non esiste override in questa tranche. Bundle precedenti oltre
quota non sono ammessi dal nuovo codice; gestire recovery di grandi
set con policy esplicita e approvazione prima di rollout.
Non è un limite allo spazio occupato dai file sorgenti o una
garanzia di spazio libero, RAM, inode o RPO/RTO. Il trasferimento
remoto verifica il MAC dell'inventario prima del payload, ma
continua a verificare l'indice GCM dopo il download.
