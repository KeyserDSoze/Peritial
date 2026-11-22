import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {getPeriziaSinistro, getSinistro, isApiUnavailable,
  linkPeriziaSinistro, unlinkPeriziaSinistro, reparentPeriziaSinistro, searchSinistri,
  type PeriziaSinistroAssociation, type SinistroSummary} from "../api";
import {Button} from "./ui/button";

type Props = {
  periziaId: string;
  periziaStatus: string;
  token: string;
  canWrite: boolean;
  onUnavailable: () => void;
};

/**
 * Load this relation only when the user opens a perizia. Never assume an
 * unlinked perizia from the parent list (which has no relation field).
 * Mutations are authoritative and never replayed/queued after 409/503.
 */
export function PeriziaSinistroPicker({periziaId, periziaStatus, token, canWrite, onUnavailable}: Props) {
  const {t} = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const [busy, setBusy] = useState<"read" | "search" | "link" | "unlink" | "reparent" | null>(null);
  const [association, setAssociation] = useState<PeriziaSinistroAssociation | null>(null);
  // A revision, not just a UUID, is needed to detect A -> B -> A.
  const hasRevision = (value: PeriziaSinistroAssociation | null) =>
    value !== null && Number.isSafeInteger(value.linkVersion) && value.linkVersion >= 1;
  const [linkedReference, setLinkedReference] = useState("");
  const [prefix, setPrefix] = useState("");
  const [results, setResults] = useState<SinistroSummary[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [searched, setSearched] = useState(false);
  const [confirmUnlink, setConfirmUnlink] = useState(false);
  const [unlinkedSuccess, setUnlinkedSuccess] = useState(false);
  const [reparenting, setReparenting] = useState(false);
  const [confirmReparent, setConfirmReparent] = useState(false);
  const [reparentedSuccess, setReparentedSuccess] = useState(false);
  const [error, setError] = useState<"conflict" | "error" | null>(null);
  const sequence = useRef(0);
  // The busy state disables controls only after React renders. Guard every
  // signed read/write synchronously so two events in one task never dispatch
  // duplicate CAS link, unlink or reparent mutations.
  const inFlight = useRef(false);
  // Match the backend's row-locked terminal-status predicate. The backend
  // remains authoritative; hiding the action is only a UX safeguard.
  const canReparent = canWrite &&
    !["Billed","Duplicated","Cancelled"].includes(periziaStatus);

  // A concurrent workflow transition to a terminal state invalidates a
  // pending proposal. Never allow an old visible selection to be submitted.
  useEffect(() => {
    if (!canReparent) {
      sequence.current += 1;
      inFlight.current = false;
      setBusy(null);
      setReparenting(false);
      setConfirmReparent(false);
      setSelectedId("");
      setResults([]);
      setSearched(false);
    }
  }, [canReparent]);

  // A rotated identity or an unmounted perizia must not receive late tenant
  // search results or a previous request's resolved association.
  useEffect(() => () => {
    sequence.current += 1;
    inFlight.current = false;
  }, [periziaId, token]);

  function resetSelection() {
    setPrefix("");
    setSelectedId("");
    setResults([]);
    setSearched(false);
  }

  async function reloadAssociation() {
    if (inFlight.current) return;
    inFlight.current = true;
    const requestId = ++sequence.current;
    setBusy("read");
    setError(null);
    setConfirmUnlink(false);
    setUnlinkedSuccess(false);
    setReparenting(false);
    setConfirmReparent(false);
    setReparentedSuccess(false);
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
      if (sequence.current === requestId) {
        inFlight.current = false;
        setBusy(null);
      }
    }
  }

  function toggle() {
    if (busy || inFlight.current) return;
    if (expanded) {
      sequence.current += 1;
      setExpanded(false);
      setAssociation(null);
      setLinkedReference("");
      setError(null);
      setConfirmUnlink(false);
      setUnlinkedSuccess(false);
      setReparenting(false);
      setConfirmReparent(false);
      setReparentedSuccess(false);
      resetSelection();
    } else {
      setExpanded(true);
      void reloadAssociation();
    }
  }

  async function search(event: FormEvent) {
    event.preventDefault();
    if (busy || inFlight.current || association === null ||
        (association.sinistroId && !reparenting) ||
        prefix.trim().length < 2 || prefix.trim().length > 100) return;
    inFlight.current = true;
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
      if (sequence.current === requestId) {
        inFlight.current = false;
        setBusy(null);
      }
    }
  }

  async function link() {
    if (busy || inFlight.current || !canWrite || !hasRevision(association) ||
        association === null || association.sinistroId ||
        !selectedId || !results.some(item => item.id === selectedId)) return;
    inFlight.current = true;
    const requestId = ++sequence.current;
    const selected = results.find(item => item.id === selectedId)!;
    setBusy("link");
    setError(null);
    try {
      // No optimistic state update, retry loop, background queue or IdP tenant
      // parameter: only a confirmed 201 authorizes the displayed new relation.
      const confirmed = await linkPeriziaSinistro(token, periziaId,
        selectedId, association.linkVersion);
      if (sequence.current !== requestId) return;
      setAssociation({periziaId: confirmed.periziaId, sinistroId: confirmed.sinistroId,
        linkVersion:confirmed.linkVersion});
      setLinkedReference(selected.externalReference);
      setConfirmUnlink(false);
      setUnlinkedSuccess(false);
      setReparenting(false);
      setConfirmReparent(false);
      setReparentedSuccess(false);
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
      if (sequence.current === requestId) {
        inFlight.current = false;
        setBusy(null);
      }
    }
  }

  async function unlink() {
    // Do not auto-unlink or use a hidden/possibly stale association.
    if (busy || inFlight.current || !canWrite || !confirmUnlink || !hasRevision(association) ||
        !association?.sinistroId) return;
    const observed = association.sinistroId;
    inFlight.current = true;
    const requestId = ++sequence.current;
    setBusy("unlink");
    setError(null);
    try {
      await unlinkPeriziaSinistro(token, periziaId, observed, association.linkVersion);
      // The 204 proves commit, but verify current state again to account for
      // a new link added by another writer immediately after the unlink.
      const current = await getPeriziaSinistro(token, periziaId);
      const linked = current.sinistroId
        ? await getSinistro(token, current.sinistroId) : null;
      if (sequence.current !== requestId) return;
      setAssociation(current);
      setLinkedReference(linked?.externalReference || "");
      setConfirmUnlink(false);
      setUnlinkedSuccess(current.sinistroId === null);
      setReparenting(false);
      setConfirmReparent(false);
      setReparentedSuccess(false);
      resetSelection();
    } catch (cause) {
      if (sequence.current !== requestId) return;
      setConfirmUnlink(false);
      setUnlinkedSuccess(false);
      if (isApiUnavailable(cause) ||
          (cause instanceof Error && cause.message === "HTTP 404")) {
        onUnavailable();
      } else if (cause instanceof Error && cause.message === "HTTP 409") {
        // Never keep a stale association after a CAS conflict. Require a
        // fresh tenant-scoped GET; do not retry or delete the new link.
        setAssociation(null);
        setLinkedReference("");
        resetSelection();
        setError("conflict");
      } else {
        setError("error");
      }
    } finally {
      if (sequence.current === requestId) {
        inFlight.current = false;
        setBusy(null);
      }
    }
  }

  function beginReparent() {
    if (busy || inFlight.current || !canReparent || !hasRevision(association) || !association?.sinistroId) return;
    sequence.current += 1;
    setReparenting(true);
    setConfirmUnlink(false);
    setConfirmReparent(false);
    setUnlinkedSuccess(false);
    setReparentedSuccess(false);
    setError(null);
    resetSelection();
  }

  function cancelReparent() {
    if (inFlight.current) return;
    sequence.current += 1;
    setReparenting(false);
    setConfirmReparent(false);
    setError(null);
    resetSelection();
  }

  async function reparent() {
    if (busy || inFlight.current || !canReparent || !confirmReparent || !reparenting ||
        !hasRevision(association) || !association?.sinistroId || !selectedId ||
        selectedId === association.sinistroId ||
        !results.some(item => item.id === selectedId)) return;
    const expected = association.sinistroId;
    const target = selectedId;
    inFlight.current = true;
    const requestId = ++sequence.current;
    setBusy("reparent");
    setError(null);
    setReparentedSuccess(false);
    try {
      await reparentPeriziaSinistro(token, periziaId, expected, target,
        association.linkVersion);
      // The PUT committed one CAS update; the GET is authoritative in case
      // another writer has modified the association again since commit.
      const current = await getPeriziaSinistro(token, periziaId);
      const linked = current.sinistroId
        ? await getSinistro(token, current.sinistroId) : null;
      if (sequence.current !== requestId) return;
      setAssociation(current);
      setLinkedReference(linked?.externalReference || "");
      setReparentedSuccess(current.sinistroId === target);
      setReparenting(false);
      setConfirmReparent(false);
      setConfirmUnlink(false);
      resetSelection();
    } catch (cause) {
      if (sequence.current !== requestId) return;
      setReparentedSuccess(false);
      setConfirmReparent(false);
      if (isApiUnavailable(cause) ||
          (cause instanceof Error && cause.message === "HTTP 404")) {
        onUnavailable();
      } else if (cause instanceof Error && cause.message === "HTTP 409") {
        // The last observed association is no longer reliable. Do not retry
        // with a fresh expected value without the operator's choice.
        setAssociation(null);
        setLinkedReference("");
        setReparenting(false);
        resetSelection();
        setError("conflict");
      } else {
        setError("error");
      }
    } finally {
      if (sequence.current === requestId) {
        inFlight.current = false;
        setBusy(null);
      }
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
      {unlinkedSuccess && <p role="status" className="text-sm">{t("sinistroUnlinkSucceeded")}</p>}
      {reparentedSuccess && <p role="status" className="text-sm">{t("sinistroReparentSucceeded")}</p>}
      {association && hasRevision(association) &&
        <p className="text-sm text-slate-600 dark:text-slate-300">
          {t("sinistroLinkVersion")} {association.linkVersion}
        </p>}
      {association?.sinistroId && <>
        <p className="text-sm font-medium">{t("sinistroLinked")}</p>
        <p className="break-words text-sm">{linkedReference || association.sinistroId}</p>
        {canWrite && hasRevision(association) && !confirmUnlink && !reparenting && <Button type="button" variant="outline"
          disabled={busy !== null} onClick={() => setConfirmUnlink(true)}>
          {t("unlinkSinistro")}
        </Button>}
        {canReparent && hasRevision(association) && !confirmUnlink && !reparenting && <Button type="button" variant="outline"
          disabled={busy !== null} onClick={beginReparent}>{t("reparentSinistro")}</Button>}
        {canWrite && confirmUnlink && <div className="space-y-2">
          <p className="text-sm" role="status">{t("sinistroUnlinkWarning")}</p>
          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={busy !== null}
              onClick={() => void unlink()}>{t("confirmUnlinkSinistro")}</Button>
            <Button type="button" variant="outline" disabled={busy !== null}
              onClick={() => setConfirmUnlink(false)}>{t("cancelUnlinkSinistro")}</Button>
          </div>
        </div>}
      </>}
      {association && hasRevision(association) && (!association.sinistroId || reparenting) && <>
        {!association.sinistroId && <p className="text-sm text-slate-600 dark:text-slate-300">{t("sinistroUnlinked")}</p>}
        {reparenting && <p className="text-sm text-slate-600 dark:text-slate-300">{t("sinistroReparentHint")}</p>}
        <form onSubmit={event => void search(event)} className="flex flex-wrap items-end gap-2">
          <div className="min-w-0 flex-1">
            <label htmlFor={"sinistro-prefix-" + periziaId}
              className="mb-1 block text-sm">{t("sinistroSearch")}</label>
            <input id={"sinistro-prefix-" + periziaId} value={prefix} minLength={2} maxLength={100}
              autoComplete="off" disabled={busy !== null}
              onChange={event => {
                if (inFlight.current) return;
                // Input edits invalidate any candidate selection immediately.
                sequence.current += 1;
                setPrefix(event.target.value);
                setSelectedId("");
                setResults([]);
                setSearched(false);
                setConfirmReparent(false);
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
            onChange={event => {
              if (inFlight.current) return;
              setSelectedId(event.target.value);
              setConfirmReparent(false);
            }}
            className="block w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700">
            <option value="">{t("chooseSinistro")}</option>
            {results.map(item => <option key={item.id} value={item.id}
              disabled={reparenting && item.id === association.sinistroId}>
              {item.externalReference} · {item.occurredOn}
            </option>)}
          </select>
          {canWrite && hasRevision(association) && !reparenting &&
            <Button type="button" disabled={busy !== null || !selectedId}
              onClick={() => void link()}>{t("linkSinistro")}</Button>}
          {!canWrite && <p className="text-sm text-slate-600 dark:text-slate-300">{t("sinistroReadOnly")}</p>}
          {canWrite && reparenting && selectedId !== association.sinistroId && !confirmReparent &&
            <Button type="button" variant="outline" disabled={busy !== null || !selectedId}
              onClick={() => setConfirmReparent(true)}>{t("prepareReparentSinistro")}</Button>}
          {canWrite && reparenting && confirmReparent &&
            <div className="space-y-2">
              <p role="status" className="text-sm">{t("sinistroReparentWarning")}</p>
              <Button type="button" disabled={busy !== null}
                onClick={() => void reparent()}>{t("confirmReparentSinistro")}</Button>
            </div>}
        </>}
        {reparenting && canWrite && <Button type="button" variant="outline"
          disabled={busy !== null} onClick={cancelReparent}>{t("cancelReparentSinistro")}</Button>}
      </>}
    </div>}
  </div>;
}
