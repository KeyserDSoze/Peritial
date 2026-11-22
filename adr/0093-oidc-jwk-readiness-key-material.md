# ADR 0093 — OIDC readiness con controllo strutturale del materiale JWK

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI da attestare dopo completamento
- Issue: #4 (OAuth/MCP), #19 (health e osservabilità)
- `death`: branch storico, non modificato

## Contesto

La readiness OIDC di Peritial scarica discovery e JWKS tramite HTTP
controllato, verifica issuer e origine della URI JWKS, e restituisce
`ready` quando almeno una chiave pubblica dichiara `kid`/uso firma.
Prima di questa decisione bastavano stringhe **non vuote** per `n`
ed `e` RSA, `x`/`y` EC e `x` OKP. Un JSON `RSA` con
modulo di 3 byte o materiale non Base64URL veniva contato come
provider pronto, pur non potendo rappresentare una chiave di firma
moderna. Questo non altera la verifica JWT ASP.NET Core, ma genera
un **falso positivo operativo** su `/health/ready`.

## Decisione

La readiness richiede almeno una chiave di firma che rispetti:
- `kid` non vuoto; `use` assente o `sig`; se `key_ops` è
  presente, deve includere `verify`.
- `alg` assente oppure coerente con il tipo dichiarato:
  RSA `RS256/384/512`, `PS256/384/512`; EC `P-256/ES256`,
  `P-384/ES384`, `P-521/ES512`; OKP `Ed25519/Ed448`
  con `EdDSA`.
- Coordinate, moduli ed esponenti **Base64URL senza padding e canonici**,
  con limiti espliciti di dimensione. Modulo RSA 2048–8192 bit con
  bit più significativo impostato e valore dispari; esponente senza
  zeri iniziali, dispari e maggiore o uguale a 3.
  Per EC le coordinate hanno lunghezza esatta 32/48/66 byte;
  per OKP la chiave ha 32/57 byte per Ed25519/Ed448.
- JSON sconosciuto, elementi non oggetto, chiavi cifranti o
  materiale invalido non contribuiscono a `ready`. Almeno una
  chiave compatibile rende la struttura pronta anche se il set
  contiene altre chiavi non usabili.

Il corpo HTTP e JSON restano dimensionati e con deadline già
configurati (3 secondi nel runtime di readiness). Nessuna
risposta esterna è riportata a client o metriche, nessuna
richiesta POST, esposizione di tenant/token o ampliamento
delle origini JWKS. Il runtime configura già `AllowAutoRedirect=false`.

## Confine di garanzia

**Readiness non autentica un issuer e non verifica firme JWT**.
Un modulo di 2048 bit dal formato plausibile può comunque non
corrispondere a una vera coppia di chiavi del provider. I controlli
di dimensione per EC/OKP non provano l'appartenenza matematica
del punto alla curva. La verifica dei JWT firmati, issuer, audience,
scope e tenant resta responsabilità del middleware OAuth.
La modifica riduce falsi positivi evidenti, ma non autorizza da
sola Entra/Azure di produzione o nuovi client MCP esterni.

## Test e tracciabilità

- Test negativi su RSA troppo corta/Base64URL malformata/esponente
  invalido, EC corte/curve non note, OKP corte/curve non note,
  `use=enc`, `key_ops` senza `verify` e `alg` cifrante o
  incoerente; prova positiva con RSA structurally 2048-bit e
  una chiave corretta tra chiavi invalide.
- [Test-first `a82eb48c`](https://github.com/KeyserDSoze/Peritial/commit/a82eb48cfbca15001249863e5918dd73b8af858b).
  Il suo primo run è **rosso per un errore di compilazione nelle
  nuove fixture C#**: non costituisce prova RED comportamentale.
- [Validazione `6d6d22df`](https://github.com/KeyserDSoze/Peritial/commit/6d6d22df7a5b4ba8817019481a9e0602efb7171c);
  fixture aggiuntive [`5b3f6fc2`](https://github.com/KeyserDSoze/Peritial/commit/5b3f6fc263d5d23f5a4759608856cb64cbc6c496);
  correzione della sintassi delle fixture
  [`6193c556`](https://github.com/KeyserDSoze/Peritial/commit/6193c556e92a7e6bce282cf006970f6c446402d8).
- [CI ultimo codice #45074102871](https://github.com/KeyserDSoze/Peritial/actions/runs/45074102871):
  risultati backend/integration/9 job da verificare prima di
  dichiarare l'intervento validato.

## Residui

#4 e #19 restano aperte per Entra reale e client MCP esterni,
configurazione e gestione delle chiavi/scope di produzione,
readiness/alerting, rate limiting, audit retention e rollout
Azure/on-prem. La prova di struttura JWKS non sostituisce
l'osservazione del ciclo di rotazione delle chiavi e dei JWT.
