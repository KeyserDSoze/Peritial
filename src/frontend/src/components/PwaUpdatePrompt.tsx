import {useEffect, useState} from "react";
import {useTranslation} from "react-i18next";
import {registerSW} from "virtual:pwa-register";
import {useOnlineStatus} from "../useOnlineStatus";
import {Button} from "./ui/button";

/**
 * The waiting worker does not replace a loaded page automatically.
 * Activation is triggered solely by the user's Update now click; a user
 * drafting a perizia or uploading a document can postpone and reopen it.
 *
 * Nothing about the authenticated tenant or any unsaved form is stored in
 * service-worker state, CacheStorage, or localStorage.
 */
export function PwaUpdatePrompt() {
  const {t} = useTranslation();
  const online = useOnlineStatus();
  const [needRefresh, setNeedRefresh] = useState(false);
  const [deferred, setDeferred] = useState(false);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState(false);
  const [updateServiceWorker, setUpdateServiceWorker] =
    useState<() => Promise<void>>(() => async () => undefined);

  useEffect(() => {
    const update = registerSW({
      immediate: true,
      onNeedRefresh: () => {
        setNeedRefresh(true);
        setDeferred(false);
        setError(false);
      },
      onRegisterError: () => setError(true)
    });
    setUpdateServiceWorker(() => update);
  }, []);

  async function applyUpdate() {
    if (!online || applying || !needRefresh) return;
    setApplying(true);
    setError(false);
    try {
      // The virtual PWA client asks the waiting worker to activate and
      // reloads when it takes control. No other callback calls this.
      await updateServiceWorker();
    } catch {
      setError(true);
    } finally {
      setApplying(false);
    }
  }

  function deferUpdate() {
    setDeferred(true);
  }

  function reopenUpdate() {
    setDeferred(false);
  }

  if (!needRefresh || !online) return null;

  return <aside role="status" aria-live="polite"
    className="fixed bottom-4 right-4 z-50 max-w-sm rounded-xl border border-slate-300 bg-white p-4 shadow-xl dark:border-slate-600 dark:bg-slate-900">
    {deferred ? <Button type="button" variant="outline" onClick={reopenUpdate}>
      {t("pwaUpdateDeferred")}
    </Button> : <>
      <p className="font-semibold">{t("pwaUpdateAvailable")}</p>
      <p className="mb-3 mt-1 text-sm text-slate-600 dark:text-slate-300">
        {t("pwaUpdateWarning")}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button type="button" disabled={applying} onClick={applyUpdate}>
          {t("pwaUpdateNow")}
        </Button>
        <Button type="button" variant="outline" disabled={applying}
          onClick={deferUpdate}>{t("pwaUpdateLater")}</Button>
      </div>
    </>}
    {error && <p role="alert" className="mt-2 text-sm text-red-700 dark:text-red-400">
      {t("pwaUpdateError")}
    </p>}
  </aside>;
}
