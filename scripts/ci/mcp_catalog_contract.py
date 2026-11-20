"""Static MCP v1 contract gate: reject unreviewed tool/parameter drift.

This is a SOURCE-code contract check, not a substitute for the live MCP
tools/list Keycloak E2E gate. It deliberately does not execute application
services or inspect tenant data.
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
_NAME_RE = re.compile(r"^[a-z][a-z0-9_]*$")
_READ = {
    "list_perizie", "get_perizia", "get_perizia_sinistro",
    "list_perizie_for_sinistro", "list_sinistri", "page_sinistri",
    "search_sinistri", "search_sinistri_page", "get_sinistro",
    "list_documents", "page_documents", "get_document",
}
_NONMUTATING_WRITE = {"preview_legacy_xml", "plan_legacy_xml"}
_PRIVILEGED = {"delete_sinistro"}


def parse_mcp_tool_signatures(source: str) -> dict[str, list[str]]:
    result = {}
    matches = list(_TOOL_RE.finditer(source))
    found_names = re.findall(r'\[McpServerTool\(Name = "([^"]+)"\)', source)
    if len(matches) != len(found_names):
        raise ValueError("Unrecognized MCP tool signature: fail closed on parser drift")
    for match in matches:
        name, args = match.groups()
        if name in result:
            raise ValueError("Duplicate MCP tool name: " + name)
        required = []
        for arg in args.split(","):
            arg = arg.strip()
            if not arg or arg.startswith("CancellationToken "):
                continue
            if "=" not in arg:
                required.append(arg.rsplit(" ", 1)[-1])
        result[name] = required
    return result


def validate_catalog(manifest: dict, source: str) -> None:
    if manifest.get("format") != 1 or manifest.get("version") != "1.0.0":
        raise ValueError("Unreviewed MCP catalog version or format")
    items = manifest.get("tools")
    if not isinstance(items, list) or not items:
        raise ValueError("Empty or missing MCP catalog")
    names = [i["name"] for i in items]
    if any(not _NAME_RE.fullmatch(n) for n in names) or len(set(names)) != len(names):
        raise ValueError("Invalid or duplicated MCP tool names")
    if names != sorted(names):
        raise ValueError("Keep the MCP tool catalog sorted by name")

    actual = parse_mcp_tool_signatures(source)
    if set(actual) != set(names):
        raise ValueError(
            f"MCP name drift: missing={sorted(set(actual) - set(names))}, "
            f"unexpected={sorted(set(names) - set(actual))}"
        )

    for item in items:
        name = item["name"]
        if set(item) != {"name", "required", "access", "effect"}:
            raise ValueError("Unreviewed MCP catalog entry shape: " + name)
        if item["required"] != actual[name]:
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
