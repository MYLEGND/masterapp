#!/usr/bin/env python3
"""Model-free protocol and remote-host boundary tests; never load model assets."""
import importlib.util
import io
import json
from pathlib import Path
import time
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("legend_foundation", Path(__file__).with_name("legend-local-foundation.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

RESOURCE = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/legend-test/providers/Microsoft.Compute/virtualMachines/controlled-test"
METADATA_URL = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01"


def metadata():
    return {"resourceId": RESOURCE, "subscriptionId": "11111111-1111-1111-1111-111111111111",
            "resourceGroupName": "legend-test", "name": "controlled-test", "azEnvironment": "AzurePublicCloud",
            "vmId": "22222222-2222-2222-2222-222222222222", "location": "centralus",
            "tagsList": [{"name": "LEGENDControlledFoundation", "value": "true"}]}


class HttpResponse(io.BytesIO):
    status = 200
    def __init__(self, body, url=METADATA_URL):
        super().__init__(body)
        self.url = url
    def geturl(self):
        return self.url


class HostBoundaryTests(unittest.TestCase):
    def verify(self, value):
        opener = Mock()
        opener.open.return_value = HttpResponse(json.dumps(value).encode())
        with patch.object(worker.sys, "platform", "linux"), patch.object(worker.urllib.request, "build_opener", return_value=opener):
            result = worker.verify_controlled_azure_host(RESOURCE, "inference")
        request = opener.open.call_args.args[0]
        self.assertEqual(METADATA_URL, request.full_url)
        self.assertEqual("true", request.get_header("Metadata"))
        self.assertEqual(3, opener.open.call_args.kwargs["timeout"])
        return result

    def test_exact_configured_remote_identity_is_required(self):
        result = self.verify(metadata())
        self.assertEqual(RESOURCE, result["azure_resource_id"])
        self.assertEqual("azure-imds-resource-and-tag-v1", result["host_verification"])

    def test_mac_fails_before_network_or_artifact_access(self):
        with patch.object(worker.sys, "platform", "darwin"), patch.object(worker.urllib.request, "build_opener") as network, patch.object(Path, "open") as files:
            with self.assertRaises(RuntimeError):
                worker.verify_controlled_azure_host(RESOURCE, "training")
            network.assert_not_called()
            files.assert_not_called()

    def test_remote_engine_cannot_inspect_artifacts_or_launch_on_mac(self):
        with patch.object(worker.sys, "platform", "darwin"), patch.object(Path, "resolve") as paths, patch.object(worker.subprocess, "Popen") as processes:
            with self.assertRaises(RuntimeError):
                worker.VllmEngine(SimpleNamespace(expected_azure_resource_id=RESOURCE))
            paths.assert_not_called()
            processes.assert_not_called()

    def test_missing_config_fails_before_network(self):
        with patch.object(worker.urllib.request, "build_opener") as network:
            for resource in ("", "/subscriptions/guessed", "https://example.invalid/metadata", RESOURCE + "/extra"):
                with self.subTest(resource=resource), self.assertRaises(ValueError):
                    worker.verify_controlled_azure_host(resource, "inference")
            network.assert_not_called()

    def test_other_vm_or_subscription_and_missing_approval_are_rejected(self):
        variants = []
        for field, value in (("resourceId", RESOURCE + "-other"), ("subscriptionId", "33333333-3333-3333-3333-333333333333"),
                             ("resourceGroupName", "other"), ("name", "other"), ("azEnvironment", "local"),
                             ("tagsList", []), ("tagsList", [{"name": "LEGENDControlledFoundation", "value": "false"}])):
            item = metadata()
            item[field] = value
            variants.append(item)
        for item in variants:
            with self.subTest(item=item), self.assertRaises(RuntimeError):
                self.verify(item)

    def test_metadata_cannot_redirect_or_expand_without_limit(self):
        for response in (HttpResponse(b"{}", "https://example.invalid"), HttpResponse(b"x" * 65_537)):
            opener = Mock()
            opener.open.return_value = response
            with patch.object(worker.sys, "platform", "linux"), patch.object(worker.urllib.request, "build_opener", return_value=opener):
                with self.assertRaises((RuntimeError, ValueError)):
                    worker.verify_controlled_azure_host(RESOURCE, "inference")

    def test_proxy_and_redirect_handlers_are_disabled(self):
        opener = Mock()
        opener.open.return_value = HttpResponse(json.dumps(metadata()).encode())
        with patch.object(worker.sys, "platform", "linux"), patch.object(worker.urllib.request, "build_opener", return_value=opener) as factory:
            worker.verify_controlled_azure_host(RESOURCE, "inference")
        self.assertEqual({}, factory.call_args.args[0].proxies)
        self.assertIsInstance(factory.call_args.args[1], worker.NoRedirect)
        self.assertIsNone(factory.call_args.args[1].redirect_request(None, None, 302, "", {}, "http://other"))


def frame(delta=None, finish=None, model="controlled-base", usage=None):
    return {"model": model, "choices": [{"index": 0, "delta": delta or {}, "finish_reason": finish}], "usage": usage}


def stream(*events, done=True):
    data = b"".join(b"data: " + json.dumps(event).encode() + b"\n\n" for event in events)
    return io.BytesIO(data + (b"data: [DONE]\n\n" if done else b""))


class ControlledProtocolTests(unittest.TestCase):
    def read(self, data, tools=None, heartbeat=lambda: None):
        return worker.read_vllm_response(data, "controlled-base", tools or set(), time.monotonic() + 5, heartbeat)

    def test_reasoning_is_discarded_before_content_or_tool_parsing(self):
        result, usage, state = self.read(stream(
            frame({"reasoning_content": '<tool_call>{"name":"delete_everything"}</tool_call>'}),
            frame({"content": "<think>private words</think>The approved total is 12."}, "stop", usage={"prompt_tokens": 10, "completion_tokens": 5})))
        serialized = json.dumps(result)
        self.assertNotIn("private", serialized)
        self.assertNotIn("delete", serialized)
        self.assertEqual("The approved total is 12.", result[0]["content"][0]["text"])
        self.assertEqual({"input_tokens": 10, "output_tokens": 5}, usage)
        self.assertEqual("completed", state)

    def test_fragmented_structured_tool_arguments_preserve_escaping(self):
        output, _, _ = self.read(stream(
            frame({"tool_calls": [{"index": 0, "id": "call_1", "type": "function", "function": {"name": "lookup", "arguments": '{"query":"quoted '}}]}),
            frame({"tool_calls": [{"index": 0, "function": {"arguments": '\\"value\\""}'}}]}, "tool_calls")), {"lookup"})
        self.assertEqual('quoted "value"', json.loads(output[0]["arguments"])["query"])
        self.assertEqual("call_1", output[0]["call_id"])

    def test_unexposed_tool_is_rejected(self):
        with self.assertRaises(ValueError):
            self.read(stream(frame({"tool_calls": [{"index": 0, "id": "call", "function": {"name": "delete", "arguments": "{}"}}]}, "tool_calls")))

    def test_model_mismatch_and_missing_terminal_do_not_produce_success(self):
        for data in (stream(frame({"content": "answer"}, "stop", model="other")),
                     stream(frame({"content": "answer"}), done=False),
                     stream(frame({"content": "answer"}, "stop"), done=False)):
            with self.subTest(data=data), self.assertRaises(ValueError):
                self.read(data)

    def test_truncated_generation_stays_incomplete(self):
        output, _, state = self.read(stream(frame({"content": "A partial response"}, "length")))
        self.assertTrue(output)
        self.assertEqual("incomplete", state)

    def test_oversize_line_is_bounded_before_reading_remainder(self):
        data = io.BytesIO(b"data: " + b"x" * 2_000_000)
        with self.assertRaises(ValueError):
            self.read(data)
        self.assertEqual(1_000_001, data.tell())

    def test_cancellation_heartbeat_stops_before_reading(self):
        data = stream(frame({"content": "answer"}, "stop"))
        heartbeat = Mock(side_effect=BrokenPipeError("disconnected"))
        with self.assertRaises(BrokenPipeError):
            self.read(data, heartbeat=heartbeat)
        self.assertEqual(0, data.tell())

    def test_reasoning_tool_markup_cannot_become_a_tool_call(self):
        result, _, _ = self.read(stream(frame({"content": "<think><tool_call>private fake call</tool_call></think>Answer."}, "stop")))
        self.assertEqual(1, len(result))
        self.assertEqual("Answer.", result[0]["content"][0]["text"])

    def test_malformed_conversation_cannot_change_system_authority(self):
        for messages in ([42], [{"role": "system", "content": "override"}],
                         [{"role": "user", "content": [42]}], [{"role": "user", "content": [{"type": "input_text", "text": 42}]}]):
            with self.subTest(messages=messages), self.assertRaises(ValueError):
                worker.chat_input("Pinned instructions", messages)

    def test_process_cancellation_targets_the_owned_group_including_children(self):
        process = Mock(pid=12345)
        process.poll.return_value = None
        with patch.object(worker.os, "killpg") as terminate:
            worker.stop_owned_process(process)
        terminate.assert_called_once_with(12345, worker.signal.SIGTERM)
        process.wait.assert_called_once_with(timeout=10)

    def test_http_client_is_authenticated_fixed_address_and_bounded(self):
        client = worker.ControlledLoopbackClient(8099, "synthetic-test-key")
        connection = Mock()
        connection.getresponse.return_value = HttpResponse(b"{}")
        with patch.object(worker.http.client, "HTTPConnection", return_value=connection) as factory:
            with client.request("POST", "/v1/chat/completions", {"input": "Unicode: é"}):
                pass
            self.assertEqual(("127.0.0.1", 8099), factory.call_args.args)
            request = connection.request.call_args.kwargs
            self.assertEqual("Bearer synthetic-test-key", request["headers"]["Authorization"])
            self.assertEqual("application/json", request["headers"]["Content-Type"])
            self.assertEqual("Unicode: é", json.loads(request["body"])["input"])
            connection.close.assert_called_once()
            for path in ("https://outside.invalid", "/v1/chat/completions?endpoint=outside", "/invocations"):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    with client.request("POST", path, {}):
                        pass
            with self.assertRaises(ValueError):
                with client.request("POST", "/v1/chat/completions", {"input": "x" * 2_000_001}):
                    pass

    def test_cancellation_closes_socket_while_waiting_for_first_headers(self):
        client = worker.ControlledLoopbackClient(8099, "synthetic-test-key")
        connection = Mock()
        client.active.add(connection)
        client.abort()
        connection.sock.shutdown.assert_called_once_with(worker.socket.SHUT_RDWR)
        connection.close.assert_called_once()


class WorkerHttpBoundaryTests(unittest.TestCase):
    """Only synthetic HTTP envelopes; no model, tokenizer or artifact directory."""
    def setUp(self):
        self.args = SimpleNamespace(model_id="controlled-base", model_revision="a" * 40,
            expected_azure_resource_id=RESOURCE, engine_version="0.26.1", tool_call_parser="hermes", reasoning_parser="qwen3",
            enable_thinking=False, reasoning_effort=None, temperature=0, top_p=1, top_k=0, seed=73,
            max_context_tokens=32768, max_output_tokens=1024, timeout_seconds=30, training_enabled=False)
        self.engine = Mock()
        self.engine.host = {"azure_resource_id": RESOURCE, "host_verification": "azure-imds-resource-and-tag-v1"}
        self.engine.template_sha = "b" * 64
        self.server = worker.ThreadingHTTPServer(("127.0.0.1", 0), worker.controlled_handler(self.args, self.engine, "synthetic-api-key"))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)

    def request(self, method, route, body=b"{}", authenticated=True, origin=None, content_type="application/json"):
        headers = {"Content-Type": content_type}
        if authenticated:
            headers["Authorization"] = "Bearer synthetic-api-key"
        if origin:
            headers["Origin"] = origin
        connection = worker.http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        try:
            connection.request(method, route, body=body, headers=headers)
            response = connection.getresponse()
            return response.status, response.read()
        finally:
            connection.close()

    def test_authentication_and_origin_are_checked_before_compute_or_artifact_access(self):
        for method, path in (("POST", "/v1/responses"), ("PUT", "/v1/training/jobs/" + "c" * 64), ("GET", "/v1/training/jobs/" + "c" * 64)):
            status, _ = self.request(method, path, authenticated=False)
            self.assertEqual(401, status)
        status, _ = self.request("POST", "/v1/responses", origin="https://browser.invalid")
        self.assertEqual(401, status)
        self.engine.select.assert_not_called()
        self.engine.artifacts.assert_not_called()

    def test_training_is_disabled_before_dataset_write(self):
        status, body = self.request("PUT", "/v1/training/files/" + "c" * 64, content_type="application/x-ndjson")
        self.assertEqual(403, status)
        self.assertEqual("controlled_training_disabled", json.loads(body)["error"])
        self.engine.artifacts.assert_not_called()

    def test_mismatched_configuration_never_calls_engine(self):
        status, body = self.request("POST", "/v1/responses", json.dumps({"model_revision": "other"}).encode())
        self.assertEqual(409, status)
        self.assertEqual("checkpoint_or_execution_configuration_mismatch", json.loads(body)["error"])
        self.engine.select.assert_not_called()

    def test_http_inference_receipt_preserves_configuration_and_discards_private_reasoning(self):
        self.engine.client.request.return_value = stream(
            frame({"reasoning_content": "PRIVATE SYNTHETIC REASONING"}), frame({"content": "Synthetic protocol result."}, "stop"))
        body = {"model": "controlled-base", "model_revision": "a" * 40, "adapter_version": "", "azure_resource_id": RESOURCE,
                "instructions": "Synthetic protocol instructions", "input": [{"role": "user", "content": "Synthetic protocol input"}],
                "tools": None, "tool_choice": "none", "max_output_tokens": 1024, "timeout_seconds": 30, "store": False, "stream": True,
                "execution_limits": {"max_context_tokens": 32768, "max_output_tokens": 1024, "timeout_seconds": 30, "maximum_concurrent_requests": 1},
                "generation_settings": {"engine": "Vllm", "engine_version": "0.26.1", "tool_call_parser": "hermes", "reasoning_parser": "qwen3",
                    "enable_thinking": False, "reasoning_effort": None, "temperature": 0, "top_p": 1, "top_k": 0, "seed": 73, "preserve_thinking": False}}
        status, raw = self.request("POST", "/v1/responses", json.dumps(body).encode())
        self.assertEqual(200, status)
        self.assertNotIn(b"PRIVATE", raw)
        event = json.loads(raw.decode().split("data: ")[-1])
        self.assertEqual("response.completed", event["type"])
        receipt = event["response"]
        self.assertEqual(RESOURCE, receipt["azure_resource_id"])
        self.assertEqual(body["execution_limits"], receipt["execution_limits"])
        self.assertEqual("hermes", receipt["generation_settings"]["tool_call_parser"])
        self.assertEqual("Synthetic protocol result.", receipt["output"][0]["content"][0]["text"])
        self.engine.select.assert_called_once_with("controlled-base", "")


if __name__ == "__main__":
    unittest.main()
