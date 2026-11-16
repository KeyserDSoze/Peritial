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

## Limiti non risolti

**Non equivale a una release di produzione riproducibile.** Le immagini
di servizi Compose (PostgreSQL, SeaweedFS, ClamAV, Keycloak, Azurite e
test SDK), i container eseguiti dagli script CI e altre dipendenze non sono
ancora tutti bloccati: fra questi rimane `chrislusf/seaweedfs:latest` nella
Compose dimostrativa. Anche `runs-on: ubuntu-latest`, tooling
`dotnet-version: 10.0.x`, registri, provenance/firme, SBOM/attestation e
recovery Azure/on-prem sono lavori separati. Un digest impedisce il drift
**della referenza**, ma non prova assenza di vulnerabilità o provenienza
critto-verificata. #6 e #20 restano aperte.
