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

Le Activity `peritial.readiness` e `peritial.readiness.dependency` usano esattamente le medesime etichette a cardinalità limitata. Un errore del server o la scadenza del budget sono fail-closed; se il client chiude la richiesta, il token di cancellazione viene propagato e l'esito aggregato è `cancelled`. La cancellazione del token condiviso può includere un timeout nel sotto-controllo, quindi usare i conteggi complessivi `not_ready` per gli alert di disponibilità.

**Privacy:** non sono etichette ammesse tenant, subject, utente, token, email, issuer, hostname, IP, connection string, bucket, document ID, eccezione o messaggio di errore. I nomi dei quattro dependency sono codificati con un enum, non letti da richieste client.

## Esportazione e allarmi

Il codice **produce** le misure tramite API standard `System.Diagnostics.Metrics` e trace tramite `ActivitySource`. **Non è ancora configurato** un exporter OTLP/Prometheus, un collector, una dashboard o un alert di produzione; quindi non dichiarare osservabilità end-to-end attiva. Il prossimo intervento su #19 dovrà registrare il Meter/ActivitySource nel provider OpenTelemetry di ogni replica, usare un collector privato autenticato/TLS e garantire retention, accessi e test di consegna senza esporre dati personali.

Una volta collegato un backend metriche, usare almeno i seguenti segnali per dashboard e alert:
- rapporto tra incremento di `peritial.readiness.requests` con `outcome=not_ready` e richieste totali, con soglie da tarare su una finestra di minuti e su tutte le repliche;
- durata p95/p99 di `peritial.readiness.duration`, confrontata con il budget di 3 s;
- breakdown per dependency dei fallimenti, **ricordando che i controlli successivi a un fallimento vengono saltati** e quindi possono mancare dati per storage durante un outage PostgreSQL;
- canale separato per backlog e lease scadute del worker recovery (#21), non coperto da questi strumenti.

Nessuna serie ha tenant o identificativi oggetto; non aggiungerli in futuro. L'endpoint pubblico va protetto dalla possibilità di polling eccessivo in produzione con limiti lato ingress/mesh: i probe eseguono chiamate reali e oggi non hanno cache, rate limiting globale o coalescing. La readiness misura raggiungibilità e struttura delle dipendenze, **non** dimostra correttezza integrale di documenti, reale separazione RBAC S3/Azure, freschezza firme ClamAV, né rotazione JWKS. Questa tranche costituisce solo la **fondazione** di trace/metriche, non la chiusura di #19.
