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

Questo gate rende deterministica la **revisione delle action** chiamate
tramite `uses:`, non la supply chain completa: immagini Docker con tag
mobili, base image, `runs-on: ubuntu-latest`, versioni SDK `10.0.x`,
Terraform installato tramite setup action, dipendenze transitive e mirror
esterni devono essere affrontati separatamente. Uno SHA fisso **non
dimostra** da solo firma, provenance/SLSA, assenza vulnerabilità o
approvazione del publisher. Non sostituisce branch protection, secrets
security, permessi minimi o attestazioni del deploy. La issue #6 resta aperta.
