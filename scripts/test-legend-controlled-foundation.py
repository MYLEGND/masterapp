#!/usr/bin/env python3
"""Model-free protocol and remote-host boundary tests; never load model assets."""
import importlib.util
import io
import hashlib
import json
import plistlib
from pathlib import Path
import time
import threading
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("legend_foundation", Path(__file__).with_name("legend-local-foundation.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

RESOURCE = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/legend-test/providers/Microsoft.Compute/virtualMachines/controlled-test"
METADATA_URL = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01"
MAC_UUID = "abcdef01-2345-6789-abcd-ef0123456789"
MAC_ID = hashlib.sha256((MAC_UUID + "\n501").encode()).hexdigest()


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

    def test_founder_mac_identity_binds_canonical_hardware_uuid_and_real_user(self):
        result = SimpleNamespace(stdout=plistlib.dumps([{"IOPlatformUUID": MAC_UUID.upper()}]))
        with patch.object(worker.sys, "platform", "darwin"), patch.object(worker.platform, "machine", return_value="arm64"), \
             patch.object(worker.os, "getuid", return_value=501), patch.object(worker.os, "geteuid", return_value=501), \
             patch.object(worker.subprocess, "run", return_value=result) as identity, patch.object(Path, "open") as assets:
            receipt = worker.verify_controlled_host("FounderMac", "inference", expected_mac_host_id=MAC_ID)
            self.assertEqual({"host_kind": "FounderMac", "mac_host_id": MAC_ID,
                              "host_verification": "macos-arm64-user-bound-v1"}, receipt)
            self.assertNotIn(MAC_UUID, json.dumps(receipt))
            self.assertEqual(3, identity.call_args.kwargs["timeout"])
            assets.assert_not_called()
            with self.assertRaises(RuntimeError):
                worker.verify_founder_mac_host("f" * 64, "training")

    def test_mac_guard_rejects_other_platform_root_and_effective_user_before_assets(self):
        for system, arch, uid, euid in (("linux", "arm64", 501, 501), ("darwin", "x86_64", 501, 501),
                                      ("darwin", "arm64", 0, 0), ("darwin", "arm64", 501, 502)):
            with self.subTest(system=system, arch=arch, uid=uid, euid=euid), \
                 patch.object(worker.sys, "platform", system), patch.object(worker.platform, "machine", return_value=arch), \
                 patch.object(worker.os, "getuid", return_value=uid), patch.object(worker.os, "geteuid", return_value=euid), \
                 patch.object(worker.subprocess, "run") as process, patch.object(Path, "resolve") as paths:
                with self.assertRaises(RuntimeError):
                    worker.MlxEngine(SimpleNamespace(expected_mac_host_id=MAC_ID))
                process.assert_not_called()
                paths.assert_not_called()

    def test_unspecified_mac_identity_or_host_kind_never_executes(self):
        with patch.object(worker.subprocess, "run") as process:
            for value in (None, "", "a" * 63, "A" * 64):
                with self.subTest(value=value), self.assertRaises(ValueError):
                    worker.verify_founder_mac_host(value, "inference")
            with self.assertRaises(ValueError):
                worker.verify_controlled_host("Automatic", "inference", expected_mac_host_id=MAC_ID)
            process.assert_not_called()


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


class MlxProtocolTests(unittest.TestCase):
    """Synthetic framing/lifecycle evidence; these do not measure model quality."""
    def test_private_reasoning_is_removed_before_hermes_tool_decoding(self):
        public = {"name": "read_records", "arguments": {"query": 'literal </tool_call> and "quote": Ayiti'}}
        output, state = worker.parse_mlx_output('<think><tool_call>{"name":"unsafe"}</tool_call></think>' +
            "Checking the permitted records. <tool_call>" + json.dumps(public) + "</tool_call>", {"read_records"}, "auto", "stop")
        self.assertEqual("completed", state)
        self.assertEqual("Checking the permitted records.", output[0]["content"][0]["text"])
        self.assertEqual(public["arguments"], json.loads(output[1]["arguments"]))
        self.assertNotIn("unsafe", json.dumps(output))

    def test_unexposed_malformed_or_truncated_plans_never_execute(self):
        call = '<tool_call>{"name":"read_records","arguments":{}}</tool_call>'
        for text, tools, choice, finish in ((call, set(), "auto", "stop"), (call, {"read_records"}, "none", "stop"),
            (call, {"read_records"}, "auto", "length"), (call[:-5], {"read_records"}, "auto", "length"),
            ("No call", {"read_records"}, "required", "stop"), ("<think>private", set(), "none", "length")):
            with self.subTest(text=text, choice=choice, finish=finish), self.assertRaises(ValueError):
                worker.parse_mlx_output(text, tools, choice, finish)

    def test_incomplete_text_preserves_its_terminal_limitation(self):
        output, state = worker.parse_mlx_output("A partial explanation", set(), "none", "length")
        self.assertEqual("incomplete", state)
        self.assertEqual("A partial explanation", output[0]["content"][0]["text"])

    def test_checkpoint_switches_reconstruct_base_and_failed_adapter_restores_base(self):
        engine = object.__new__(worker.MlxEngine)
        engine.args = SimpleNamespace(model_id="base")
        engine.healthy, engine.loaded_adapter = True, None
        engine.cancelled = threading.Event()
        engine.verify_checkpoint = Mock(return_value=(Path("/synthetic/adapter"), b"", "a" * 64, b""))
        loads = []
        def unload():
            engine.healthy, engine.loaded_adapter = False, None
        def load(path=None):
            loads.append(path)
            engine.healthy = True
        engine.unload_model, engine.load_model = Mock(side_effect=unload), Mock(side_effect=load)
        model = "controlled:" + "b" * 64
        engine.select(model, "a" * 64)
        engine.select("base", "")
        self.assertEqual([Path("/synthetic/adapter"), None], loads)
        engine.load_model.side_effect = [RuntimeError("invalid adapter"), None]
        with self.assertRaises(RuntimeError):
            engine.select(model, "a" * 64)
        self.assertIsNone(engine.loaded_adapter)
        self.assertEqual(None, engine.load_model.call_args.args[0] if engine.load_model.call_args.args else None)

    def test_lifetime_worker_lock_survives_compute_release_for_training(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            engine = worker.ControlledArtifacts()
            with patch.object(worker, "verify_base", return_value={"repository": "synthetic"}), \
                 patch.object(worker, "sha256_file", return_value="b" * 64), \
                 patch.object(Path, "is_file", return_value=True), patch.object(Path, "read_text", return_value="synthetic template"):
                engine.initialize_artifacts(SimpleNamespace(model_id="synthetic", model_revision="a" * 40,
                    host_kind="AzureVm", model_directory=root, artifacts_directory=root))
            worker.fcntl.flock(engine.capacity, worker.fcntl.LOCK_UN)
            with (root / "foundation-worker.lock").open("a+") as duplicate:
                with self.assertRaises(BlockingIOError):
                    worker.fcntl.flock(duplicate, worker.fcntl.LOCK_EX | worker.fcntl.LOCK_NB)
            engine.close_artifacts()

    def test_mac_storage_quota_reserves_concurrent_writes_and_retention_never_deletes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            engine = worker.ControlledArtifacts()
            engine.args = SimpleNamespace(max_storage_bytes=100, max_retained_adapters=2, artifacts_directory=root)
            engine.storage_root, engine.storage_lock, engine.reserved_bytes = root, threading.Lock(), 0
            self.assertEqual(60, engine.reserve_storage(60))
            with self.assertRaisesRegex(ValueError, "controlled_storage_capacity"):
                engine.reserve_storage(60)
            engine.release_storage(60)
            self.assertEqual(60, engine.reserve_storage(60))
            engine.release_storage(60)
            for index in (1, 2):
                (root / "jobs" / str(index) / "adapter").mkdir(parents=True)
            with self.assertRaisesRegex(ValueError, "controlled_adapter_retention_limit"):
                engine.reserve_storage(1, adapter=True)
            self.assertTrue((root / "jobs" / "1" / "adapter").is_dir())
            self.assertTrue((root / "jobs" / "2" / "adapter").is_dir())

    def test_mac_storage_rejects_symlinks_overlapping_roots_and_git_before_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            model = root / "model"
            model.mkdir()
            arguments = SimpleNamespace(host_kind="FounderMac", storage_root=root, model_directory=model,
                artifacts_directory=model / "jobs", max_storage_bytes=worker.MAC_STORAGE_MAX_BYTES, max_retained_adapters=2)
            with patch.object(worker, "verify_base") as assets:
                with self.assertRaisesRegex(ValueError, "separate, contained, and outside Git"):
                    worker.ControlledArtifacts().initialize_artifacts(arguments)
                arguments.artifacts_directory = root / "artifacts"
                (root / ".git").mkdir()
                with self.assertRaisesRegex(ValueError, "separate, contained, and outside Git"):
                    worker.ControlledArtifacts().initialize_artifacts(arguments)
                (root / ".git").rmdir()
                (root / "symlink").symlink_to(model, target_is_directory=True)
                with self.assertRaisesRegex(ValueError, "symbolic links"):
                    worker.ControlledArtifacts().initialize_artifacts(arguments)
                assets.assert_not_called()

    def test_model_metrics_measure_generation_and_clear_stale_values_on_failure(self):
        engine = object.__new__(worker.MlxEngine)
        engine.args = SimpleNamespace(max_context_tokens=8192, seed=73, temperature=0, top_p=1, top_k=0, presence_penalty=1)
        engine.mx, engine.tokenizer, engine.make_sampler, engine.make_logits_processors = Mock(), Mock(), Mock(), Mock()
        engine.mx.get_active_memory.return_value = 100
        engine.mx.get_peak_memory.return_value = 987654
        engine.memory_limit, engine.cancelled, engine.model = 1000, threading.Event(), object()
        engine.tokenizer.apply_chat_template.return_value = [1, 2]
        clock = [10.0]
        def generate(*_args, **_kwargs):
            engine.make_logits_processors.assert_called_with(presence_penalty=1, presence_context_size=20)
            self.assertIs(_kwargs["logits_processors"], engine.make_logits_processors.return_value)
            clock[0] = 10.125
            yield SimpleNamespace(text="Synthetic ", generation_tokens=1, finish_reason=None)
            clock[0] = 10.5
            yield SimpleNamespace(text="result.", generation_tokens=2, finish_reason="stop")
        engine.stream_generate = generate
        payload = {"messages": [{"role": "user", "content": "Synthetic protocol input"}],
                   "tools": None, "tool_choice": "none", "max_tokens": 16}
        with patch.object(worker.time, "monotonic", side_effect=lambda: clock[0]):
            output, usage, state = engine.generate(payload, 20, lambda: None)
            self.assertEqual("completed", state)
            self.assertEqual({"input_tokens": 2, "output_tokens": 2}, usage)
            self.assertEqual({"model_first_token_ms": 125.0, "model_completion_ms": 500.0,
                              "peak_allocated_bytes": 987654}, engine.last_timing)
            self.assertEqual("Synthetic result.", output[0]["content"][0]["text"])
            engine.args.max_context_tokens = 1
            with self.assertRaisesRegex(ValueError, "local_context_limit"):
                engine.generate(payload, 20, lambda: None)
            self.assertEqual({}, engine.last_timing)


class ExecutionQueueTests(unittest.TestCase):
    def test_two_waiters_are_fifo_and_extra_requests_do_not_take_compute(self):
        queue = worker.ControlledExecutionQueue()
        self.assertTrue(queue.acquire())
        entered = [threading.Event(), threading.Event()]
        order, failures = [], []
        def waiter(index):
            try:
                self.assertTrue(queue.acquire_inference(time.monotonic() + 3, entered[index].set))
                order.append(index)
                queue.release()
            except BaseException as error:
                failures.append(error)
        threads = []
        for index in (0, 1):
            threads.append(threading.Thread(target=waiter, args=(index,)))
            threads[-1].start()
            self.assertTrue(entered[index].wait(1))
        self.assertEqual(2, queue.snapshot()["queued_requests"])
        self.assertFalse(queue.acquire_inference(time.monotonic() + 1, lambda: None))
        self.assertFalse(queue.acquire())
        queue.release()
        for thread in threads:
            thread.join(2)
        self.assertEqual([], failures)
        self.assertEqual([0, 1], order)
        self.assertEqual(0, queue.snapshot()["queued_requests"])
        self.assertFalse(queue.snapshot()["busy"])

    def test_queue_deadline_and_disconnection_leave_active_owner_untouched(self):
        queue = worker.ControlledExecutionQueue()
        self.assertTrue(queue.acquire())
        with self.assertRaisesRegex(TimeoutError, "local_deadline_exceeded"):
            queue.acquire_inference(time.monotonic() + 0.02, lambda: None)
        with self.assertRaises(BrokenPipeError):
            queue.acquire_inference(time.monotonic() + 1, lambda: (_ for _ in ()).throw(BrokenPipeError()))
        self.assertEqual({"busy": True, "training_busy": True, "queued_requests": 0,
                          "maximum_queued_requests": 2, "maximum_concurrent_requests": 1}, queue.snapshot())
        queue.release()


class WorkerHttpBoundaryTests(unittest.TestCase):
    """Only synthetic HTTP envelopes; no model, tokenizer or artifact directory."""
    def setUp(self):
        self.args = SimpleNamespace(model_id="controlled-base", model_revision="a" * 40,
            host_kind="AzureVm", engine="Vllm", expected_mac_host_id=None,
            expected_azure_resource_id=RESOURCE, engine_version="0.26.1", tool_call_parser="hermes", reasoning_parser="qwen3",
            enable_thinking=False, reasoning_effort=None, temperature=0, top_p=1, top_k=0, presence_penalty=0, seed=73,
            max_context_tokens=32768, max_output_tokens=1024, timeout_seconds=30, training_enabled=False)
        self.engine = Mock()
        self.engine.host = {"azure_resource_id": RESOURCE, "host_verification": "azure-imds-resource-and-tag-v1"}
        self.engine.template_sha = "b" * 64
        self.engine.healthy, self.engine.closing = True, False
        self.engine.generate.side_effect = lambda payload, deadline, heartbeat: worker.VllmEngine.generate(self.engine, payload, deadline, heartbeat)
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

    def test_health_is_authenticated_readiness_without_model_or_artifact_work(self):
        status, _ = self.request("GET", "/v1/health", authenticated=False)
        self.assertEqual(401, status)
        status, raw = self.request("GET", "/v1/health")
        self.assertEqual(200, status)
        health = json.loads(raw)
        self.assertEqual(RESOURCE, health["azure_resource_id"])
        self.assertTrue(health["ready"])
        self.assertEqual(2, health["maximum_queued_requests"])
        self.assertNotIn("input", health)
        self.assertNotIn("artifacts", health)
        self.engine.healthy = False
        status, raw = self.request("GET", "/v1/health")
        self.assertEqual(503, status)
        self.assertFalse(json.loads(raw)["ready"])
        self.engine.select.assert_not_called()
        self.engine.generate.assert_not_called()
        self.engine.artifacts.assert_not_called()

    def test_mismatched_configuration_never_calls_engine(self):
        status, body = self.request("POST", "/v1/responses", json.dumps({"model_revision": "other"}).encode())
        self.assertEqual(409, status)
        self.assertEqual("checkpoint_or_execution_configuration_mismatch", json.loads(body)["error"])
        self.engine.select.assert_not_called()

    def inference_body(self, streaming=True):
        return {"model": "controlled-base", "model_revision": "a" * 40, "adapter_version": "", "azure_resource_id": RESOURCE,
                "instructions": "Synthetic protocol instructions", "input": [{"role": "user", "content": "Synthetic protocol input"}],
                "tools": None, "tool_choice": "none", "max_output_tokens": 1024, "timeout_seconds": 30, "store": False, "stream": streaming,
                "execution_limits": {"max_context_tokens": 32768, "max_output_tokens": 1024, "timeout_seconds": 30, "maximum_concurrent_requests": 1},
                "generation_settings": {"engine": "Vllm", "engine_version": "0.26.1", "tool_call_parser": "hermes", "reasoning_parser": "qwen3",
                    "enable_thinking": False, "reasoning_effort": None, "temperature": 0, "top_p": 1, "top_k": 0, "presence_penalty": 0, "seed": 73, "preserve_thinking": False}}

    def test_http_inference_receipt_preserves_configuration_and_discards_private_reasoning(self):
        self.engine.client.request.return_value = stream(
            frame({"reasoning_content": "PRIVATE SYNTHETIC REASONING"}), frame({"content": "Synthetic protocol result."}, "stop"))
        body = self.inference_body()
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
        self.assertNotIn("model_first_token_ms", receipt["timing"])
        self.assertNotIn("peak_allocated_bytes", receipt["timing"])
        self.engine.select.assert_called_once_with("controlled-base", "")

    def test_buffered_response_uses_same_engine_receipt_without_sse(self):
        self.engine.last_timing = {"model_first_token_ms": 125.5, "model_completion_ms": 220.75,
                                  "peak_allocated_bytes": 456789, "unexpected": "private"}
        self.engine.client.request.return_value = stream(frame({"reasoning_content": "PRIVATE WORDS"}),
                                                         frame({"content": "Buffered protocol result."}, "stop"))
        status, raw = self.request("POST", "/v1/responses", json.dumps(self.inference_body(False)).encode())
        self.assertEqual(200, status)
        self.assertNotIn(b"data: ", raw)
        self.assertNotIn(b"PRIVATE", raw)
        receipt = json.loads(raw)
        self.assertIs(False, receipt["stream"])
        self.assertEqual("completed", receipt["status"])
        self.assertEqual(RESOURCE, receipt["azure_resource_id"])
        self.assertEqual("Buffered protocol result.", receipt["output"][0]["content"][0]["text"])
        self.assertEqual(125.5, receipt["timing"]["model_first_token_ms"])
        self.assertEqual(220.75, receipt["timing"]["model_completion_ms"])
        self.assertEqual(456789, receipt["timing"]["peak_allocated_bytes"])
        self.assertNotIn("unexpected", receipt["timing"])
        self.engine.generate.assert_called_once()

    def test_buffered_context_limit_is_truthful_and_does_not_return_an_answer(self):
        self.engine.generate.side_effect = ValueError("local_context_limit")
        status, raw = self.request("POST", "/v1/responses", json.dumps(self.inference_body(False)).encode())
        self.assertEqual(422, status)
        self.assertEqual({"error": "local_context_limit"}, json.loads(raw))

    def test_buffered_disconnection_cancels_active_generation(self):
        entered, exited = threading.Event(), threading.Event()
        def wait_for_cancellation(payload, deadline, heartbeat):
            entered.set()
            try:
                for _ in range(30):
                    time.sleep(0.05)
                    heartbeat()
                self.fail("Disconnected generation continued beyond the bounded monitor interval")
            finally:
                exited.set()
        self.engine.generate.side_effect = wait_for_cancellation
        connection = worker.http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.request("POST", "/v1/responses", json.dumps(self.inference_body(False)).encode(),
            {"Content-Type": "application/json", "Authorization": "Bearer synthetic-api-key"})
        self.assertTrue(entered.wait(1))
        connection.close()
        self.assertTrue(exited.wait(2))
        self.engine.abort.assert_called_once()

    def test_mac_receipt_and_request_identity_do_not_reuse_azure_identity(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.args.host_kind, self.args.engine = "FounderMac", "Mlx"
        self.args.expected_mac_host_id, self.args.expected_azure_resource_id = MAC_ID, None
        self.args.engine_version = "0.31.3"
        self.engine.host = {"host_kind": "FounderMac", "mac_host_id": MAC_ID,
                            "host_verification": "macos-arm64-user-bound-v1"}
        self.engine.generate.side_effect = lambda payload, deadline, heartbeat: (
            worker.parse_mlx_output("Synthetic Mac protocol receipt.", set(), "none", "stop")[0],
            {"input_tokens": 2, "output_tokens": 5}, "completed")
        self.server = worker.ThreadingHTTPServer(("127.0.0.1", 0), worker.controlled_handler(self.args, self.engine, "synthetic-api-key"))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        body = self.inference_body(False)
        body["generation_settings"].update(engine="Mlx", engine_version="0.31.3")
        status, _ = self.request("POST", "/v1/responses", json.dumps(body).encode())
        self.assertEqual(409, status)
        self.engine.generate.assert_not_called()
        body.update(host_kind="FounderMac", mac_host_id=MAC_ID, azure_resource_id=None)
        status, raw = self.request("POST", "/v1/responses", json.dumps(body).encode())
        self.assertEqual(200, status)
        receipt = json.loads(raw)
        self.assertEqual(MAC_ID, receipt["mac_host_id"])
        self.assertNotIn("azure_resource_id", receipt)
        self.assertFalse(receipt["stream"])


if __name__ == "__main__":
    unittest.main()
