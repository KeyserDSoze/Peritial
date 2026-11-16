# Telemetria della readiness (issue #19)

`GET /health/ready` resta un gate **read-only** e pubblico: restituisce HTTP 200 se PostgreSQL, ClamAV, discovery/JWKS OIDC e storage documentale rispondono entro il **budget cumulativo di 3 s**; altrimenti HTTP 503. `GET /health/live` resta distinto. Il risultato non espone dettagli operativi.

## Instrumentazione .NET built-in

La libreria `Peritial.Infrastructures` pubblica **senza ulteriori pacchetti** il Meter e l'ActivitySource chiamati entrambi `Peritial.Readiness`, versione `1.0.0`.

| Strumento | Tipo e unità | Etichette consentite | Significato |
| --- | --- | --- | --- |
| `peritial.readiness.requests` | Counter<long>, `1` | `outcome=ready\|not_ready\|cancelled` | Un incremento per richiesta /health/ready, anche se la prima dipendenza fallisce |
| `peritial.readiness.duration` | Histogram<double>, secondi (`s`) | `outcome` | Durata complessiva della verifica |
| `peritial.readiness.dependency.checks` | Counter<long>, `1` | `dependency=postgres\|clamav\|oidc\|storage`, `outcome=ok\|failed\|cancelled` | Un incremento per ogni controllo **effettivamente eseguito**, non per quelli saltati in seguito a errore |
| `peritial.readiness.dependency.duration` | Histogram<double>, secondi (`s`) | `dependency`, `outcome` | Durata di ogni singolo controllo effettivamente eseguito |

Le Activity `peritial.readiness` e `peritial.readiness.dependency` usano esattamente le medesime etichette a cardinalità limitata. Il tracer registra sempre **solo** gli span di `Peritial.Readiness` quando l'exporter è attivo, anche se l'Activity HTTP genitore non è campionata; ciò evita la perdita silenziosa delle trace di health, ma richiede controllo della frequenza dei probe lato ingress per contenere il volume. Un errore del server o la scadenza del budget sono fail-closed; se il client chiude la richiesta, il token di cancellazione viene propagato e l'esito aggregato è `cancelled`. La cancellazione del token condiviso può includere un timeout nel sotto-controllo, quindi usare i conteggi complessivi `not_ready` per gli alert di disponibilità.

**Privacy:** non sono etichette ammesse tenant, subject, utente, token, email, issuer, hostname, IP, connection string, bucket, document ID, eccezione o messaggio di errore. I nomi dei quattro dependency sono codificati con un enum, non letti da richieste client.

## Esportazione e allarmi

Il codice **produce** le misure tramite API standard `System.Diagnostics.Metrics` e trace tramite `ActivitySource`. L'API include ora un **exporter OTLP/HTTP-Protobuf opzionale**, ma non lo attiva in assenza di opt-in esplicito. Sono registrati **soltanto** Meter e ActivitySource `Peritial.Readiness`, senza exporter di log, body HTTP, utenti o altri provider framework. Il resource attribute `service.name` è `peritial-api`.

### Abilitazione controllata dell'exporter

In un'installazione con collector **privato, autenticato e TLS**, configurare queste variabili da secret manager / orchestratore, non nel repository:

```sh
Observability__Otlp__Enabled=true
Observability__Otlp__Endpoint=https://collector.interno.example:4318/
# Solo se il collector lo richiede, gestito tramite Secret Store:
# OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer%20...
```

L'endpoint è una **base URL**: il client aggiunge esattamente `/v1/metrics` e `/v1/traces` e usa `application/x-protobuf`. Il validatore di startup rifiuta opt-in senza endpoint, URL senza HTTPS in ambienti non-Development, path diversi dalla radice, query, frammenti e credenziali embedded. `http://` è ammesso **solo in Development** per un ricevitore locale temporaneo, mai per un rilascio. Impostazione assente o `false` significa **nessuna trasmissione esterna**; la sola presenza di `Observability__Otlp__Endpoint` non abilita l'export. Le chiavi di autenticazione devono essere passate soltanto come variabili segrete/headers, mai tramite URI, labels o file versionati.

