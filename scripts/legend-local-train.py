#!/usr/bin/env python3
"""Bounded controlled Azure compute worker behind LEGEND's existing training lifecycle.

Only remotely stored, already-admitted JSONL is accepted. This worker cannot retrieve
data, approve evidence, evaluate promotion, mutate the registry, or call a
teacher. Its result is a verifiable compute/checkpoint receipt only.
"""
import argparse
import fcntl
import hashlib
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def atomic_json(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
    temporary.replace(path)


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
        encoded.append((tokens, [-100] * len(prefix) + tokens[len(prefix):]))
    if not encoded:
        raise ValueError("no admitted training tokens")
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
    parser.add_argument("--engine", choices=["transformers"], default="transformers")
    parser.add_argument("--configuration-json-file", type=Path)
    parser.add_argument("--expected-azure-resource-id")
    parser.add_argument("--compute-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--probe-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--run-key", required=True)
    parser.add_argument("--configuration-identity", required=True)
    parser.add_argument("--capacity-directory", type=Path, required=True)
    args = parser.parse_args()
    if not args.expected_azure_resource_id:
        parser.error("an explicitly configured Azure worker identity is required")
    # Reuse the serving worker's one host-attestation authority. This runs
    # before model/dataset file inspection, ML imports or child execution.
    spec = importlib.util.spec_from_file_location("legend_controlled_worker_host", Path(__file__).with_name("legend-local-foundation.py"))
    worker = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = worker
    spec.loader.exec_module(worker)
    host_receipt = worker.verify_controlled_azure_host(args.expected_azure_resource_id, "training")
    if not (len(args.run_key) == 64 and all(c in "0123456789abcdef" for c in args.run_key)):
        parser.error("run key must be a lowercase SHA-256 identity")
    if not args.base.is_absolute() or not args.base.is_dir() or not (args.base / "config.json").is_file():
        parser.error("an existing local base checkpoint is required")
    if not args.data.is_absolute() or not args.data.is_file() or not args.output.is_absolute() or not args.capacity_directory.is_absolute():
        parser.error("local absolute dataset and output paths are required")
    if not (len(args.configuration_identity) == 64 and all(c in "0123456789abcdef" for c in args.configuration_identity)):
        parser.error("configuration identity must be SHA-256")
    if sys.platform == "darwin":
        parser.error("controlled Transformers training requires the explicitly configured remote CUDA worker")
    if args.configuration_json_file is None or not args.configuration_json_file.is_absolute():
        parser.error("remote immutable training configuration is required")
    raw = args.configuration_json_file.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.configuration_identity:
        parser.error("training configuration integrity failed")
    settings = json.loads(raw)
    if set(settings) != {"schema", "base_model", "base_revision", "azure_resource_id", "trainer_sha256", "iterations", "learning_rate", "lora_rank", "max_sequence_tokens", "deadline_seconds", "seed"} or settings["schema"] != "controlled-transformers-training-v1":
        parser.error("training configuration schema is invalid")
    if settings["azure_resource_id"] != args.expected_azure_resource_id or settings["trainer_sha256"] != digest(Path(__file__)) or not (1 <= settings["iterations"] <= 2000 and 1 <= settings["lora_rank"] <= 64 and 64 <= settings["max_sequence_tokens"] <= 8192 and 30 <= settings["deadline_seconds"] <= 3600 and 0.000001 <= settings["learning_rate"] <= 0.001 and 0 <= settings["seed"] <= 1000000):
        parser.error("training code identity or resource budget is invalid")
    args.iterations = settings["iterations"]
    args.deadline_seconds = settings["deadline_seconds"]
    args.learning_rate = settings["learning_rate"]
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    if args.compute_child:
        transformers_compute(args, settings)
        return 0
    if args.probe_child:
        transformers_probe(args)
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
        (data_directory / "train.jsonl").write_text("\n".join(training) + "\n", encoding="utf-8")
        adapter = args.output / "adapter"
        child_arguments = [argument for argument in sys.argv[1:] if argument not in ("--compute-child", "--probe-child")]
        command = [sys.executable, str(Path(__file__).resolve()), *child_arguments, "--compute-child"]
        with (args.output / "compute.log").open("w", encoding="utf-8") as log:
            remaining = args.deadline_seconds - (time.monotonic() - deadline_started)
            if remaining <= 0:
                raise TimeoutError("training setup exceeded the compute deadline")
            completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                       timeout=remaining, check=False)
        if completed.returncode:
            raise RuntimeError("controlled training process failed")
        weights_name = "adapter_model.safetensors"
        import torch
        from safetensors.torch import load_file
        tensors = load_file(str(adapter / weights_name))
        if not tensors or not all(torch.isfinite(tensor).all().item() for tensor in tensors.values()):
            raise ValueError("checkpoint contains missing or nonfinite adapter tensors")
        updated = any("lora_b" in name.lower() and torch.any(tensor != 0).item() for name, tensor in tensors.items())
        if not updated:
            raise ValueError("checkpoint contains no trained LoRA update")
        remaining = args.deadline_seconds - (time.monotonic() - deadline_started)
        if remaining <= 0:
            raise TimeoutError("checkpoint probe exceeded the compute deadline")
        # A separate bounded process reloads PEFT after releasing training memory.
        with (args.output / "probe.log").open("w", encoding="utf-8") as log:
            probe_command = [sys.executable, str(Path(__file__).resolve()), *child_arguments, "--probe-child"]
            subprocess.run(probe_command,
                           stdout=log, stderr=subprocess.STDOUT, timeout=remaining, check=True)
        receipt.update(status="succeeded", weightsUpdated=True, usableCheckpoint=True,
                       adapterSha256=digest(adapter / weights_name),
                       adapterConfigSha256=digest(adapter / "adapter_config.json"),
                       engine=args.engine,
                       engineVersion=importlib.metadata.version("transformers"),
                       trainingRows=len(training), developmentRows=0)
        atomic_json(adapter / "checkpoint.json", {
            "model_version": "controlled:" + args.run_key,
            "adapter_format": "peft",
            "azure_resource_id": host_receipt["azure_resource_id"],
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
    except Exception as failure:
        # No private dataset text or arbitrary exception message enters receipt.
        receipt["failureType"] = type(failure).__name__
    receipt["finishedUnixSeconds"] = time.time()
    atomic_json(result_path, receipt)
    return 0 if receipt["status"] == "succeeded" else 1


if __name__ == "__main__":
    raise SystemExit(main())
