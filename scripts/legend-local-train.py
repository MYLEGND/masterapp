#!/usr/bin/env python3
"""Bounded controlled compute worker behind LEGEND's existing training lifecycle.

Only already-admitted JSONL on an explicitly verified host is accepted. This worker cannot retrieve
data, approve evidence, evaluate promotion, mutate the registry, or call a
teacher. Its result is a verifiable compute/checkpoint receipt only.
"""
import argparse
import fcntl
import hashlib
import importlib.metadata
import importlib.util
import json
import math
import uuid
import os
from pathlib import Path
import subprocess
import selectors
import sys
import time


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def atomic_json(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
    temporary.replace(path)


def check_output_budget(args, additional_bytes=0):
    if args.engine == "mlx":
        return args.storage_authority.verify_training_output_budget(args.output, additional_bytes)
    return 0


def run_bounded_child(args, command, log_path, remaining):
    """Bound child time, logs and the reserved job directory before each write."""
    deadline = time.monotonic() + remaining
    if remaining <= 0:
        raise TimeoutError("training execution deadline exceeded")
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               stdin=subprocess.DEVNULL, bufsize=0)
    total = 0
    try:
        with selectors.DefaultSelector() as selector, log_path.open("wb") as log:
            os.chmod(log_path, 0o600)
            selector.register(process.stdout, selectors.EVENT_READ)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("training execution deadline exceeded")
                check_output_budget(args)
                for key, _ in selector.select(min(0.2, remaining)):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    total += len(chunk)
                    if total > 1048576:
                        raise ValueError("controlled_training_log_capacity")
                    check_output_budget(args, len(chunk))
                    log.write(chunk)
                    log.flush()
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("training execution deadline exceeded")
            if process.wait(timeout=remaining) != 0:
                raise RuntimeError("controlled training process failed")
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()
        if process.stdout is not None:
            process.stdout.close()


