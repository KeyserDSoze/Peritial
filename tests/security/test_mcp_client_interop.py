"""Offline unit tests for the independent stdlib-only MCP client."""
import json
import unittest

from mcp_client_interop import Client, decode_rpc, reviewed_fields, MAX_HTTP_BODY


class IndependentMcpClientTests(unittest.TestCase):
    def test_jsonrpc_json_and_multievent_sse(self):
        message = {"jsonrpc": "2.0", "id": 7, "result": {"tools": []}}
        direct = json.dumps(message).encode()
        self.assertEqual(message, decode_rpc(direct, 7))
        events = (
            'event: message\n'
            'data: {"jsonrpc":"2.0","method":"notifications/progress"}\n\n'
            'event: message\n'
            'data: ' + json.dumps(message) + '\n\n'
        ).encode()
        self.assertEqual(message, decode_rpc(events, 7))
        self.assertEqual(message, decode_rpc(events.replace(b"\n", b"\r\n"), 7))

    def test_jsonrpc_denial_error_is_a_reply_not_silent_success(self):
        response = {"jsonrpc": "2.0", "id": 8,
                    "error": {"code": -32022, "message": "Unsupported"}}
        self.assertEqual(response, decode_rpc(json.dumps(response).encode(), 8))

    def test_fail_closed_duplicate_mismatch_missing_and_malformed(self):
        valid = json.dumps({"jsonrpc": "2.0", "id": 9, "result": {}})
        cases = [
            (valid.encode(), 10),
            (b"event: ping\n\n", 9),
            (("data: " + valid + "\n\n" +
              "data: " + valid + "\n\n").encode(), 9),
            (b'{"jsonrpc":"2.0","id":9,"result":{},"error":{}}', 9),
            (b'{"jsonrpc":"1.0","id":9,"result":{}}', 9),
            (b'{"jsonrpc":"2.0","id":9,"unexpected":{}}', 9),
            (b'{"jsonrpc":"2.0","id":9,"result":', 9),
            (b"x" * (MAX_HTTP_BODY + 1), 9),
        ]
        for body, request_id in cases:
            with self.subTest(body=body[:45]), self.assertRaises(
                (AssertionError, ValueError, json.JSONDecodeError)
            ):
                decode_rpc(body, request_id)

    def test_no_external_password_grant_or_sensitive_endpoint(self):
        for base in ["https://api.example.org",
                     "http://api.example.org",
                     "http://localhost.evil.example",
                     "http://root:password@localhost:8080",
                     "http://localhost:8080/?token=secret"]:
            with self.subTest(base=base), self.assertRaises(ValueError):
                Client(api=base)
        self.assertIsInstance(Client(), Client)

    def test_schema_root_and_defs_have_only_declared_properties(self):
        schema = {"$ref": "#/$defs/Safe",
                  "$defs": {"Safe": {"type": "object",
                                     "properties": {"id": {"type": "string"}}}}}
        self.assertEqual({"id"}, reviewed_fields(schema))
        self.assertEqual({"id"}, reviewed_fields({"properties": {"id": {}}}))
        with self.assertRaises(AssertionError):
            reviewed_fields({"type": "array", "items": {}})


if __name__ == "__main__":
    unittest.main()
