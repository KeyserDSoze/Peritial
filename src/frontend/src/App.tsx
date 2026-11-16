import { useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "react-oidc-context";
import { useTranslation } from "react-i18next";
import { FileUp, Moon, Sun, ShieldCheck } from "lucide-react";
import { Button } from "./components/ui/button";
import { createPerizia, isApiUnavailable, listDocuments, listPerizie, uploadDocument, type DocumentSummary, type Perizia } from "./api";
import { PeriziaWorkflow } from "./components/PeriziaWorkflow";
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
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [dataStatus, setDataStatus] = useState<"loading" | "ready" | "unavailable">("loading");
  const [refreshIndex, setRefreshIndex] = useState(0);
  const token = auth.user?.access_token;
  function lockProtectedUi() {
    // Never retain a potentially stale tenant record after REST, auth or
    // network failure. Invalidate any pre-existing GET that may finish late.
    requestEpoch.current += 1;
    setVerifiedToken(null);
    mutations.current.clear();
    setItems([]);
    setDocuments([]);
    setUploadPeriziaId("");
    setDataStatus("unavailable");
    setError("");
  }
  function refreshPerizie() {
    if (!online || !token) return;
    setDataStatus("loading");
    setRefreshIndex(value => value + 1);
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
      activeToken.current = token;
      requestEpoch.current += 1;
      mutations.current.clear();
      setItems([]);
      setDocuments([]);
      setUploadPeriziaId("");
      setVerifiedToken(null);
    }
    // Never display locally retained tenant data while disconnected.
    // After reconnect the app must fetch new data using the current JWT.
    if (!token || !online) {
      requestEpoch.current += 1;
      setItems([]);
      setDocuments([]);
      mutations.current.clear();
      setError("");
      setReference("");
      setUpload(null);
      setUploadPeriziaId("");
      setDataStatus("loading");
    }
  }, [token, online]);
  useEffect(() => {
    if (!token || !online) return;
    let active = true;
    setDataStatus("loading");
    const epochAtStart = requestEpoch.current;
    const revisionsAtStart = new Map(mutations.current);
    // Fetch both scoped projections before making any write controls
    // available. A failure of either one fails closed.
    Promise.all([listPerizie(token), listDocuments(token)]).then(([data, latestDocuments]) => {
      if (!active || requestEpoch.current !== epochAtStart) return;
      setItems(current => {
        const changedAfterFetch = new Set([...mutations.current].filter(
          ([id, version]) => version !== (revisionsAtStart.get(id) ?? 0)
        ).map(([id]) => id));
        const incoming = new Set(data.map(item => item.id));
        return [
          ...current.filter(item => changedAfterFetch.has(item.id) || !incoming.has(item.id)),
          ...data.filter(item => !changedAfterFetch.has(item.id))
        ];
      });
      setDocuments(latestDocuments);
      // A case removed from the authenticated projection must not remain selectable.
      setUploadPeriziaId(value => value && !data.some(item => item.id === value) ? "" : value);
      setVerifiedToken(token);
      setDataStatus("ready");
      setError("");
    }).catch(() => {
      if (active) lockProtectedUi();
    });
    return () => { active = false; };
  }, [token, t, online, refreshIndex]);
  async function create(event:FormEvent) {
    event.preventDefault(); if(!token || !online || dataStatus !== "ready" || !reference.trim()) return;
    setBusy(true); setError("");
    try {
      const epochAtStart = requestEpoch.current;
      const item = await createPerizia(token,reference.trim());
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      mutations.current.set(item.id, (mutations.current.get(item.id) ?? 0) + 1);
      setItems(current => [item,...current]);
      setReference("");
    }
    catch (cause) {
      if (isApiUnavailable(cause)) lockProtectedUi();
      else setError(t("error"));
    } finally { setBusy(false); }
  }
  async function sendFile() {
    if(!token || !online || dataStatus !== "ready" || !upload ||
      (uploadPeriziaId !== "" && !items.some(item => item.id === uploadPeriziaId))) return;
    setBusy(true); setError("");
    try {
      const epochAtStart = requestEpoch.current;
      await uploadDocument(token, upload, uploadPeriziaId || undefined);
      // Never replay an uncertain POST. Instead refresh both read-only
      // projections with the signed current tenant after a confirmed upload.
      if (requestEpoch.current !== epochAtStart || activeToken.current !== token) return;
      setUpload(null);
      setUploadPeriziaId("");
      refreshPerizie();
    }
    catch (cause) {
      if (isApiUnavailable(cause)) lockProtectedUi();
      else setError(t("error"));
    } finally { setBusy(false); }
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
            <form onSubmit={event => void create(event)} className="mb-6 flex flex-wrap gap-2">
              <label className="sr-only" htmlFor="reference">{t("reference")}</label>
              <input id="reference" className="min-w-0 flex-1 rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" value={reference} onChange={e=>setReference(e.target.value)} maxLength={160} placeholder={t("newPerizia")} required />
              <Button type="submit" disabled={busy}>{t("create")}</Button>
            </form>
            {items.length===0 ? <p className="text-slate-500">{t("empty")}</p> :
              <ul className="divide-y divide-slate-200 dark:divide-slate-800">{items.map(item =>
                <li key={item.id} className="py-4">
                  <PeriziaWorkflow item={item} token={token!} onUnavailable={lockProtectedUi} onUpdated={updated => {
                    mutations.current.set(updated.id, (mutations.current.get(updated.id) ?? 0) + 1);
                    setItems(current => current.map(value => value.id === updated.id ? updated : value));
                  }} />
                </li>)}</ul>}
          </section>
          <section className="h-fit rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
            <h2 className="mb-5 text-xl font-semibold">{t("upload")}</h2>
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
            <div className="mt-8 border-t border-slate-200 pt-5 dark:border-slate-700">
              <h3 className="mb-3 font-semibold">{t("savedDocuments")}</h3>
              <p className="mb-3 text-xs text-slate-500 dark:text-slate-400">{t("metadataOnly")}</p>
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
                  </li>)}
                </ul>}
            </div>
          </section>
        </div>}
      {error && <p role="alert" className="mt-6 text-red-600">{error}</p>}
    </main>
    <PwaUpdatePrompt />
  </div>;
}