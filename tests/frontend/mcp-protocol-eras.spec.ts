import {readFileSync} from "node:fs";
import {test, expect, type APIRequestContext} from "@playwright/test";

// This is a protocol-level client probe, intentionally independent of
// Peritial's REST DTO validation. It uses raw JSON-RPC and Streamable HTTP
// headers to catch silent SDK upgrades / mixed-era routing regressions.
type ProtocolContract = {
  format:number; version:string; sdkPackage:string; sdkVersion:string;
  transport:string; modern:string; legacy:string;
  requiredModernHeaders:string[];
  errorCodes:{unsupportedProtocolVersion:number;headerMismatch:number};
};
const contract = JSON.parse(readFileSync(
  new URL("../../../docs/mcp-protocol-eras.v1.json",import.meta.url),"utf8"
)) as ProtocolContract;
const api = "http://localhost:8080";
const issuer = "http://localhost:8081/realms/peritial";
const baseHeaders = {
  Accept:"application/json,text/event-stream",
  "Content-Type":"application/json"
};

type Envelope = {
  id?:number;
  result?:Record<string,unknown>;
  error?:{code?:number;message?:string;data?:unknown};
};
function decode(body:string):Envelope {
  const packets = body.trimStart().startsWith("{")
    ? [JSON.parse(body) as Envelope]
    : body.split(/\r?\n/).filter(row=>row.startsWith("data:"))
      .map(row=>JSON.parse(row.slice(5).trim()) as Envelope);
  const packet = packets.find(row=>row.result!==undefined || row.error!==undefined);
  expect(packet,"A JSON-RPC response packet is required").toBeDefined();
  return packet!;
}
async function token(request:APIRequestContext,clientId:string) {
  const response=await request.post(issuer+"/protocol/openid-connect/token",{
    form:{grant_type:"password",client_id:clientId,
      username:"localuser",password:"local-only-user"}
  });
  expect(response.status(),await response.text()).toBe(200);
  return (await response.json() as {access_token:string}).access_token;
}
function modernMeta(version:string) {
  return {
    "io.modelcontextprotocol/protocolVersion":version,
    "io.modelcontextprotocol/clientInfo":{
      name:"peritial-protocol-compat-e2e",version:"1.0.0"
    },
    "io.modelcontextprotocol/clientCapabilities":{}
  };
}

