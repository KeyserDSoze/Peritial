# ADR 0118 — Quota operatore bounded per bundle sigillati

- Data: 24 novembre 2026
- Stato: implementazione su main, CI da attestare
- Issue: #6, #27

ADR 0117 introduce un limite complessivo di 1 GiB. Un valore fisso
può impedire il recovery di un backup legittimo. Mantenere il default,
consentendo un limite esplicito positivo fino a 4 GiB, scelto
conservativamente sotto il limite single PUT S3. Nessun unlimited.

Il CLI legge PERITIAL_BUNDLE_MAX_PLAINTEXT_BYTES prima delle chiavi
e dei client; parser invariant, NumberStyles.None, default solo se
variabile assente. La quota viene passata a seal, unseal, push-sealed,
pull-sealed e pull-historic, inclusa la validazione finale AES-GCM.
I port pubblici validano direttamente la quota prima di I/O.
L'overhead cifrato resta 64 MiB e la quota è derivata senza overflow.
Il limite S3 interno e count restano 512 MiB e 10.000. Il formato v1,
Object Lock, HMAC, SHA-256, schema e death restano invariati.

Test di parsing, limiti e overflow senza allocazioni gigabyte;
contratto bundle locale e SeaweedFS con quota ridotta, rifiuto senza
destinazione pubblicata, controprova con quota sufficiente.

La configurazione è una decisione operativa da approvare. La quota
non prova capacità libera o compatibilità cloud. Non supporta set
oltre 4 GiB né aumenta il budget dei blob. Per pull, l'inventario
cifrato è verificato prima dello staging; la quota plaintext esatta
è verificata sull'indice autenticato prima della pubblicazione.
Il cap superiore non è prova di recovery reale da 4 GiB.
