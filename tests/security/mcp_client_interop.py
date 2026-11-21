"""Independent MCP Streamable HTTP client interoperability probe.

Python standard library only: intentionally no Playwright, .NET SDK or
shared MCP parser. This is a LOCAL Keycloak/Compose CI fixture, not a
production OAuth client (password grant is never appropriate for PKCE apps).
No tokens, response bodies, references or tenant data are printed.
"""
import json
import sys
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode, urlparse
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[2]
CATALOG = json.loads((ROOT / "docs/mcp-catalog.v1.json").read_text())
STRUCTURED = json.loads((ROOT / "docs/mcp-structured-outputs.v1.json").read_text())
TYPES = json.loads((ROOT / "docs/mcp-output-dtos.v1.json").read_text())
ERAS = json.loads((ROOT / "docs/mcp-protocol-eras.v1.json").read_text())
MAX_HTTP_BODY = 2 * 1024 * 1024


def decode_rpc(body: bytes, request_id: int) -> dict:
    """Fail closed on missing, duplicate, malformed or wrong-ID replies."""
    if len(body) > MAX_HTTP_BODY:
        raise AssertionError("Oversized MCP response")
    text = body.decode("utf-8")
    if text.lstrip().startswith("{"):
        packets = [json.loads(text)]
    else:
        packets = []
        for chunk in text.replace("\r\n", "\n").split("\n\n"):
            data = [line[5:].strip() for line in chunk.split("\n")
                    if line.startswith("data:")]
            if data:
                packets.append(json.loads("\n".join(data)))
    matches = [p for p in packets if isinstance(p, dict)
               and p.get("id") == request_id
               and ("result" in p or "error" in p)]
    if len(matches) != 1:
        raise AssertionError("MCP reply missing, duplicated or ID mismatch")
    reply = matches[0]
    if reply.get("jsonrpc") != "2.0" or (
        ("result" in reply) == ("error" in reply)
    ):
        raise AssertionError("Malformed MCP JSON-RPC envelope")
    return reply


def metadata(version: str) -> dict:
    return {
        "io.modelcontextprotocol/protocolVersion": version,
        "io.modelcontextprotocol/clientInfo": {
            "name": "peritial-independent-python-ci", "version": "1.0.0"
        },
        "io.modelcontextprotocol/clientCapabilities": {},
    }


def reviewed_fields(schema: dict) -> set[str]:
    defs = schema.get("$defs", {})
    ref = schema.get("$ref")
    if isinstance(ref, str) and ref.startswith("#/$defs/"):
        schema = defs[ref[len("#/$defs/"):]]
    properties = schema.get("properties")
    if (schema.get("type") not in (None, "object")
            or not isinstance(properties, dict) or not properties):
        raise AssertionError("MCP outputSchema is not a reviewed nonempty object")
    return set(properties)


