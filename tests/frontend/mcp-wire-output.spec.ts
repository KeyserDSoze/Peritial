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
// Keep covered and explicitly deferred mutating operations disjoint.
// A new catalogued mutator must be consciously classified and reviewed.
const primaryMutationWireTools = [
  "advance_perizia", "assign_perizia", "cancel_perizia", "create_perizia",
  "create_sinistro", "update_sinistro"
] as const;
const mutationWireTools = [
  ...primaryMutationWireTools, "ingest_file", "link_perizia_sinistro",
  "reparent_perizia_sinistro", "unlink_perizia_sinistro"
] as const;
// Hard-delete deliberately has no SUCCESS probe while the production
// retention/legal-hold/approval gate is absent; test an explicit denial.
const deferredMutationTools = ["delete_sinistro"] as const;

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

  test("ten successful wire probes and guarded delete exhaust all 11 mutators", () => {
    expect(catalog.tools.filter(t => t.effect === "mutating").map(t => t.name).sort())
      .toEqual([...mutationWireTools, ...deferredMutationTools].sort());
    for (const name of mutationWireTools) {
      const tool = catalog.tools.find(t => t.name === name);
      expect(tool?.access, name + " requires writer").toBe("write");
      expect(tool?.effect, name).toBe("mutating");
      expect(tool?.returnType, name).toMatch(/^Task<(PeriziaDto|SinistroDto|DocumentDto|PeriziaSinistroLinkDto|PeriziaSinistroAssociationDto)>$/);
    }
    expect(catalog.tools.find(t => t.name === "delete_sinistro")).toMatchObject({
      access:"write+delete", effect:"mutating", returnType:"Task<bool>"
    });
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

  test("six signed MCP mutators return reviewed JSON and preserve REST/tenant authorization", async ({request}) => {
    test.skip(!process.env.E2E_FULL_STACK,
      "Real Keycloak, PostgreSQL, and audited MCP writers required");
    test.setTimeout(120_000);
    const writeToken = await tokenFor(request, "peritial-test-write");
    const readToken = await tokenFor(request);
    const nonce = Date.now() + "-" + Math.floor(Math.random()*1000000);
    const writeHeaders = {Authorization:"Bearer " + writeToken};
    const readHeaders = {Authorization:"Bearer " + readToken};
    const probed = new Set<string>();

    async function signedCall(name:string, args:Record<string, unknown>,
      token:string, rpcId:number) {
      const response = await request.post(api + "/mcp", {
        headers:{
          Authorization:"Bearer " + token,
          Accept:"application/json,text/event-stream",
          "Content-Type":"application/json"
        },
        data:{jsonrpc:"2.0",id:rpcId,method:"tools/call",
          params:{name,arguments:args}}
      });
      const raw = await response.text();
      expect(response.ok(), raw).toBeTruthy();
      return raw;
    }

    async function writerCall(name:typeof mutationWireTools[number],
      args:Record<string, unknown>, rpcId:number) {
      const entry = catalog.tools.find(t => t.name === name);
      expect(entry?.access, name).toBe("write");
      expect(entry?.effect, name).toBe("mutating");
      const outputType = /^Task<(.+)>$/.exec(entry!.returnType);
      expect(outputType, name + " requires reviewed result type").not.toBeNull();
      const raw = await signedCall(name, args, writeToken, rpcId);
      const result = extractCallResult(raw);
      checkWireValue(outputType![1], result, name);
      expect(raw).not.toContain("storageKey");
      expect(raw).not.toContain("tenantId");
      probed.add(name);
      return result as Record<string, unknown>;
    }

    async function get(path:string) {
      const response = await request.get(api + path, {headers:readHeaders});
      expect(response.status(), await response.text()).toBe(200);
      return response.json() as Promise<Record<string, unknown>>;
    }

    // Two entirely synthetic cases separate the workflow and cancellation paths.
    const reference = "MCP-MUT-P-" + nonce;
    const perizia = await writerCall("create_perizia", {reference}, 6401);
    const periziaId = perizia.id as string;
    expect(perizia.reference).toBe(reference);
    expect((await get("/api/perizie/" + periziaId)).status).toBe(perizia.status);

    const assignee = "assigned-through-mcp";
    const assigned = await writerCall("assign_perizia", {
      id:periziaId,assignee
    }, 6402);
    expect(assigned).toMatchObject({id:periziaId,assignedTo:assignee,status:"Assigned"});
    expect(await get("/api/perizie/" + periziaId)).toMatchObject({
      id:periziaId,reference,assignedTo:assignee,status:"Assigned"
    });

    const advanced = await writerCall("advance_perizia", {
      id:periziaId,target:"ToBeRevised"
    }, 6403);
    expect(advanced).toMatchObject({id:periziaId,status:"ToBeRevised"});
    expect(await get("/api/perizie/" + periziaId)).toMatchObject({
      id:periziaId,assignedTo:assignee,status:"ToBeRevised"
    });

    const cancelReference = "MCP-MUT-CANCEL-" + nonce;
    const cancelSeed = await writerCall("create_perizia", {
      reference:cancelReference
    }, 6404);
    const cancelId = cancelSeed.id as string;
    const cancelled = await writerCall("cancel_perizia", {id:cancelId}, 6405);
    expect(cancelled).toMatchObject({id:cancelId,reference:cancelReference,
      status:"Cancelled"});
    expect(await get("/api/perizie/" + cancelId)).toMatchObject({
      id:cancelId,status:"Cancelled"
    });

    const claimRef = "MCP-MUT-S-" + nonce;
    const createdClaim = await writerCall("create_sinistro", {
      externalReference:claimRef,occurredOn:"2026-11-19",
      reportedOn:null
    }, 6406);
    const claimId = createdClaim.id as string;
    expect(createdClaim).toMatchObject({
      id:claimId,externalReference:claimRef,occurredOn:"2026-11-19",version:1
    });
    expect(await get("/api/sinistri/" + claimId)).toMatchObject({
      id:claimId,externalReference:claimRef,version:1
    });

    const updatedRef = "MCP-MUT-UPDATED-" + nonce;
    const updatedClaim = await writerCall("update_sinistro", {
      id:claimId,externalReference:updatedRef,
      occurredOn:"2026-11-20",reportedOn:"2026-11-21",expectedVersion:1
    }, 6407);
    expect(updatedClaim).toMatchObject({
      id:claimId,externalReference:updatedRef,
      occurredOn:"2026-11-20",reportedOn:"2026-11-21",version:2
    });
    expect(await get("/api/sinistri/" + claimId)).toMatchObject({
      id:claimId,externalReference:updatedRef,version:2,
      reportedOn:"2026-11-21"
    });

    expect([...probed].sort()).toEqual([...primaryMutationWireTools].sort());

    // Attempt the same six writers with a signed read-only subject.
    // Authorization is enforced before any side effect.
    const forbiddenPeriziaRef = "MCP-DENY-P-" + nonce;
    const forbiddenSinistroRef = "MCP-DENY-S-" + nonce;
    const denied = [
      {name:"create_perizia",args:{reference:forbiddenPeriziaRef}},
      {name:"assign_perizia",args:{id:periziaId,assignee:"forbidden"}},
      {name:"advance_perizia",args:{id:periziaId,target:"Revised"}},
      {name:"cancel_perizia",args:{id:periziaId}},
      {name:"create_sinistro",args:{
        externalReference:forbiddenSinistroRef,occurredOn:"2026-11-20"}},
      {name:"update_sinistro",args:{
        id:claimId,externalReference:forbiddenSinistroRef,
        occurredOn:"2026-11-20",expectedVersion:2}}
    ];
    for (const [i, item] of denied.entries()) {
      const raw = await signedCall(item.name, item.args, readToken, 6420+i);
      expect(raw, item.name + " must be denied").toContain('"isError":true');
      expect(raw).not.toContain("tenantId");
    }
    expect(await get("/api/perizie/" + periziaId)).toMatchObject({
      id:periziaId,status:"ToBeRevised",assignedTo:assignee
    });
    expect(await get("/api/perizie/" + cancelId)).toMatchObject({
      id:cancelId,status:"Cancelled"
    });
    expect(await get("/api/sinistri/" + claimId)).toMatchObject({
      id:claimId,externalReference:updatedRef,version:2
    });
    const perizieList = await request.get(api + "/api/perizie", {
      headers:readHeaders
    });
    expect(perizieList.status(), await perizieList.text()).toBe(200);
    expect((await perizieList.json() as {reference:string}[])
      .some(item => item.reference === forbiddenPeriziaRef)).toBe(false);
    const deniedSearch = await request.get(api + "/api/sinistri/search?" +
      new URLSearchParams({prefix:forbiddenSinistroRef,limit:"10"}), {
      headers:readHeaders
    });
    expect(deniedSearch.status(), await deniedSearch.text()).toBe(200);
    expect((await deniedSearch.json() as {externalReference:string}[])
      .some(item => item.externalReference === forbiddenSinistroRef)).toBe(false);
  });

  test("signed MCP CAS link lifecycle, scanned ingestion and guarded delete output", async ({request}) => {
    test.skip(!process.env.E2E_FULL_STACK, "Real Keycloak, PostgreSQL, ClamAV and S3 required");
    test.setTimeout(120_000);
    const writer = await tokenFor(request, "peritial-test-write");
    const reader = await tokenFor(request);
    const headers = {Authorization:"Bearer " + reader};
    const nonce = Date.now() + "-" + Math.floor(Math.random()*1000000);
    const covered = new Set<string>();

    async function call(name:string, args:Record<string, unknown>, token:string, id:number) {
      const response = await request.post(api + "/mcp", {
        headers:{
          Authorization:"Bearer " + token,
          Accept:"application/json,text/event-stream",
          "Content-Type":"application/json"
        },
        data:{jsonrpc:"2.0",id,method:"tools/call",params:{name,arguments:args}}
      });
      const raw = await response.text();
      expect(response.ok(), raw).toBeTruthy();
      return raw;
    }
    async function mutate(name:typeof mutationWireTools[number],
      args:Record<string, unknown>,id:number) {
      const entry = catalog.tools.find(t=>t.name===name);
      expect(entry?.access).toBe("write");
      expect(entry?.effect).toBe("mutating");
      const match = /^Task<(.+)>$/.exec(entry!.returnType);
      expect(match).not.toBeNull();
      const raw = await call(name,args,writer,id);
      const result = extractCallResult(raw);
      checkWireValue(match![1],result,name);
      expect(raw).not.toContain("tenantId");
      expect(raw).not.toContain("storageKey");
      covered.add(name);
      return result as Record<string, unknown>;
    }
    async function restGet(path:string) {
      const response = await request.get(api + path,{headers});
      expect(response.status(),await response.text()).toBe(200);
      return response.json() as Promise<Record<string, unknown>>;
    }
    async function create(path:string, data:Record<string,unknown>) {
      const response = await request.post(api + path,{
        headers:{Authorization:"Bearer " + writer},data
      });
      expect(response.status(),await response.text()).toBe(201);
      return response.json() as Promise<{id:string}>;
    }

    const perizia = await create("/api/perizie",{
      reference:"MCP-WIRE-LINK-" + nonce
    });
    const sinistroA = await create("/api/sinistri",{
      externalReference:"MCP-WIRE-LINK-A-" + nonce,occurredOn:"2026-11-18"
    });
    const sinistroB = await create("/api/sinistri",{
      externalReference:"MCP-WIRE-LINK-B-" + nonce,occurredOn:"2026-11-19"
    });
    const associationPath = "/api/perizie/" + perizia.id + "/sinistro";
    expect(await restGet(associationPath)).toMatchObject({
      periziaId:perizia.id,sinistroId:null,linkVersion:1
    });

    const linked = await mutate("link_perizia_sinistro",{
      periziaId:perizia.id,sinistroId:sinistroA.id,expectedLinkVersion:1
    },6501);
    expect(linked).toMatchObject({
      periziaId:perizia.id,sinistroId:sinistroA.id,linkVersion:2
    });
    expect(await restGet(associationPath)).toMatchObject(linked);

    // One single CAS advances the revision 2 -> 3, never a transient unlink.
    const moved = await mutate("reparent_perizia_sinistro",{
      periziaId:perizia.id,expectedSinistroId:sinistroA.id,
      newSinistroId:sinistroB.id,expectedLinkVersion:2
    },6502);
    expect(moved).toMatchObject({
      periziaId:perizia.id,sinistroId:sinistroB.id,linkVersion:3
    });
    expect(await restGet(associationPath)).toMatchObject(moved);

    const unlinked = await mutate("unlink_perizia_sinistro",{
      periziaId:perizia.id,expectedSinistroId:sinistroB.id,
      expectedLinkVersion:3
    },6503);
    expect(unlinked).toMatchObject({periziaId:perizia.id,linkVersion:4});
    expect(unlinked.sinistroId ?? null).toBeNull();
    expect(await restGet(associationPath)).toMatchObject({
      periziaId:perizia.id,sinistroId:null,linkVersion:4
    });

    // ABA: a stale version 1 cannot relink the same entity after unlink.
    const stale = await call("link_perizia_sinistro",{
      periziaId:perizia.id,sinistroId:sinistroA.id,expectedLinkVersion:1
    },writer,6504);
    expect(stale).toContain('"isError":true');
    expect(await restGet(associationPath)).toMatchObject({
      sinistroId:null,linkVersion:4
    });

    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const fileName = "mcp-wire-ingest-" + nonce + ".pdf";
    const ingestArgs = {
      fileName,contentType:"application/pdf",base64:pdf.toString("base64"),
      periziaId:perizia.id,idempotencyKey:"wire-" + nonce
    };
    const doc = await mutate("ingest_file",ingestArgs,6505);
    expect(doc).toMatchObject({
      periziaId:perizia.id,fileName,contentType:"application/pdf",length:pdf.length
    });
    const documentId = doc.id as string;
    expect(await restGet("/api/files/" + documentId)).toMatchObject({
      id:documentId,periziaId:perizia.id,fileName,length:pdf.length
    });

    // Idempotency key must return the exact prior document, not write a
    // second object or expose its storage key in the MCP result.
    const replay = await mutate("ingest_file",ingestArgs,6506);
    expect(replay).toEqual(doc);
    const catalogResponse = await request.get(
      api + "/api/files?periziaId=" + perizia.id + "&limit=50",{headers});
    expect(catalogResponse.status(),await catalogResponse.text()).toBe(200);
    const docs = await catalogResponse.json() as {id:string}[];
    expect(docs.filter(x=>x.id===documentId)).toHaveLength(1);

    const denied = [
      {name:"link_perizia_sinistro",
        args:{periziaId:perizia.id,sinistroId:sinistroA.id,expectedLinkVersion:4}},
      {name:"reparent_perizia_sinistro",
        args:{periziaId:perizia.id,expectedSinistroId:sinistroA.id,
          newSinistroId:sinistroB.id,expectedLinkVersion:4}},
      {name:"unlink_perizia_sinistro",
        args:{periziaId:perizia.id,expectedSinistroId:sinistroA.id,
          expectedLinkVersion:4}},
      {name:"ingest_file",
        args:{...ingestArgs,idempotencyKey:"denied-wire-" + nonce}}
    ];
    for (const [i,item] of denied.entries()) {
      const raw = await call(item.name,item.args,reader,6510+i);
      expect(raw,item.name+" must deny read scope").toContain('"isError":true');
      expect(raw).not.toContain("storageKey");
      expect(raw).not.toContain("tenantId");
    }
    expect(await restGet(associationPath)).toMatchObject({
      periziaId:perizia.id,sinistroId:null,linkVersion:4
    });

    // Hard deletion is not a supported successful wire operation until
    // production retention, legal holds, approvals and recovery exist.
    // It must remain denied even to the ordinary signed writer.
    const deleteEntry = catalog.tools.find(t=>t.name==="delete_sinistro");
    expect(deleteEntry).toMatchObject({
      access:"write+delete",effect:"mutating",returnType:"Task<bool>"
    });
    const blocked = await call("delete_sinistro",{
      id:sinistroA.id,expectedVersion:1
    },writer,6520);
    expect(blocked).toContain('"isError":true');
    expect(blocked).not.toContain("tenantId");
    expect(await restGet("/api/sinistri/" + sinistroA.id)).toMatchObject({
      id:sinistroA.id,version:1
    });
    expect(await restGet("/api/sinistri/" + sinistroB.id)).toMatchObject({
      id:sinistroB.id,version:1
    });
    expect([...covered].sort()).toEqual([
      "ingest_file","link_perizia_sinistro",
      "reparent_perizia_sinistro","unlink_perizia_sinistro"
    ].sort());
  });

  test("modern detail tools publish an output schema and natural structuredContent", async ({request}) => {
    test.skip(!process.env.E2E_FULL_STACK,"Real Keycloak and PostgreSQL required");
    test.setTimeout(90_000);
    const manifest=JSON.parse(readFileSync(
      new URL("../../../docs/mcp-structured-outputs.v1.json",import.meta.url),
      "utf8"
    )) as {format:number;version:string;mcpCatalogVersion:string;tools:string[]};
    expect(manifest).toMatchObject({
      format:1,version:"1.0.0",mcpCatalogVersion:catalog.version
    });
    expect(manifest.tools).toEqual([
      "get_document","get_perizia","get_perizia_sinistro","get_sinistro",
      "page_documents","page_sinistri","plan_legacy_xml",
      "preview_legacy_xml","search_sinistri_page"
    ]);
    const read=await tokenFor(request);
    const writer=await tokenFor(request,"peritial-test-write");
    const meta={
      "io.modelcontextprotocol/protocolVersion":"2026-07-28",
      "io.modelcontextprotocol/clientInfo":{
        name:"peritial-structured-e2e",version:"1.0.0"
      },
      "io.modelcontextprotocol/clientCapabilities":{}
    };
    async function call(method:"tools/list"|"tools/call",args:Record<string,unknown>,
      id:number, token=read) {
      const name=method==="tools/call" ? String(args.name) : "";
      const response=await request.post(api+"/mcp",{
        headers:{
          Authorization:"Bearer "+token,
          Accept:"application/json,text/event-stream",
          "Content-Type":"application/json",
          "MCP-Protocol-Version":"2026-07-28",
          "Mcp-Method":method,
          ...(method==="tools/call" ? {"Mcp-Name":name} : {})
        },
        data:{jsonrpc:"2.0",id,method,params:{...args,_meta:meta}}
      });
      const body=await response.text();
      expect(response.ok(),body).toBeTruthy();
      const packets:RpcResponse[]=body.trimStart().startsWith("{")
        ? [JSON.parse(body) as RpcResponse]
        : body.split(/\r?\n/).filter(x=>x.startsWith("data:"))
          .map(x=>JSON.parse(x.slice(5).trim()) as RpcResponse);
      const packet=packets.find(x=>x.result||x.error);
      expect(packet?.error).toBeUndefined();
      expect(packet?.result).toBeDefined();
      return packet!.result!;
    }
    const listing=await call("tools/list",{},6601);
    const tools=(listing as unknown as {tools:{name:string;outputSchema?:unknown}[]}).tools;
    expect(tools).toHaveLength(25);
    expect(tools.filter(x=>x.outputSchema!==undefined).map(x=>x.name).sort())
      .toEqual(manifest.tools);

    const nonce=Date.now()+"-"+Math.floor(Math.random()*1000000);
    const fixtures=[
      {name:"get_perizia",dtype:"PeriziaDto",path:"/api/perizie",
        body:{reference:"MCP-STRUCT-P-"+nonce}},
      {name:"get_sinistro",dtype:"SinistroDto",path:"/api/sinistri",
        body:{externalReference:"MCP-STRUCT-S-"+nonce,occurredOn:"2026-11-21"}}
    ];
    for(const [i,item] of fixtures.entries()){
      const create=await request.post(api+item.path,{
        headers:{Authorization:"Bearer "+writer},data:item.body
      });
      expect(create.status(),await create.text()).toBe(201);
      const id=(await create.json() as {id:string}).id;
      const rawSchema=tools.find(x=>x.name===item.name)?.outputSchema;
      expect(rawSchema).toBeDefined();
      const schema=rawSchema as Record<string,unknown>;
      const defs=(schema.$defs ?? {}) as Record<string,Record<string,unknown>>;
      const root=typeof schema.$ref==="string" && schema.$ref.startsWith("#/$defs/")
        ? defs[schema.$ref.slice(8)] : schema;
      const props=root?.properties as Record<string,unknown>|undefined;
      expect(props,item.name+" must publish JSON object properties").toBeDefined();
      expect(Object.keys(props!).sort()).toEqual(
        Object.keys(outputs.types[item.dtype])
          .map(k=>k[0].toLowerCase()+k.slice(1)).sort()
      );
      expect(JSON.stringify(schema)).not.toContain("tenantId");
      expect(JSON.stringify(schema)).not.toContain("storageKey");
      const result=await call("tools/call",{
        name:item.name,arguments:{id}
      },6602+i);
      expect(result.isError).not.toBe(true);
      expect(result.structuredContent,item.name).toBeDefined();
      checkWireValue(item.dtype,result.structuredContent,item.name);
      expect((result.structuredContent as Record<string,unknown>).id).toBe(id);
      expect(JSON.stringify(result.structuredContent)).not.toContain("tenantId");
      const rest=await request.get(api+item.path+"/"+id,{
        headers:{Authorization:"Bearer "+read}
      });
      expect(rest.status(),await rest.text()).toBe(200);
      const restValue=await rest.json() as Record<string,unknown>;
      const structuredValue=result.structuredContent as Record<string,unknown>;
      // The MCP serializer omits nullable nulls, whereas ASP.NET REST
      // includes them explicitly. Compare every reviewed property: only
      // a declared nullable null can be absent in the MCP projection.
      for(const [key,value] of Object.entries(restValue)){
        const property=key[0].toUpperCase()+key.slice(1);
        const reviewedType=outputs.types[item.dtype][property];
        expect(reviewedType,item.name+" REST property must be reviewed").toBeDefined();
        if(value===null && reviewedType.endsWith("?")){
          expect(structuredValue[key]??null,item.name+"."+key).toBeNull();
        }else{
          expect(Object.prototype.hasOwnProperty.call(structuredValue,key),
            item.name+"."+key+" missing").toBe(true);
          expect(structuredValue[key],item.name+"."+key).toEqual(value);
        }
      }
    }

    // New read-only DTOs: scanned document metadata and a revisioned
    // perizia association. Use REST only for fixture setup/read-back;
    // never use the REST JSON as a substitute for an actual MCP result.
    const caseResponse=await request.post(api+"/api/perizie",{
      headers:{Authorization:"Bearer "+writer},
      data:{reference:"MCP-STRUCT-ASSOC-"+nonce}
    });
    expect(caseResponse.status(),await caseResponse.text()).toBe(201);
    const caseId=(await caseResponse.json() as {id:string}).id;
    const claimResponse=await request.post(api+"/api/sinistri",{
      headers:{Authorization:"Bearer "+writer},
      data:{externalReference:"MCP-STRUCT-CLAIM-"+nonce,
        occurredOn:"2026-11-20"}
    });
    expect(claimResponse.status(),await claimResponse.text()).toBe(201);
    const claimId=(await claimResponse.json() as {id:string}).id;

    const associationBefore=await call("tools/call",{
      name:"get_perizia_sinistro",arguments:{periziaId:caseId}
    },6610);
    checkWireValue("PeriziaSinistroAssociationDto",
      associationBefore.structuredContent,"association.unlinked");
    expect(associationBefore.structuredContent).toMatchObject({
      periziaId:caseId,linkVersion:1
    });
    expect((associationBefore.structuredContent as Record<string,unknown>)
      .sinistroId??null).toBeNull();

    const linked=await request.post(api+"/api/perizie/"+caseId+"/sinistro",{
      headers:{Authorization:"Bearer "+writer},
      data:{sinistroId:claimId,expectedLinkVersion:1}
    });
    expect(linked.status(),await linked.text()).toBe(201);

    const pdf=Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const fileName="mcp-structured-document-"+nonce+".pdf";
    const uploaded=await request.post(api+"/api/files",{
      headers:{Authorization:"Bearer "+writer},
      multipart:{file:{name:fileName,mimeType:"application/pdf",buffer:pdf},
        periziaId:caseId}
    });
    expect(uploaded.status(),await uploaded.text()).toBe(201);
    const documentId=(await uploaded.json() as {id:string}).id;

    const more=[
      {name:"get_document",dtype:"DocumentSummaryDto",
        args:{id:documentId},restPath:"/api/files/"+documentId},
      {name:"get_perizia_sinistro",dtype:"PeriziaSinistroAssociationDto",
        args:{periziaId:caseId},restPath:"/api/perizie/"+caseId+"/sinistro"}
    ];
    for(const [offset,item] of more.entries()){
      const rawSchema=tools.find(t=>t.name===item.name)?.outputSchema;
      expect(rawSchema,item.name+" must publish outputSchema").toBeDefined();
      const schema=rawSchema as Record<string,unknown>;
      const defs=(schema.$defs??{}) as Record<string,Record<string,unknown>>;
      const root=typeof schema.$ref==="string" &&
        schema.$ref.startsWith("#/$defs/")
        ? defs[schema.$ref.slice(8)] : schema;
      const props=root?.properties as Record<string,unknown>|undefined;
      expect(props,item.name+" output properties").toBeDefined();
      expect(Object.keys(props!).sort()).toEqual(
        Object.keys(outputs.types[item.dtype])
          .map(key=>key[0].toLowerCase()+key.slice(1)).sort()
      );
      expect(JSON.stringify(schema)).not.toContain("tenantId");
      expect(JSON.stringify(schema)).not.toContain("storageKey");
      const value=await call("tools/call",{
        name:item.name,arguments:item.args
      },6611+offset);
      expect(value.isError).not.toBe(true);
      expect(value.structuredContent,item.name+" structuredContent").toBeDefined();
      checkWireValue(item.dtype,value.structuredContent,item.name+".structured");
      const actual=value.structuredContent as Record<string,unknown>;
      expect(JSON.stringify(actual)).not.toContain("tenantId");
      expect(JSON.stringify(actual)).not.toContain("storageKey");
      const readback=await request.get(api+item.restPath,{
        headers:{Authorization:"Bearer "+read}
      });
      expect(readback.status(),await readback.text()).toBe(200);
      const expected=await readback.json() as Record<string,unknown>;
      for(const [key,entry] of Object.entries(expected)){
        const typed=outputs.types[item.dtype][key[0].toUpperCase()+key.slice(1)];
        expect(typed,item.name+"."+key+" approved property").toBeDefined();
        if(entry===null && typed.endsWith("?")){
          expect(actual[key]??null,item.name+"."+key).toBeNull();
        }else{
          expect(Object.prototype.hasOwnProperty.call(actual,key),
            item.name+"."+key+" required").toBe(true);
          expect(actual[key],item.name+"."+key).toEqual(entry);
        }
      }
    }
    expect((await request.get(api+"/api/perizie/"+caseId+"/sinistro",{
      headers:{Authorization:"Bearer "+read}
    })).status()).toBe(200);

    // Paginated nested DTOs: MCP must advertise their outputSchema and
    // preserve a filter-bound cursor over REAL reader-scoped records.
    // Reuse the scanned document / linked claim created above, then add
    // one document and two filtered Sinistri for deterministic 2-page tests.
    const secondName="mcp-structured-second-"+nonce+".pdf";
    const uploaded2=await request.post(api+"/api/files",{
      headers:{Authorization:"Bearer "+writer},
      multipart:{file:{name:secondName,mimeType:"application/pdf",buffer:pdf},
        periziaId:caseId}
    });
    expect(uploaded2.status(),await uploaded2.text()).toBe(201);
    const document2=(await uploaded2.json() as {id:string}).id;
    const prefix="MCP-STRUCT-PAGED-"+nonce;
    const pagedClaims=[
      {externalReference:prefix+"-A",occurredOn:"2026-11-18"},
      {externalReference:prefix+"-B",occurredOn:"2026-11-19"}
    ];
    const claimIds:string[]=[];
    for(const body of pagedClaims){
      const response=await request.post(api+"/api/sinistri",{
        headers:{Authorization:"Bearer "+writer},data:body
      });
      expect(response.status(),await response.text()).toBe(201);
      claimIds.push((await response.json() as {id:string}).id);
    }
    const pageSchemas=[
      {name:"page_documents",type:"DocumentPageDto",
        nested:"DocumentSummaryDto"},
      {name:"page_sinistri",type:"SinistroPageDto",
        nested:"SinistroDto"},
      {name:"search_sinistri_page",type:"SinistroPageDto",
        nested:"SinistroDto"}
    ];
    for(const item of pageSchemas){
      const schema = tools.find(t => t.name === item.name)?.outputSchema as Record<string, unknown> | undefined;
      expect(schema,item.name+" outputSchema").toBeDefined();
      const defs=(schema!.$defs??{}) as Record<string,Record<string,unknown>>;
      const root=typeof schema!.$ref==="string" &&
        schema!.$ref.startsWith("#/$defs/")
        ? defs[schema!.$ref.slice(8)] : schema!;
      expect(Object.keys((root.properties??{}) as Record<string,unknown>).sort(),
        item.name+" public pagination contract").toEqual(["items","nextCursor"]);
      const property=(root.properties as Record<string,unknown>).items;
      expect(property,item.name+" items array schema").toBeDefined();
      expect(JSON.stringify(schema)).not.toContain("storageKey");
      expect(JSON.stringify(schema)).not.toContain("tenantId");
    }
    const pageCases=[
      {name:"page_documents",dto:"DocumentPageDto",
        args:{periziaId:caseId,limit:1},
        restPath:"/api/files/page?periziaId="+caseId+"&limit=1",
        expectedIds:[documentId,document2]},
      {name:"search_sinistri_page",dto:"SinistroPageDto",
        args:{prefix,occurredFrom:"2026-11-18",
          occurredTo:"2026-11-19",limit:1},
        restPath:"/api/sinistri/search/page?prefix="+encodeURIComponent(prefix)+
          "&occurredFrom=2026-11-18&occurredTo=2026-11-19&limit=1",
        expectedIds:claimIds}
    ];
    for(const [offset,paged] of pageCases.entries()){
      const page1=await call("tools/call",{
        name:paged.name,arguments:paged.args
      },6640+offset*5);
      expect(page1.isError,paged.name+" first page").not.toBe(true);
      checkWireValue(paged.dto,page1.structuredContent,paged.name+".page1");
      const first=page1.structuredContent as {
        items:Record<string,unknown>[];nextCursor?:string|null};
      expect(first.items).toHaveLength(1);
      expect(typeof first.nextCursor,paged.name+" must paginate").toBe("string");
      expect(first.nextCursor?.length).toBeGreaterThan(0);
      const page2=await call("tools/call",{
        name:paged.name,arguments:{...paged.args,cursor:first.nextCursor}
      },6641+offset*5);
      expect(page2.isError,paged.name+" second page").not.toBe(true);
      checkWireValue(paged.dto,page2.structuredContent,paged.name+".page2");
      const second=page2.structuredContent as {
        items:Record<string,unknown>[];nextCursor?:string|null};
      expect(second.items).toHaveLength(1);
      expect(second.nextCursor??null,paged.name+" pagination ends").toBeNull();
      expect([first.items[0].id,second.items[0].id].sort(),
        paged.name+" paged IDs").toEqual([...paged.expectedIds].sort());
      for(const [page,cursor] of [[first,undefined],[second,first.nextCursor]] as const){
        const url=api+paged.restPath+
          (cursor===undefined?"":"&cursor="+encodeURIComponent(cursor??""));
        const rest=await request.get(url,{
          headers:{Authorization:"Bearer "+read}
        });
        expect(rest.status(),await rest.text()).toBe(200);
        const restPage=await rest.json() as {
          items:Record<string,unknown>[];nextCursor?:string|null};
        expect(page.items.map(x=>x.id)).toEqual(restPage.items.map(x=>x.id));
        expect(page.nextCursor??null).toEqual(restPage.nextCursor??null);
        expect(JSON.stringify(page)).not.toContain("tenantId");
        expect(JSON.stringify(page)).not.toContain("storageKey");
      }
      // A cursor for another filter is never an authorization primitive.
      // The REST gate must reject it even for an otherwise signed reader.
      const wrongFilter=paged.name==="page_documents"
        ? {periziaId:"00000000-0000-4000-8000-000000000002",
          limit:1,cursor:first.nextCursor}
        : {prefix:prefix+"-MISMATCH",occurredFrom:"2026-11-18",
          occurredTo:"2026-11-19",limit:1,cursor:first.nextCursor};
      const bad=await call("tools/call",{
        name:paged.name,arguments:wrongFilter
      },6642+offset*5);
      expect(bad.isError,paged.name+" foreign-filter cursor denied").toBe(true);
    }

    // Global (unfiltered) Sinistri cursor: do not assume an empty or
    // isolated tenant catalog. Compare both signed MCP pages with REST,
    // including the cursor and reviewed nested DTOs, under the same read token.
    const globalArgs={limit:1};
    const globalFirst=await call("tools/call",{
      name:"page_sinistri",arguments:globalArgs
    },6660);
    expect(globalFirst.isError).not.toBe(true);
    checkWireValue("SinistroPageDto",globalFirst.structuredContent,
      "page_sinistri.page1");
    const globalPage1=globalFirst.structuredContent as {
      items:Record<string,unknown>[];nextCursor?:string|null};
    expect(globalPage1.items).toHaveLength(1);
    expect(typeof globalPage1.nextCursor).toBe("string");
    const globalSecond=await call("tools/call",{
      name:"page_sinistri",arguments:{
        ...globalArgs,cursor:globalPage1.nextCursor
      }
    },6661);
    expect(globalSecond.isError).not.toBe(true);
    checkWireValue("SinistroPageDto",globalSecond.structuredContent,
      "page_sinistri.page2");
    const globalPage2=globalSecond.structuredContent as {
      items:Record<string,unknown>[];nextCursor?:string|null};
    expect(globalPage2.items).toHaveLength(1);
    expect(globalPage1.items[0].id).not.toBe(globalPage2.items[0].id);
    for(const [page,cursor] of [[globalPage1,undefined],
      [globalPage2,globalPage1.nextCursor]] as const){
      const suffix=cursor===undefined?"":"&cursor="+encodeURIComponent(cursor??"");
      const rest=await request.get(api+"/api/sinistri/page?limit=1"+suffix,{
        headers:{Authorization:"Bearer "+read}
      });
      expect(rest.status(),await rest.text()).toBe(200);
      const expected=await rest.json() as {
        items:Record<string,unknown>[];nextCursor?:string|null};
      expect(page.items.map(x=>x.id)).toEqual(expected.items.map(x=>x.id));
      for(const [index,actual] of page.items.entries()){
        const reviewed=expected.items[index];
        for(const [key,value] of Object.entries(reviewed)){
          const type=outputs.types.SinistroDto[key[0].toUpperCase()+key.slice(1)];
          expect(type,"SinistroDto."+key+" reviewed").toBeDefined();
          if(value===null && type.endsWith("?")){
            // MCP omits nullable null, while REST can include it.
            expect(actual[key]??null,"SinistroDto."+key).toBeNull();
          }else{
            expect(Object.prototype.hasOwnProperty.call(actual,key),
              "SinistroDto."+key+" required").toBe(true);
            expect(actual[key],"SinistroDto."+key).toEqual(value);
          }
        }
      }
      expect(page.nextCursor??null).toEqual(expected.nextCursor??null);
      expect(JSON.stringify(page)).not.toContain("tenantId");
      expect(JSON.stringify(page)).not.toContain("storageKey");
    }

    // XML preflights are read-only in effect, but NEVER read-scope
    // authorized. Modern MCP must expose reviewed nested DTO schemas
    // without returning source PII or importing an unrepresentable case.
    const xmlPrivate="PRIVATE-STRUCT-PREFLIGHT-"+nonce;
    const xmlFixtures=[
      {
        format:"accertatore",
        reference:"MCP-STRUCT-ACCERTATORE-"+nonce,
        occurredOn:"2026-11-17",
        xml:"<it.autosoftws.IncaricoAccertatoreRecord>"+
          "<NumeroSinistro>MCP-STRUCT-ACCERTATORE-"+nonce+"</NumeroSinistro>"+
          "<DataOraSinistro>2026-11-17</DataOraSinistro>"+
          "<DataIncarico>2026-11-18</DataIncarico>"+
          "<Contraente><Nome>"+xmlPrivate+"</Nome></Contraente>"+
          "</it.autosoftws.IncaricoAccertatoreRecord>"
      },
      {
        format:"classic",
        reference:"MCP-STRUCT-CLASSIC-"+nonce,
        occurredOn:"2026-11-19",
        xml:"<it.autosoftws.ReIncaricoBaseRecord>"+
          "<NumeroSinistro>MCP-STRUCT-CLASSIC-"+nonce+"</NumeroSinistro>"+
          "<DataOraSinistro>2026-11-19 09:00:00</DataOraSinistro>"+
          "<DataIncarico>2026-11-20</DataIncarico>"+
          "</it.autosoftws.ReIncaricoBaseRecord>"
      }
    ];
    const preflightShapes=[
      {name:"preview_legacy_xml",dto:"LegacyXmlPreviewDto",rest:"preview"},
      {name:"plan_legacy_xml",dto:"LegacyXmlImportPlanDto",rest:"plan"}
    ];
    for(const item of preflightShapes){
      const schema=tools.find(t=>t.name===item.name)?.outputSchema as
        Record<string,unknown>|undefined;
      expect(schema,item.name+" must publish outputSchema").toBeDefined();
      const defs=(schema!.$defs??{}) as Record<string,Record<string,unknown>>;
      const root=typeof schema!.$ref==="string" &&
        schema!.$ref.startsWith("#/$defs/")
        ? defs[schema!.$ref.slice(8)] : schema!;
      expect(Object.keys((root.properties??{}) as Record<string,unknown>).sort())
        .toEqual(Object.keys(outputs.types[item.dto])
          .map(key=>key[0].toLowerCase()+key.slice(1)).sort());
      const schemaJson=JSON.stringify(schema);
      expect(schemaJson).not.toContain("tenantId");
      expect(schemaJson).not.toContain("storageKey");
      if(item.name==="plan_legacy_xml")
        expect(schemaJson).toContain("hasUnmappedBusinessData");
    }
    function assertPreflightParity(type:string,mcp:unknown,rest:unknown,label:string):void{
      checkWireValue(type,mcp,label);
      const actual=mcp as Record<string,unknown>;
      const expected=rest as Record<string,unknown>;
      for(const [prop,kind] of Object.entries(outputs.types[type])){
        const name=prop[0].toLowerCase()+prop.slice(1);
        expect(Object.prototype.hasOwnProperty.call(expected,name),
          label+" REST."+name+" must exist").toBe(true);
        if(expected[name]===null && kind.endsWith("?")){
          expect(actual[name]??null,label+"."+name).toBeNull();
        }else if(outputs.types[kind]){
          assertPreflightParity(kind,actual[name],expected[name],label+"."+name);
        }else{
          expect(Object.prototype.hasOwnProperty.call(actual,name),
            label+"."+name+" required").toBe(true);
          expect(actual[name],label+"."+name).toEqual(expected[name]);
        }
      }
    }
    for(const [i,fixture] of xmlFixtures.entries()){
      for(const [j,item] of preflightShapes.entries()){
        const result=await call("tools/call",{
          name:item.name,arguments:{xml:fixture.xml}
        },6680+i*4+j,writer);
        expect(result.isError,item.name+" must succeed with write").not.toBe(true);
        expect(result.structuredContent,item.name).toBeDefined();
        checkWireValue(item.dto,result.structuredContent,item.name);
        const actual=result.structuredContent as Record<string,unknown>;
        const preview=(item.name==="plan_legacy_xml" ? actual.preview : actual)
          as Record<string,unknown>;
        expect(preview).toMatchObject({
          format:fixture.format,externalReference:fixture.reference,
          occurredOn:fixture.occurredOn,attachmentCount:0,
          hasUnmappedBusinessData:true
        });
        if(item.name==="plan_legacy_xml"){
          expect(actual.status).toBe("new");
          expect(actual.existingSinistroId??null).toBeNull();
        }
        const serialized=JSON.stringify(actual);
        expect(serialized).not.toContain(xmlPrivate);
        expect(serialized).not.toContain("09:00:00");
        expect(serialized).not.toContain("tenantId");
        expect(serialized).not.toContain("storageKey");
        const rest=await request.post(api+"/api/imports/xml/"+item.rest,{
          headers:{Authorization:"Bearer "+writer,
            "Content-Type":"application/xml"},
          data:Buffer.from(fixture.xml,"utf8")
        });
        expect(rest.status(),await rest.text()).toBe(200);
        assertPreflightParity(item.dto,actual,await rest.json(),item.name);
        const denied=await call("tools/call",{
          name:item.name,arguments:{xml:fixture.xml}
        },6690+i*4+j);
        expect(denied.isError,item.name+" read scope must be denied").toBe(true);
        expect(JSON.stringify(denied)).not.toContain(xmlPrivate);
      }
      const searched=await request.get(api+"/api/sinistri/search?"+
        new URLSearchParams({prefix:fixture.reference,limit:"10"}),{
        headers:{Authorization:"Bearer "+writer}
      });
      expect(searched.status(),await searched.text()).toBe(200);
      const cases=await searched.json() as {externalReference:string}[];
      expect(cases.some(x=>x.externalReference===fixture.reference)).toBe(false);
    }

  });

});
