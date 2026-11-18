import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {getPeriziaSinistro, getSinistro, isApiUnavailable,
  linkPeriziaSinistro, searchSinistri,
  type PeriziaSinistroAssociation, type SinistroSummary} from "../api";
import {Button} from "./ui/button";

type Props = {
  periziaId: string;
  token: string;
  canWrite: boolean;
  onUnavailable: () => void;
};

/**
 * Load this relation only when the user opens a perizia. Never assume an
 * unlinked perizia from the parent list (which has no relation field).
 * Mutations are authoritative and never replayed/queued after 409/503.
 */
export function PeriziaSinistroPicker({periziaId, token, canWrite, onUnavailable}: Props) {
  const {t} = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const [busy, setBusy] = useState<"read" | "search" | "link" | null>(null);
  const [association, setAssociation] = useState<PeriziaSinistroAssociation | null>(null);
  const [linkedReference, setLinkedReference] = useState("");
  const [prefix, setPrefix] = useState("");
  const [results, setResults] = useState<SinistroSummary[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [searched, setSearched] = useState(false);
  const [error, setError] = useState<"conflict" | "error" | null>(null);
  const sequence = useRef(0);

  // A rotated identity or an unmounted perizia must not receive late tenant
  // search results or a previous request's resolved association.
  useEffect(() => () => { sequence.current += 1; }, [periziaId, token]);

  function resetSelection() {
    setPrefix("");
    setSelectedId("");
    setResults([]);
    setSearched(false);
  }

  async function reloadAssociation() {
    const requestId = ++sequence.current;
    setBusy("read");
    setError(null);
    setAssociation(null);
    setLinkedReference("");
    resetSelection();
    try {
      const current = await getPeriziaSinistro(token, periziaId);
      let reference = "";
      if (current.sinistroId) {
        const linked = await getSinistro(token, current.sinistroId);
        reference = linked.externalReference;
      }
      if (sequence.current !== requestId) return;
      setAssociation(current);
      setLinkedReference(reference);
    } catch (cause) {
      if (sequence.current !== requestId) return;
      // A vanished perizia is no longer an authorized tenant projection.
      // Stop showing the parent list instead of offering a blind POST.
      if (isApiUnavailable(cause) || (cause instanceof Error && cause.message === "HTTP 404"))
        onUnavailable();
      else setError("error");
    } finally {
      if (sequence.current === requestId) setBusy(null);
    }
  }

  function toggle() {
    if (busy) return;
    if (expanded) {
      sequence.current += 1;
      setExpanded(false);
      setAssociation(null);
      setLinkedReference("");
      setError(null);
      resetSelection();
    } else {
      setExpanded(true);
      void reloadAssociation();
    }
  }

  async function search(event: FormEvent) {
    event.preventDefault();
    if (busy || association === null || association.sinistroId ||
        prefix.trim().length < 2 || prefix.trim().length > 100) return;
    const requestId = ++sequence.current;
    setBusy("search");
    setError(null);
    setSelectedId("");
    setResults([]);
    setSearched(false);
    try {
      const found = await searchSinistri(token, prefix);
      if (sequence.current !== requestId) return;
      setResults(found);
      setSearched(true);
    } catch (cause) {
      if (sequence.current !== requestId) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("error");
    } finally {
      if (sequence.current === requestId) setBusy(null);
    }
  }

  async function link() {
    if (busy || !canWrite || association === null || association.sinistroId ||
        !selectedId || !results.some(item => item.id === selectedId)) return;
    const requestId = ++sequence.current;
    const selected = results.find(item => item.id === selectedId)!;
    setBusy("link");
    setError(null);
    try {
      // No optimistic state update, retry loop, background queue or IdP tenant
      // parameter: only a confirmed 201 authorizes the displayed new relation.
      const confirmed = await linkPeriziaSinistro(token, periziaId, selectedId);
      if (sequence.current !== requestId) return;
      setAssociation({periziaId: confirmed.periziaId, sinistroId: confirmed.sinistroId});
      setLinkedReference(selected.externalReference);
      setSelectedId("");
      setResults([]);
      setSearched(false);
    } catch (cause) {
      if (sequence.current !== requestId) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else if (cause instanceof Error && cause.message === "HTTP 409") {
        // The persisted association may have been created by another client.
        // Discard every stale candidate and require a fresh signed GET.
        setAssociation(null);
        setSelectedId("");
        setResults([]);
        setSearched(false);
        setError("conflict");
      } else setError("error");
    } finally {
      if (sequence.current === requestId) setBusy(null);
    }
  }

  return <div className="mt-3 border-t border-slate-200 pt-3 dark:border-slate-800">
    <Button type="button" variant="outline" aria-expanded={expanded}
      disabled={busy !== null} onClick={toggle}>{t("sinistroLinkSection")}</Button>
    {expanded && <div className="mt-3 space-y-3">
      {busy === "read" && <p role="status" className="text-sm">{t("loading")}</p>}
      {error === "conflict" && <>
        <p role="alert" className="text-sm text-red-700 dark:text-red-400">{t("sinistroLinkConflict")}</p>
        <Button type="button" variant="outline" disabled={busy !== null}
          onClick={() => void reloadAssociation()}>{t("reloadSinistroLink")}</Button>
      </>}
      {error === "error" && <p role="alert" className="text-sm text-red-700 dark:text-red-400">{t("sinistroLinkError")}</p>}
      {association?.sinistroId && <>
        <p className="text-sm font-medium">{t("sinistroLinked")}</p>
        <p className="break-words text-sm">{linkedReference || association.sinistroId}</p>
      </>}
      {association && !association.sinistroId && <>
        <p className="text-sm text-slate-600 dark:text-slate-300">{t("sinistroUnlinked")}</p>
        <form onSubmit={event => void search(event)} className="flex flex-wrap items-end gap-2">
          <div className="min-w-0 flex-1">
            <label htmlFor={"sinistro-prefix-" + periziaId}
              className="mb-1 block text-sm">{t("sinistroSearch")}</label>
            <input id={"sinistro-prefix-" + periziaId} value={prefix} minLength={2} maxLength={100}
              autoComplete="off" disabled={busy !== null}
              onChange={event => {
                // Input edits invalidate any candidate selection immediately.
                sequence.current += 1;
                setPrefix(event.target.value);
                setSelectedId("");
                setResults([]);
                setSearched(false);
                setError(null);
              }}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
          <Button type="submit" variant="outline"
            disabled={busy !== null || prefix.trim().length < 2}>
            {t("searchSinistri")}
          </Button>
        </form>
        {searched && results.length === 0 && <p className="text-sm">{t("sinistroNoMatches")}</p>}
        {results.length > 0 && <>
          <label htmlFor={"sinistro-choice-" + periziaId}
            className="block text-sm">{t("chooseSinistro")}</label>
          <select id={"sinistro-choice-" + periziaId} value={selectedId}
            disabled={busy !== null}
            onChange={event => setSelectedId(event.target.value)}
            className="block w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700">
            <option value="">{t("chooseSinistro")}</option>
            {results.map(item => <option key={item.id} value={item.id}>
              {item.externalReference} · {item.occurredOn}
            </option>)}
          </select>
          {canWrite ?
            <Button type="button" disabled={busy !== null || !selectedId}
              onClick={() => void link()}>{t("linkSinistro")}</Button> :
            <p className="text-sm text-slate-600 dark:text-slate-300">{t("sinistroReadOnly")}</p>}
        </>}
      </>}
    </div>}
  </div>;
}
