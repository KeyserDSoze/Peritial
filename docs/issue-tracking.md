# GitHub Issues: fonte ufficiale dello stato

Sviluppare soltanto sul branch main; death serve esclusivamente come riferimento storico e non viene mai mergiato.

## Processo

1. Prima di ogni modifica controllare issue aperte/chiuse e GitHub Actions su main.
2. Aprire o aggiornare una issue con criteri di accettazione prima del codice; evitare duplicati.
3. TDD: commit Red con test, commit Green con implementazione minima, Refactor e aggiornamento della documentazione.
4. Ogni commit di funzionalita cita #numero. Una issue si chiude solo con criteri verificati e CI verde.
5. Se i requisiti sono parzialmente realizzati, lasciare la issue aperta e riportare precisamente il lavoro mancante.

## Priorita

P0 = sicurezza, tenant e affidabilita; P1 = parita funzionale e portabilita; P2 = analisi, messaggi e fatturazione.

La situazione aggiornata si legge su GitHub Issues: https://github.com/KeyserDSoze/Peritial/issues . Non duplicare qui stati manuali che diventerebbero obsoleti.
