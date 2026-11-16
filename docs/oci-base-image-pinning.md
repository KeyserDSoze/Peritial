# Dockerfile OCI base images: digest immutabili (#6)

Aggiornamento 16 novembre 2026. Branch di sviluppo: `main`; `death` non viene modificato.

## Ambito e provenance operativa

La base immagini dei **due Dockerfile applicativi** è ora bloccata al digest
effettivamente risolto da Docker BuildKit nel workflow Compose
[Actions #43868851989](https://github.com/KeyserDSoze/Peritial/actions/runs/43868851989),
con esito `SUCCESS` sul runner GitHub Linux x86_64.
La risoluzione nei log `FROM image:tag@sha256:...` è distinta dal semplice
SHA dell'immagine locale e indica il riferimento distribuito dal registry.
Il tag descrittivo è mantenuto **solo** per leggibilità; il digest seleziona i byte.

| Dockerfile / stage | Riferimento bloccato |
| --- | --- |
| Backend .NET 10 SDK | `mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb` |
| Backend ASP.NET 10 runtime | `mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e` |
| Frontend Node 22 build | `node:22-alpine@sha256:1314fb30ec37592d5845e5a160d522c0e680972aa534549940b8d73bde88481a` |
| Frontend Nginx 1.29 runtime | `nginx:1.29-alpine@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de` |

## Gate CI automatico

Il job `supply-chain-actions` esegue anche:

```sh
python3 -m unittest discover -s tests/security -p 'test_dockerfile_base_pins.py' -v
```

La policy `scripts/security/dockerfile_base_policy.py` scansiona ricorsivamente
tutti i `Dockerfile*` del repository. Accetta solo i due percorsi controllati
e la sequenza esatta di riferimenti `FROM` approvati. Fallisce chiuso
su nuovi Dockerfile senza approvazione, file mancanti, tag mobili,
SHA mancanti/corti/sconosciuti, stage duplicati, interpolazioni
`ARG` e direttive `FROM` ambigue. I test negativi coprono questi casi.

La prova CI **RED** è
[Actions #43871881059](https://github.com/KeyserDSoze/Peritial/actions/runs/43871881059):
il gate individua le quattro direttive `FROM` non fissate, mentre gli altri
cinque test di policy passano. Dopo i pin, verificare che il job gate sia
verde e che Compose costruisca API e frontend ed esegua tutti gli smoke.

## Aggiornamento responsabile

1. Verificare release notes e vulnerabilità dell'immagine base e del vendor.
   Non modificare il digest solo perché un tag è stato aggiornato.
2. Risolvere sul registry ufficiale l'indice/manifests per le architetture
   supportate; confermare le differenze tra digest **index multi-platform** e
   digest **manifest per-architettura**. I quattro SHA della baseline sono stati
   osservati durante un build `linux/amd64`: **non è attestato** il supporto
   a `linux/arm64`. Prima di dichiarare multi-arch, aggiungere una build CI
   della piattaforma e aggiornare la policy di conseguenza.
3. Aggiornare insieme Dockerfile, `APPROVED_BY_DOCKERFILE` e questa tabella,
   mediante commit su `main` con riferimento all'issue; eseguire i 6 test
   della policy, le build reali e l'intera CI. Non fare fallback automatico
   su tag mobili.
4. Documentare in issue SHA old/new, risultati, test di compatibilità e
   eventuali roll-back. Conservare un percorso di ripristino funzionante
   per immagini non più disponibili.

## Pin selettivo delle immagini infrastrutturali PostgreSQL e Prometheus (#6)

La policy addizionale `scripts/security/infra_oci_policy.py` blocca esattamente
**sei occorrenze** in tre sorgenti (non tutte le immagini Compose):

| Immagine | Riferimento OCI verificato | Ambito controllato |
| --- | --- | --- |
| PostgreSQL 17 Alpine | `postgres:17-alpine@sha256:a77aa70e641402a21411b92b721c58d10f23b50ae5fdd2333efd22d166e1e5f7` | `docker/compose.yml` (postgres e postgres-restore), service PostgreSQL del backend in `.github/workflows/ci.yml` |
| Prometheus 3.5.0 | `prom/prometheus:v3.5.0@sha256:63805ebb8d2b3920190daf1cb14a60871b16fd38bed42b857a3182bc621f4996` | `docker/compose.prometheus.yml` e due invocazioni `docker run --entrypoint /bin/promtool` in CI |

La provenienza *osservabile* dei riferimenti è circoscritta: il primo digest
compare nel `docker pull postgres:17-alpine` del job `backend` di
[CI #43872211334](https://github.com/KeyserDSoze/Peritial/actions/runs/43872211334);
il secondo nel `docker pull prom/prometheus:v3.5.0` del job
`observability-rules` verde di
[CI #43868851989](https://github.com/KeyserDSoze/Peritial/actions/runs/43868851989).
Entrambi sono stati osservati su runner Linux amd64: **non equivalgono a
verifica ARM64, firma, certificazione della distribuzione o immutabilità di
tutte le dipendenze**.

Il job `supply-chain-actions` esegue:

```sh
python3 -m unittest discover -s tests/security -p test_infra_oci_pins.py -v
```

I sette test richiedono i file e il numero esatto delle occorrenze
approvate, respingendo drift, digest errati, tag mobili, rimozioni,
duplicazioni e commenti che tentano di sostituire una definizione.
**RED verificato**: [CI #43873317873](https://github.com/KeyserDSoze/Peritial/actions/runs/43873317873),
sei test passati e rifiuto dei riferimenti non ancora fissati.
Il blocco non costituisce un parser YAML completo o una garanzia che nuovi
servizi Docker Compose siano approvati: i nuovi servizi e container invocati
dagli script richiedono un inventario supply-chain completo.

Per aggiornare un digest: verificare release e immagine sul registry,
testare compatibilità, aggiornare sia manifest sia costanti della policy,
rieseguire i sette test, l'intera CI Compose e il controllo `promtool`,
quindi registrare nella issue SHA/URL e limitazioni. Non sostituire digest
con tag mobili per aggirare un errore di pull.

## Limiti non risolti

**Non equivale a una release di produzione riproducibile.** Le immagini
di diversi servizi Compose (SeaweedFS, ClamAV, Keycloak, Azurite,
test SDK e servizi dell'overlay di osservabilità), ulteriori container
invocati dagli script CI e altre dipendenze non sono ancora tutti bloccati: fra questi rimane `chrislusf/seaweedfs:latest` nella
Compose dimostrativa. Anche `runs-on: ubuntu-latest`, tooling
`dotnet-version: 10.0.x`, registri, provenance/firme, SBOM/attestation e
recovery Azure/on-prem sono lavori separati. Un digest impedisce il drift
**della referenza**, ma non prova assenza di vulnerabilità o provenienza
critto-verificata. #6 e #20 restano aperte.
