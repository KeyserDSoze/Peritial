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
type Catalog = {format:number;version:string;tools:{
  name:string;access:string;effect:string;returnType:string
}[]};
const catalog = JSON.parse(readFileSync(
  new URL("../../../docs/mcp-catalog.v1.json", import.meta.url), "utf8"
)) as Catalog;
// The test fails when a new read-access tool is added without a signed
// tools/call wire probe. Write-scoped preview/plan have a different grant.
const readTools = [
  "get_document", "get_perizia", "get_perizia_sinistro", "get_sinistro",
  "list_documents", "list_perizie", "list_perizie_for_sinistro",
  "list_sinistri", "page_documents", "page_sinistri",
  "search_sinistri", "search_sinistri_page"
] as const;
const preflightTools = ["plan_legacy_xml", "preview_legacy_xml"] as const;

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
  const list = /^IReadOnlyList<([A-Za-z]\w*)>$/.exec(type);
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
    const reviewedNames = Object.keys(expected).map(toWire);
    const actualNames = Object.keys(record);
    // MCP SDK's JSON serializer omits null-valued properties. Nullable
    // CLR fields are allowed to be absent, but no unknown key is allowed.
    // Non-nullable fields MUST still be present. A present nullable field
    // is always validated, including explicit JSON null.
    expect(actualNames.filter(name => !reviewedNames.includes(name)),
      label + " cannot expose unreviewed fields").toEqual([]);
    const requiredNames = Object.entries(expected)
      .filter(([, type]) => !type.endsWith("?"))
      .map(([name]) => toWire(name));
    expect(requiredNames.filter(name => !Object.prototype.hasOwnProperty.call(record, name)),
      label + " is missing required fields").toEqual([]);
    for (const [property, fieldType] of Object.entries(expected)) {
      const wireName = toWire(property);
      if (Object.prototype.hasOwnProperty.call(record, wireName))
        checkWireValue(fieldType, record[wireName], label + "." + property);
    }
    return;
  }
  if (type === "Guid") expect(value, label).toMatch(guid);
  else if (type === "DateOnly") expect(value, label).toMatch(isoDate);
  else if (type === "DateTime") expect(value, label).toMatch(isoDateTime);
  else if (type === "string") expect(typeof value, label).toBe("string");
  else if (type === "int" || type === "long")
    expect(typeof value === "number" && Number.isSafeInteger(value), label).toBe(true);
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

async function tokenFor(request: APIRequestContext,
  clientId = "peritial-test-readonly"): Promise<string> {
  const response = await request.post(issuer + "/protocol/openid-connect/token", {
    form: {
      grant_type: "password", client_id: clientId,
      username: "localuser", password: "local-only-user"
    }
  });
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json() as {access_token:string}).access_token;
}

