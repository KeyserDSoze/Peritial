import {readFileSync} from "node:fs";
import {test, expect, type APIRequestContext} from "@playwright/test";

// Signed wire probes complement the CLR reflection gate in Business.Tests.
// These are intentionally allowlist checks: unexpected JSON fields are errors,
// even when a TypeScript consumer would otherwise ignore them.
type OutputManifest = {
  format: number; version: string; mcpCatalogVersion: string;
  types: Record<string, Record<string, string>>;
};
const outputs = JSON.parse(readFileSync(
  new URL("../../../docs/mcp-output-dtos.v1.json", import.meta.url), "utf8"
)) as OutputManifest;

const api = "http://localhost:8080";
const issuer = "http://localhost:8081/realms/peritial";
const guid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const isoDate = /^\d{4}-\d{2}-\d{2}$/;
const isoDateTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/;

function checkWireValue(type: string, value: unknown, label: string): void {
  if (type.endsWith("?")) {
    if (value === null) return;
    type = type.slice(0, -1);
  }
  const list = /^IReadOnlyList<([A-Za-z]\w*Dto)>$/.exec(type);
  if (list) {
    expect(Array.isArray(value), label).toBe(true);
    (value as unknown[]).forEach((row, index) =>
      checkWireValue(list[1], row, label + "[" + index + "]"));
    return;
  }
  if (outputs.types[type]) {
    expect(value !== null && typeof value === "object" &&
      !Array.isArray(value), label + " must be a JSON object").toBe(true);
    const record = value as Record<string, unknown>;
    const expected = outputs.types[type];
    const toWire = (name:string) => name[0].toLowerCase() + name.slice(1);
    expect(Object.keys(record).sort(), label + " must have exact reviewed keys")
      .toEqual(Object.keys(expected).map(toWire).sort());
    for (const [property, fieldType] of Object.entries(expected))
      checkWireValue(fieldType, record[toWire(property)], label + "." + property);
    return;
  }
  if (type === "Guid") expect(value, label).toMatch(guid);
  else if (type === "DateOnly") expect(value, label).toMatch(isoDate);
  else if (type === "DateTime") expect(value, label).toMatch(isoDateTime);
  else if (type === "string") expect(typeof value, label).toBe("string");
  else if (type === "int" || type === "long")
    expect(Number.isSafeInteger(value), label).toBe(true);
  else if (type === "bool") expect(typeof value, label).toBe("boolean");
  else throw new Error("Unreviewed MCP wire field type: " + type);
}

type RpcResponse = {
  result?: {isError?: boolean; content?: {type?: string; text?: string}[];
    structuredContent?: unknown};
  error?: unknown;
};

function extractCallResult(body: string): unknown {
  const envelopes: RpcResponse[] = body.trimStart().startsWith("{")
    ? [JSON.parse(body) as RpcResponse]
    : body.split(/\r?\n/).filter(x => x.startsWith("data:"))
      .map(x => JSON.parse(x.slice(5).trim()) as RpcResponse);
  const packet = envelopes.find(x => x.result || x.error);
  expect(packet, "MCP must return a JSON-RPC envelope").toBeDefined();
  expect(packet?.error, "MCP JSON-RPC error").toBeUndefined();
  expect(packet?.result?.isError, "MCP tool-level error").not.toBe(true);
  const result = packet?.result;
  expect(result, "MCP call must return a result").toBeDefined();

  // The SDK can advertise a structuredContent object as well as a
  // text JSON content block. Validate the actual transport output, not
  // a reserialized REST DTO. Prefer text when it contains valid JSON.
  for (const part of result?.content ?? []) {
    if (part.type !== "text" || typeof part.text !== "string") continue;
    try { return JSON.parse(part.text) as unknown; }
    catch { /* Fall back to structuredContent where present. */ }
  }
  expect(result?.structuredContent, "MCP result has no JSON content").toBeDefined();
  return result!.structuredContent;
}

async function tokenFor(request: APIRequestContext): Promise<string> {
  const response = await request.post(issuer + "/protocol/openid-connect/token", {
    form: {
      grant_type: "password", client_id: "peritial-test-readonly",
      username: "localuser", password: "local-only-user"
    }
  });
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json() as {access_token:string}).access_token;
}

