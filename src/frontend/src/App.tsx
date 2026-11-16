import { useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "react-oidc-context";
import { useTranslation } from "react-i18next";
import { FileUp, Moon, Sun, ShieldCheck } from "lucide-react";
import { Button } from "./components/ui/button";
import { createPerizia, listPerizie, uploadDocument, type Perizia } from "./api";
import { PeriziaWorkflow } from "./components/PeriziaWorkflow";
import {preferredTheme, saveLanguage, saveTheme} from "./uiPreferences";
export default function App() {
  const auth = useAuth();
  const { t, i18n } = useTranslation();
  const [dark, setDark] = useState(() => preferredTheme() === "dark");
  const [items, setItems] = useState<Perizia[]>([]);
  // Each confirmed mutation increments a local revision. A GET started before
  // it must not overwrite that result; a later GET may refresh it normally.
  const mutations = useRef<Map<string, number>>(new Map());
  const [reference, setReference] = useState("");
  const [upload, setUpload] = useState<File|null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const token = auth.user?.access_token;
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
    // Do not leave another authenticated tenant's list visible after logout.
    if (!token) { setItems([]); mutations.current.clear(); }
  }, [token]);
  useEffect(() => {
    if (!token) return;
    let active = true;
    const revisionsAtStart = new Map(mutations.current);
    listPerizie(token).then(data => { if (active) setItems(current => {
        const changedAfterFetch = new Set([...mutations.current].filter(
          ([id, version]) => version !== (revisionsAtStart.get(id) ?? 0)
        ).map(([id]) => id));
        const incoming = new Set(data.map(item => item.id));
        return [
          ...current.filter(item => changedAfterFetch.has(item.id) || !incoming.has(item.id)),
          ...data.filter(item => !changedAfterFetch.has(item.id))
        ];
      }); }).catch(() => { if(active) setError(t("error")); });
    return () => { active = false; };
  }, [token, t]);
  async function create(event:FormEvent) {
    event.preventDefault(); if(!token || !reference.trim()) return;
    setBusy(true); setError("");
    try {
      const item = await createPerizia(token,reference.trim());
      mutations.current.set(item.id, (mutations.current.get(item.id) ?? 0) + 1);
      setItems(current => [item,...current]);
      setReference("");
    }
    catch { setError(t("error")); } finally { setBusy(false); }
  }
  async function sendFile() {
    if(!token || !upload) return;
    setBusy(true); setError("");
    try { await uploadDocument(token,upload); setUpload(null); }
    catch { setError(t("error")); } finally { setBusy(false); }
  }
  return <div className="min-h-screen">
    <header className="border-b border-slate-200 dark:border-slate-800"><div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-3 px-4 py-4">
      <div className="flex items-center gap-3"><div className="rounded-lg bg-sky-600 p-2 text-white"><FileUp size={24}/></div><strong className="text-lg">{t("title")}</strong></div>
      <div className="flex items-center gap-2">
        <Button variant="outline" aria-label={t("language")} onClick={toggleLanguage}>{i18n.language.toUpperCase()}</Button>
        <Button variant="outline" aria-label={t("theme")} aria-pressed={dark} onClick={toggleTheme}>{dark ? <Sun size={17}/> : <Moon size={17}/>}</Button>
        {auth.isAuthenticated && <Button variant="outline" onClick={() => void auth.signoutRedirect()}>{t("signOut")}</Button>}
      </div>
    </div></header>
    <main className="mx-auto max-w-5xl px-4 py-10 sm:py-14">
      <div className="mb-8"><p className="mb-2 flex items-center gap-2 text-xs uppercase tracking-widest text-sky-700 dark:text-sky-300"><ShieldCheck size={15}/>{t("tenant")}</p>
        <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">{t("welcome")}</h1><p className="mt-2 text-slate-500 dark:text-slate-400">{t("subtitle")}</p></div>
      {auth.isLoading ? <p>{t("loading")}</p> : !auth.isAuthenticated ?
        <section className="max-w-lg rounded-xl border border-slate-200 bg-white p-7 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <p className="mb-6 text-slate-600 dark:text-slate-300">{t("signInHelp")}</p>
          <Button onClick={() => void auth.signinRedirect()}>{t("signIn")}</Button>
        </section> :
        <div className="grid gap-6 lg:grid-cols-[2fr_1fr]">
          <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
            <h2 className="mb-5 text-xl font-semibold">{t("perizie")}</h2>
            <form onSubmit={event => void create(event)} className="mb-6 flex flex-wrap gap-2">
              <label className="sr-only" htmlFor="reference">{t("reference")}</label>
              <input id="reference" className="min-w-0 flex-1 rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" value={reference} onChange={e=>setReference(e.target.value)} maxLength={160} placeholder={t("newPerizia")} required />
              <Button type="submit" disabled={busy}>{t("create")}</Button>
            </form>
            {items.length===0 ? <p className="text-slate-500">{t("empty")}</p> :
              <ul className="divide-y divide-slate-200 dark:divide-slate-800">{items.map(item =>
                <li key={item.id} className="py-4">
                  <PeriziaWorkflow item={item} token={token!} onUpdated={updated => {
                    mutations.current.set(updated.id, (mutations.current.get(updated.id) ?? 0) + 1);
                    setItems(current => current.map(value => value.id === updated.id ? updated : value));
                  }} />
                </li>)}</ul>}
          </section>
          <section className="h-fit rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
            <h2 className="mb-5 text-xl font-semibold">{t("upload")}</h2>
            <label htmlFor="file" className="mb-2 block text-sm">{t("file")}</label>
            <input id="file" type="file" accept=".pdf,.xml,.jpg,.jpeg,.png,.docx" className="mb-4 block w-full text-sm" onChange={e=>setUpload(e.target.files?.[0]||null)}/>
            <Button disabled={!upload||busy} onClick={() => void sendFile()}><FileUp size={16}/>{t("upload")}</Button>
          </section>
        </div>}
      {error && <p role="alert" className="mt-6 text-red-600">{error}</p>}
    </main>
  </div>;
}