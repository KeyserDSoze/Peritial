# ADR 0001 — Ricostruzione clean-room

Stato: accettata — 2026-11-15

Contesto: `death` contiene una piattaforma ASP.NET MVC legacy; `main` non esisteva all'avvio della ricostruzione.

Decisione: snapshot e codice di `main` nuovi e separati dalla struttura Expertiser.*. `death` rimane immutato e non sarà mergiato. Sviluppo solo `main`.

Conseguenze: parità funzionale misurata tramite inventario e test. La creazione del branch con strumenti GitHub che richiedono un parent commit può lasciare il commit `death` come antenato **storico** del nuovo ramo; non equivale a un merge o a conservare i file legacy nello snapshot. Per storie Git completamente scollegate sarebbe necessaria creazione di un commit root orfano e successivo re-point del branch.
