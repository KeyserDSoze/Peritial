import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {createSinistro, isApiUnavailable, pageSinistri, pageSearchSinistri, type SinistroPage, type SinistroSummary} from "../api";
import {Button} from "./ui/button";
import {SinistroEditor} from "./SinistroEditor";

type CatalogFilter = {prefix: string; from: string; to: string};

type Props = {
  token: string;
  canWrite: boolean;
  onUnavailable: () => void;
};

/**
 * Deliberately on-demand: the existing unversioned V001 Compose demo has no
 * Sinistri table until an explicit migration. Do not block the Perizie PWA
 * by fetching this separate, optional catalog on application startup.
 *
 * Successful creates are shown only after a confirmed 201. No queued writes,
 * automatic mutation retries, offline cache or optimistic fake records.
 */
export function SinistroCatalog({token, canWrite, onUnavailable}: Props) {
  const {t} = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const [rows, setRows] = useState<SinistroSummary[]>([]);
  const [conflictedIds, setConflictedIds] = useState<Set<string>>(() => new Set());
  const [busy, setBusy] = useState<"load" | "more" | "create" | null>(null);
  const [error, setError] = useState<"load" | "create" | null>(null);
  const [reference, setReference] = useState("");
  const [occurredOn, setOccurredOn] = useState("");
  const [reportedOn, setReportedOn] = useState("");
  const [createdReference, setCreatedReference] = useState<string | null>(null);
  const [searchPrefix, setSearchPrefix] = useState("");
  const [searchFrom, setSearchFrom] = useState("");
  const [searchTo, setSearchTo] = useState("");
  const [appliedPrefix, setAppliedPrefix] = useState<string | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const activeFilter = useRef<CatalogFilter | null>(null);
  const epoch = useRef(0);
  // React busy state updates after render. This synchronous ref prevents
  // repeated requestSubmit() events from starting duplicate POSTs.
  const createInFlight = useRef(false);
  // State-driven disabled controls do not stop two synchronous click events.
  // Protect both initial reads and keyset continuations with one immediate
  // lock; the matching epoch alone may release it on settlement.
  const readInFlight = useRef(false);

  // When the parent unmounts this tenant view (logout, token rotation,
  // network failure, offline), no late REST result can restore its rows.
  useEffect(() => () => {
    epoch.current += 1;
    createInFlight.current = false;
    readInFlight.current = false;
  }, [token]);

  async function fetchCatalog(
    filter: CatalogFilter | null, cursor?: string): Promise<SinistroPage> {
    return filter
      ? pageSearchSinistri(token, filter.prefix, filter.from || undefined,
          filter.to || undefined, cursor)
      : pageSinistri(token, cursor);
  }

  async function load(filter: CatalogFilter | null = activeFilter.current) {
    if (busy || createInFlight.current || readInFlight.current) return;
    readInFlight.current = true;
    const current = ++epoch.current;
    setBusy("load");
    setError(null);
    setCreatedReference(null);
    setRows([]);
    setNextCursor(null);
    setConflictedIds(new Set());
    try {
      const results = await fetchCatalog(filter);
      if (epoch.current !== current) return;
      setRows(results.items);
      setNextCursor(results.nextCursor);
    } catch (cause) {
      if (epoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("load");
    } finally {
      if (epoch.current === current) {
        readInFlight.current = false;
        setBusy(null);
      }
    }
  }

  async function loadMore() {
    if (busy || createInFlight.current || readInFlight.current || !expanded || !nextCursor) return;
    readInFlight.current = true;
    const filter = activeFilter.current;
    const current = ++epoch.current;
    setBusy("more");
    setError(null);
    try {
      const next = await fetchCatalog(filter, nextCursor);
      if (epoch.current !== current) return;
      setRows(previous => {
        const seen = new Set(previous.map(i => i.id));
        return [...previous, ...next.items.filter(i => !seen.has(i.id))];
      });
      setNextCursor(next.nextCursor);
    } catch (cause) {
      if (epoch.current !== current) return;
      setRows([]);
      setNextCursor(null);
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("load");
    } finally {
      if (epoch.current === current) {
        readInFlight.current = false;
        setBusy(null);
      }
    }
  }

  function toggle() {
    if (busy || createInFlight.current || readInFlight.current) return;
    if (expanded) {
      epoch.current += 1;
      setExpanded(false);
      setRows([]);
      setNextCursor(null);
      setConflictedIds(new Set());
      setError(null);
      setCreatedReference(null);
      activeFilter.current = null;
      setAppliedPrefix(null);
      setSearchPrefix("");
      setSearchFrom("");
      setSearchTo("");
    } else {
      activeFilter.current = null;
      setExpanded(true);
      void load(null);
    }
  }

  function search(event: FormEvent) {
    event.preventDefault();
    const prefix = searchPrefix.trim();
    if (!expanded || busy || createInFlight.current || readInFlight.current || prefix.length < 2 || prefix.length > 100 ||
        (searchFrom && searchTo && searchFrom > searchTo)) return;
    const filter = {prefix, from: searchFrom, to: searchTo};
    activeFilter.current = filter;
    setAppliedPrefix(prefix);
    void load(filter);
  }

  function resetSearch() {
    if (busy || createInFlight.current || readInFlight.current) return;
    activeFilter.current = null;
    setAppliedPrefix(null);
    setSearchPrefix("");
    setSearchFrom("");
    setSearchTo("");
    void load(null);
  }

  function applyServerRecord(confirmed: SinistroSummary) {
    setRows(previous => {
      const remaining = previous.filter(item => item.id !== confirmed.id);
      const filter = activeFilter.current;
      if (filter) {
        // The initial search and bounded window are authoritative PostgreSQL
        // reads. Reconcile only an already displayed row after signed GET/PUT;
        // do not accidentally inject an out-of-window record into search.
        if (!previous.some(item => item.id === confirmed.id)) return previous;
        const matches = confirmed.externalReference.toLocaleLowerCase()
          .startsWith(filter.prefix.toLocaleLowerCase()) &&
          (!filter.from || confirmed.occurredOn >= filter.from) &&
          (!filter.to || confirmed.occurredOn <= filter.to);
        if (!matches) return remaining;
      }
      return [...remaining, confirmed]
        .sort((a, b) => b.occurredOn.localeCompare(a.occurredOn) ||
          a.id.localeCompare(b.id))
        .slice(0, Math.max(50, previous.length));
    });
    setConflictedIds(previous => {
      const next = new Set(previous);
      next.delete(confirmed.id);
      return next;
    });
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    const normalized = reference.trim();
    if (!canWrite || busy || createInFlight.current || readInFlight.current || !expanded || normalized.length < 1 ||
        normalized.length > 100 || !occurredOn) return;
    createInFlight.current = true;
    const current = ++epoch.current;
    let committed = false;
    setBusy("create");
    setError(null);
    setCreatedReference(null);
    try {
      const confirmed = await createSinistro(token, normalized,
        occurredOn, reportedOn || null);
      if (epoch.current !== current) return;
      // A successful POST is definitive. When a search is active, refresh
      // that query from the server rather than inserting a nonmatching case
      // into its bounded result window.
      committed = true;
      setCreatedReference(confirmed.externalReference);
      setReference("");
      setOccurredOn("");
      setReportedOn("");
      if (activeFilter.current) {
        setRows([]);
        setBusy("load");
        const refreshed = await fetchCatalog(activeFilter.current);
        if (epoch.current !== current) return;
        setRows(refreshed.items);
        setNextCursor(refreshed.nextCursor);
      } else {
        // After a create the previous keyset boundary must not be reused.
        // The separate receipt remains visible even if the case falls
        // outside the recent-50 window; explicit refresh reopens paging.
        setNextCursor(null);
        applyServerRecord(confirmed);
      }
    } catch (cause) {
      if (epoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError(committed ? "load" : "create");
    } finally {
      if (epoch.current === current) {
        createInFlight.current = false;
        setBusy(null);
      }
    }
  }

  return <div className="mt-6 border-t border-slate-200 pt-5 dark:border-slate-800">
    <Button type="button" variant="outline" aria-expanded={expanded}
      disabled={busy !== null} onClick={toggle}>
      {t(expanded ? "closeSinistroCatalog" : "openSinistroCatalog")}
    </Button>
    {expanded && <section aria-label={t("sinistroCatalog")} className="mt-4 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-lg font-semibold">{t("sinistroCatalog")}</h3>
        <Button type="button" variant="outline" disabled={busy !== null}
          onClick={() => void load()}>{t("refreshSinistri")}</Button>
      </div>
      <form onSubmit={search} className="space-y-3" aria-label={t("sinistroCatalogSearch")}>
        <div>
          <label htmlFor="sinistro-catalog-prefix" className="mb-1 block text-sm">{t("sinistroCatalogPrefix")}</label>
          <input id="sinistro-catalog-prefix" value={searchPrefix} minLength={2} maxLength={100}
            required autoComplete="off" disabled={busy !== null}
            onChange={event => setSearchPrefix(event.target.value)}
            className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t("sinistroCatalogSearchHint")}</p>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <div>
            <label htmlFor="sinistro-catalog-from" className="mb-1 block text-sm">{t("sinistroCatalogFrom")}</label>
            <input id="sinistro-catalog-from" type="date" value={searchFrom} disabled={busy !== null}
              onChange={event => setSearchFrom(event.target.value)}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
          <div>
            <label htmlFor="sinistro-catalog-to" className="mb-1 block text-sm">{t("sinistroCatalogTo")}</label>
            <input id="sinistro-catalog-to" type="date" value={searchTo} min={searchFrom || undefined}
              disabled={busy !== null} onChange={event => setSearchTo(event.target.value)}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={busy !== null || searchPrefix.trim().length < 2 ||
            searchPrefix.trim().length > 100 || Boolean(searchFrom && searchTo && searchFrom > searchTo)}>
            {t("sinistroCatalogSearch")}
          </Button>
          {appliedPrefix && <Button type="button" variant="outline" disabled={busy !== null}
            onClick={resetSearch}>{t("sinistroCatalogReset")}</Button>}
        </div>
      </form>
      {appliedPrefix && <p role="status" className="text-xs text-slate-600 dark:text-slate-300">
        {t("sinistroCatalogSearchApplied")} {appliedPrefix}
      </p>}
      {canWrite && <form onSubmit={event => void create(event)} className="space-y-3">
        <div>
          <label htmlFor="sinistro-create-ref" className="mb-1 block text-sm">{t("sinistroReference")}</label>
          <input id="sinistro-create-ref" value={reference} required maxLength={100}
            disabled={busy !== null} onChange={event => {
              setReference(event.target.value); setError(null);
            }} autoComplete="off"
            className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <div>
            <label htmlFor="sinistro-create-occurred" className="mb-1 block text-sm">{t("sinistroOccurredOn")}</label>
            <input id="sinistro-create-occurred" type="date" value={occurredOn}
              required disabled={busy !== null}
              onChange={event => {setOccurredOn(event.target.value);setError(null);}}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
          <div>
            <label htmlFor="sinistro-create-reported" className="mb-1 block text-sm">{t("sinistroReportedOn")}</label>
            <input id="sinistro-create-reported" type="date" value={reportedOn}
              disabled={busy !== null}
              onChange={event => {setReportedOn(event.target.value);setError(null);}}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
        </div>
        <Button type="submit" disabled={busy !== null || !reference.trim() || !occurredOn}>
          {t("createSinistro")}
        </Button>
      </form>}
      {busy === "load" && <p role="status">{t("loading")}</p>}
      {error && <p role="alert" className="text-sm text-red-700 dark:text-red-400">
        {t(error === "create" ? "sinistroCreateError" : "sinistroCatalogError")}
      </p>}
      {createdReference && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400">
        {t("sinistroCreated")} {createdReference}
      </p>}
      {!busy && rows.length === 0 && !error && <p className="text-sm text-slate-600 dark:text-slate-300">
        {t("sinistroCatalogEmpty")}
      </p>}
      {rows.length > 0 && <>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {t(appliedPrefix ? "sinistroCatalogSearchLimit" : "sinistroCatalogLimit")}
        </p>
        <ul aria-label={t("sinistroCatalog")} className="divide-y divide-slate-200 dark:divide-slate-800">
          {rows.map(item => <li key={item.id} data-sinistro-id={item.id} className="py-2 text-sm">
            {conflictedIds.has(item.id) ?
              <p className="text-red-700 dark:text-red-400">{t("sinistroStale")}</p> :
              <>
                <strong className="break-words">{item.externalReference}</strong>
                <p className="text-slate-600 dark:text-slate-300">
                  {t("sinistroOccurredOn")}: {item.occurredOn}
                  {item.reportedOn && <> · {t("sinistroReportedOn")}: {item.reportedOn}</>}
                </p>
              </>}
            {canWrite && <SinistroEditor id={item.id} token={token}
              onUnavailable={onUnavailable} onConfirmed={applyServerRecord}
              onConflict={id => setConflictedIds(previous => new Set(previous).add(id))} />}
          </li>)}
        </ul>
      </>}
      {nextCursor && <Button type="button" variant="outline"
        disabled={busy !== null} onClick={() => void loadMore()}>
        {t("sinistroCatalogMore")}
      </Button>}
      {busy === "more" && <p role="status">{t("loading")}</p>}
    </section>}
  </div>;
}
