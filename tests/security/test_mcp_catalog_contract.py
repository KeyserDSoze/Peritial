import copy
import json
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "ci"))
from mcp_catalog_contract import (  # noqa: E402
    CATALOG, SOURCE, parse_mcp_tool_signatures, validate_catalog
)


class McpCatalogContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_versioned_manifest_must_match_mcp_server_signatures(self):
        self.assertTrue(CATALOG.is_file(), "Versioned MCP catalog is missing")
        validate_catalog(json.loads(CATALOG.read_text(encoding="utf-8")), self.source)

    def test_duplicate_server_tool_names_fail_closed(self):
        source = self.source + '''
    [McpServerTool(Name = "list_perizie"), Description("duplicate")]
    public Task<object> Duplicate() => throw new NotImplementedException();
    '''
        with self.assertRaisesRegex(ValueError, "Duplicate MCP tool"):
            parse_mcp_tool_signatures(source)

    def test_unrecognized_tool_signature_fails_closed(self):
        source = self.source + '''
    [McpServerTool(Name = "new_tool"), Description("unknown")]
    public object NewTool
    {
      get { return null!; }
    }
    '''
        with self.assertRaisesRegex(ValueError, "Unrecognized MCP tool"):
            parse_mcp_tool_signatures(source)

    def test_renamed_tool_requires_explicit_catalog_revision(self):
        manifest = self._manifest()
        altered = copy.deepcopy(manifest)
        altered["tools"][0]["name"] = "renamed_tool"
        with self.assertRaisesRegex(ValueError, "MCP name drift"):
            validate_catalog(altered, self.source)

    def test_removed_required_argument_requires_explicit_catalog_revision(self):
        manifest = self._manifest()
        altered = copy.deepcopy(manifest)
        target = next(i for i in altered["tools"] if i["name"] == "update_sinistro")
        target["required"].remove("expectedVersion")
        with self.assertRaisesRegex(ValueError, "required-argument drift"):
            validate_catalog(altered, self.source)

    def test_dangerous_tool_cannot_be_mislabeled_read_only(self):
        manifest = self._manifest()
        altered = copy.deepcopy(manifest)
        target = next(i for i in altered["tools"] if i["name"] == "delete_sinistro")
        target["access"] = "read"
        target["effect"] = "read-only"
        with self.assertRaisesRegex(ValueError, "scope/effect"):
            validate_catalog(altered, self.source)

    def test_version_and_duplicate_manifest_entries_fail_closed(self):
        manifest = self._manifest()
        altered = copy.deepcopy(manifest)
        altered["version"] = "1.0.1"
        with self.assertRaisesRegex(ValueError, "version"):
            validate_catalog(altered, self.source)
        altered = copy.deepcopy(manifest)
        altered["tools"].append(altered["tools"][-1])
        with self.assertRaisesRegex(ValueError, "duplicated"):
            validate_catalog(altered, self.source)

    @staticmethod
    def _manifest():
        return json.loads(CATALOG.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
