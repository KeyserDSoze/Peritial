import { useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "react-oidc-context";
import { useTranslation } from "react-i18next";
import { FileUp, Moon, Sun, ShieldCheck } from "lucide-react";
import { Button } from "./components/ui/button";
import { createPerizia, downloadDocument, isApiUnavailable, pageDocuments, listPerizie, uploadDocument, type DocumentSummary, type Perizia } from "./api";
import { PeriziaWorkflow } from "./components/PeriziaWorkflow";
import {PeriziaSinistroPicker} from "./components/PeriziaSinistroPicker";
import {SinistroCatalog} from "./components/SinistroCatalog";
import {preferredTheme, saveLanguage, saveTheme} from "./uiPreferences";
import {useOnlineStatus} from "./useOnlineStatus";
import {PwaUpdatePrompt} from "./components/PwaUpdatePrompt";
export default function App() {
  const auth = useAuth();
  const { t, i18n } = useTranslation();
  const online = useOnlineStatus();
  const [dark, setDark] = useState(() => preferredTheme() === "dark");
  const [items, setItems] = useState<Perizia[]>([]);
  const [documents, setDocuments] = useState<DocumentSummary[]>([]);
  const [documentsNextCursor, setDocumentsNextCursor] = useState<string | null>(null);
  const [documentsLoadingMore, setDocumentsLoadingMore] = useState(false);
  const [downloadingDocumentId, setDownloadingDocumentId] = useState<string | null>(null);
  // A single active buffered download bounds concurrent browser memory usage.
  const activeDocumentDownload = useRef<{
    id: string; token: string; controller: AbortController
  } | null>(null);
  function cancelDocumentDownload() {
    const current = activeDocumentDownload.current;
    activeDocumentDownload.current = null;
    current?.controller.abort();
    setDownloadingDocumentId(null);
  }
  useEffect(() => () => { activeDocumentDownload.current?.controller.abort(); }, []);
  // A cursor belongs to exactly one signed projection. React's state does not
  // synchronously block two click events in the same task.
  const documentsPageSequence = useRef(0);
  const activeDocumentsPage = useRef<number | null>(null);
  const documentsProjectionGeneration = useRef(0);
  function cancelDocumentsContinuation() {
    // An obsolete page must not release a newer request's lock.
    activeDocumentsPage.current = null;
    setDocumentsLoadingMore(false);
  }
  // Each confirmed mutation increments a local revision. A GET started before
  // it must not overwrite that result; a later GET may refresh it normally.
  const mutations = useRef<Map<string, number>>(new Map());
  // A failed mutation must invalidate GETs that began before that failure.
  const requestEpoch = useRef(0);
  const activeToken = useRef<string | undefined>(undefined);
  const [verifiedToken, setVerifiedToken] = useState<string | null>(null);
  const [reference, setReference] = useState("");
  const [upload, setUpload] = useState<File|null>(null);
  const [uploadPeriziaId, setUploadPeriziaId] = useState("");
  const [documentsPeriziaId, setDocumentsPeriziaId] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  // A ref is an immediate, shared lock for create and upload. React's busy
  // state only disables controls after a render, so it cannot stop two
  // synchronous requestSubmit()/click events from starting duplicate POSTs.
  const mutationSequence = useRef(0);
  const activeMutation = useRef<number | null>(null);
  const [dataStatus, setDataStatus] = useState<"loading" | "ready" | "unavailable">("loading");
  const [refreshIndex, setRefreshIndex] = useState(0);
  const token = auth.user?.access_token;
  // UI hints only: REST/MCP enforce the real signed JWT scope. A
  // read-only identity must not be offered any write action in the PWA.
  // Do not infer write access merely from "authenticated" or "online".
  const canWrite = Boolean(auth.user?.scope?.split(/\s+/).includes("peritial.write"));
  function lockProtectedUi() {
    cancelDocumentDownload();
    // Never retain a potentially stale tenant record after REST, auth or
    // network failure. Invalidate any pre-existing GET that may finish late.
    requestEpoch.current += 1;
    setVerifiedToken(null);
    mutations.current.clear();
    setItems([]);
    setDocuments([]);
    setDocumentsNextCursor(null);
    cancelDocumentsContinuation();
    setUploadPeriziaId("");
    setDocumentsPeriziaId("");
    setDataStatus("unavailable");
    setError("");
  }
  function refreshPerizie() {
    if (!online || !token) return;
    cancelDocumentDownload();
    requestEpoch.current += 1;
    setDocumentsNextCursor(null);
    cancelDocumentsContinuation();
    setDataStatus("loading");
    setRefreshIndex(value => value + 1);
  }
  function changeDocumentFilter(periziaId: string) {
    cancelDocumentDownload();
    // Do not render an old tenant-scoped projection for the new filter, even
    // if the previous GET resolves just before React runs effect cleanup.
    requestEpoch.current += 1;
    setDocuments([]);
    setDocumentsNextCursor(null);
    cancelDocumentsContinuation();
    setDataStatus("loading");
    setDocumentsPeriziaId(periziaId);
  }
  useEffect(() => { document.documentElement.classList.toggle("dark", dark); }, [dark]);
  useEffect(() => {
    document.documentElement.lang = i18n.language === "en" ? "en" : "it";
  }, [i18n.language]);
  function toggleLanguage() {
    const next = i18n.language === "it" ? "en" : "it";
    void i18n.changeLanguage(next);
    saveLanguage(next);
  }
  function toggleTheme() {
    const next = !dark;
    setDark(next);
    saveTheme(next ? "dark" : "light");
  }
  useEffect(() => {
    // A different signed-in identity must never inherit the previous tenant's
    // locally merged records, even for one render while the next GET is pending.
    if (activeToken.current !== token) {
      cancelDocumentDownload();
      // An old identity's pending write must not keep the next signed
      // session locked or clear a new session's in-flight state on settle.
      activeMutation.current = null;
      setBusy(false);
      activeToken.current = token;
      requestEpoch.current += 1;
      mutations.current.clear();
      setItems([]);
      setDocuments([]);
      setDocumentsNextCursor(null);
      cancelDocumentsContinuation();
      setUploadPeriziaId("");
      setDocumentsPeriziaId("");
      setVerifiedToken(null);
    }
    // Never display locally retained tenant data while disconnected.
    // After reconnect the app must fetch new data using the current JWT.
    if (!token || !online) {
      cancelDocumentDownload();
      activeMutation.current = null;
      setBusy(false);
      requestEpoch.current += 1;
      setItems([]);
      setDocuments([]);
      setDocumentsNextCursor(null);
      cancelDocumentsContinuation();
      mutations.current.clear();
      setError("");
      setReference("");
      setUpload(null);
      setUploadPeriziaId("");
      setDocumentsPeriziaId("");
      setDataStatus("loading");
    }
  }, [token, online]);
  useEffect(() => {
    if (!token || !online) return;
    let active = true;
    cancelDocumentDownload();
    // A new authenticated base-page read supersedes every old continuation,
    // including a language-triggered refetch with an unchanged parent epoch.
    documentsProjectionGeneration.current += 1;
    cancelDocumentsContinuation();
    setDataStatus("loading");
    setDocumentsNextCursor(null);
    const epochAtStart = requestEpoch.current;
    const revisionsAtStart = new Map(mutations.current);
    // Fetch both scoped projections before making any write controls
    // available. A failure of either one fails closed.
    Promise.all([listPerizie(token), pageDocuments(token, documentsPeriziaId || undefined)]).then(([data, latestDocuments]) => {
      if (!active || requestEpoch.current !== epochAtStart) return;
      setItems(current => {
        const changedAfterFetch = new Set([...mutations.current].filter(
          ([id, version]) => version !== (revisionsAtStart.get(id) ?? 0)
        ).map(([id]) => id));
        // The completed signed GET is authoritative for membership.
        // Do not keep a case missing from it merely because it was visible
        // before refresh: its visibility may have been revoked or removed.
        // Retain only a mutation confirmed AFTER this GET began, because
        // that result must not be overwritten by an older read.
        return [
          ...current.filter(item => changedAfterFetch.has(item.id)),
          ...data.filter(item => !changedAfterFetch.has(item.id))
        ];
      });
      setDocuments(latestDocuments.items);
      setDocumentsNextCursor(latestDocuments.nextCursor);
      // A case removed from the authenticated projection must not remain selectable.
      setUploadPeriziaId(value => value && !data.some(item => item.id === value) ? "" : value);
      setDocumentsPeriziaId(value => value && !data.some(item => item.id === value) ? "" : value);
      setVerifiedToken(token);
      setDataStatus("ready");
      setError("");
    }).catch(() => {
      // An earlier failed GET cannot lock a view that has already been
      // superseded by a newer authenticated refresh or document filter.
      if (active && requestEpoch.current === epochAtStart &&
          activeToken.current === token) lockProtectedUi();
    });
    return () => { active = false; };
  }, [token, t, online, refreshIndex, documentsPeriziaId]);
  async function loadMoreDocuments() {
    if (!token || !online || dataStatus !== "ready" || verifiedToken !== token ||
        busy || documentsLoadingMore || activeDocumentsPage.current !== null ||
        !documentsNextCursor) return;
    // Claim the current cursor synchronously, before the first await.
    const operation = ++documentsPageSequence.current;
    activeDocumentsPage.current = operation;
    const epochAtStart = requestEpoch.current;
    const projectionAtStart = documentsProjectionGeneration.current;
    const filter = documentsPeriziaId;
    const cursor = documentsNextCursor;
    const stillCurrent = () =>
      activeDocumentsPage.current === operation &&
      documentsProjectionGeneration.current === projectionAtStart &&
      requestEpoch.current === epochAtStart && activeToken.current === token;
    setDocumentsLoadingMore(true);
    setError("");
    try {
      const result = await pageDocuments(token, filter || undefined, cursor);
      if (!stillCurrent()) return;
      setDocuments(previous => {
        const known = new Set(previous.map(item => item.id));
        return [...previous, ...result.items.filter(item => !known.has(item.id))];
      });
      setDocumentsNextCursor(result.nextCursor);
    } catch (cause) {
      if (!stillCurrent()) return;
      if (isApiUnavailable(cause)) lockProtectedUi();
      else {
        // An invalid cursor cannot be replayed: discard the partial page.
        setDocuments([]);
        setDocumentsNextCursor(null);
        setError(t("documentPageError"));
      }
    } finally {
      if (activeDocumentsPage.current === operation) cancelDocumentsContinuation();
    }
  }
  async function saveDocument(doc: DocumentSummary) {
    if (!token || !online || dataStatus !== "ready" || verifiedToken !== token ||
        busy || activeDocumentDownload.current !== null ||
        !documents.some(item => item.id === doc.id)) return;
    const operation = {id: doc.id, token, controller: new AbortController()};
    activeDocumentDownload.current = operation; // synchronous single-flight lock
    const epochAtStart = requestEpoch.current;
    const projectionAtStart = documentsProjectionGeneration.current;
    const stillCurrent = () =>
      activeDocumentDownload.current === operation &&
      requestEpoch.current === epochAtStart &&
      documentsProjectionGeneration.current === projectionAtStart &&
      activeToken.current === token && online;
    setDownloadingDocumentId(doc.id);
    setError("");
    try {
      const content = await downloadDocument(token, doc.id, doc.length, operation.controller.signal);
      if (!stillCurrent()) return; // no file from an obsolete tenant/session/filter
      const objectUrl = URL.createObjectURL(content);
      const anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = doc.fileName; // server-canonicalized file name
      anchor.hidden = true;
      document.body.appendChild(anchor);
      try { anchor.click(); }
      finally {
        anchor.remove();
        // Give browsers time to start the download before releasing the URL.
        window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
      }
    } catch (cause) {
      if (!stillCurrent() || operation.controller.signal.aborted) return;
      if (isApiUnavailable(cause)) lockProtectedUi();
      else setError(t("documentDownloadError"));
    } finally {
      if (activeDocumentDownload.current === operation) {
        activeDocumentDownload.current = null;
        setDownloadingDocumentId(null);
      }
    }
  }
  async function create(event:FormEvent) {
    event.preventDefault(); if(!token || !online || !canWrite ||
      dataStatus !== "ready" || !reference.trim() ||
      activeMutation.current !== null) return;
    const operation = ++mutationSequence.current;
    activeMutation.current = operation;
    const epochAtStart = requestEpoch.current;
    setBusy(true); setError("");
    try {
      const item = await createPerizia(token,reference.trim());
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      mutations.current.set(item.id, (mutations.current.get(item.id) ?? 0) + 1);
      setItems(current => [item,...current]);
      setReference("");
    }
    catch (cause) {
      // The POST may fail after a later signed GET already restored the UI.
      // Only the current epoch may transition to the unavailable state.
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      if (isApiUnavailable(cause)) lockProtectedUi();
      else setError(t("error"));
    } finally {
      if (activeMutation.current === operation) {
        activeMutation.current = null;
        setBusy(false);
      }
    }
  }
  async function sendFile() {
    if(!token || !online || !canWrite || dataStatus !== "ready" || !upload ||
      (uploadPeriziaId !== "" && !items.some(item => item.id === uploadPeriziaId)) ||
      activeMutation.current !== null) return;
    const operation = ++mutationSequence.current;
    activeMutation.current = operation;
    const epochAtStart = requestEpoch.current;
    setBusy(true); setError("");
    try {
      await uploadDocument(token, upload, uploadPeriziaId || undefined);
      // Never replay an uncertain POST. Instead refresh both read-only
      // projections with the signed current tenant after a confirmed upload.
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      setUpload(null);
      setUploadPeriziaId("");
      refreshPerizie();
    }
    catch (cause) {
      // An obsolete upload failure must not invalidate an authenticated
      // projection established after its request began; never auto-retry PUT.
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      if (isApiUnavailable(cause)) lockProtectedUi();
      else setError(t("error"));
    } finally {
      if (activeMutation.current === operation) {
        activeMutation.current = null;
        setBusy(false);
      }
    }
  }
  return <div className="min-h-screen">
    <header className="border-b border-slate-200 dark:border-slate-800"><div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-3 px-4 py-4">
      <div className="flex items-center gap-3"><div className="rounded-lg bg-sky-600 p-2 text-white"><FileUp size={24}/></div><strong className="text-lg">{t("title")}</strong></div>
      <div className="flex items-center gap-2">
        <Button variant="outline" aria-label={t("language")} onClick={toggleLanguage}>{i18n.language.toUpperCase()}</Button>
        <Button variant="outline" aria-label={t("theme")} aria-pressed={dark} onClick={toggleTheme}>{dark ? <Sun size={17}/> : <Moon size={17}/>}</Button>
        {auth.isAuthenticated && online && <Button variant="outline" onClick={() => void auth.signoutRedirect()}>{t("signOut")}</Button>}
      </div>
    </div></header>
    <main className="mx-auto max-w-5xl px-4 py-10 sm:py-14">
      <div className="mb-8"><p className="mb-2 flex items-center gap-2 text-xs uppercase tracking-widest text-sky-700 dark:text-sky-300"><ShieldCheck size={15}/>{t("tenant")}</p>
        <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">{t("welcome")}</h1><p className="mt-2 text-slate-500 dark:text-slate-400">{t("subtitle")}</p></div>
      {!online ? <section role="status" aria-live="polite" className="max-w-xl rounded-xl border border-amber-300 bg-white p-7 shadow-sm dark:border-amber-700 dark:bg-slate-900">
          <h2 className="mb-3 text-xl font-semibold">{t("offlineTitle")}</h2>
          <p className="text-slate-700 dark:text-slate-300">{t("offlineMessage")}</p>
        </section> : auth.isLoading ? <p>{t("loading")}</p> : !auth.isAuthenticated ?
        <section className="max-w-lg rounded-xl border border-slate-200 bg-white p-7 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <p className="mb-6 text-slate-600 dark:text-slate-300">{t("signInHelp")}</p>
          <Button onClick={() => void auth.signinRedirect()}>{t("signIn")}</Button>
        </section> :
        dataStatus === "unavailable" ?
        <section role="alert" className="max-w-xl rounded-xl border border-amber-300 bg-white p-7 shadow-sm dark:border-amber-700 dark:bg-slate-900">
          <h2 className="mb-3 text-xl font-semibold">{t("apiUnavailableTitle")}</h2>
          <p className="mb-4 text-slate-700 dark:text-slate-300">{t("apiUnavailableMessage")}</p>
          <Button type="button" onClick={refreshPerizie}>{t("retryConnection")}</Button>
        </section> : (dataStatus === "loading" || verifiedToken !== token) ? <p role="status">{t("loading")}</p> :
        <div className="grid gap-6 lg:grid-cols-[2fr_1fr]">
          <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
            <div className="mb-5 flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-xl font-semibold">{t("perizie")}</h2>
              <Button type="button" variant="outline" disabled={busy} onClick={refreshPerizie}>{t("refreshList")}</Button>
            </div>
            {canWrite && <form onSubmit={event => void create(event)} className="mb-6 flex flex-wrap gap-2">
              <label className="sr-only" htmlFor="reference">{t("reference")}</label>
              <input id="reference" className="min-w-0 flex-1 rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" value={reference} onChange={e=>setReference(e.target.value)} maxLength={160} placeholder={t("newPerizia")} required />
              <Button type="submit" disabled={busy}>{t("create")}</Button>
            </form>}
            {items.length===0 ? <p className="text-slate-500">{t("empty")}</p> :
              <ul aria-label={t("perizie")} className="divide-y divide-slate-200 dark:divide-slate-800">{items.map(item =>
                <li key={item.id} data-perizia-id={item.id} className="py-4">
                  <PeriziaWorkflow item={item} token={token!} canWrite={canWrite} onUnavailable={lockProtectedUi} onUpdated={updated => {
                    mutations.current.set(updated.id, (mutations.current.get(updated.id) ?? 0) + 1);
                    setItems(current => current.map(value => value.id === updated.id ? updated : value));
                  }} />
                  <PeriziaSinistroPicker periziaId={item.id} periziaStatus={item.status}
                    token={token!} canWrite={canWrite} onUnavailable={lockProtectedUi} />
                </li>)}</ul>}
            <SinistroCatalog token={token!} canWrite={canWrite} onUnavailable={lockProtectedUi} />
          </section>
          <section className="h-fit rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
            <h2 className="mb-5 text-xl font-semibold">{t(canWrite ? "upload" : "savedDocuments")}</h2>
            {canWrite && <>
            <label htmlFor="upload-perizia" className="mb-2 block text-sm">{t("linkedPerizia")}</label>
            <select id="upload-perizia" value={uploadPeriziaId} disabled={busy}
              onChange={event => setUploadPeriziaId(event.target.value)}
              className="mb-4 block w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700">
              <option value="">{t("noLinkedPerizia")}</option>
              {items.map(item => <option key={item.id} value={item.id}>{item.reference}</option>)}
            </select>
            <label htmlFor="file" className="mb-2 block text-sm">{t("file")}</label>
            <input id="file" type="file" accept=".pdf,.xml,.jpg,.jpeg,.png,.docx" className="mb-4 block w-full text-sm" onChange={e=>setUpload(e.target.files?.[0]||null)}/>
            <Button disabled={!upload||busy} onClick={() => void sendFile()}><FileUp size={16}/>{t("upload")}</Button>
            </>}
            <div className={canWrite ? "mt-8 border-t border-slate-200 pt-5 dark:border-slate-700" : ""}>
              {canWrite && <h3 className="mb-3 font-semibold">{t("savedDocuments")}</h3>}
              <label htmlFor="documents-perizia" className="mb-2 block text-sm">{t("documentsFilter")}</label>
              <select id="documents-perizia" value={documentsPeriziaId}
                onChange={event => changeDocumentFilter(event.target.value)}
                className="mb-3 block w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700">
                <option value="">{t("allDocuments")}</option>
                {items.map(item => <option key={item.id} value={item.id}>{item.reference}</option>)}
              </select>
              <p className="mb-3 text-xs text-slate-500 dark:text-slate-400">{t("documentAccessHint")}</p>
              {documents.length === 0 ? <p className="text-sm text-slate-500">{t("noDocuments")}</p> :
                <ul aria-label={t("savedDocuments")} className="divide-y divide-slate-200 dark:divide-slate-700">
                  {documents.map(doc => <li key={doc.id} className="py-3">
                    <p className="break-all text-sm font-medium">{doc.fileName}</p>
                    {doc.periziaId && <p className="mt-1 break-words text-xs text-slate-500 dark:text-slate-400">
                      {t("linkedPerizia")}: {items.find(item => item.id === doc.periziaId)?.reference ?? t("linkedPeriziaUnknown")}
                    </p>}
                    <p className="mt-1 break-words text-xs text-slate-500 dark:text-slate-400">
                      {doc.contentType} · {doc.length} {t("bytes")} ·
                      {" "}{new Date(doc.createdAtUtc).toLocaleDateString(i18n.language === "en" ? "en-GB" : "it-IT")}
                    </p>
                    <Button type="button" variant="outline"
                      aria-label={t("downloadDocument") + ": " + doc.fileName}
                      disabled={busy || downloadingDocumentId !== null || !online || documentsLoadingMore}
                      onClick={() => void saveDocument(doc)}
                      className="mt-2">
                      {downloadingDocumentId === doc.id ? t("downloadingDocument") : t("downloadDocument")}
                    </Button>
                  </li>)}
                </ul>}
               {documentsNextCursor && <Button type="button" variant="outline"
                 disabled={busy || documentsLoadingMore || !online}
                 onClick={() => void loadMoreDocuments()}>
                   {documentsLoadingMore ? t("loading") : t("documentCatalogMore")}
               </Button>}
            </div>
          </section>
        </div>}
      {error && <p role="alert" className="mt-6 text-red-600">{error}</p>}
    </main>
    <PwaUpdatePrompt />
  </div>;
}