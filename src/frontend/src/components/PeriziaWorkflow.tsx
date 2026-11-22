import {useEffect, useRef, useState, type FormEvent} from "react";
import {useTranslation} from "react-i18next";
import {advancePerizia, assignPerizia, cancelPerizia, getPerizia, isApiUnavailable, type Perizia} from "../api";
import {Button} from "./ui/button";

const nextStatus: Record<string, string> = {
  Assigned: "ToBeRevised",
  ToBeRevised: "Revised",
  Revised: "ToSend",
  ToSend: "Sent",
  Sent: "Billed"
};
const cancellable = new Set(["Registered", "Assigned", "ToBeRevised", "Revised", "ToSend", "Sent"]);

type Props = {
  item: Perizia;
  token: string;
  canWrite: boolean;
  onUpdated: (updated: Perizia) => void;
  onUnavailable: () => void;
};

export function PeriziaWorkflow({item, token, canWrite, onUpdated, onUnavailable}: Props) {
  const {t} = useTranslation();
  const [assignee, setAssignee] = useState("");
  const [busy, setBusy] = useState(false);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [error, setError] = useState<"conflict" | "error" | null>(null);
  const next = nextStatus[item.status];
  const canCancel = cancellable.has(item.status);
  // The parent intentionally unmounts this tenant-owned view during a
  // refresh, auth rotation, offline transition or API failure. A response
  // from the old workflow must never mutate the new signed projection or
  // invoke onUnavailable() against a newly verified healthy session.
  const requestEpoch = useRef(0);
  const inFlight = useRef(false);
  useEffect(() => {
    setAssignee("");
    setBusy(false);
    setError(null);
    setConfirmCancel(false);
    return () => {
      requestEpoch.current += 1;
      inFlight.current = false;
    };
  }, [item.id, item.status, item.assignedTo, token]);

  async function mutate(operation: () => Promise<Perizia>) {
    if (!canWrite || inFlight.current) return;
    // No second POST before the first one is acknowledged, even if two
    // click events arrive before React commits the disabled button.
    const current = ++requestEpoch.current;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      const updated = await operation();
      if (requestEpoch.current !== current) return;
      onUpdated(updated);
      setConfirmCancel(false);
    } catch (cause) {
      if (requestEpoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError(cause instanceof Error && cause.message === "HTTP 409" ? "conflict" : "error");
    } finally {
      if (requestEpoch.current === current) {
        inFlight.current = false;
        setBusy(false);
      }
    }
  }

  function assign(event: FormEvent) {
    event.preventDefault();
    if (!canWrite || !assignee.trim()) return;
    void mutate(() => assignPerizia(token, item.id, assignee.trim()));
  }

  function advance() {
    if (canWrite && next) void mutate(() => advancePerizia(token, item.id, next));
  }

  async function reload() {
    if (inFlight.current) return;
    const current = ++requestEpoch.current;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      const fresh = await getPerizia(token, item.id);
      if (requestEpoch.current !== current) return;
      onUpdated(fresh);
      setConfirmCancel(false);
    } catch (cause) {
      if (requestEpoch.current !== current) return;
      if (isApiUnavailable(cause)) onUnavailable();
      else setError("error");
    } finally {
      if (requestEpoch.current === current) {
        inFlight.current = false;
        setBusy(false);
      }
    }
  }

  return <div className="space-y-3">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <strong className="min-w-0 break-words">{item.reference}</strong>
      <span className="rounded-md bg-slate-100 px-2 py-1 text-sm text-slate-700 dark:bg-slate-800 dark:text-slate-200">
        {t("periziaStatus." + item.status, {defaultValue: item.status})}
      </span>
    </div>
    {item.assignedTo && <p className="flex flex-wrap gap-2 text-sm text-slate-600 dark:text-slate-300">
      <span>{t("assignee")}:</span><span>{item.assignedTo}</span>
    </p>}
    {canWrite && <div className="flex flex-wrap items-center gap-2">
      {item.status === "Registered" && <form className="flex min-w-0 flex-wrap items-center gap-2" onSubmit={assign}>
        <input id={"assignee-" + item.id} aria-label={t("assignee") + " " + item.reference}
          value={assignee} required maxLength={160}
          placeholder={t("assignee")} disabled={busy}
          onChange={event => setAssignee(event.target.value)}
          className="min-w-0 flex-1 rounded-md border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700" />
        <Button type="submit" disabled={busy || !assignee.trim()}>{t("assign")}</Button>
      </form>}
      {next && <Button type="button" disabled={busy} onClick={advance}>{t("advance")}</Button>}
      {canCancel && !confirmCancel && <Button type="button" variant="outline" disabled={busy}
        onClick={() => setConfirmCancel(true)}>{t("cancel")}</Button>}
      {canCancel && confirmCancel && <>
        <Button type="button" disabled={busy} onClick={() => void mutate(() => cancelPerizia(token, item.id))}>
          {t("confirmCancel")}
        </Button>
        <Button type="button" variant="outline" disabled={busy}
          onClick={() => setConfirmCancel(false)}>{t("keep")}</Button>
      </>}
    </div>}
    {error && <div className="flex flex-wrap items-center gap-2">
      <p role="alert" className="text-sm text-red-700 dark:text-red-400">{t(error)}</p>
      {error === "conflict" && <Button type="button" variant="outline"
        disabled={busy} onClick={() => void reload()}>{t("reload")}</Button>}
    </div>}
  </div>;
}