class Client:
    def __init__(self, api: str = "http://localhost:8080",
                 issuer: str = "http://localhost:8081/realms/peritial"):
        for base in (api, issuer):
            url = urlparse(base)
            if url.scheme != "http" or url.hostname not in (
                "localhost", "127.0.0.1"
            ) or url.username or url.password or url.query or url.fragment:
                raise ValueError("CI fixture refuses non-loopback hosts")
        self.api, self.issuer = api.rstrip("/"), issuer.rstrip("/")

    @staticmethod
    def request(url: str, method: str, body: bytes | None = None,
                headers: dict | None = None) -> tuple[int, dict, bytes]:
        request = Request(url, data=body, method=method, headers=headers or {})
        try:
            with urlopen(request, timeout=15) as response:
                payload = response.read(MAX_HTTP_BODY + 1)
                status, response_headers = response.status, dict(response.headers)
        except HTTPError as response:
            with response:
                payload = response.read(MAX_HTTP_BODY + 1)
                status, response_headers = response.code, dict(response.headers)
        if len(payload) > MAX_HTTP_BODY:
            raise AssertionError("Response byte limit exceeded")
        return status, {k.lower(): v for k, v in response_headers.items()}, payload

    def token(self, client: str) -> str:
        form = urlencode({
            "grant_type": "password", "client_id": client,
            "username": "localuser", "password": "local-only-user"
        }).encode("ascii")
        status, _, body = self.request(
            self.issuer + "/protocol/openid-connect/token", "POST", form,
            {"Content-Type": "application/x-www-form-urlencoded"})
        if status != 200:
            raise AssertionError("Local fixture token acquisition failed")
        return json.loads(body)["access_token"]

    def rest(self, token: str, route: str, method: str = "GET",
             payload: dict | None = None) -> tuple[int, dict]:
        status, headers, body = self.request(
            self.api + route, method,
            json.dumps(payload).encode("utf-8") if payload is not None else None,
            {"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        if headers.get("cache-control", "").find("no-store") == -1:
            raise AssertionError("Protected REST response must be no-store")
        return status, json.loads(body) if body else {}

    def rpc(self, token: str, version: str, method: str, args: dict,
            request_id: int, name: str | None = None,
            method_header: str | None = None) -> tuple[int, dict]:
        headers = {
            "Authorization": "Bearer " + token,
            "Accept": "application/json,text/event-stream",
            "Content-Type": "application/json",
            "MCP-Protocol-Version": version,
        }
        params = dict(args)
        if version == ERAS["modern"]:
            headers["Mcp-Method"] = method_header or method
            params["_meta"] = metadata(version)
        if name is not None:
            headers["Mcp-Name"] = name
        packet = {"jsonrpc": "2.0", "id": request_id, "method": method,
                  "params": params}
        status, response_headers, body = self.request(
            self.api + "/mcp", "POST",
            json.dumps(packet, separators=(",", ":")).encode("utf-8"), headers)
        if "no-store" not in response_headers.get("cache-control", ""):
            raise AssertionError("Protected MCP response must be no-store")
        if status == 400:
            # A transport-level rejection can legally have JSON-RPC id=null
            # because the server rejects the protocol header before it
            # dispatches the request body. Accept null only for this error
            # status, never for success or any tool-level result.
            error_reply = json.loads(body)
            if (not isinstance(error_reply, dict)
                    or error_reply.get("jsonrpc") != "2.0"
                    or error_reply.get("id") not in (None, request_id)
                    or not isinstance(error_reply.get("error"), dict)
                    or "result" in error_reply):
                raise AssertionError("Malformed HTTP 400 MCP transport error")
            return status, error_reply
        return status, decode_rpc(body, request_id)


def run_probe() -> None:
    client = Client()
    reader = client.token("peritial-test-readonly")
    writer = client.token("peritial-test-write")
    catalog_names = {entry["name"] for entry in CATALOG["tools"]}
    structured_names = set(STRUCTURED["tools"])
    assert len(catalog_names) == 25
    assert len(structured_names) == 19
    assert STRUCTURED["mcpCatalogVersion"] == CATALOG["version"]

    status, discovered = client.rpc(
        reader, ERAS["modern"], "server/discover", {}, 8101)
    assert status == 200 and "error" not in discovered
    assert ERAS["modern"] in discovered["result"]["supportedVersions"]

    status, listed = client.rpc(reader, ERAS["modern"], "tools/list", {}, 8102)
    assert status == 200 and "error" not in listed
    tools = listed["result"]["tools"]
    assert {item["name"] for item in tools} == catalog_names
    assert {t["name"] for t in tools if "outputSchema" in t} == structured_names
    for tool in tools:
        assert "tenantId" not in json.dumps(tool.get("outputSchema", {}))
        assert "storageKey" not in json.dumps(tool.get("outputSchema", {}))
    detail = next(t for t in tools if t["name"] == "get_perizia")
    expected = {s[0].lower() + s[1:] for s in TYPES["types"]["PeriziaDto"]}
    assert reviewed_fields(detail["outputSchema"]) == expected

    # This independent client creates a fixture only through signed REST
    # and reads it through modern and legacy MCP. Both surfaces must agree.
    reference = "MCP-PY-INTEROP-" + uuid.uuid4().hex
    status, created = client.rest(
        writer, "/api/perizie", "POST", {"reference": reference})
    assert status == 201 and created["reference"] == reference
    status, modern = client.rpc(
        reader, ERAS["modern"], "tools/call",
        {"name": "get_perizia", "arguments": {"id": created["id"]}},
        8103, name="get_perizia")
    assert status == 200 and "error" not in modern
    result = modern["result"]
    assert not result.get("isError")
    data = result["structuredContent"]
    assert set(data) <= expected and expected - {"assignedTo"} <= set(data)
    assert data["id"] == created["id"]
    assert data["reference"] == reference
    assert data["status"] == created["status"]
    assert data["createdAtUtc"] == created["createdAtUtc"]
    assert "tenantId" not in json.dumps(result)
    assert "storageKey" not in json.dumps(result)

    status, initialized = client.rpc(reader, ERAS["legacy"], "initialize", {
        "protocolVersion": ERAS["legacy"], "capabilities": {},
        "clientInfo": {"name": "peritial-independent-legacy-ci", "version": "1.0.0"},
    }, 8104)
    assert status == 200 and initialized["result"]["protocolVersion"] == ERAS["legacy"]
    status, legacy_list = client.rpc(
        reader, ERAS["legacy"], "tools/list", {}, 8105)
    assert status == 200
    assert {t["name"] for t in legacy_list["result"]["tools"]} == catalog_names
    status, legacy_read = client.rpc(reader, ERAS["legacy"], "tools/call",
                                     {"name": "get_perizia",
                                      "arguments": {"id": created["id"]}}, 8106)
    assert status == 200 and not legacy_read["result"].get("isError")
    assert reference in json.dumps(legacy_read["result"])

    # Read scope is NOT write permission. Also reject incompatible
    # protocol versions and header/method mismatches before side effects.
    blocked_ref = "MCP-PY-DENY-" + uuid.uuid4().hex
    call = {"name": "create_perizia", "arguments": {"reference": blocked_ref}}
    status, denied = client.rpc(reader, ERAS["modern"],
                                "tools/call", call, 8107, name="create_perizia")
    assert status == 200 and denied["result"].get("isError") is True
    status, unsupported = client.rpc(
        writer, "1900-01-01", "tools/call", call, 8108, name="create_perizia")
    assert status == 400
    assert unsupported["error"]["code"] == ERAS["errorCodes"]["unsupportedProtocolVersion"]
    status, mismatched = client.rpc(
        writer, ERAS["modern"], "tools/call", call, 8109,
        name="create_perizia", method_header="tools/list")
    assert status == 400
    assert mismatched["error"]["code"] == ERAS["errorCodes"]["headerMismatch"]
    status, rows = client.rest(reader, "/api/perizie")
    assert status == 200 and not any(p["reference"] == blocked_ref for p in rows)


if __name__ == "__main__":
    try:
        run_probe()
    except Exception as error:
        # No credentials/URLs/payloads are printed on failure.
        print("Independent MCP CI probe failed:", type(error).__name__,
              file=sys.stderr)
        sys.exit(1)
    print("Independent Python MCP client: modern and legacy protocol,"
          " output schema and signed OAuth authorizations verified.")
