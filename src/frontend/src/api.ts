export type Perizia = { id:string; reference:string; status:string; assignedTo:string|null; createdAtUtc:string };
export type SinistroSummary = {
  id: string; externalReference: string; occurredOn: string;
  reportedOn: string | null; createdAtUtc: string; version: number;
};
// No tenant ID is accepted or returned from the link endpoints.
export type PeriziaSinistroAssociation = {periziaId: string; sinistroId: string | null; linkVersion: number};
export type PeriziaSinistroLink = {periziaId: string; sinistroId: string; linkVersion: number};
export type DocumentRecord = { id:string; periziaId:string|null; fileName:string; contentType:string; length:number };
// Safe read-only metadata projection: no tenant ID, internal storage key,
// object URL, file bytes or scanner details.
export type DocumentSummary = DocumentRecord & {createdAtUtc:string};
export type DocumentPage = {items:DocumentSummary[]; nextCursor:string|null};
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
// Cursor values are positional opaque metadata, never parsed or cached offline.
export const pageDocuments = (token:string, periziaId?:string, cursor?:string, limit=50) => {
  const params = new URLSearchParams({limit:String(limit)});
  if (periziaId) params.set("periziaId", periziaId);
  if (cursor) params.set("cursor", cursor);
  return request<DocumentPage>("/files/page?" + params.toString(), token);
};
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
// Page cursor is an opaque, bounded REST value; the browser never parses
// database ordering keys or uses a tenant-selected offset.
export type SinistroPage = {items: SinistroSummary[]; nextCursor: string | null};
export const pageSinistri = (token: string, cursor?: string, limit = 50) =>
  request<SinistroPage>("/sinistri/page?" + new URLSearchParams({
    limit: String(limit), ...(cursor === undefined ? {} : {cursor})
  }).toString(), token);
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
// Paged server-side literal-prefix search. The 20-result picker autocomplete
// keeps the existing /sinistri/search API unchanged.
export const pageSearchSinistri = (token: string, prefix: string,
  occurredFrom?: string, occurredTo?: string, cursor?: string, limit = 50) => {
  const params = new URLSearchParams({prefix: prefix.trim(), limit: String(limit)});
  if (occurredFrom) params.set("occurredFrom", occurredFrom);
  if (occurredTo) params.set("occurredTo", occurredTo);
  if (cursor) params.set("cursor", cursor);
  return request<SinistroPage>("/sinistri/search/page?" + params.toString(), token);
};
// DELETE returns no JSON body: never call request<T> which expects JSON.
// Only the last observed link can be unlinked; no optimistic mutation.
export async function unlinkPeriziaSinistro(
  token: string, periziaId: string, expectedSinistroId: string,
  expectedLinkVersion: number): Promise<void> {
  const query = new URLSearchParams({expectedSinistroId, expectedLinkVersion:String(expectedLinkVersion)});
  const response = await fetch(base + "/perizie/" + encodeURIComponent(periziaId) +
    "/sinistro?" + query.toString(), {
      method:"DELETE", headers:{Authorization:"Bearer " + token}
    });
  if (response.status !== 204)
    throw new Error("HTTP " + response.status);
}
// One authenticated CAS write: no unlink-then-link, optimistic success,
// or tenant-selected mutation. PUT returns an allowlisted link projection.
export const reparentPeriziaSinistro = (
  token: string, periziaId: string, expectedSinistroId: string,
  newSinistroId: string, expectedLinkVersion: number) =>
  request<PeriziaSinistroLink>("/perizie/" + encodeURIComponent(periziaId) +
    "/sinistro", token, {
      method:"PUT", headers:{"Content-Type":"application/json"},
      body:JSON.stringify({expectedSinistroId, newSinistroId, expectedLinkVersion})
    });
export const linkPeriziaSinistro = (token: string, periziaId: string,
  sinistroId: string, expectedLinkVersion: number) =>
  request<PeriziaSinistroLink>("/perizie/" + encodeURIComponent(periziaId) + "/sinistro",
    token, {method: "POST", headers: {"Content-Type": "application/json"},
      body: JSON.stringify({sinistroId, expectedLinkVersion})});

export async function uploadDocument(token:string, file:File, periziaId?:string):Promise<DocumentRecord> {
  const body = new FormData(); body.append("file",file); if(periziaId) body.append("periziaId",periziaId);
  return request<DocumentRecord>("/files",token,{method:"POST",body});
}

// Download is a separate, bounded binary read. Never put Bearer credentials in
// an <a href>, query string, object storage URL or service-worker cache.
// Reject redirects, incorrect MIME and a mismatched body before making a Blob.
export async function downloadDocument(token: string, id: string,
  expectedLength: number, signal: AbortSignal): Promise<Blob> {
  if (!Number.isSafeInteger(expectedLength) || expectedLength <= 0 ||
      expectedLength > 25 * 1024 * 1024)
    throw new Error("Invalid document length");
  const response = await fetch(base + "/files/" + encodeURIComponent(id) + "/download", {
    headers: {Authorization: "Bearer " + token},
    method: "GET", cache: "no-store", credentials: "omit", redirect: "error", signal
  });
  if (!response.ok) throw new Error("HTTP " + response.status);
  if (response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !==
      "application/octet-stream") throw new Error("Unexpected document content type");
  const declared = response.headers.get("content-length");
  if (declared !== null && Number(declared) !== expectedLength)
    throw new Error("Document length mismatch");
  if (!response.body) throw new Error("Missing document response body");
  const reader = response.body.getReader();
  const bytes = new Uint8Array(expectedLength);
  let length = 0;
  try {
    while (true) {
      const {done, value} = await reader.read();
      if (done) break;
      if (value.byteLength > expectedLength - length)
        throw new Error("Document exceeds declared size");
      bytes.set(value, length);
      length += value.byteLength;
    }
  } finally {
    if (length !== expectedLength) await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
  if (length !== expectedLength) throw new Error("Document length mismatch");
  return new Blob([bytes], {type: "application/octet-stream"});
}
