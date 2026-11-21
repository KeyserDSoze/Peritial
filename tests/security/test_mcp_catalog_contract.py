import copy
import json
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "ci"))
from mcp_catalog_contract import (  # noqa: E402
    ANNOTATIONS, CATALOG, SOURCE, OUTPUT_DTOS, STRUCTURED, parse_mcp_returns,
    parse_mcp_tool_signatures, parse_mcp_annotations,
    validate_catalog, validate_output_dtos, validate_annotation_manifest,
    validate_structured_output_tools
)


class McpCatalogContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_mcp_tool_annotations_are_versioned_and_match_source(self):
        catalog = self._manifest()
        declared = json.loads(ANNOTATIONS.read_text(encoding="utf-8"))
        validate_annotation_manifest(catalog, declared, self.source)
        self.assertEqual(25, len(parse_mcp_annotations(self.source)))
        self.assertTrue(parse_mcp_annotations(self.source)["get_perizia"]["readOnlyHint"])
        self.assertTrue(parse_mcp_annotations(self.source)["plan_legacy_xml"]["readOnlyHint"])
        self.assertFalse(parse_mcp_annotations(self.source)["create_perizia"]["destructiveHint"])
        self.assertTrue(parse_mcp_annotations(self.source)["delete_sinistro"]["destructiveHint"])

    def test_mcp_annotation_changes_cannot_be_silent(self):
        catalog = self._manifest()
        declared = json.loads(ANNOTATIONS.read_text(encoding="utf-8"))
        for before, after in [
            ('Name = "get_perizia", ReadOnly = true',
             'Name = "get_perizia", ReadOnly = false'),
            ('Name = "delete_sinistro", ReadOnly = false, Destructive = true',
             'Name = "delete_sinistro", ReadOnly = false, Destructive = false'),
            ('Name = "ingest_file", ReadOnly = false, Destructive = false, OpenWorld = false',
             'Name = "ingest_file", ReadOnly = false, Destructive = false, OpenWorld = true'),
            ('Name = "get_sinistro", ReadOnly = true, Destructive = false, OpenWorld = false',
             'Name = "get_sinistro"'),
        ]:
            with self.subTest(before=before):
                changed = self.source.replace(before, after, 1)
                self.assertNotEqual(changed, self.source)
                with self.assertRaisesRegex(ValueError, "annotation drift"):
                    validate_annotation_manifest(catalog, declared, changed)
        changed = copy.deepcopy(declared)
        changed["tools"]["delete_sinistro"]["destructiveHint"] = False
        with self.assertRaisesRegex(ValueError, "annotation drift"):
            validate_annotation_manifest(catalog, changed, self.source)

    def test_structured_outputs_are_reviewed_and_fail_closed(self):
        manifest = json.loads(STRUCTURED.read_text(encoding="utf-8"))
        self.assertEqual(["get_document", "get_perizia", "get_perizia_sinistro",
                          "get_sinistro"], manifest["tools"])
        validate_structured_output_tools(self._manifest(), manifest, self.source)
        validate_catalog(self._manifest(), self.source)
        missing = self.source.replace(
            'Name = "get_perizia", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true',
            'Name = "get_perizia", ReadOnly = true, Destructive = false, OpenWorld = false', 1)
        self.assertNotEqual(missing, self.source)
        with self.assertRaisesRegex(ValueError, "structured output drift"):
            validate_structured_output_tools(self._manifest(), manifest, missing)
        new_writer = self.source.replace(
            'Name = "create_perizia", ReadOnly = false, Destructive = false, OpenWorld = false',
            'Name = "create_perizia", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true', 1)
        self.assertNotEqual(new_writer, self.source)
        with self.assertRaisesRegex(ValueError, "structured output drift"):
            validate_structured_output_tools(self._manifest(), manifest, new_writer)
        added_without_annotation = copy.deepcopy(manifest)
        added_without_annotation["tools"].append("list_documents")
        added_without_annotation["tools"].sort()
        with self.assertRaisesRegex(ValueError, "structured output drift"):
            validate_structured_output_tools(
                self._manifest(), added_without_annotation, self.source)
        changed = copy.deepcopy(manifest)
        changed["tools"].append("delete_sinistro")
        changed["tools"].sort()
        with self.assertRaisesRegex(ValueError, "requires reviewed read DTOs"):
            validate_structured_output_tools(self._manifest(), changed, self.source)

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
        # Keep sorting valid so the test probes the *name* invariant,
        # not the independent catalog-order invariant.
        altered["tools"].sort(key=lambda item: item["name"])
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

    def test_manifest_includes_every_optional_argument_and_its_wire_type(self):
        manifest = self._manifest()
        self.assertEqual("1.2.0", manifest["version"])
        for item in manifest["tools"]:
            self.assertIn("parameters", item)
            self.assertIsInstance(item["parameters"], dict)
        create = next(i for i in manifest["tools"] if i["name"] == "create_sinistro")
        self.assertEqual(
            {"type": "date", "optional": False, "nullable": False, "defaultLiteral": None},
            create["parameters"]["occurredOn"],
        )
        self.assertEqual(
            {"type": "date", "optional": True, "nullable": True, "defaultLiteral": "null"},
            create["parameters"]["reportedOn"],
        )
        page = next(i for i in manifest["tools"] if i["name"] == "page_sinistri")
        self.assertEqual(
            {"type": "string", "optional": True, "nullable": True, "defaultLiteral": "null"},
            page["parameters"]["cursor"],
        )
        self.assertEqual(
            {"type": "integer", "optional": True, "nullable": False, "defaultLiteral": "50"},
            page["parameters"]["limit"],
        )
        self.assertNotIn("ct", page["parameters"])

    def test_optional_rename_is_rejected_even_when_required_unchanged(self):
        manifest = self._manifest()
        changed = copy.deepcopy(manifest)
        page = next(i for i in changed["tools"] if i["name"] == "page_sinistri")
        page["parameters"]["newCursor"] = page["parameters"].pop("cursor")
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(changed, self.source)

    def test_required_and_optional_types_are_immutable_without_version_review(self):
        manifest = self._manifest()
        for tool, parameter, type_name in [
            ("update_sinistro", "expectedVersion", "string"),
            ("search_sinistri", "occurredFrom", "integer"),
            ("create_sinistro", "reportedOn", "string"),
        ]:
            changed = copy.deepcopy(manifest)
            row = next(i for i in changed["tools"] if i["name"] == tool)
            row["parameters"][parameter]["type"] = type_name
            with self.subTest(tool=tool, parameter=parameter):
                with self.assertRaisesRegex(ValueError, "parameter"):
                    validate_catalog(changed, self.source)

    def test_nullable_and_optional_contract_cannot_be_misclassified(self):
        manifest = self._manifest()
        changed = copy.deepcopy(manifest)
        row = next(i for i in changed["tools"] if i["name"] == "search_sinistri")
        row["parameters"]["occurredFrom"]["nullable"] = False
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(changed, self.source)
        changed = copy.deepcopy(manifest)
        row = next(i for i in changed["tools"] if i["name"] == "list_sinistri")
        row["parameters"]["limit"]["optional"] = False
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(changed, self.source)

    def test_server_optional_signature_drift_is_detected(self):
        manifest = self._manifest()
        changed = self.source.replace(
            "string? cursor = null, int limit = 50, CancellationToken ct = default",
            "Guid? cursor = null, int limit = 50, CancellationToken ct = default",
            1,
        )
        self.assertNotEqual(changed, self.source)
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(manifest, changed)

    def test_default_literal_is_explicit_and_distinguishes_null_from_missing(self):
        manifest = self._manifest()
        page = next(item for item in manifest["tools"]
                    if item["name"] == "page_sinistri")
        self.assertEqual("50", page["parameters"]["limit"]["defaultLiteral"])
        self.assertEqual("null", page["parameters"]["cursor"]["defaultLiteral"])
        update = next(item for item in manifest["tools"]
                      if item["name"] == "update_sinistro")
        self.assertIsNone(update["parameters"]["expectedVersion"]["defaultLiteral"])

    def test_changing_optional_default_without_changing_required_is_blocked(self):
        manifest = self._manifest()
        changed = copy.deepcopy(manifest)
        page = next(item for item in changed["tools"]
                    if item["name"] == "page_sinistri")
        page["parameters"]["limit"]["defaultLiteral"] = "100"
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(changed, self.source)

        # Changing the C# numeric default must trigger the same gate, even
        # though all parameter names/types/required flags remain identical.
        changed_source = self.source.replace(
            "string? cursor = null, int limit = 50, CancellationToken ct = default",
            "string? cursor = null, int limit = 100, CancellationToken ct = default",
            1,
        )
        self.assertNotEqual(changed_source, self.source)
        with self.assertRaisesRegex(ValueError, "parameter"):
            validate_catalog(manifest, changed_source)


    def test_every_csharp_result_signature_is_pinned(self):
        manifest = self._manifest()
        actual = parse_mcp_returns(self.source)
        self.assertEqual(25, len(actual))
        self.assertEqual({item["name"] for item in manifest["tools"]}, set(actual))
        for item in manifest["tools"]:
            self.assertEqual(actual[item["name"]], item["returnType"])
        self.assertEqual("Task<IReadOnlyList<PeriziaDto>>", actual["list_perizie"])
        self.assertEqual("Task<DocumentPageDto>", actual["page_documents"])
        self.assertEqual("Task<bool>", actual["delete_sinistro"])
        self.assertEqual("LegacyXmlPreviewDto", actual["preview_legacy_xml"])

    def test_source_return_type_change_rejected_with_same_arguments(self):
        changed = self.source.replace(
            "public Task<SinistroDto> GetSinistro(",
            "public Task<PeriziaDto> GetSinistro(", 1)
        self.assertNotEqual(changed, self.source)
        with self.assertRaisesRegex(ValueError, "return-type drift"):
            validate_catalog(self._manifest(), changed)

    def test_nested_collection_return_change_fails_closed(self):
        changed = self.source.replace(
            "public Task<IReadOnlyList<PeriziaDto>> ListPerizie(",
            "public Task<IReadOnlyList<Guid>> ListPerizie(", 1)
        self.assertNotEqual(changed, self.source)
        with self.assertRaisesRegex(ValueError, "return-type drift"):
            validate_catalog(self._manifest(), changed)

    def test_manifest_return_tampering_and_missing_field_fail_closed(self):
        for replacement in ("Task<PeriziaDto>", "", None):
            changed = copy.deepcopy(self._manifest())
            item = next(i for i in changed["tools"] if i["name"] == "get_document")
            item["returnType"] = replacement
            with self.subTest(replacement=replacement):
                with self.assertRaisesRegex(ValueError, "return-type drift"):
                    validate_catalog(changed, self.source)
        changed = copy.deepcopy(self._manifest())
        changed["tools"][0].pop("returnType")
        with self.assertRaisesRegex(ValueError, "entry shape"):
            validate_catalog(changed, self.source)


    def test_output_dto_manifest_reaches_all_public_mcp_results(self):
        catalog = self._manifest()
        outputs = json.loads(OUTPUT_DTOS.read_text(encoding="utf-8"))
        self.assertEqual("1.2.0", outputs["mcpCatalogVersion"])
        self.assertEqual(10, len(outputs["types"]))
        validate_output_dtos(catalog, outputs)

    def test_missing_nested_or_direct_output_dto_is_rejected(self):
        catalog = self._manifest()
        outputs = json.loads(OUTPUT_DTOS.read_text(encoding="utf-8"))
        for missing in ("DocumentSummaryDto", "SinistroPageDto", "LegacyXmlPreviewDto"):
            changed = copy.deepcopy(outputs)
            del changed["types"][missing]
            with self.subTest(missing=missing):
                with self.assertRaisesRegex(ValueError, "Missing reachable"):
                    validate_output_dtos(catalog, changed)

    def test_unreachable_or_unrecognized_output_dto_is_rejected(self):
        catalog = self._manifest()
        outputs = json.loads(OUTPUT_DTOS.read_text(encoding="utf-8"))
        added = copy.deepcopy(outputs)
        added["types"]["ExposedSecretDto"] = {"TenantId": "Guid"}
        with self.assertRaisesRegex(ValueError, "Unreachable/unreviewed"):
            validate_output_dtos(catalog, added)
        added = copy.deepcopy(outputs)
        added["types"]["PeriziaDto"]["Internal"] = "Dictionary<string, string>"
        with self.assertRaisesRegex(ValueError, "Invalid MCP output DTO field"):
            validate_output_dtos(catalog, added)

    def test_output_manifest_version_must_match_input_catalog(self):
        outputs = json.loads(OUTPUT_DTOS.read_text(encoding="utf-8"))
        changed = copy.deepcopy(outputs)
        changed["mcpCatalogVersion"] = "1.0.0"
        with self.assertRaisesRegex(ValueError, "version"):
            validate_output_dtos(self._manifest(), changed)

    @staticmethod
    def _manifest():
        return json.loads(CATALOG.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
