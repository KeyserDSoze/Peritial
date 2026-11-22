import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {getSinistro, isApiUnavailable, updateSinistro, type SinistroSummary} from "../api";
import {Button} from "./ui/button";

type Props = {
  id: string;
  token: string;
  onConfirmed: (item: SinistroSummary) => void;
  onConflict: (id: string) => void;
  onUnavailable: () => void;
};

/**
 * One Sinistro can be edited only after an authoritative signed GET.
 * A failed CAS discards both the stale snapshot and the form: the operator
 * explicitly reloads and inspects the winning version before retrying.
 */
export function SinistroEditor({id, token, onConfirmed, onConflict, onUnavailable}: Props) {
  const {t} = useTranslation();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState<"read" | "save" | null>(null);
  const [snapshot, setSnapshot] = useState<SinistroSummary | null>(null);
  const [reference, setReference] = useState("");
  const [occurredOn, setOccurredOn] = useState("");
  const [reportedOn, setReportedOn] = useState("");
  const [error, setError] = useState<"invalid" | "conflict" | "read" | "save" | null>(null);
  const [saved, setSaved] = useState(false);
  const epoch = useRef(0);
  // A synchronous CAS guard, independent of React setBusy render timing.
  const saveInFlight = useRef(false);

  // A previous JWT, case, or component lifetime must not restore late data.
  useEffect(() => () => {
    epoch.current += 1;
    saveInFlight.current = false;
  }, [id, token]);

  function discardFields() {
    setSnapshot(null);
    setReference("");
    setOccurredOn("");
    setReportedOn("");
    setSaved(false);
  }

  async function reload() {
    if (busy || saveInFlight.current) return;
    const current = ++epoch.current;
    setBusy("read");
    setError(null);
    discardFields();
    try {
      const record = await getSinistro(token, id);
      if (epoch.current !== current) return;
      setSnapshot(record);
      setReference(record.externalReference);
      setOccurredOn(record.occurredOn);
      setReportedOn(record.reportedOn ?? "");
      onConfirmed(record);
    } catch (cause) {
      if (epoch.current !== current) return;
      // Missing and foreign records cannot remain visible in the catalog.
      if (isApiUnavailable(cause) ||
          (cause instanceof Error && cause.message === "HTTP 404"))
        onUnavailable();
      else setError("read");
    } finally {
      if (epoch.current === current) setBusy(null);
    }
  }

  function toggle() {
    if (busy || saveInFlight.current) return;
    if (open) {
      epoch.current += 1;
      setOpen(false);
      setError(null);
      discardFields();
    } else {
      setOpen(true);
      void reload();
    }
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    const normalized = reference.trim();
    if (busy || saveInFlight.current || !snapshot || !open ||
        !normalized || normalized.length > 100 || !occurredOn) return;
    saveInFlight.current = true;
    const expectedVersion = snapshot.version;
    const current = ++epoch.current;
    setBusy("save");
    setError(null);
    setSaved(false);
    try {
      const updated = await updateSinistro(token, id, normalized, occurredOn,
        reportedOn || null, expectedVersion);
      if (epoch.current !== current) return;
      // Only the successful CAS response is authoritative, including its
      // server-generated next version. Never assume expectedVersion+1.
      setSnapshot(updated);
      setReference(updated.externalReference);
      setOccurredOn(updated.occurredOn);
      setReportedOn(updated.reportedOn ?? "");
      onConfirmed(updated);
      setSaved(true);
    } catch (cause) {
      if (epoch.current !== current) return;
      if (cause instanceof Error && cause.message === "HTTP 409") {
        // The winning version is unknown. Hide even the stale catalog row
        // until the operator chooses to reload it using the current JWT.
        discardFields();
        setError("conflict");
        onConflict(id);
      } else if (isApiUnavailable(cause) ||
                 (cause instanceof Error && cause.message === "HTTP 404")) {
        onUnavailable();
      } else if (cause instanceof Error && cause.message === "HTTP 400") {
        setError("invalid");
      } else {
        setError("save");
      }
    } finally {
      if (epoch.current === current) {
        saveInFlight.current = false;
        setBusy(null);
      }
    }
  }

  return <div className="mt-2">
    <Button type="button" variant="outline" disabled={busy !== null}
      aria-expanded={open} onClick={toggle}>
      {t(open ? "closeSinistroEdit" : "editSinistro")}
    </Button>
    {open && <div className="mt-3 space-y-2">
      {busy === "read" && <p role="status" className="text-sm">{t("loading")}</p>}
      {error && <p role="alert" className="text-sm text-red-700 dark:text-red-400">
        {t(error === "conflict" ? "sinistroEditConflict" :
           error === "invalid" ? "sinistroEditInvalid" :
           "sinistroEditError")}
      </p>}
      {error === "conflict" && <Button type="button" variant="outline"
        disabled={busy !== null} onClick={() => void reload()}>
        {t("reloadSinistro")}
      </Button>}
      {snapshot && <>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {t("sinistroVersion")} {snapshot.version}
        </p>
        <form onSubmit={event => void save(event)} className="space-y-2">
          <div>
            <label htmlFor={"sinistro-edit-ref-" + id} className="mb-1 block text-sm">
              {t("editSinistroReference")}
            </label>
            <input id={"sinistro-edit-ref-" + id} value={reference} autoComplete="off"
              required maxLength={100} disabled={busy !== null}
              onChange={event => {setReference(event.target.value);setSaved(false);setError(null);}}
              className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
          </div>
          <div className="grid gap-2 sm:grid-cols-2">
            <div>
              <label htmlFor={"sinistro-edit-occurred-" + id} className="mb-1 block text-sm">
                {t("editSinistroOccurredOn")}
              </label>
              <input id={"sinistro-edit-occurred-" + id} type="date" required
                value={occurredOn} disabled={busy !== null}
                onChange={event => {setOccurredOn(event.target.value);setSaved(false);setError(null);}}
                className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
            </div>
            <div>
              <label htmlFor={"sinistro-edit-reported-" + id} className="mb-1 block text-sm">
                {t("editSinistroReportedOn")}
              </label>
              <input id={"sinistro-edit-reported-" + id} type="date"
                value={reportedOn} disabled={busy !== null}
                onChange={event => {setReportedOn(event.target.value);setSaved(false);setError(null);}}
                className="w-full rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
            </div>
          </div>
          <Button type="submit" disabled={busy !== null ||
            !reference.trim() || !occurredOn}>{t("saveSinistroEdit")}</Button>
        </form>
        {saved && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400">
          {t("sinistroUpdated")}
        </p>}
      </>}
    </div>}
  </div>;
}
