export type Perizia = { id:string; reference:string; status:string; assignedTo:string|null; createdAtUtc:string };
export type DocumentRecord = { id:string; periziaId:string|null; fileName:string; contentType:string; length:number };
const base = import.meta.env.VITE_API_URL || "/api";
async function request<T>(url:string, token:string, init?:RequestInit):Promise<T> {
  const response = await fetch(base + url, { ...init, headers: { Authorization: "Bearer " + token, ...init?.headers } });
  if (!response.ok) throw new Error("HTTP " + response.status);
  return response.json() as Promise<T>;
}
export const listPerizie = (token:string) => request<Perizia[]>("/perizie",token);
export const createPerizia = (token:string, reference:string) => request<Perizia>("/perizie",token,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({reference})});
export async function uploadDocument(token:string, file:File, periziaId?:string):Promise<DocumentRecord> {
  const body = new FormData(); body.append("file",file); if(periziaId) body.append("periziaId",periziaId);
  return request<DocumentRecord>("/files",token,{method:"POST",body});
}