def encode_admitted_rows(args, settings, tokenizer):
    """Exact target masking shared by both engines; never silently truncate."""
    rows = [json.loads(line) for line in (args.output / "data" / "train.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    encoded = []
    for row in rows:
        messages = row["messages"]
        if messages[-1]["role"] != "assistant":
            raise ValueError("the admitted target must be the final assistant message")
        tokens = tokenizer.apply_chat_template(messages, tokenize=True, add_generation_prompt=False, enable_thinking=False)
        prefix = tokenizer.apply_chat_template(messages[:-1], tokenize=True, add_generation_prompt=True, enable_thinking=False)
        if len(tokens) > settings["max_sequence_tokens"] or len(tokens) <= len(prefix) or tokens[:len(prefix)] != prefix:
            raise ValueError("exact prompt masking or sequence budget could not be preserved")
        encoded.append((tokens, len(prefix)))
    if not encoded:
        raise ValueError("no admitted training tokens")
    return encoded


class AdmittedTokenDataset:
    """MLX's dataset format adapter for already checked token sequences."""
    def __init__(self, rows):
        self.rows = rows

    def __len__(self):
        return len(self.rows)

    def __getitem__(self, index):
        return self.rows[index]

    @staticmethod
    def process(row):
        return row


def admitted_target_mask(positions, prefix_length, sequence_length):
    return (positions >= prefix_length) & (positions < sequence_length)


def mlx_compute(args, settings):
    import copy
    import mlx.core as mx
    from mlx_lm.utils import load
    import mlx.nn as nn
    import mlx.optimizers as optim
    from mlx_lm.lora import CONFIG_DEFAULTS
    from mlx_lm.tuner.datasets import CacheDataset
    from mlx_lm.tuner.trainer import TrainingArgs, train
    from mlx_lm.tuner.utils import linear_to_lora_layers
    from mlx_lm.utils import save_config
    from mlx.utils import tree_flatten
    from mlx_lm.tuner.callbacks import TrainingCallback
    model, tokenizer = load(str(args.base), tokenizer_config={"trust_remote_code": False})
    encoded = encode_admitted_rows(args, settings, tokenizer)
    options = copy.deepcopy(CONFIG_DEFAULTS)
    options.update(model=str(args.base), data=str(args.output / "data"), train=True,
                   seed=settings["seed"], num_layers=4, batch_size=1, iters=settings["iterations"],
                   val_batches=0, learning_rate=settings["learning_rate"], optimizer="adamw",
                   steps_per_report=1, steps_per_eval=settings["iterations"], save_every=settings["iterations"],
                   adapter_path=str(args.output / "adapter"), max_seq_length=settings["max_sequence_tokens"],
                   grad_checkpoint=True, mask_prompt=True,
                   lora_parameters={"rank": settings["lora_rank"], "dropout": 0.0, "scale": 2 * settings["lora_rank"]})
    losses = []

    class Metrics(TrainingCallback):
        def on_train_loss_report(self, info):
            loss = float(info["train_loss"])
            if not math.isfinite(loss):
                raise ValueError("nonfinite training loss")
            losses.append(loss)

    # The upstream default loss includes the first padding token (<= length).
    # Use its supported loss callback to preserve this authority's exact target
    # contract: token indices [prompt length, actual sequence length).
    def target_loss(model, batch, lengths):
        targets = batch[:, 1:]
        logits = model(batch[:, :-1])
        positions = mx.arange(1, targets.shape[1] + 1)
        mask = admitted_target_mask(positions, lengths[:, 0:1], lengths[:, 1:])
        count = mask.sum()
        loss = (nn.losses.cross_entropy(logits, targets) * mask).astype(mx.float32).sum() / count
        return loss, count

    mx.random.seed(settings["seed"])
    import numpy as np
    np.random.seed(settings["seed"])
    model.freeze()
    if len(model.layers) < options["num_layers"]:
        raise ValueError("the pinned model has insufficient adapter layers")
    linear_to_lora_layers(model, options["num_layers"], options["lora_parameters"])
    # One final safetensors write, with actual tensor bytes plus a conservative
    # header/config/metrics and two bounded log allowances reserved in advance.
    adapter_bytes = sum(tensor.nbytes for _, tensor in tree_flatten(model.trainable_parameters()))
    check_output_budget(args, adapter_bytes + 4 * 1048576)
    adapter = args.output / "adapter"
    adapter.mkdir(parents=True, exist_ok=True)
    save_config(options, adapter / "adapter_config.json")
    mx.reset_peak_memory()
    training_args = TrainingArgs(batch_size=1, iters=settings["iterations"], val_batches=0,
        steps_per_report=1, steps_per_eval=settings["iterations"], steps_per_save=settings["iterations"] + 1,
        adapter_file=adapter / "adapters.safetensors", max_seq_length=settings["max_sequence_tokens"], grad_checkpoint=True)
    train(model, optim.AdamW(learning_rate=settings["learning_rate"]),
          CacheDataset(AdmittedTokenDataset(encoded)), val_dataset=None, args=training_args,
          loss=target_loss, training_callback=Metrics())
    if len(losses) != settings["iterations"]:
        raise ValueError("training iteration receipt is incomplete")
    check_output_budget(args, 65536)
    atomic_json(args.output / "compute-metrics.json", {
        "engine": "Mlx", "iterations": len(losses), "first_loss": losses[0], "last_loss": losses[-1],
        "peak_mlx_bytes": mx.get_peak_memory(), "mlx_version": importlib.metadata.version("mlx"),
        "mlx_lm_version": importlib.metadata.version("mlx-lm")})


def mlx_probe(args):
    from mlx_lm.utils import load
    from mlx_lm import stream_generate
    from mlx_lm.sample_utils import make_sampler
    model, tokenizer = load(str(args.base), adapter_path=str(args.output / "adapter"),
                            tokenizer_config={"trust_remote_code": False})
    token = next(stream_generate(model, tokenizer, prompt="1", max_tokens=1, sampler=make_sampler(temp=0)), None)
    if token is None or token.generation_tokens != 1:
        raise ValueError("the loaded checkpoint did not generate a token")


def validate_recipe(settings, args, trainer_sha):
    common = {"schema", "base_model", "base_revision", "trainer_sha256", "iterations", "learning_rate",
              "lora_rank", "max_sequence_tokens", "deadline_seconds", "seed"}
    mac = args.engine == "mlx"
    expected = common | ({"host_kind", "mac_host_id", "founder_id"} if mac else {"azure_resource_id"})
    if set(settings) != expected or settings["schema"] != ("controlled-mlx-training-v1" if mac else "controlled-transformers-training-v1"):
        raise ValueError("training configuration schema is invalid")
    if mac:
        try:
            founder = str(uuid.UUID(settings["founder_id"]))
        except (ValueError, AttributeError, TypeError):
            raise ValueError("training owner identity is invalid") from None
        if (args.host_kind != "FounderMac" or settings["host_kind"] != "FounderMac" or
                settings["mac_host_id"] != args.expected_mac_host_id or
                founder == str(uuid.UUID(int=0)) or founder != settings["founder_id"] or founder != args.founder_id):
            raise ValueError("training owner or host identity is invalid")
    elif args.host_kind != "AzureVm" or settings["azure_resource_id"] != args.expected_azure_resource_id:
        raise ValueError("training host identity is invalid")
    limits = {"iterations": (1, 2000), "lora_rank": (1, 64), "max_sequence_tokens": (64, 2048 if mac else 8192),
              "deadline_seconds": (30, 3600), "seed": (0, 1000000)}
    if (settings["trainer_sha256"] != trainer_sha or
            any(type(settings[name]) is not int or not lower <= settings[name] <= upper for name, (lower, upper) in limits.items()) or
            type(settings["learning_rate"]) not in (int, float) or not math.isfinite(settings["learning_rate"]) or
            not 0.000001 <= settings["learning_rate"] <= 0.001):
        raise ValueError("training code identity or resource budget is invalid")


def transformers_model(base):
    import torch
    from transformers import AutoConfig, AutoModelForCausalLM, AutoModelForImageTextToText, BitsAndBytesConfig
    if not torch.cuda.is_available():
        raise RuntimeError("controlled CUDA training resource is unavailable")
    config = AutoConfig.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    architectures = config.architectures or []
    factory = AutoModelForImageTextToText if any(name.endswith("ForConditionalGeneration") for name in architectures) else AutoModelForCausalLM
    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                                     bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.bfloat16)
    return factory.from_pretrained(str(base), local_files_only=True, trust_remote_code=False,
                                   quantization_config=quantization, dtype=torch.bfloat16, device_map={"": 0})


def transformers_compute(args, settings):
    import random
    import torch
    from transformers import AutoTokenizer
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training, TaskType
    random.seed(settings["seed"])
    torch.manual_seed(settings["seed"])
    model = prepare_model_for_kbit_training(transformers_model(args.base), use_gradient_checkpointing=True)
    model.config.use_cache = False
    tokenizer = AutoTokenizer.from_pretrained(str(args.base), local_files_only=True, trust_remote_code=False)
    model = get_peft_model(model, LoraConfig(r=settings["lora_rank"], lora_alpha=2 * settings["lora_rank"],
                          lora_dropout=0.0, target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
                          bias="none", task_type=TaskType.CAUSAL_LM))
    encoded = [(tokens, [-100] * prefix + tokens[prefix:])
               for tokens, prefix in encode_admitted_rows(args, settings, tokenizer)]
    optimizer = torch.optim.AdamW((parameter for parameter in model.parameters() if parameter.requires_grad), lr=settings["learning_rate"])
    model.train()
    torch.cuda.reset_peak_memory_stats()
    order = list(range(len(encoded)))
    random.shuffle(order)
    losses = []
    for step in range(settings["iterations"]):
        tokens, labels = encoded[order[step % len(order)]]
        inputs = torch.tensor([tokens], dtype=torch.long, device="cuda")
        targets = torch.tensor([labels], dtype=torch.long, device="cuda")
        optimizer.zero_grad(set_to_none=True)
        loss = model(input_ids=inputs, labels=targets).loss
        if not torch.isfinite(loss):
            raise ValueError("nonfinite training loss")
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        optimizer.step()
        losses.append(float(loss.detach().cpu()))
    model.save_pretrained(str(args.output / "adapter"), safe_serialization=True)
    atomic_json(args.output / "compute-metrics.json", {
        "engine": "TransformersPeft", "iterations": len(losses), "first_loss": losses[0], "last_loss": losses[-1],
        "peak_cuda_allocated_bytes": torch.cuda.max_memory_allocated(), "peak_cuda_reserved_bytes": torch.cuda.max_memory_reserved(),
        "torch_version": torch.__version__, "transformers_version": importlib.metadata.version("transformers"),
        "peft_version": importlib.metadata.version("peft"), "bitsandbytes_version": importlib.metadata.version("bitsandbytes")})


def transformers_probe(args):
    from transformers import AutoTokenizer
    from peft import PeftModel
    model = PeftModel.from_pretrained(transformers_model(args.base), str(args.output / "adapter"), is_trainable=False)
    tokenizer = AutoTokenizer.from_pretrained(str(args.base), local_files_only=True, trust_remote_code=False)
    inputs = tokenizer("1", return_tensors="pt").to("cuda")
    result = model.generate(**inputs, max_new_tokens=1, do_sample=False)
    if result.shape[-1] <= inputs["input_ids"].shape[-1]:
        raise ValueError("the loaded checkpoint did not generate a token")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", choices=["transformers", "mlx"], default="transformers")
    parser.add_argument("--configuration-json-file", type=Path)
    parser.add_argument("--host-kind", choices=["AzureVm", "FounderMac"], default="AzureVm")
    parser.add_argument("--expected-azure-resource-id")
    parser.add_argument("--expected-mac-host-id")
    parser.add_argument("--founder-id")
    parser.add_argument("--storage-root", type=Path)
    parser.add_argument("--max-storage-bytes", type=int, default=8 * 1024 ** 3)
    parser.add_argument("--compute-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--probe-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--run-key", required=True)
    parser.add_argument("--configuration-identity", required=True)
    parser.add_argument("--capacity-directory", type=Path, required=True)
    args = parser.parse_args()
    if ((args.host_kind == "AzureVm" and (args.engine != "transformers" or not args.expected_azure_resource_id)) or
            (args.host_kind == "FounderMac" and (args.engine != "mlx" or not args.expected_mac_host_id or not args.founder_id))):
        parser.error("an explicitly configured matching engine and host identity is required")
    # Reuse the serving worker's one host-attestation authority. This runs
    # before model/dataset file inspection, ML imports or child execution.
    spec = importlib.util.spec_from_file_location("legend_controlled_worker_host", Path(__file__).with_name("legend-local-foundation.py"))
    worker = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = worker
    spec.loader.exec_module(worker)
    host_receipt = worker.verify_controlled_host(args.host_kind, "training", args.expected_azure_resource_id, args.expected_mac_host_id)
    args.storage_authority = worker
    if args.engine == "mlx":
        worker.verify_founder_storage_layout(args.storage_root, args.base, args.capacity_directory, args.max_storage_bytes)
        if args.output.resolve() != args.capacity_directory.resolve() / "jobs" / args.run_key:
            parser.error("training output must use its immutable controlled job location")
        check_output_budget(args)
    if not (len(args.run_key) == 64 and all(c in "0123456789abcdef" for c in args.run_key)):
        parser.error("run key must be a lowercase SHA-256 identity")
    if not args.base.is_absolute() or not args.base.is_dir() or not (args.base / "config.json").is_file():
        parser.error("an existing local base checkpoint is required")
    if not args.data.is_absolute() or not args.data.is_file() or not args.output.is_absolute() or not args.capacity_directory.is_absolute():
        parser.error("local absolute dataset and output paths are required")
    if not (len(args.configuration_identity) == 64 and all(c in "0123456789abcdef" for c in args.configuration_identity)):
        parser.error("configuration identity must be SHA-256")
    if args.configuration_json_file is None or not args.configuration_json_file.is_absolute():
        parser.error("remote immutable training configuration is required")
    raw = args.configuration_json_file.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.configuration_identity:
        parser.error("training configuration integrity failed")
    settings = json.loads(raw)
    try:
        validate_recipe(settings, args, digest(Path(__file__)))
    except ValueError as error:
        parser.error(str(error))
    args.iterations = settings["iterations"]
    args.deadline_seconds = settings["deadline_seconds"]
    args.learning_rate = settings["learning_rate"]
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    if args.compute_child:
        (mlx_compute if args.engine == "mlx" else transformers_compute)(args, settings)
        return 0
    if args.probe_child:
        (mlx_probe if args.engine == "mlx" else transformers_probe)(args)
        return 0
    args.output.mkdir(parents=True, exist_ok=True)
    result_path = args.output / "result.json"
    if result_path.exists():
        return  # The immutable job identity is never retrained implicitly.
    started = time.time()
    deadline_started = time.monotonic()
    receipt = {"runKey": args.run_key, "baseModel": str(args.base),
               "datasetSha256": digest(args.data), "baseConfigSha256": digest(args.base / "config.json"),
               "iterations": args.iterations, "learningRate": args.learning_rate,
               "configurationIdentity": args.configuration_identity, "trainerSha256": digest(Path(__file__)),
               "startedUnixSeconds": started, "weightsUpdated": False,
               "usableCheckpoint": False, "status": "failed"}
    try:
        args.capacity_directory.mkdir(parents=True, exist_ok=True)
        capacity = (args.capacity_directory / "foundation-compute.lock").open("a")
        while True:
            try:
                fcntl.flock(capacity, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() - deadline_started >= args.deadline_seconds:
                    raise TimeoutError("local compute capacity deadline exceeded")
                time.sleep(0.25)
        download_path = args.base / "legend-download-manifest.json"
        download = json.loads(download_path.read_text(encoding="utf-8"))
        if download.get("license") != "apache-2.0" or len(download.get("revision", "")) != 40:
            raise ValueError("base model license or immutable revision is unverified")
        for file in download["files"]:
            path = (args.base / file["file"]).resolve()
            if not path.is_relative_to(args.base.resolve()) or digest(path) != file["sha256"]:
                raise ValueError("base model integrity verification failed")
        receipt["baseManifestSha256"] = digest(download_path)
        if (settings["base_model"] != download["repository"] or settings["base_revision"] != download["revision"]):
            raise ValueError("the training base does not match the pinned job configuration")
        lines = [line for line in args.data.read_text(encoding="utf-8").splitlines() if line.strip()]
        if len(lines) < 4:
            raise ValueError("at least four training rows are required")
        for line in lines:
            row = json.loads(line)
            if not isinstance(row.get("messages"), list) or not any(m.get("role") == "assistant" for m in row["messages"]):
                raise ValueError("training messages are invalid")
        # All admitted training rows retain their existing weights. Independent
        # validation runs only through the locked evaluator; this compute loop
        # does not invent an unexecuted development-validation score.
        training = lines
        data_directory = args.output / "data"
        data_directory.mkdir(exist_ok=True)
        training_bytes = sum(len(line.encode("utf-8")) + 1 for line in training)
        check_output_budget(args, training_bytes + 4 * 1048576)
        (data_directory / "train.jsonl").write_text("\n".join(training) + "\n", encoding="utf-8")
        adapter = args.output / "adapter"
        child_arguments = [argument for argument in sys.argv[1:] if argument not in ("--compute-child", "--probe-child")]
        command = [sys.executable, str(Path(__file__).resolve()), *child_arguments, "--compute-child"]
        run_bounded_child(args, command, args.output / "compute.log",
                          args.deadline_seconds - (time.monotonic() - deadline_started))
        weights_name = "adapters.safetensors" if args.engine == "mlx" else "adapter_model.safetensors"
        if args.engine == "mlx":
            import mlx.core as mx
            tensors = mx.load(str(adapter / weights_name))
            finite = bool(tensors) and all(mx.all(mx.isfinite(tensor)).item() for tensor in tensors.values())
            updated = any("lora_b" in name.lower() and mx.any(tensor != 0).item() for name, tensor in tensors.items())
        else:
            import torch
            from safetensors.torch import load_file
            tensors = load_file(str(adapter / weights_name))
            finite = bool(tensors) and all(torch.isfinite(tensor).all().item() for tensor in tensors.values())
            updated = any("lora_b" in name.lower() and torch.any(tensor != 0).item() for name, tensor in tensors.items())
        if not finite:
            raise ValueError("checkpoint contains missing or nonfinite adapter tensors")
        if not updated:
            raise ValueError("checkpoint contains no trained LoRA update")
        remaining = args.deadline_seconds - (time.monotonic() - deadline_started)
        if remaining <= 0:
            raise TimeoutError("checkpoint probe exceeded the compute deadline")
        # A separate bounded process reloads the adapter after training exits.
        probe_command = [sys.executable, str(Path(__file__).resolve()), *child_arguments, "--probe-child"]
        run_bounded_child(args, probe_command, args.output / "probe.log", remaining)
        receipt.update(adapterSha256=digest(adapter / weights_name),
                       adapterConfigSha256=digest(adapter / "adapter_config.json"),
                       engine=args.engine,
                       engineVersion=importlib.metadata.version("mlx-lm" if args.engine == "mlx" else "transformers"),
                       trainingRows=len(training), developmentRows=0)
        check_output_budget(args, 65536)
        atomic_json(adapter / "checkpoint.json", {
            "model_version": "controlled:" + args.run_key,
            "adapter_format": "mlx" if args.engine == "mlx" else "peft",
            **({"host_kind": "FounderMac", "mac_host_id": host_receipt["mac_host_id"], "founder_id": settings["founder_id"]}
               if args.engine == "mlx" else {"azure_resource_id": host_receipt["azure_resource_id"]}),
            "training_configuration_identity": args.configuration_identity,
            "dataset_sha256": receipt["datasetSha256"],
            "base_manifest_sha256": receipt["baseManifestSha256"],
            "trainer_sha256": receipt["trainerSha256"],
            "base_model_revision": download["revision"],
            "base_repository": download["repository"],
            "adapter_sha256": receipt["adapterSha256"],
            "adapter_config_sha256": receipt["adapterConfigSha256"],
        })
        receipt["checkpointManifestSha256"] = digest(adapter / "checkpoint.json")
        receipt.update(status="succeeded", weightsUpdated=True, usableCheckpoint=True)
    except Exception as failure:
        # No private dataset text or arbitrary exception message enters receipt.
        receipt["failureType"] = type(failure).__name__
    receipt["finishedUnixSeconds"] = time.time()
    atomic_json(result_path, receipt)
    return 0 if receipt["status"] == "succeeded" else 1


if __name__ == "__main__":
    raise SystemExit(main())
