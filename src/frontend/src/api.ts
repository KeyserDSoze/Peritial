export type Perizia = { id:string; reference:string; status:string; assignedTo:string|null; createdAtUtc:string };
export type SinistroSummary = {
  id: string; externalReference: string; occurredOn: string;
  reportedOn: string | null; createdAtUtc: string; version: number;
};
// No tenant ID is accepted or returned from the link endpoints.
export type PeriziaSinistroAssociation = {periziaId: string; sinistroId: string | null};
export type PeriziaSinistroLink = {periziaId: string; sinistroId: string};
export type DocumentRecord = { id:string; periziaId:string|null; fileName:string; contentType:string; length:number };
// Safe read-only metadata projection: no tenant ID, internal storage key,
// object URL, file bytes or scanner details.
export type DocumentSummary = DocumentRecord & {createdAtUtc:string};
// When the browser is still "online", REST can still be unreachable or the
// current JWT can be rejected. Stop showing previously loaded tenant records
// until a new authenticated GET succeeds; never queue or replay a mutation.
export function isApiUnavailable(error: unknown): boolean {
  if (error instanceof TypeError) return true; // fetch network failure
  if (!(error instanceof Error)) return false;
  if (error.name === "AbortError") return true;
  const code = /^HTTP (\d{3})$/.exec(error.message);
  if (!code) return false;
  const status = Number(code[1]);
  return status === 401 || status === 403 || status === 408 ||
    status === 429 || status >= 500;
}

const base = import.meta.env.VITE_API_URL || "/api";
async function request<T>(url:string, token:string, init?:RequestInit):Promise<T> {
  const response = await fetch(base + url, { ...init, headers: { Authorization: "Bearer " + token, ...init?.headers } });
  if (!response.ok) throw new Error("HTTP " + response.status);
  return response.json() as Promise<T>;
}
export const listPerizie = (token:string) => request<Perizia[]>("/perizie",token);
export const listDocuments = (token:string, periziaId?:string) =>
  request<DocumentSummary[]>("/files?limit=50" + (periziaId ? "&periziaId=" + encodeURIComponent(periziaId) : ""), token);
export const createPerizia = (token:string, reference:string) => request<Perizia>("/perizie",token,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({reference})});
// Mutations reuse the Business layer's scoped REST endpoints. Responses are
// authoritative: the UI does not optimistically advance a workflow state.
export const assignPerizia = (token: string, id: string, assignee: string) =>
  request<Perizia>("/perizie/" + encodeURIComponent(id) + "/assign", token, {
    method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify({assignee})
  });
export const advancePerizia = (token: string, id: string, target: string) =>
  request<Perizia>("/perizie/" + encodeURIComponent(id) + "/advance", token, {
    method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify({target})
  });
export const cancelPerizia = (token: string, id: string) =>
  request<Perizia>("/perizie/" + encodeURIComponent(id) + "/cancel", token, {method: "POST"});
export const getPerizia = (token: string, id: string) =>
  request<Perizia>("/perizie/" + encodeURIComponent(id), token);

export const getPeriziaSinistro = (token: string, periziaId: string) =>
  request<PeriziaSinistroAssociation>("/perizie/" + encodeURIComponent(periziaId) + "/sinistro", token);
export const getSinistro = (token: string, sinistroId: string) =>
  request<SinistroSummary>("/sinistri/" + encodeURIComponent(sinistroId), token);
// Catalog is always server-side, signed-tenant scoped and bounded.
export const listSinistri = (token: string) =>
  request<SinistroSummary[]>("/sinistri?limit=50", token);
export const createSinistro = (token: string, externalReference: string,
  occurredOn: string, reportedOn: string | null) =>
  request<SinistroSummary>("/sinistri", token, {
    method: "POST", headers: {"Content-Type": "application/json"},
    body: JSON.stringify({externalReference, occurredOn, reportedOn})
  });
// The caller MUST provide the last version returned by GET/PUT. Never
// auto-fetch the newest version and overwrite a concurrent operator's work.
export const updateSinistro = (token: string, id: string, externalReference: string,
  occurredOn: string, reportedOn: string | null, expectedVersion: number) =>
  request<SinistroSummary>("/sinistri/" + encodeURIComponent(id), token, {
    method: "PUT", headers: {"Content-Type": "application/json"},
    body: JSON.stringify({externalReference, occurredOn, reportedOn, expectedVersion})
  });
// Filters are evaluated server-side BEFORE the bounded result window. The
// picker keeps its 20-row default; the full catalog can request 50 rows.
export const searchSinistri = (token: string, prefix: string,
  occurredFrom?: string, occurredTo?: string, limit = 20) => {
  const params = new URLSearchParams({prefix: prefix.trim(), limit: String(limit)});
  if (occurredFrom) params.set("occurredFrom", occurredFrom);
  if (occurredTo) params.set("occurredTo", occurredTo);
  return request<SinistroSummary[]>("/sinistri/search?" + params.toString(), token);
};
export const linkPeriziaSinistro = (token: string, periziaId: string, sinistroId: string) =>
  request<PeriziaSinistroLink>("/perizie/" + encodeURIComponent(periziaId) + "/sinistro",
    token, {method: "POST", headers: {"Content-Type": "application/json"},
      body: JSON.stringify({sinistroId})});

export async function uploadDocument(token:string, file:File, periziaId?:string):Promise<DocumentRecord> {
  const body = new FormData(); body.append("file",file); if(periziaId) body.append("periziaId",periziaId);
  return request<DocumentRecord>("/files",token,{method:"POST",body});
}