test.describe("Versioned MCP result JSON allowlist", () => {
  test("all read-access MCP tools have a signed output wire probe", () => {
    expect(catalog).toMatchObject({format:1,version:"1.2.0"});
    const actual = catalog.tools.filter(t => t.access === "read").map(t => t.name).sort();
    expect(actual).toEqual([...readTools].sort());
    for (const name of readTools) {
      const entry = catalog.tools.find(t => t.name === name);
      expect(entry?.effect, name).toBe("read-only");
      expect(entry?.returnType, name).toMatch(/^Task<.+>$/);
    }
  });

  test("catalogue pins all write-scoped non-mutating MCP preflight tools", () => {
    const actual = catalog.tools.filter(t => t.effect === "read-only" &&
      t.access === "write").map(t => t.name).sort();
    expect(actual).toEqual([...preflightTools].sort());
    for (const name of preflightTools)
      expect(catalog.tools.find(t => t.name === name)?.returnType)
        .toMatch(/Dto>?$/);
  });

  test("wire validator rejects added fields, missing fields, wrong types and nullability", () => {
    expect(outputs).toMatchObject({format:1, version:"1.0.0", mcpCatalogVersion:"1.2.0"});
    const record = {
      id:"11111111-1111-4111-8111-111111111111", reference:"TEST",
      status:"New", assignedTo:null, createdAtUtc:"2026-11-21T12:00:00Z"
    };
    expect(() => checkWireValue("PeriziaDto", record, "perizia")).not.toThrow();
    const {assignedTo: _omittedNullable, ...withoutAssignedTo} = record;
    expect(() => checkWireValue("PeriziaDto", withoutAssignedTo, "perizia")).not.toThrow();
    expect(() => checkWireValue("PeriziaDto", {...record, tenantId:"SECRET"}, "perizia")).toThrow();
    expect(() => checkWireValue("PeriziaDto", {id:record.id}, "perizia")).toThrow();
    expect(() => checkWireValue("PeriziaDto",
      { ...withoutAssignedTo, status:undefined }, "perizia")).toThrow();
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
    expect(() => checkWireValue("IReadOnlyList<Guid>", [record.id], "links")).not.toThrow();
    expect(() => checkWireValue("IReadOnlyList<Guid>", ["not-a-guid"], "links")).toThrow();
    expect(() => checkWireValue("IReadOnlyList<Guid>", [42], "links")).toThrow();
    expect(() => checkWireValue("IReadOnlyList<Guid>", {items:[]}, "links")).toThrow();
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

    async function probe(name: string, args: Record<string, unknown>, id: number) {
      // The manifest, not the caller, decides the expected output envelope.
      const entry = catalog.tools.find(tool => tool.name === name);
      expect(entry, name + " must be catalogued").toBeDefined();
      expect(entry?.access, name).toBe("read");
      expect(entry?.effect, name).toBe("read-only");
      const outputType = /^Task<(.+)>$/.exec(entry!.returnType);
      expect(outputType, name + " must have a reviewed async output").not.toBeNull();
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
      checkWireValue(outputType![1], decoded, name);
      return decoded as Record<string, unknown> & unknown[];
    }

    const periziaDto = await probe("get_perizia", {id:periziaId}, 6101);
    expect(periziaDto.id).toBe(periziaId);
    expect(periziaDto.reference).toBe(periziaReference);
    // RED #44905796126 established SDK omits null-valued properties.
    expect(periziaDto).not.toHaveProperty("assignedTo");
    const sinistroDto = await probe("get_sinistro", {id:sinistroId}, 6102);
    expect(sinistroDto.id).toBe(sinistroId);
    expect(sinistroDto.externalReference).toBe(sinistroReference);
    expect(sinistroDto.reportedOn ?? null).toBeNull();
    const association = await probe("get_perizia_sinistro", {periziaId}, 6103);
    expect(association.periziaId).toBe(periziaId);
    expect(association.linkVersion).toBe(1);
    expect(association.sinistroId ?? null).toBeNull();
    const page = await probe("search_sinistri_page", {
      prefix:sinistroReference, limit:10
    }, 6104);
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
    const doc = await probe("get_document", {id:documentId}, 6105);
    expect(doc).toMatchObject({id:documentId,periziaId,fileName});
    const docs = await probe("page_documents", {periziaId,limit:1}, 6106);
    expect(docs.items).toEqual(expect.arrayContaining([
      expect.objectContaining({id:documentId,periziaId,fileName})
    ]));

    // Every read-access tool is exercised under a real read-only token.
    // Verify the returned JSON against each catalog returnType, not only
    // the primary single-record get operations.
    const listedPerizie = await probe("list_perizie", {}, 6107);
    expect(listedPerizie).toEqual(expect.arrayContaining([
      expect.objectContaining({id:periziaId,reference:periziaReference})
    ]));
    const listedSinistri = await probe("list_sinistri", {limit:100}, 6108);
    expect(Array.isArray(listedSinistri)).toBe(true);
    const pagedSinistri = await probe("page_sinistri", {limit:10}, 6109);
    expect(Array.isArray(pagedSinistri.items)).toBe(true);
    const searched = await probe("search_sinistri", {
      prefix:sinistroReference,limit:10
    }, 6110);
    expect(searched).toEqual(expect.arrayContaining([
      expect.objectContaining({id:sinistroId,externalReference:sinistroReference})
    ]));
    const listedDocs = await probe("list_documents", {
      periziaId,limit:50
    }, 6111);
    expect(listedDocs).toEqual(expect.arrayContaining([
      expect.objectContaining({id:documentId,periziaId,fileName})
    ]));

    // A nonempty GUID-list is necessary to verify its element wire type.
    // Use a signed REST writer for the setup; the subsequent MCP read is
    // strictly scoped to peritial.read and never performs the mutation.
    const linked = await request.post(
      api + "/api/perizie/" + periziaId + "/sinistro", {
        headers:auth, data:{sinistroId,expectedLinkVersion:1}
      });
    expect(linked.status(), await linked.text()).toBe(201);
    const linkedPerizie = await probe("list_perizie_for_sinistro", {
      sinistroId
    }, 6112);
    expect(linkedPerizie).toContain(periziaId);
  });
  test("signed write-only XML preflights use reviewed MCP JSON and do not import", async ({request}) => {
    test.skip(!process.env.E2E_FULL_STACK, "Real Keycloak, PostgreSQL, and MCP required");
    test.setTimeout(90_000);
    const writer = await tokenFor(request, "peritial-test-write");
    const reader = await tokenFor(request);
    const nonce = Date.now() + "-" + Math.floor(Math.random() * 1000000);
    const secret = "PRIVATE-PREFLIGHT-" + nonce;
    const samples = [
      {
        ref:"MCP-WIRE-ACCERTATORE-" + nonce,
        format:"accertatore",
        date:"2026-11-17",
        xml: "<it.autosoftws.IncaricoAccertatoreRecord>" +
          "<NumeroSinistro>MCP-WIRE-ACCERTATORE-" + nonce + "</NumeroSinistro>" +
          "<DataOraSinistro>2026-11-17</DataOraSinistro>" +
          "<DataIncarico>2026-11-18</DataIncarico>" +
          "<Contraente><Nome>" + secret + "</Nome></Contraente>" +
          "</it.autosoftws.IncaricoAccertatoreRecord>"
      },
      {
        ref:"MCP-WIRE-CLASSIC-" + nonce,
        format:"classic",
        date:"2026-11-19",
        xml:"<it.autosoftws.ReIncaricoBaseRecord>" +
          "<NumeroSinistro>MCP-WIRE-CLASSIC-" + nonce + "</NumeroSinistro>" +
          "<DataOraSinistro>2026-11-19 09:00:00</DataOraSinistro>" +
          "<DataIncarico>2026-11-20</DataIncarico>" +
          "</it.autosoftws.ReIncaricoBaseRecord>"
      }
    ];

    async function assertUnimported(reference:string) {
      const response = await request.get(api + "/api/sinistri/search?" +
        new URLSearchParams({prefix:reference,limit:"10"}), {
        headers:{Authorization:"Bearer " + writer}
      });
      expect(response.status(), await response.text()).toBe(200);
      const matches = await response.json() as {externalReference:string}[];
      expect(matches.some(row => row.externalReference === reference)).toBe(false);
    }

    async function call(name:typeof preflightTools[number], xml:string,
      token:string, id:number) {
      const response = await request.post(api + "/mcp", {
        headers:{
          Authorization:"Bearer " + token,
          Accept:"application/json,text/event-stream",
          "Content-Type":"application/json"
        },
        data:{jsonrpc:"2.0",id,method:"tools/call",
          params:{name,arguments:{xml}}}
      });
      const raw = await response.text();
      expect(response.ok(), raw).toBeTruthy();
      return raw;
    }

    for (const [i, sample] of samples.entries()) {
      await assertUnimported(sample.ref);
      for (const [j, name] of preflightTools.entries()) {
        const entry = catalog.tools.find(t => t.name === name);
        expect(entry?.access).toBe("write");
        expect(entry?.effect).toBe("read-only");
        const raw = await call(name, sample.xml, writer, 6300+i*2+j);
        const actual = extractCallResult(raw);
        const outputType = /^Task<(.+)>$/.exec(entry!.returnType)?.[1] ??
          entry!.returnType;
        checkWireValue(outputType, actual, name);
        const result = actual as Record<string, unknown>;
        const preview = (name === "plan_legacy_xml" ? result.preview : result) as
          Record<string, unknown>;
        expect(preview).toMatchObject({
          format:sample.format,
          externalReference:sample.ref,
          occurredOn:sample.date,
          attachmentCount:0,
          hasUnmappedBusinessData:true
        });
        if (name === "plan_legacy_xml") {
          expect(result.status).toBe("new");
          expect(result.existingSinistroId ?? null).toBeNull();
        }
        const visible = JSON.stringify(actual);
        expect(visible).not.toContain(secret);
        expect(visible).not.toContain("09:00:00");
        expect(visible).not.toContain("tenantId");
      }
      await assertUnimported(sample.ref);
    }
    for (const [i,name] of preflightTools.entries()) {
      const raw = await call(name, samples[0].xml, reader, 6320+i);
      expect(raw).toContain('"isError":true');
      expect(raw).not.toContain(secret);
    }
  });

});
