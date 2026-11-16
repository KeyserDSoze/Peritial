export type Perizia = { id:string; reference:string; status:string; assignedTo:string|null; createdAtUtc:string };
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

export async function uploadDocument(token:string, file:File, periziaId?:string):Promise<DocumentRecord> {
  const body = new FormData(); body.append("file",file); if(periziaId) body.append("periziaId",periziaId);
  return request<DocumentRecord>("/files",token,{method:"POST",body});
}