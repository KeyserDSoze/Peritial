import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {createSinistro, isApiUnavailable, listSinistri, type SinistroSummary} from "../api";
import {Button} from "./ui/button";
import {SinistroEditor} from "./SinistroEditor";

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
  const [busy, setBusy] = useState<"load" | "create" | null>(null);
  const [error, setError] = useState<"load" | "create" | null>(null);
  const [reference, setReference] = useState("");
  const [occurredOn, setOccurredOn] = useState("");
  const [reportedOn, setReportedOn] = useState("");
  const [createdReference, setCreatedReference] = useState<string | null>(null);
  const epoch = useRef(0);

  // When the parent unmounts this tenant view (logout, token rotation,
  // network failure, offline), no late REST result can restore its rows.
  useEffect(() => () => {epoch.current += 1;}, [token]);

  async function load() {
    if (busy) return;
    const current = ++epoch.current;
    setBusy("load");
    setError(null);
    setCreatedReference(null);
    setRows([]);
    setConflictedIds(new Set());
    try {
      const results = await listSinistri(token);
      if (epoch.current !== current) return;
      setRows(results);
    } catch (cause) {
      if (epoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("load");
    } finally {
      if (epoch.current === current) setBusy(null);
    }
  }

  function toggle() {
    if (busy) return;
    if (expanded) {
      epoch.current += 1;
      setExpanded(false);
      setRows([]);
      setConflictedIds(new Set());
      setError(null);
      setCreatedReference(null);
    } else {
      setExpanded(true);
      void load();
    }
  }

  function applyServerRecord(confirmed: SinistroSummary) {
    setRows(previous => [...previous.filter(item => item.id !== confirmed.id), confirmed]
      .sort((a, b) => b.occurredOn.localeCompare(a.occurredOn) ||
        a.id.localeCompare(b.id))
      .slice(0, 50));
    setConflictedIds(previous => {
      const next = new Set(previous);
      next.delete(confirmed.id);
      return next;
    });
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    const normalized = reference.trim();
    if (!canWrite || busy || !expanded || normalized.length < 1 ||
        normalized.length > 100 || !occurredOn) return;
    const current = ++epoch.current;
    setBusy("create");
    setError(null);
    setCreatedReference(null);
    try {
      const confirmed = await createSinistro(token, normalized,
        occurredOn, reportedOn || null);
      if (epoch.current !== current) return;
      // The response came from the server after atomic PostgreSQL/audit
      // commit. Keep the same server ordering/window; show a separate
      // receipt even if an old event date falls outside the recent 50.
      applyServerRecord(confirmed);
      setCreatedReference(confirmed.externalReference);
      setReference("");
      setOccurredOn("");
      setReportedOn("");
    } catch (cause) {
      if (epoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("create");
    } finally {
      if (epoch.current === current) setBusy(null);
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
        <p className="text-xs text-slate-500 dark:text-slate-400">{t("sinistroCatalogLimit")}</p>
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
    </section>}
  </div>;
}