test.describe("Versioned MCP result JSON allowlist", () => {
  test("wire validator rejects added fields, missing fields, wrong types and nullability", () => {
    expect(outputs).toMatchObject({format:1, version:"1.0.0", mcpCatalogVersion:"1.2.0"});
    const record = {
      id:"11111111-1111-4111-8111-111111111111", reference:"TEST",
      status:"New", assignedTo:null, createdAtUtc:"2026-11-21T12:00:00Z"
    };
    expect(() => checkWireValue("PeriziaDto", record, "perizia")).not.toThrow();
    expect(() => checkWireValue("PeriziaDto", {...record, tenantId:"SECRET"}, "perizia")).toThrow();
    expect(() => checkWireValue("PeriziaDto", {id:record.id}, "perizia")).toThrow();
    expect(() => checkWireValue("PeriziaDto", {...record, id:"not-a-guid"}, "perizia")).toThrow();
    expect(() => checkWireValue("PeriziaDto", {...record, assignedTo:42}, "perizia")).toThrow();
    const sinistro = {
      id:record.id, externalReference:"S1", occurredOn:"2026-11-20",
      reportedOn:null, createdAtUtc:record.createdAtUtc, version:1
    };
    expect(() => checkWireValue("SinistroDto", sinistro, "sinistro")).not.toThrow();
    expect(() => checkWireValue("SinistroDto",
      {...sinistro, occurredOn:"2026-11-20T14:00:00Z"}, "sinistro")).toThrow();
    expect(() => checkWireValue("SinistroDto", {...sinistro, version:"1"}, "sinistro")).toThrow();
  });

  test("real signed Keycloak MCP tools/call output matches the reviewed wire DTOs", async ({request}) => {
    test.skip(!process.env.E2E_FULL_STACK, "Real Keycloak, PostgreSQL, scanner and MCP required");
    test.setTimeout(100_000);
    const writer = await request.post(issuer + "/protocol/openid-connect/token", {
      form: {grant_type:"password", client_id:"peritial-test-write",
        username:"localuser",password:"local-only-user"}
    });
    expect(writer.status(), await writer.text()).toBe(200);
    const writeToken = (await writer.json() as {access_token:string}).access_token;
    const readToken = await tokenFor(request);
    const auth = {Authorization:"Bearer " + writeToken};
    const nonce = Date.now() + "-" + Math.floor(Math.random() * 1000000);
    const periziaReference = "MCP-WIRE-P-" + nonce;
    const sinistroReference = "MCP-WIRE-S-" + nonce;
    const perizia = await request.post(api + "/api/perizie", {
      headers:auth, data:{reference:periziaReference}
    });
    expect(perizia.status(), await perizia.text()).toBe(201);
    const periziaId = (await perizia.json() as {id:string}).id;
    const sinistro = await request.post(api + "/api/sinistri", {
      headers:auth, data:{externalReference:sinistroReference,
        occurredOn:"2026-11-21", reportedOn:null}
    });
    expect(sinistro.status(), await sinistro.text()).toBe(201);
    const sinistroId = (await sinistro.json() as {id:string}).id;

    async function probe(name: string, args: Record<string, unknown>, dto: string, id: number) {
      const response = await request.post(api + "/mcp", {
        headers: {
          Authorization:"Bearer " + readToken,
          Accept:"application/json,text/event-stream",
          "Content-Type":"application/json"
        },
        data:{jsonrpc:"2.0",id,method:"tools/call",params:{name,arguments:args}}
      });
      const body = await response.text();
      expect(response.ok(), body).toBeTruthy();
      const decoded = extractCallResult(body);
      checkWireValue(dto, decoded, name);
      return decoded as Record<string, unknown>;
    }

    const periziaDto = await probe("get_perizia", {id:periziaId}, "PeriziaDto", 6101);
    expect(periziaDto.id).toBe(periziaId);
    expect(periziaDto.reference).toBe(periziaReference);
    const sinistroDto = await probe("get_sinistro", {id:sinistroId}, "SinistroDto", 6102);
    expect(sinistroDto.id).toBe(sinistroId);
    expect(sinistroDto.externalReference).toBe(sinistroReference);
    expect(sinistroDto.reportedOn).toBeNull();
    const association = await probe("get_perizia_sinistro", {periziaId},
      "PeriziaSinistroAssociationDto", 6103);
    expect(association).toMatchObject({periziaId,sinistroId:null,linkVersion:1});
    const page = await probe("search_sinistri_page", {
      prefix:sinistroReference, limit:10
    }, "SinistroPageDto", 6104);
    expect(page.items).toEqual(expect.arrayContaining([
      expect.objectContaining({id:sinistroId,externalReference:sinistroReference})
    ]));

    const fileName = "mcp-wire-" + nonce + ".pdf";
    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const upload = await request.post(api + "/api/files", {
      headers:auth, multipart:{file:{name:fileName,mimeType:"application/pdf",buffer:pdf},
        periziaId}
    });
    expect(upload.status(), await upload.text()).toBe(201);
    const documentId = (await upload.json() as {id:string}).id;
    const doc = await probe("get_document", {id:documentId}, "DocumentSummaryDto", 6105);
    expect(doc).toMatchObject({id:documentId,periziaId,fileName});
    const docs = await probe("page_documents", {periziaId,limit:1},
      "DocumentPageDto", 6106);
    expect(docs.items).toEqual(expect.arrayContaining([
      expect.objectContaining({id:documentId,periziaId,fileName})
    ]));
  });
});