**Prova CI**: test xUnit `ReadinessOtlpExportContractTests` con receiver OTLP/HTTP-Protobuf su loopback effettivamente acquisisce un batch di metriche e uno di trace, oltre ai test che verificano la disattivazione e i vincoli URL. Il test loopback non prova TLS, autenticazione, collector vero, retention, esportazione distribuita o dashboard.

### Collector effimero Compose per CI e sviluppo

L'overlay facoltativo `docker/compose.observability.yml` abilita l'export soltanto sulla API della demo e aggiunge `otel/opentelemetry-collector:0.165.0` con il file `docker/otel-collector-demo.yml`. Il collector riceve **solo OTLP/HTTP port 4318 sulla rete interna Compose**, non pubblica porte sull'host, usa filesystem di root in sola lettura e capabilities Linux ridotte. Non ha pipeline di log, né storage, né una connessione a un backend esterno: l'exporter `debug` stampa esclusivamente le metriche e le trace ricevute nei log Docker. Questa configurazione non offre autenticazione o TLS: il trasporto HTTP è consentito **solo** perché l'API gira in Development e la rete è di test.

```sh
# Partire dalla stack di sviluppo; richiede Docker Compose v2.
docker compose -f docker/compose.yml up -d --build
docker compose -f docker/compose.yml -f docker/compose.observability.yml up -d otel-collector api
# Genera probe di readiness; i batch sono periodici.
curl --fail http://localhost:8080/health/ready
docker compose -f docker/compose.yml -f docker/compose.observability.yml logs --tail=200 otel-collector
# Tornare alla configurazione senza exporter:
docker compose -f docker/compose.yml up -d --no-deps --force-recreate api
docker compose -f docker/compose.yml -f docker/compose.observability.yml stop otel-collector
```

Il test CI `tests/observability/run_collector_e2e.sh` verifica il Compose effettivo, l'assenza di port binding, l'arrivo di una misura `peritial.readiness.requests` **e** uno span `peritial.readiness` ai log del collector, e prova che fermarlo **non cambia** la disponibilità di `/health/live` o `/health/ready` se le quattro dipendenze applicative funzionano. Ripristina l'API standard prima degli ulteriori drill di fault injection. Le misure si perdono se il collector si arresta: la prova non misura retention né consegna garantita.

**Non copiare in produzione:** nel collector operativo servono TLS autenticato (o mTLS), destinazioni/backend privati, credenziali/headers da secret manager, controllo egress e accessi, retention, audit, regole di sampling/limiti e dashboard/alert. L'exporter debug è un diagnostico e non va usato con dati sensibili o telemetria generale dell'API.

**Non è ancora configurato alcun collector persistente, dashboard o alert di produzione.** Occorrono provisioning di rete privata/TLS, rotazione delle credenziali del collector, regole di retention/ACL, metriche da tutte le repliche e test di consegna su collector gestito prima di dichiarare osservabilità end-to-end attiva.

Una volta collegato un backend metriche, usare almeno i seguenti segnali per dashboard e alert:
- rapporto tra incremento di `peritial.readiness.requests` con `outcome=not_ready` e richieste totali, con soglie da tarare su una finestra di minuti e su tutte le repliche;
- durata p95/p99 di `peritial.readiness.duration`, confrontata con il budget di 3 s;
- breakdown per dependency dei fallimenti, **ricordando che i controlli successivi a un fallimento vengono saltati** e quindi possono mancare dati per storage durante un outage PostgreSQL;
- canale separato per backlog e lease scadute del worker recovery (#21), non coperto da questi strumenti.

Nessuna serie ha tenant o identificativi oggetto; non aggiungerli in futuro. L'endpoint pubblico va protetto dalla possibilità di polling eccessivo in produzione con limiti lato ingress/mesh: i probe eseguono chiamate reali e oggi non hanno cache, rate limiting globale o coalescing. La readiness misura raggiungibilità e struttura delle dipendenze, **non** dimostra correttezza integrale di documenti, reale separazione RBAC S3/Azure, freschezza firme ClamAV, né rotazione JWKS. Questa tranche costituisce solo la **fondazione** di trace/metriche, non la chiusura di #19.
