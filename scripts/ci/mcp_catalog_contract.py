"""Fail-closed MCP v1 source-to-manifest compatibility policy.

The manifest is declarative; JWT and tenant authorization remain in Business.
The live Keycloak tools/list E2E separately verifies the SDK's JSON schema.
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/backend/Applications/McpTools.cs"
CATALOG = ROOT / "docs/mcp-catalog.v1.json"

_TOOL_RE = re.compile(
    r'\[McpServerTool\(Name = "([^"]+)"\)[^\n]*\]\s*'
    r'public\s+(?:async\s+)?[^\n(]+\((.*?)\)\s*(?:=>|\{)',
    re.DOTALL,
)
_ARGUMENT_RE = re.compile(
    r'^(string|Guid|DateOnly|int|long)(\?)?\s+([A-Za-z]\w*)'
    r'(?:\s*=\s*(.+))?$'
)
_KIND = {"string": "string", "Guid": "uuid",
         "DateOnly": "date", "int": "integer", "long": "integer"}
_NAME_RE = re.compile(r"^[a-z][a-z0-9_]*$")
_READ = {
    "list_perizie", "get_perizia", "get_perizia_sinistro",
    "list_perizie_for_sinistro", "list_sinistri", "page_sinistri",
    "search_sinistri", "search_sinistri_page", "get_sinistro",
    "list_documents", "page_documents", "get_document",
}
_NONMUTATING_WRITE = {"preview_legacy_xml", "plan_legacy_xml"}
_PRIVILEGED = {"delete_sinistro"}


def parse_mcp_parameters(source: str) -> dict[str, dict[str, dict]]:
    """Return each tool's ordered SDK-visible signature; fail on new shapes."""
    result = {}
    matches = list(_TOOL_RE.finditer(source))
    declared = re.findall(r'\[McpServerTool\(Name = "([^"]+)"\)', source)
    if len(matches) != len(declared):
        raise ValueError("Unrecognized MCP tool signature: fail closed on parser drift")
    for match in matches:
        name, arguments = match.groups()
        if name in result:
            raise ValueError("Duplicate MCP tool name: " + name)
        parameters = {}
        for arg in arguments.split(","):
            arg = arg.strip()
            if not arg:
                continue
            if arg == "CancellationToken ct = default":
                continue
            parsed = _ARGUMENT_RE.fullmatch(arg)
            if not parsed:
                raise ValueError(f"Unrecognized MCP parameter signature: {name}")
            cs_type, nullable, param_name, default = parsed.groups()
            if param_name in parameters:
                raise ValueError(f"Duplicated MCP parameter: {name}")
            parameters[param_name] = {
                "type": _KIND[cs_type],
                "optional": default is not None,
                "nullable": nullable is not None,
                # Preserve the exact literal: absent != explicit 'null', and
                # changing a numeric/default value is an MCP behavior change.
                "defaultLiteral": default,
            }
        result[name] = parameters
    return result


def parse_mcp_tool_signatures(source: str) -> dict[str, list[str]]:
    return {
        name: [key for key, contract in parameters.items()
               if not contract["optional"]]
        for name, parameters in parse_mcp_parameters(source).items()
    }


def validate_catalog(manifest: dict, source: str) -> None:
    if manifest.get("format") != 1 or manifest.get("version") != "1.1.0":
        raise ValueError("Unreviewed MCP catalog version or format")
    items = manifest.get("tools")
    if not isinstance(items, list) or not items:
        raise ValueError("Empty or missing MCP catalog")
    if not all(isinstance(item, dict) and isinstance(item.get("name"), str)
               for item in items):
        raise ValueError("Malformed MCP catalog entry")
    names = [item["name"] for item in items]
    if any(not _NAME_RE.fullmatch(name) for name in names) or (
        len(set(names)) != len(names)
    ):
        raise ValueError("Invalid or duplicated MCP tool names")
    if names != sorted(names):
        raise ValueError("Keep the MCP tool catalog sorted by name")

    actual = parse_mcp_parameters(source)
    if set(actual) != set(names):
        raise ValueError(
            f"MCP name drift: missing={sorted(set(actual) - set(names))}, "
            f"unexpected={sorted(set(names) - set(actual))}"
        )

    for item in items:
        name = item["name"]
        if set(item) != {"name", "required", "parameters", "access", "effect"}:
            raise ValueError("Unreviewed MCP catalog entry shape: " + name)
        if not isinstance(item["parameters"], dict):
            raise ValueError("Malformed MCP parameter contract: " + name)
        if item["parameters"] != actual[name] or (
            list(item["parameters"]) != list(actual[name])
        ):
            raise ValueError(f"MCP parameter contract drift: {name}")
        expected_required = [
            key for key, value in actual[name].items() if not value["optional"]
        ]
        if item["required"] != expected_required:
            raise ValueError(f"MCP required-argument drift: {name}")
        access = (
            "read" if name in _READ else
            "write+delete" if name in _PRIVILEGED else "write"
        )
        effect = "read-only" if name in _READ | _NONMUTATING_WRITE else "mutating"
        if item["access"] != access or item["effect"] != effect:
            raise ValueError(f"MCP scope/effect classification drift: {name}")


def validate_repository() -> None:
    validate_catalog(
        json.loads(CATALOG.read_text(encoding="utf-8")),
        SOURCE.read_text(encoding="utf-8"),
    )


if __name__ == "__main__":
    validate_repository()
