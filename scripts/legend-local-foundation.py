#!/usr/bin/env python3
"""Pinned, offline token execution for LEGEND's existing model transport.

This process has no tools, database, learning admission, or escalation authority.
The existing C# conversation executor owns those decisions. Requests cannot
choose paths, download models, or change serving policy.
"""
from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
import fcntl
import hashlib
import hmac
import http.client
import importlib.metadata
import json
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
import urllib.error
import urllib.request
import uuid

# Model resolution is local, including tokenizer assets. No implicit Hub fetch,
# telemetry, remote code, answering API, or hosted training integration exists.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def verify_controlled_azure_host(expected_resource_id: str, purpose: str) -> dict[str, str]:
    """Verify the configured remote VM before artifact access or execution.

    This is an Azure deployment boundary, not a model's self-reported hosting
    label. There is no caller-selected metadata URL, proxy, redirect, or local
    development exception. The trainer imports this same authority.
    """
    resource_pattern = (r"/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/([^/\s]{1,90})/"
                        r"providers/Microsoft\.Compute/virtualMachines/([^/\s]{1,64})")
    match = re.fullmatch(resource_pattern, expected_resource_id or "", re.I)
    if not match or purpose not in ("inference", "training"):
        raise ValueError("A pinned Azure VM resource identity and execution purpose are required")
    try:
        uuid.UUID(match[1])
    except ValueError as error:
        raise ValueError("The Azure VM subscription identity is invalid") from error
    if sys.platform != "linux":
        raise RuntimeError("Model execution is restricted to the configured remote Azure Linux VM")
    endpoint = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01"
    request = urllib.request.Request(endpoint, headers={"Metadata": "true", "Accept": "application/json"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=3) as response:
        if response.status != 200 or response.geturl() != endpoint:
            raise RuntimeError("Azure instance metadata identity is unavailable")
        raw = response.read(65_537)
        if len(raw) > 65_536:
            raise ValueError("Azure instance metadata exceeded the bounded identity response")
    metadata = json.loads(raw)
    if not isinstance(metadata, dict):
        raise ValueError("Azure instance metadata must be a structured identity")
    actual = metadata.get("resourceId")
    if not isinstance(actual, str) or actual.casefold() != expected_resource_id.casefold():
        raise RuntimeError("Azure VM resource identity does not match the configured deployment")
    if (str(metadata.get("subscriptionId", "")).casefold() != match[1].casefold() or
        str(metadata.get("resourceGroupName", "")).casefold() != match[2].casefold() or
        str(metadata.get("name", "")).casefold() != match[3].casefold() or
        metadata.get("azEnvironment") != "AzurePublicCloud"):
        raise RuntimeError("Azure VM identity fields disagree with the configured deployment")
    tags = metadata.get("tagsList")
    approved = [tag.get("value") for tag in tags if isinstance(tag, dict) and
                tag.get("name") == "LEGENDControlledFoundation"] if isinstance(tags, list) else []
    if approved != ["true"]:
        raise RuntimeError("The configured Azure VM is not approved for controlled foundation execution")
    try:
        vm_id = str(uuid.UUID(metadata.get("vmId", "")))
    except (ValueError, TypeError, AttributeError) as error:
        raise ValueError("Azure VM instance identity is invalid") from error
    location = metadata.get("location")
    if not isinstance(location, str) or not re.fullmatch(r"[a-z0-9]{1,40}", location):
        raise ValueError("Azure VM location is invalid")
    return {"azure_resource_id": expected_resource_id, "azure_vm_id": vm_id,
            "azure_location": location, "host_verification": "azure-imds-resource-and-tag-v1"}


class ControlledLoopbackClient:
    """HTTP framing for the worker-owned engine, never a model-selected URL."""
    def __init__(self, port: int, key: str):
        if not 1024 <= port <= 65535 or not key:
            raise ValueError("The private engine address and authentication are required")
        self.root = f"http://127.0.0.1:{port}"
        self.port = port
        self.key = key
        self.active: set[http.client.HTTPConnection] = set()
        self.active_lock = threading.Lock()

    @contextmanager
    def request(self, method: str, path: str, payload: Any = None, timeout: float = 30):
        allowed = {("GET", "/v1/models"), ("POST", "/v1/chat/completions"),
                   ("POST", "/v1/load_lora_adapter"), ("POST", "/v1/unload_lora_adapter"),
                   ("POST", "/sleep?level=2"), ("GET", "/is_sleeping"),
                   ("POST", "/wake_up?tags=weights"), ("POST", "/wake_up?tags=kv_cache"),
                   ("POST", "/collective_rpc")}
        if (method, path) not in allowed or not 0 < timeout <= 300:
            raise ValueError("Unsupported private engine control request")
        raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode() if payload is not None else None
        if raw is not None and len(raw) > 2_000_000:
            raise ValueError("Private engine request exceeded the bounded JSON contract")
        # HTTPConnection never uses ambient proxy settings or follows
        # redirects. Retain the socket so cancellation also interrupts prefill
        # while getresponse() is waiting for the engine's first headers.
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        with self.active_lock:
            self.active.add(connection)
        try:
            connection.request(method, path, body=raw, headers={
                "Authorization": "Bearer " + self.key, "Content-Type": "application/json",
                "Accept": "text/event-stream" if path == "/v1/chat/completions" else "application/json"})
            response = connection.getresponse()
            if 300 <= response.status < 400:
                raise ValueError("Private engine redirects are forbidden")
            if response.status != 200:
                error = response.read(8193)
                if len(error) <= 8192 and response.status == 400:
                    detail = json.loads(error).get("error", {})
                    message = detail.get("message", "") if isinstance(detail, dict) else ""
                    if isinstance(message, str) and "maximum context length" in message.lower():
                        raise ValueError("local_context_limit")
                raise RuntimeError("The private engine rejected the bounded request")
            yield response
        finally:
            with self.active_lock:
                self.active.discard(connection)
            connection.close()

    def abort(self):
        with self.active_lock:
            connections = list(self.active)
        for connection in connections:
            if connection.sock is not None:
                try:
                    connection.sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
            connection.close()

    def json(self, method: str, path: str, payload: Any = None, timeout: float = 30) -> Any:
        with self.request(method, path, payload, timeout) as response:
            raw = response.read(2_000_001)
            if len(raw) > 2_000_000:
                raise ValueError("Private engine control response exceeded the bounded JSON contract")
            return json.loads(raw) if raw else None


def read_vllm_response(response, expected_model: str, allowed_tools: set[str],
                       deadline: float, heartbeat) -> tuple[list[dict[str, Any]], dict[str, int], str]:
    """Read a bounded engine stream; private reasoning is never accumulated.

    Structured engine tool calls are protocol data, not authorization. The
    existing application executor still authorizes every resulting call.
    """
    text: list[str] = []
    calls: dict[int, dict[str, str]] = {}
    usage = {"input_tokens": 0, "output_tokens": 0}
    finish = None
    total = 0
    model_seen = False
    done = False
    while True:
        if time.monotonic() >= deadline:
            raise TimeoutError("The controlled engine exceeded its request deadline")
        heartbeat()
        line = response.readline(1_000_001)
        if not line:
            break
        total += len(line)
        if len(line) > 1_000_000 or total > 8_000_000:
            raise ValueError("The private engine exceeded its bounded event stream")
        if not line.startswith(b"data:"):
            continue
        value = line[5:].strip()
        if value == b"[DONE]":
            done = True
            break
        event = json.loads(value)
        if not isinstance(event, dict) or event.get("error"):
            raise ValueError("The private engine returned an invalid event")
        if "model" in event:
            if event["model"] != expected_model:
                raise ValueError("The private engine model identity changed")
            model_seen = True
        event_usage = event.get("usage")
        if event_usage is not None:
            if not isinstance(event_usage, dict):
                raise ValueError("Invalid private engine token usage")
            for source, target in (("prompt_tokens", "input_tokens"), ("completion_tokens", "output_tokens")):
                count = event_usage.get(source)
                if not isinstance(count, int) or isinstance(count, bool) or not 0 <= count <= 1_000_000:
                    raise ValueError("Invalid private engine token count")
                usage[target] = count
        choices = event.get("choices")
        if not isinstance(choices, list) or len(choices) > 1:
            raise ValueError("The private engine returned an unsupported choice count")
        if not choices:
            continue
        choice = choices[0]
        if not isinstance(choice, dict) or choice.get("index") != 0:
            raise ValueError("The private engine returned an invalid choice")
        delta = choice.get("delta")
        if not isinstance(delta, dict):
            raise ValueError("The private engine returned an invalid delta")
        # reasoning and reasoning_content deliberately never enter text, tool
        # parsing, events, history, receipts, or logs.
        content = delta.get("content")
        if content is not None:
            if not isinstance(content, str):
                raise ValueError("The private engine returned an unsupported content modality")
            text.append(content)
        tool_deltas = delta.get("tool_calls")
        if tool_deltas is not None:
            if not isinstance(tool_deltas, list) or len(tool_deltas) > 64:
                raise ValueError("The private engine returned invalid tool requests")
            for item in tool_deltas:
                if not isinstance(item, dict):
                    raise ValueError("Invalid structured tool delta")
                index = item.get("index")
                if not isinstance(index, int) or isinstance(index, bool) or not 0 <= index < 64:
                    raise ValueError("Invalid structured tool index")
                call = calls.setdefault(index, {"id": "", "name": "", "arguments": ""})
                if item.get("type") not in (None, "function"):
                    raise ValueError("Unsupported structured tool type")
                if item.get("id") is not None:
                    if not isinstance(item["id"], str) or call["id"] and call["id"] != item["id"]:
                        raise ValueError("Structured tool identity changed")
                    call["id"] = item["id"]
                function = item.get("function", {})
                if not isinstance(function, dict):
                    raise ValueError("Invalid structured function delta")
                for name in ("name", "arguments"):
                    if function.get(name) is not None:
                        if not isinstance(function[name], str):
                            raise ValueError("Invalid structured function value")
                        call[name] += function[name]
                if len(call["id"]) > 200 or len(call["name"]) > 120 or len(call["arguments"]) > 160_000:
                    raise ValueError("Structured tool request exceeded its bounded contract")
        reason = choice.get("finish_reason")
        if reason is not None:
            if reason not in ("stop", "tool_calls", "length") or finish is not None:
                raise ValueError("The private engine returned an invalid terminal reason")
            finish = reason
    if not done or not model_seen or finish is None:
        raise ValueError("The private engine stream ended without a verified terminal result")
    content = re.sub(r"<think>.*?</think>", "", "".join(text), flags=re.S).strip()
    if "<think>" in content or "</think>" in content:
        raise ValueError("The private engine returned an incomplete reasoning block")
    output: list[dict[str, Any]] = []
    if content:
        output.append({"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": content}]})
    for index in sorted(calls):
        call = calls[index]
        if not call["id"] or call["name"] not in allowed_tools:
            raise ValueError("The private engine requested an unexposed tool")
        arguments = json.loads(call["arguments"])
        if not isinstance(arguments, dict):
            raise ValueError("Structured tool arguments must be an object")
        output.append({"type": "function_call", "call_id": call["id"], "name": call["name"],
                       "arguments": json.dumps(arguments, ensure_ascii=False, separators=(",", ":"))})
    if not output or len(json.dumps(output, ensure_ascii=False).encode()) > 1_500_000:
        raise ValueError("The private engine returned no bounded usable result")
    return output, usage, "incomplete" if finish == "length" else "completed"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_base(path: Path, revision: str) -> dict[str, Any]:
    manifest = json.loads((path / "legend-download-manifest.json").read_text())
    if manifest.get("revision") != revision or not manifest.get("files"):
        raise ValueError("Base checkpoint revision is not the configured revision")
    for receipt in manifest["files"]:
        artifact = (path / receipt["file"]).resolve()
        if artifact.parent != path.resolve() or not artifact.is_file():
            raise ValueError("Checkpoint contains an invalid artifact path")
        if artifact.stat().st_size != receipt["bytes"] or sha256_file(artifact) != receipt["sha256"]:
            raise ValueError("Checkpoint artifact verification failed")
    return manifest


