# FreshClam con firme persistenti — laboratorio pre-rilascio (#5)

Il file [`docker/compose.clamav-updates.yml`](../docker/compose.clamav-updates.yml)
è un **overlay opt-in di laboratorio** sul Compose locale, NON un manifesto di
produzione. Il Compose base conserva `CLAMAV_NO_FRESHCLAMD=true` per non
dipendere dai mirror pubblici durante la CI; l'overlay abilita il servizio
FreshClam dell'immagine ufficiale `clamav/clamav:1.5`, imposta
`FRESHCLAM_CHECKS=12` (12 controlli al giorno, uno ogni due ore), `TZ=UTC` e un volume Docker
nominato `clamav-signatures` montato scrivibile in `/var/lib/clamav`.
La porta 3310 di clamd **non viene pubblicata**. Si applica inoltre
`Malware__Clamd__MaxSignatureAgeHours=48` all'API, anche in Development,
abilitando il controllo fail-closed di `zVERSION` prima della scansione
e nella readiness. La healthcheck del container esegue soltanto PING:
**non dimostra che le firme siano aggiornate**.

## Avvio e verifica con server di aggiornamento reali

Questo test è **manuale e opt-in**: richiede rete in uscita verso gli endpoint
ClamAV, sufficiente RAM per caricare l'intero database e tempo per ricevere
le firme. Non eseguirlo contro bucket/database contenenti dati veri.

```sh
# Dalla root del repository, solo con la demo locale:
docker compose -f docker/compose.yml up --build -d
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml config --quiet

# Ricrea i due servizi con la nuova configurazione, senza cancellare i volumi.
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml \
  up -d --no-deps --force-recreate clamav
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml \
  up -d --no-deps --force-recreate api

# Confermare che il daemon è pronto, che FreshClam è stato avviato,
# la data del database effettivamente caricato e l'esito della readiness.
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml \
  exec -T clamav clamdscan --ping 1
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml \
  exec -T clamav clamdscan --version
docker compose -f docker/compose.yml -f docker/compose.clamav-updates.yml \
  logs --tail=80 clamav
curl -i --max-time 6 http://localhost:8080/health/ready
```

**Accettazione operativa:** verificare nei log che FreshClam esegua davvero
un controllo/aggiornamento, che clamd carichi il database (osservare
`clamdscan --version` prima e dopo un aggiornamento effettivo) e che
`/health/ready` restituisca 200 soltanto con il database caricato entro le
48 ore. Quando il server remoto è indisponibile per abbastanza tempo, la
data delle firme caricate supera la soglia e readiness passa a 503; gli
upload danno 503 prima di INSTREAM. Un esito 200 non garantisce che
l'ultimo contatto remoto sia riuscito: `zVERSION` espone la data del
database **caricato**, non l'ora dell'ultima verifica FreshClam. Senza
una nuova pubblicazione delle firme, una verifica di FreshClam riuscita
non comporta necessariamente un cambiamento di versione; non dichiarare
per questo un reload dimostrato.

Per tornare alla demo offline, ricreare i servizi **solo con il Compose base**:

```sh
docker compose -f docker/compose.yml up -d --no-deps --force-recreate clamav
docker compose -f docker/compose.yml up -d --no-deps --force-recreate api
```

Non usare `down -v` né eliminare il volume `clamav-signatures` per
riavviare lo scanner: perderesti la cache persistita delle firme.

## Sicurezza e limiti prima di un deployment reale

- Il Compose base contiene credenziali dimostrative, porte HTTP esposte per
  API/Keycloak/PostgreSQL/S3 e `ASPNETCORE_ENVIRONMENT=Development`; **non
  distribuire neppure il merge con l'overlay in produzione**.
- In produzione collocare clamd (porta TCP 3310 senza TLS/auth) soltanto in
  rete privata protetta e monitorare aggiornamenti FreshClam, DNS/egress,
  rate-limit del mirror, spazio su disco, memoria, reload e fallimenti.
  Conservare una sola istanza FreshClam *writer* per database condiviso e
  verificare che ogni replica clamd carichi la nuova firma.
- Il database di firme persistente **non è quarantena** né object storage dei
  documenti. Le policy di aggiornamento/reload, lock sui volumi, backup e
  monitoraggio della rete variano per Azure e on-prem e richiedono prove su
  infrastruttura vera. Non allargare la soglia per aggirare un guasto.
- Il gate CI `clamav-signatures` in
  `tests/security/test_clamav_update_overlay.py` valida soltanto il
  **merge Docker Compose** e i requisiti configurativi; non contatta
  FreshClam né certifica un download/reload reale. I contratti TCP
  `ClamdSignatureFreshnessTests`, `ClamdScannerContractTests` e
  `ServiceReadinessProbeTests` verificano separatamente rifiuti
  di database vecchi, ma usano uno scanner controllato per questi scenari.

Riferimento upstream:
[ClamAV — official Docker image](https://docs.clamav.net/manual/Installing/Docker.html)
(daemon FreshClam avviato per default, persistenza in `/var/lib/clamav`,
self-check e reload clamd).

La issue **#5** resta aperta fino a prove di aggiornamento/reload reali,
quarantena durevole, rate limiting, download autorizzati con audit e
prove di deploy Azure/on-prem; la **#20** continua a tracciare l'impostazione
di `main` come branch predefinito di GitHub.