test.describe("MCP C# SDK 2.0 dual-era version negotiation",()=>{
  test("the versioned protocol manifest remains explicit and bounded",()=>{
    expect(contract).toEqual({
      format:1,version:"1.0.0",
      sdkPackage:"ModelContextProtocol.AspNetCore",sdkVersion:"2.0.0",
      transport:"streamable-http-stateless",
      modern:"2026-07-28",legacy:"2025-11-25",
      requiredModernHeaders:["MCP-Protocol-Version","Mcp-Method"],
      errorCodes:{unsupportedProtocolVersion:-32022,headerMismatch:-32020}
    });
  });

  test("modern discovery and legacy initialize both reach scoped tools",async ({request})=>{
    test.skip(!process.env.E2E_FULL_STACK,"Real Keycloak and MCP endpoint required");
    test.setTimeout(75_000);
    const read=await token(request,"peritial-test-readonly");
    const writer=await token(request,"peritial-test-write");
    const headers={...baseHeaders,Authorization:"Bearer "+read};

    // Modern era: server/discover is the bootstrap, no initialize or session.
    const discover=await request.post(api+"/mcp",{
      headers:{...headers,"MCP-Protocol-Version":contract.modern,
        "Mcp-Method":"server/discover"},
      data:{jsonrpc:"2.0",id:7001,method:"server/discover",
        params:{_meta:modernMeta(contract.modern)}}
    });
    const discoverBody=await discover.text();
    expect(discover.ok(),discoverBody).toBeTruthy();
    expect(discover.headers()["mcp-session-id"]).toBeUndefined();
    expect(discover.headers()["cache-control"]).toContain("no-store");
    const discovered=decode(discoverBody);
    expect(discovered.error).toBeUndefined();
    expect(discovered.result?.supportedVersions).toContain(contract.modern);
    expect(discovered.result?.capabilities).toHaveProperty("tools");

    const modernList=await request.post(api+"/mcp",{
      headers:{...headers,"MCP-Protocol-Version":contract.modern,
        "Mcp-Method":"tools/list"},
      data:{jsonrpc:"2.0",id:7002,method:"tools/list",
        params:{_meta:modernMeta(contract.modern)}}
    });
    expect(modernList.ok(),await modernList.text()).toBeTruthy();
    expect(modernList.headers()["mcp-session-id"]).toBeUndefined();
    const modernTools=decode(await modernList.text()).result?.tools as {name:string}[];
    expect(modernTools.map(t=>t.name)).toContain("get_perizia");
    expect(modernTools).toHaveLength(25);

    const nonce=Date.now()+"-"+Math.floor(Math.random()*1000000);
    const reference="MCP-MODERN-"+nonce;
    const created=await request.post(api+"/api/perizie",{
      headers:{Authorization:"Bearer "+writer},data:{reference}
    });
    expect(created.status(),await created.text()).toBe(201);
    const id=(await created.json() as {id:string}).id;

    const readModern=await request.post(api+"/mcp",{
      headers:{...headers,"MCP-Protocol-Version":contract.modern,
        "Mcp-Method":"tools/call","Mcp-Name":"get_perizia"},
      data:{jsonrpc:"2.0",id:7003,method:"tools/call",params:{
        name:"get_perizia",arguments:{id},_meta:modernMeta(contract.modern)
      }}
    });
    const modernBody=await readModern.text();
    expect(readModern.ok(),modernBody).toBeTruthy();
    expect(modernBody).toContain(reference);
    expect(modernBody).not.toContain('"isError":true');
    expect(modernBody).not.toContain("tenantId");

    // Legacy era: initialize handshake still works for 2025-11-25 clients.
    const initialized=await request.post(api+"/mcp",{
      headers:{...headers,"MCP-Protocol-Version":contract.legacy},
      data:{jsonrpc:"2.0",id:7004,method:"initialize",params:{
        protocolVersion:contract.legacy,capabilities:{},
        clientInfo:{name:"peritial-legacy-compat-e2e",version:"1.0.0"}
      }}
    });
    expect(initialized.ok(),await initialized.text()).toBeTruthy();
    expect(initialized.headers()["mcp-session-id"]).toBeUndefined();
    const legacy=decode(await initialized.text());
    expect(legacy.error).toBeUndefined();
    expect(legacy.result?.protocolVersion).toBe(contract.legacy);
    expect(legacy.result?.capabilities).toHaveProperty("tools");

    const legacyList=await request.post(api+"/mcp",{
      headers:{...headers,"MCP-Protocol-Version":contract.legacy},
      data:{jsonrpc:"2.0",id:7005,method:"tools/list",params:{}}
    });
    expect(legacyList.ok(),await legacyList.text()).toBeTruthy();
    const tools=decode(await legacyList.text()).result?.tools as {name:string}[];
    expect(tools).toHaveLength(25);
    expect(tools.map(t=>t.name).sort()).toEqual(
      modernTools.map(t=>t.name).sort());
  });

  test("unsupported versions and mismatched modern headers cannot execute writes",async ({request})=>{
    test.skip(!process.env.E2E_FULL_STACK,"Real Keycloak and MCP endpoint required");
    test.setTimeout(60_000);
    const writer=await token(request,"peritial-test-write");
    const reader=await token(request,"peritial-test-readonly");
    const nonce=Date.now()+"-"+Math.floor(Math.random()*1000000);
    const reference="MCP-NEGOTIATE-DENIED-"+nonce;

    async function rejected(versionHeader:string,versionMeta:string,
      methodHeader:string,id:number) {
      const response=await request.post(api+"/mcp",{
        headers:{...baseHeaders,Authorization:"Bearer "+writer,
          "MCP-Protocol-Version":versionHeader,
          "Mcp-Method":methodHeader,"Mcp-Name":"create_perizia"},
        data:{jsonrpc:"2.0",id,method:"tools/call",params:{
          name:"create_perizia",arguments:{reference},
          _meta:modernMeta(versionMeta)
        }}
      });
      const body=await response.text();
      expect(response.status(),body).toBe(400);
      expect(body).not.toContain(reference);
      expect(body).not.toContain("tenantId");
      return decode(body).error?.code;
    }

    const badVersion="1900-01-01";
    expect(await rejected(badVersion,badVersion,"tools/call",7011))
      .toBe(contract.errorCodes.unsupportedProtocolVersion);
    expect(await rejected(contract.modern,contract.legacy,"tools/call",7012))
      .toBe(contract.errorCodes.headerMismatch);

    // Neither HTTP rejection may reach the Perizia writer or its audit.
    const response=await request.get(api+"/api/perizie",{
      headers:{Authorization:"Bearer "+reader}
    });
    expect(response.status(),await response.text()).toBe(200);
    expect((await response.json() as {reference:string}[])
      .some(row=>row.reference===reference)).toBe(false);

    const anonymous=await request.post(api+"/mcp",{
      headers:{...baseHeaders,"MCP-Protocol-Version":contract.modern,
        "Mcp-Method":"server/discover"},
      data:{jsonrpc:"2.0",id:7013,method:"server/discover",
        params:{_meta:modernMeta(contract.modern)}}
    });
    expect(anonymous.status()).toBe(401);
    expect(await anonymous.text()).not.toContain("supportedVersions");
  });
});