def chat_input(instructions: str, items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    if not isinstance(instructions, str) or not isinstance(items, list) or len(items) > 512:
        raise ValueError("Invalid bounded conversation input")
    messages: list[dict[str, Any]] = [{"role": "system", "content": instructions}]
    for item in items:
        if not isinstance(item, dict):
            raise ValueError("Conversation items must be structured messages")
        kind = item.get("type")
        if kind == "function_call":
            if any(not isinstance(item.get(name), str) for name in ("call_id", "name", "arguments")):
                raise ValueError("Invalid structured tool receipt")
            call = {"id": item["call_id"], "type": "function", "function": {
                "name": item["name"], "arguments": item["arguments"]}}
            if messages[-1].get("role") == "assistant" and messages[-1].get("tool_calls"):
                messages[-1]["tool_calls"].append(call)
            else:
                messages.append({"role": "assistant", "content": "", "tool_calls": [call]})
        elif kind == "function_call_output":
            if not isinstance(item.get("call_id"), str) or not isinstance(item.get("output"), str):
                raise ValueError("Invalid structured tool output")
            messages.append({"role": "tool", "tool_call_id": item["call_id"], "content": item["output"]})
        elif kind in (None, "message"):
            role = item.get("role")
            if role not in ("user", "assistant"):
                raise ValueError("Conversation input cannot change system authority")
            content = item.get("content", "")
            if isinstance(content, list):
                if any(not isinstance(part, dict) or part.get("type") not in ("input_text", "output_text") or
                       not isinstance(part.get("text"), str) for part in content):
                    raise ValueError("Unsupported input modality")
                content = "".join(part.get("text", "") for part in content)
            if not isinstance(content, str):
                raise ValueError("Invalid message content")
            messages.append({"role": role, "content": content})
        else:
            raise ValueError("Unsupported conversation item")
    return messages


def stop_owned_process(process):
    """All callers created this child with start_new_session=True."""
    if process is None or process.poll() is not None:
        return
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


class VllmEngine:
    """Compute on the exact approved Azure VM; construction rejects this Mac.

    Host verification precedes every model path read, ML import and subprocess.
    The child binds only loopback. Its administrative API is never forwarded
    from an application request; the existing worker owns that private control.
    """
    def __init__(self, args):
        self.host = verify_controlled_azure_host(args.expected_azure_resource_id, "inference")
        # Nothing above this line accesses model assets or starts compute.
        self.args = args
        self.base = args.model_directory.resolve(strict=True)
        self.manifest = verify_base(self.base, args.model_revision)
        if self.manifest.get("repository") != args.model_id:
            raise ValueError("The configured base model differs from the immutable artifact manifest")
        self.base_manifest_sha = sha256_file(self.base / "legend-download-manifest.json")
        if importlib.metadata.version("vllm") != args.engine_version:
            raise RuntimeError("The installed serving engine differs from the pinned deployment version")
        import torch
        if not torch.cuda.is_available():
            raise RuntimeError("The approved remote VM has no available CUDA serving device")
        self.artifacts = args.artifacts_directory.resolve()
        self.artifacts.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.capacity = (self.artifacts / "foundation-compute.lock").open("a+")
        fcntl.flock(self.capacity, fcntl.LOCK_EX | fcntl.LOCK_NB)
        template_path = self.base / "chat_template.jinja"
        if template_path.is_file():
            template = template_path.read_text()
        else:
            template = json.loads((self.base / "tokenizer_config.json").read_text()).get("chat_template")
        if not isinstance(template, str) or not template:
            raise ValueError("The verified tokenizer has no supported pinned chat template")
        self.template_sha = hashlib.sha256(template.encode()).hexdigest()
        self.loaded_adapter = None
        self.training_process = None
        self.healthy = False
        private_key = secrets.token_urlsafe(48)
        self.client = ControlledLoopbackClient(args.engine_port, private_key)
        environment = os.environ.copy()
        environment.update({"VLLM_API_KEY": private_key, "VLLM_SERVER_DEV_MODE": "1",
                            "VLLM_ALLOW_RUNTIME_LORA_UPDATING": "True"})
        command = [sys.executable, "-m", "vllm.entrypoints.openai.api_server", "--model", str(self.base),
                   "--served-model-name", args.model_id, "--host", "127.0.0.1", "--port", str(args.engine_port),
                   "--max-model-len", str(args.max_context_tokens), "--max-num-seqs", "1",
                   "--load-format", "safetensors", "--generation-config", "vllm", "--no-trust-remote-code",
                   "--enable-auto-tool-choice", "--tool-call-parser", args.tool_call_parser,
                   "--reasoning-parser", args.reasoning_parser, "--enable-lora", "--max-loras", "1", "--max-lora-rank", "64",
                   "--enable-sleep-mode", "--no-trust-request-chat-template", "--no-enable-log-requests",
                   "--no-enable-log-outputs", "--no-enable-log-deltas", "--disable-uvicorn-access-log"]
        if template_path.is_file():
            command += ["--chat-template", str(template_path)]
        self.process = subprocess.Popen(command, env=environment, stdin=subprocess.DEVNULL,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        try:
            expires = time.monotonic() + 300
            while time.monotonic() < expires:
                if self.process.poll() is not None:
                    raise RuntimeError("The pinned remote serving process failed during startup")
                try:
                    catalog = self.client.json("GET", "/v1/models", timeout=2)
                    models = catalog.get("data", []) if isinstance(catalog, dict) else []
                    if any(isinstance(item, dict) and item.get("id") == args.model_id and
                           item.get("root") == str(self.base) for item in models):
                        self.healthy = True
                        return
                except (OSError, ValueError):
                    pass
                time.sleep(1)
            raise TimeoutError("The pinned remote serving process did not become ready")
        except BaseException:
            self.close()
            raise

    def verify_checkpoint(self, run_key: str, adapter_version: str | None = None):
        if not re.fullmatch(r"[0-9a-f]{64}", run_key):
            raise ValueError("Invalid controlled checkpoint identity")
        directory = (self.artifacts / "jobs" / run_key / "adapter").resolve(strict=True)
        if not directory.is_relative_to(self.artifacts):
            raise ValueError("Checkpoint escaped the configured artifact boundary")
        raw = (directory / "checkpoint.json").read_bytes()
        if len(raw) > 65_536:
            raise ValueError("Checkpoint manifest exceeded its bounded contract")
        digest = hashlib.sha256(raw).hexdigest()
        if adapter_version is not None and digest != adapter_version:
            raise ValueError("Checkpoint differs from the application-selected manifest")
        checkpoint = json.loads(raw)
        configuration_raw = (directory.parent / "configuration.json").read_bytes()
        configuration = json.loads(configuration_raw)
        if (checkpoint.get("model_version") != "controlled:" + run_key or
            checkpoint.get("adapter_format") != "peft" or
            checkpoint.get("base_repository") != self.args.model_id or
            checkpoint.get("base_model_revision") != self.args.model_revision or
            checkpoint.get("base_manifest_sha256") != self.base_manifest_sha or
            checkpoint.get("training_configuration_identity") != hashlib.sha256(configuration_raw).hexdigest() or
            checkpoint.get("azure_resource_id") != configuration.get("azure_resource_id") or
            checkpoint.get("adapter_sha256") != sha256_file(directory / "adapter_model.safetensors") or
            checkpoint.get("adapter_config_sha256") != sha256_file(directory / "adapter_config.json")):
            raise ValueError("Checkpoint weights, configuration and training provenance do not agree")
        return directory, raw, digest, configuration_raw

    def select(self, model: str, adapter_version: str) -> None:
        if not self.healthy or self.process.poll() is not None:
            raise RuntimeError("The controlled engine is unavailable")
        if model == self.args.model_id:
            if adapter_version:
                raise ValueError("The pretrained base cannot silently select an adapter")
            return
        matched = re.fullmatch(r"controlled:([0-9a-f]{64})", model)
        if not matched or not re.fullmatch(r"[0-9a-f]{64}", adapter_version):
            raise ValueError("Unknown controlled model identity")
        directory, _, _, _ = self.verify_checkpoint(matched[1], adapter_version)
        if self.loaded_adapter == (model, adapter_version):
            return
        if self.loaded_adapter is not None:
            self.client.json("POST", "/v1/unload_lora_adapter", {"lora_name": self.loaded_adapter[0]})
            self.loaded_adapter = None
        self.client.json("POST", "/v1/load_lora_adapter", {"lora_name": model, "lora_path": str(directory)})
        self.loaded_adapter = (model, adapter_version)

    def sleep_for_training(self) -> None:
        self.healthy = False
        if self.loaded_adapter is not None:
            self.client.json("POST", "/v1/unload_lora_adapter", {"lora_name": self.loaded_adapter[0]})
            self.loaded_adapter = None
        self.client.json("POST", "/sleep?level=2")
        if self.client.json("GET", "/is_sleeping").get("is_sleeping") is not True:
            raise RuntimeError("The controlled engine did not release compute for training")
        fcntl.flock(self.capacity, fcntl.LOCK_UN)

    def restore_after_training(self) -> None:
        fcntl.flock(self.capacity, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            self.client.json("POST", "/wake_up?tags=weights", timeout=300)
            self.client.json("POST", "/collective_rpc", {"method": "reload_weights"}, timeout=300)
            self.client.json("POST", "/wake_up?tags=kv_cache", timeout=300)
            self.healthy = self.client.json("GET", "/is_sleeping").get("is_sleeping") is False
            if not self.healthy:
                raise RuntimeError("The controlled engine failed to restore its pinned base")
        except BaseException:
            self.healthy = False
            raise

    def close(self):
        stop_owned_process(getattr(self, "training_process", None))
        stop_owned_process(getattr(self, "process", None))
        capacity = getattr(self, "capacity", None)
        if capacity is not None:
            capacity.close()


def controlled_handler(args, engine: VllmEngine, api_key: str):
    """Existing worker HTTP boundary: compute receipts, never app decisions."""
    execution = threading.BoundedSemaphore(1)
    jobs_lock = threading.Lock()
    active_jobs: set[str] = set()
    identity = {**engine.host, "model_revision": args.model_revision, "hosting": "LegendControlled"}
    limits = {"max_context_tokens": args.max_context_tokens, "max_output_tokens": args.max_output_tokens,
              "timeout_seconds": args.timeout_seconds, "maximum_concurrent_requests": 1}
    generation = {"engine": "Vllm", "engine_version": args.engine_version,
                  "tool_call_parser": args.tool_call_parser, "reasoning_parser": args.reasoning_parser,
                  "enable_thinking": args.enable_thinking, "reasoning_effort": args.reasoning_effort,
                  "temperature": args.temperature, "top_p": args.top_p, "top_k": args.top_k,
                  "seed": args.seed, "preserve_thinking": False}

    def atomic_json(path: Path, value: dict[str, Any]):
        temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
        try:
            with temporary.open("x") as target:
                os.chmod(temporary, 0o600)
                json.dump(value, target, separators=(",", ":"))
                target.flush()
                os.fsync(target.fileno())
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)

    def state_for(run_key: str):
        path = engine.artifacts / "jobs" / run_key / "worker.json"
        if not path.is_file():
            return None
        raw = path.read_bytes()
        if len(raw) > 65_536:
            raise ValueError("Invalid bounded job receipt")
        state = json.loads(raw)
        if state.get("run_key") != run_key:
            raise ValueError("Job receipt identity mismatch")
        if state.get("status") == "running" and run_key not in active_jobs:
            # A restarted worker does not invent a queued job or launch the
            # same training mutation again. Existing lifecycle reconciliation
            # observes the interrupted compute claim as a failure.
            return {**state, "status": "failed", "error": "controlled_worker_interrupted"}
        return state

    def train(run_key: str, settings: dict[str, Any], initial_state: dict[str, Any]):
        directory = engine.artifacts / "jobs" / run_key
        state = {**initial_state}
        process = None
        slept = False
        try:
            verify_controlled_azure_host(args.expected_azure_resource_id, "training")
            slept = True
            engine.sleep_for_training()
            runner = Path(__file__).with_name("legend-local-train.py").resolve(strict=True)
            command = [sys.executable, "-B", str(runner), "--engine", "transformers", "--base", str(engine.base),
                       "--data", str(engine.artifacts / "datasets" / (state["dataset_sha256"] + ".jsonl")),
                       "--output", str(directory), "--run-key", run_key,
                       "--configuration-identity", state["configuration_identity"],
                       "--configuration-json-file", str(directory / "configuration.json"),
                       "--capacity-directory", str(engine.artifacts),
                       "--expected-azure-resource-id", args.expected_azure_resource_id]
            with (directory / "runner.log").open("ab") as log:
                os.chmod(directory / "runner.log", 0o600)
                process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
                engine.training_process = process
                process.wait(timeout=settings["deadline_seconds"] + 30)
            if process.returncode != 0:
                raise RuntimeError("Controlled training compute failed")
            result = json.loads((directory / "result.json").read_bytes())
            if (result.get("runKey") != run_key or result.get("status") != "succeeded" or
                result.get("weightsUpdated") is not True or result.get("usableCheckpoint") is not True or
                result.get("configurationIdentity") != state["configuration_identity"] or
                result.get("datasetSha256") != state["dataset_sha256"]):
                raise ValueError("Controlled training completion receipt is invalid")
            _, _, manifest_sha, _ = engine.verify_checkpoint(run_key)
            if result.get("checkpointManifestSha256") != manifest_sha:
                raise ValueError("Controlled training manifest receipt differs from its checkpoint")
            state.update(status="succeeded", model_version="controlled:" + run_key)
        except (ValueError, KeyError, TypeError, RuntimeError, OSError, subprocess.TimeoutExpired):
            state.update(status="failed", error="controlled_training_compute_failed")
        finally:
            stop_owned_process(process)
            engine.training_process = None
            if slept:
                try:
                    engine.restore_after_training()
                except (ValueError, RuntimeError, OSError):
                    state.update(status="failed", error="controlled_serving_restore_failed")
            try:
                atomic_json(directory / "worker.json", state)
            finally:
                with jobs_lock:
                    active_jobs.discard(run_key)
                execution.release()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def setup(self):
            super().setup()
            self.connection.settimeout(5)
            self.event_lock = threading.Lock()

        def log_message(self, _format, *values):
            return

        def respond(self, status: int, value: dict[str, Any]):
            body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode()
            self.close_connection = True
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def authenticated(self, content_type: str | None = None) -> bool:
            if self.headers.get("Origin") or not api_key or not hmac.compare_digest(
                    self.headers.get("Authorization", ""), "Bearer " + api_key):
                self.respond(401, {"error": "unauthorized"})
                return False
            if content_type and self.headers.get_content_type() != content_type:
                self.respond(415, {"error": "trusted_content_type_required"})
                return False
            if self.headers.get("Transfer-Encoding"):
                self.respond(400, {"error": "known_content_length_required"})
                return False
            return True

        def read_json(self) -> dict[str, Any]:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 2_000_000:
                raise ValueError("request_size_limit")
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise ValueError("incomplete_request")
            body = json.loads(raw)
            if not isinstance(body, dict):
                raise ValueError("invalid_json_object")
            return body

        def event(self, value):
            with self.event_lock:
                self.wfile.write(("data: " + json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n\n").encode())
                self.wfile.flush()

        def do_POST(self):
            if not self.authenticated("application/json"):
                return
            if self.path != "/v1/responses":
                self.respond(404, {"error": "unknown_route"})
                return
            acquired = False
            streaming = False
            stop = threading.Event()
            watcher = None
            try:
                body = self.read_json()
                if (body.get("model_revision") != args.model_revision or body.get("execution_limits") != limits or
                    body.get("generation_settings") != generation or body.get("azure_resource_id") != args.expected_azure_resource_id):
                    self.respond(409, {"error": "checkpoint_or_execution_configuration_mismatch"})
                    return
                maximum = body.get("max_output_tokens")
                timeout = body.get("timeout_seconds")
                if (body.get("store") is not False or body.get("stream") is not True or
                    not isinstance(maximum, int) or isinstance(maximum, bool) or not 0 < maximum <= args.max_output_tokens or
                    not isinstance(timeout, (int, float)) or isinstance(timeout, bool) or not 0 < timeout <= args.timeout_seconds):
                    raise ValueError("invalid_execution_contract")
                definitions = body.get("tools") or []
                if not isinstance(definitions, list) or len(definitions) > 64:
                    raise ValueError("Invalid exposed tools")
                tools = []
                for definition in definitions:
                    if not isinstance(definition, dict) or not isinstance(definition.get("name"), str):
                        raise ValueError("Invalid exposed tool definition")
                    tools.append({"type": "function", "function": {key: definition[key] for key in ("name", "description", "parameters")}})
                if body.get("tool_choice") not in ("none", "auto", "required"):
                    raise ValueError("Invalid tool planning contract")
                messages = chat_input(body["instructions"], body["input"])
                if not execution.acquire(blocking=False):
                    self.respond(429, {"error": "controlled_compute_busy"})
                    return
                acquired = True
                engine.select(body["model"], body.get("adapter_version", ""))
                started = time.monotonic()
                deadline = started + timeout
                self.close_connection = True
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Connection", "close")
                self.end_headers()
                streaming = True

                def monitor():
                    while not stop.wait(0.25):
                        try:
                            if time.monotonic() >= deadline:
                                raise TimeoutError("controlled_deadline")
                            self.event({"type": "response.in_progress"})
                        except (OSError, TimeoutError):
                            stop.set()
                            engine.client.abort()
                            return

                watcher = threading.Thread(target=monitor, daemon=True)
                watcher.start()
                payload = {"model": body["model"], "messages": messages, "tools": tools or None,
                           "tool_choice": body["tool_choice"], "max_tokens": maximum,
                           "temperature": args.temperature, "top_p": args.top_p, "top_k": args.top_k, "seed": args.seed,
                           "presence_penalty": 0, "frequency_penalty": 0, "repetition_penalty": 1,
                           "stream": True, "stream_options": {"include_usage": True},
                           "chat_template_kwargs": {"enable_thinking": args.enable_thinking, "preserve_thinking": False}}
                if args.reasoning_effort:
                    payload["reasoning_effort"] = args.reasoning_effort

                def still_connected():
                    if stop.is_set():
                        raise BrokenPipeError("controlled_request_cancelled")

                with engine.client.request("POST", "/v1/chat/completions", payload, timeout=timeout) as response:
                    output, usage, state = read_vllm_response(response, body["model"],
                        {tool["function"]["name"] for tool in tools} if body["tool_choice"] != "none" else set(), deadline, still_connected)
                stop.set()
                watcher.join(timeout=1)
                self.event({"type": "response.completed", "response": {
                    **identity, "model": body["model"], "adapter_version": body.get("adapter_version", ""),
                    "execution_limits": limits, "generation_settings": {**generation, "max_output_tokens": maximum,
                        "chat_template_sha256": engine.template_sha}, "id": "controlled_" + str(uuid.uuid4()),
                    "status": state, "output": output, "usage": usage,
                    "timing": {"elapsed_ms": round((time.monotonic() - started) * 1000)}}})
            except (ValueError, KeyError, TypeError, RuntimeError, OSError, http.client.HTTPException) as error:
                try:
                    code = "local_context_limit" if str(error) == "local_context_limit" else "local_execution_failed"
                    if streaming:
                        self.event({"type": "response.failed", "error": code})
                    else:
                        self.respond(400, {"error": "controlled_request_invalid"})
                except OSError:
                    self.close_connection = True
            finally:
                stop.set()
                if watcher:
                    watcher.join(timeout=1)
                if acquired:
                    execution.release()

        def do_PUT(self):
            upload = re.fullmatch(r"/v1/training/files/([0-9a-f]{64})", self.path)
            job = re.fullmatch(r"/v1/training/jobs/([0-9a-f]{64})", self.path)
            if not self.authenticated("application/x-ndjson" if upload else "application/json"):
                return
            if not args.training_enabled:
                self.respond(403, {"error": "controlled_training_disabled"})
                return
            if not upload and not job:
                self.respond(404, {"error": "unknown_route"})
                return
            try:
                verify_controlled_azure_host(args.expected_azure_resource_id, "training")
                if upload:
                    length = int(self.headers.get("Content-Length", "0"))
                    if not 0 < length <= 200_000_000:
                        self.respond(413, {"error": "dataset_size_limit"})
                        return
                    directory = engine.artifacts / "datasets"
                    directory.mkdir(exist_ok=True, mode=0o700)
                    destination = directory / (upload[1] + ".jsonl")
                    temporary = directory / (upload[1] + "." + uuid.uuid4().hex + ".tmp")
                    digest = hashlib.sha256()
                    try:
                        with temporary.open("xb") as target:
                            os.chmod(temporary, 0o600)
                            remaining = length
                            while remaining:
                                chunk = self.rfile.read(min(remaining, 1_048_576))
                                if not chunk:
                                    raise ValueError("Incomplete dataset upload")
                                target.write(chunk)
                                digest.update(chunk)
                                remaining -= len(chunk)
                            target.flush()
                            os.fsync(target.fileno())
                        if digest.hexdigest() != upload[1]:
                            raise ValueError("Dataset content differs from its immutable identity")
                        try:
                            os.link(temporary, destination)
                        except FileExistsError:
                            if sha256_file(destination) != upload[1]:
                                self.respond(409, {"error": "dataset_identity_conflict"})
                                return
                    finally:
                        temporary.unlink(missing_ok=True)
                    self.respond(200, {"file_id": upload[1], "bytes": length, "host_receipt": engine.host})
                    return
                body = self.read_json()
                dataset_sha = body.get("dataset_sha256", "")
                configuration_sha = body.get("configuration_identity", "")
                raw = body.get("configuration_json", "")
                if (not re.fullmatch(r"[0-9a-f]{64}", dataset_sha) or not re.fullmatch(r"[0-9a-f]{64}", configuration_sha) or
                    not isinstance(raw, str) or len(raw.encode()) > 65_536 or hashlib.sha256(raw.encode()).hexdigest() != configuration_sha):
                    raise ValueError("Invalid immutable training configuration")
                settings = json.loads(raw)
                expected_fields = {"schema", "base_model", "base_revision", "azure_resource_id", "trainer_sha256", "iterations",
                                   "learning_rate", "lora_rank", "max_sequence_tokens", "deadline_seconds", "seed"}
                runner = Path(__file__).with_name("legend-local-train.py").resolve(strict=True)
                if (not isinstance(settings, dict) or set(settings) != expected_fields or
                    settings["schema"] != "controlled-transformers-training-v1" or settings["base_model"] != args.model_id or
                    settings["base_revision"] != args.model_revision or settings["azure_resource_id"] != args.expected_azure_resource_id or
                    settings["trainer_sha256"] != sha256_file(runner)):
                    raise ValueError("Training configuration differs from the pinned deployment")
                bounds = {"iterations": (1, args.max_training_iterations), "lora_rank": (1, 64), "max_sequence_tokens": (64, 8192),
                          "deadline_seconds": (30, args.max_training_seconds), "seed": (0, 1000000)}
                if any(not isinstance(settings[name], int) or isinstance(settings[name], bool) or not low <= settings[name] <= high
                       for name, (low, high) in bounds.items()) or not isinstance(settings["learning_rate"], (int, float)) or isinstance(settings["learning_rate"], bool) or not 0.000001 <= settings["learning_rate"] <= 0.001:
                    raise ValueError("Training configuration exceeded its approved execution bounds")
                dataset = engine.artifacts / "datasets" / (dataset_sha + ".jsonl")
                if not dataset.is_file() or sha256_file(dataset) != dataset_sha:
                    raise ValueError("Training dataset is unavailable or changed")
                run_key = job[1]
                with jobs_lock:
                    existing = state_for(run_key)
                    if existing is not None:
                        if existing.get("configuration_identity") != configuration_sha or existing.get("dataset_sha256") != dataset_sha:
                            self.respond(409, {"error": "training_identity_conflict"})
                        else:
                            self.respond(200, existing)
                        return
                    if not execution.acquire(blocking=False):
                        self.respond(429, {"error": "controlled_compute_busy"})
                        return
                    try:
                        directory = engine.artifacts / "jobs" / run_key
                        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
                        with (directory / "configuration.json").open("xb") as target:
                            os.chmod(directory / "configuration.json", 0o600)
                            target.write(raw.encode())
                            target.flush()
                            os.fsync(target.fileno())
                        state = {"run_key": run_key, "status": "running", "configuration_identity": configuration_sha,
                                 "dataset_sha256": dataset_sha, "host_receipt": engine.host}
                        atomic_json(directory / "worker.json", state)
                        active_jobs.add(run_key)
                        threading.Thread(target=train, args=(run_key, settings, state), daemon=True).start()
                    except BaseException:
                        active_jobs.discard(run_key)
                        execution.release()
                        raise
                self.respond(200, state)
            except (ValueError, KeyError, TypeError, RuntimeError, OSError):
                self.respond(400, {"error": "controlled_training_request_invalid"})

        def do_GET(self):
            if not self.authenticated():
                return
            matched = re.fullmatch(r"/v1/training/jobs/([0-9a-f]{64})(/checkpoint)?", self.path)
            if not matched:
                self.respond(404, {"error": "unknown_route"})
                return
            try:
                with jobs_lock:
                    state = state_for(matched[1])
                if state is None:
                    self.respond(404, {"error": "training_job_not_found"})
                elif not matched[2]:
                    self.respond(200, state)
                elif state["status"] != "succeeded":
                    self.respond(409, {"error": "training_checkpoint_not_complete"})
                else:
                    _, raw, digest, configuration = engine.verify_checkpoint(matched[1])
                    self.respond(200, {**state, "weights_updated": True, "usable_checkpoint": True,
                        "checkpoint_manifest_base64": base64.b64encode(raw).decode(), "checkpoint_manifest_sha256": digest,
                        "configuration_json": configuration.decode(), "host_receipt": engine.host})
            except (ValueError, KeyError, TypeError, OSError):
                self.respond(409, {"error": "controlled_checkpoint_unverified"})

    return Handler


def main() -> None:
    if "--verify-host-only" in sys.argv[1:]:
        admission = argparse.ArgumentParser(description="Verify the remote test/compute host without accessing model artifacts")
        admission.add_argument("--verify-host-only", action="store_true", required=True)
        admission.add_argument("--expected-azure-resource-id", required=True)
        arguments = admission.parse_args()
        print(json.dumps(verify_controlled_azure_host(arguments.expected_azure_resource_id, "inference")))
        return
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-directory", required=True, type=Path)
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--model-revision", required=True)
    parser.add_argument("--artifacts-directory", required=True, type=Path)
    parser.add_argument("--expected-azure-resource-id", required=True)
    parser.add_argument("--engine-version", required=True)
    parser.add_argument("--tool-call-parser", required=True, choices=("hermes", "qwen3_coder"))
    parser.add_argument("--reasoning-parser", default="qwen3", choices=("qwen3",))
    parser.add_argument("--engine-port", type=int, default=8090)
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--max-context-tokens", type=int, default=32768)
    parser.add_argument("--max-output-tokens", type=int, default=1024)
    parser.add_argument("--timeout-seconds", type=int, default=120)
    parser.add_argument("--enable-thinking", action="store_true")
    parser.add_argument("--reasoning-effort", choices=("low", "medium", "xhigh"))
    parser.add_argument("--temperature", type=float, default=0)
    parser.add_argument("--top-p", type=float, default=1)
    parser.add_argument("--top-k", type=int, default=0)
    parser.add_argument("--seed", type=int, default=73)
    parser.add_argument("--training-enabled", action="store_true")
    parser.add_argument("--max-training-iterations", type=int, default=100)
    parser.add_argument("--max-training-seconds", type=int, default=600)
    args = parser.parse_args()
    verify_controlled_azure_host(args.expected_azure_resource_id, "inference")
    if not 512 <= args.max_context_tokens <= 131072 or not 128 <= args.max_output_tokens <= 8192:
        parser.error("Model context/output limits are outside the supported bounded range")
    if not 5 <= args.timeout_seconds <= 300 or not 1024 <= args.port <= 65535:
        parser.error("Timeout or port is outside the supported bounded range")
    if (not 1024 <= args.engine_port <= 65535 or args.engine_port == args.port or
        not 0 <= args.temperature <= 2 or not 0 < args.top_p <= 1 or not -1 <= args.top_k <= 100000 or
        not 0 <= args.seed <= 2147483647 or args.reasoning_effort and not args.enable_thinking):
        parser.error("The pinned serving configuration is invalid")
    if not 1 <= args.max_training_iterations <= 2000 or not 30 <= args.max_training_seconds <= 3600:
        parser.error("Training limits are outside the bounded remote job contract")
    api_key = os.environ.get("LEGEND_CONTROLLED_FOUNDATION_KEY", "")
    if not api_key:
        parser.error("The remote worker requires server-side authentication even on loopback")
    engine = VllmEngine(args)
    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), controlled_handler(args, engine, api_key))
        print(json.dumps({"ready": True, **engine.host, "model": args.model_id,
                          "model_revision": args.model_revision, "engine": "Vllm", "engine_version": args.engine_version,
                          "pid": os.getpid(), "external_answering": False}), flush=True)
        server.serve_forever()
    finally:
        engine.close()


if __name__ == "__main__":
    main()
