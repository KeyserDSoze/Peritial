# CI: action GitHub fissate a commit immutabili (#6)

Lo sviluppo si effettua esclusivamente su `main`; `death` rimane storico.
La CI esegue un gate indipendente, `supply-chain-actions`, che controlla tutte
le definizioni `.github/workflows/**/*.yml` e `*.yaml` prima di ogni rilascio.

## Baseline approvata (16 novembre 2026)

| Action | Tag upstream usato come riferimento | Commit verificato nel repository ufficiale |
| --- | --- | --- |
| `actions/checkout` | `v4` | `11d5960a326750d5838078e36cf38b85af677262` |
| `actions/setup-node` | `v4` | `49933ea5288caeca8642d1e84afbd3f7d6820020` |
| `actions/setup-dotnet` | `v4` | `67a3573c9a986a3f9c594539f4ab511d57bb3ce9` |
| `hashicorp/setup-terraform` | `v3` | `b9cd54a3c349d3f38e8881555d616ced269862dd` |

Gli SHA provengono dai riferimenti `refs/tags/v4` / `refs/tags/v3`
interrogati nei repository GitHub ufficiali; sono revisioni deliberate e
**non** devono essere aggiornate automaticamente ai tag mobili.

## Policy automatizzata

`scripts/security/workflow_action_policy.py` contiene l'allowlist esatta
`repository/action -> SHA`. Il job `supply-chain-actions` esegue:

```sh
python3 -m unittest discover -s tests/security \
  -p 'test_workflow_action_pins.py' -v
```

Il gate rifiuta: riferimenti mobili `@vN`/`@main`, SHA abbreviati o
sconosciuti, action non esplicitamente autorizzate, `docker://`,
action locali non verificate, sintassi `uses:` ambigue e assenza della
directory workflow. I test negativi servono a impedire che un nuovo job
indebolisca silenziosamente la policy. Nuovi workflow vengono inclusi
automaticamente dal controllo ricorsivo.

## Baseline esplicita runner e SDK della CI (16 novembre 2026)

La CI contiene ora anche una policy distinta
`scripts/security/ci_toolchain_policy.py` e la suite
`tests/security/test_ci_toolchain_pins.py`. Il job `supply-chain-actions`
richiede **14 occorrenze** esatte nell'unico workflow CI:

| Campo | Versione approvata | Occorrenze | Evidenza |
| --- | --- | --- | --- |
| `runs-on` | `ubuntu-26.04` | 9 | Runner Image `ubuntu26/20261111.196` nel job backend CI |
| `dotnet-version` | `10.0.404` | 3 | Log `dotnet-install: .NET Core SDK ... 10.0.404` |
| `node-version` | `22.23.4` | 2 | Log `node: v22.23.4` e cache toolchain |

Evidenza: [run #43873685545](https://github.com/KeyserDSoze/Peritial/actions/runs/43873685545)
sul commit precedente `5b476f43`. GitHub conferma il label
`ubuntu-26.04` per i runner ospitati nella
[documentazione ufficiale](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/choose-the-runner-for-a-job).

Il gate aggiuntivo esegue:

```sh
python3 -m unittest discover -s tests/security -p test_ci_toolchain_pins.py -v
```

Verifica esatta del label e delle versioni, numero di occorrenze,
mancanze/duplicati, espressioni di matrix e alternative non revisionate.
TDD RED documentato in [run #43874811777](https://github.com/KeyserDSoze/Peritial/actions/runs/43874811777),
dove la policy rifiuta `ubuntu-latest`, Node `22` e .NET `10.0.x`
ancora presenti nel workflow.

Il label `ubuntu-26.04` **stabilizza solo la famiglia OS**: GitHub può
aggiornare l'immagine VM e il tooling preinstallato senza cambiare il label.
Le versioni Node e SDK sono esatte **solo dove configurate nel workflow**;
l'immagine Docker SDK e i tool di altri ecosistemi hanno una propria
provenienza. Aggiornare runner/SDK soltanto con release notes, prova CI e
cambio contestuale delle costanti di policy, documentando il nuovo numero
di versione. Non usare versioni fluttuanti come fallback automatico.

## Upgrade controllato

1. Aprire o aggiornare una issue; leggere release notes/security advisory
   upstream, verificare repository e commit. Controllare il **commit Git**,
   non assumere che un tag mobile sia immutabile.
2. Recuperare il commit puntato dalla release ufficiale. Per tag annotati
   risolvere l'oggetto tag fino al commit; non usare il solo SHA del tag.
   Revisione obbligatoria prima di accettare codice di terze parti.
3. Modificare **insieme** i riferimenti `uses:` dei workflow e la mappa
   `APPROVED_ACTIONS`; non usare versioni mobili come fallback.
4. Eseguire test Python e l'intera GitHub Actions su `main`; verificare
   che i job esistenti non regrediscano e che il nuovo SHA sia realmente
   eseguito. Aggiornare questa tabella, l'issue e la documentazione di release.

## Confini espliciti

Il job copre ora anche il controllo dei digest dei **due Dockerfile applicativi**,
tramite `test_dockerfile_base_pins.py` (runbook: [pin OCI](oci-base-image-pinning.md)).
Non copre ancora la supply chain completa: diverse immagini Compose/CI con tag mobili,
build effettivo della VM Ubuntu, SDK incorporati nelle immagini OCI,
Terraform installato tramite setup action, dipendenze transitive, pacchetti
sistema e mirror esterni devono essere affrontati separatamente. Uno SHA fisso **non
dimostra** da solo firma, provenance/SLSA, assenza vulnerabilità o
approvazione del publisher. Non sostituisce branch protection, secrets
security, permessi minimi o attestazioni del deploy. La issue #6 resta aperta.
