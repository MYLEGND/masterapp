#!/usr/bin/env python3
"""Model-free admission checks: no training data, model files or compute."""
import contextlib
import importlib.util
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
import tempfile
from unittest.mock import Mock, patch

_spec = importlib.util.spec_from_file_location("legend_controlled_training", Path(__file__).with_name("legend-local-train.py"))
trainer = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trainer)


class ControlledTrainingHostBoundaryTests(unittest.TestCase):
    def arguments(self, *extra):
        return ["trainer", "--base", "/never-read/base", "--data", "/never-read/data", "--output", "/never-write/output",
                "--run-key", "a" * 64, "--configuration-identity", "b" * 64, "--capacity-directory", "/never-write/capacity", *extra]

    def test_mlx_without_explicit_founder_host_cannot_reach_any_path_or_compute(self):
        with patch.object(trainer.sys, "argv", self.arguments("--engine", "mlx")), \
                patch.object(Path, "is_dir", side_effect=AssertionError("path access before authorization")) as paths, \
                contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as rejected:
            trainer.main()
        self.assertEqual(2, rejected.exception.code)
        paths.assert_not_called()

    def test_missing_host_identity_cannot_reach_any_path(self):
        with patch.object(trainer.sys, "argv", self.arguments()), \
                patch.object(Path, "is_dir", side_effect=AssertionError("path access before authorization")) as paths, \
                contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as rejected:
            trainer.main()
        self.assertEqual(2, rejected.exception.code)
        paths.assert_not_called()

    def test_parent_compute_and_probe_all_use_shared_guard_before_artifact_access(self):
        for mode in ([], ["--compute-child"], ["--probe-child"]):
            with self.subTest(mode=mode):
                guard = Mock(side_effect=PermissionError("fixture host rejected"))
                worker = SimpleNamespace(verify_controlled_host=guard)
                spec = SimpleNamespace(name="fixture_guard", loader=SimpleNamespace(exec_module=Mock()))
                with patch.object(trainer.sys, "argv", self.arguments("--expected-azure-resource-id", "fixture-vm", *mode)), \
                        patch.object(trainer.importlib.util, "spec_from_file_location", return_value=spec), \
                        patch.object(trainer.importlib.util, "module_from_spec", return_value=worker), \
                        patch.dict(trainer.sys.modules), \
                        patch.object(Path, "is_dir", side_effect=AssertionError("model inspection")) as inspect, \
                        patch.object(Path, "read_bytes", side_effect=AssertionError("artifact read")) as read, \
                        patch.object(Path, "mkdir", side_effect=AssertionError("artifact write")) as write, \
                        patch.object(trainer.subprocess, "run", side_effect=AssertionError("compute execution")) as compute, \
                        self.assertRaises(PermissionError):
                    trainer.main()
                guard.assert_called_once_with("AzureVm", "training", "fixture-vm", None)
                for operation in (inspect, read, write, compute):
                    operation.assert_not_called()

    def test_mac_parent_compute_and_probe_use_shared_guard_before_artifact_access(self):
        for mode in ([], ["--compute-child"], ["--probe-child"]):
            with self.subTest(mode=mode):
                guard = Mock(side_effect=PermissionError("fixture Mac rejected"))
                worker = SimpleNamespace(verify_controlled_host=guard)
                spec = SimpleNamespace(name="fixture_guard", loader=SimpleNamespace(exec_module=Mock()))
                with patch.object(trainer.sys, "argv", self.arguments("--engine", "mlx", "--host-kind", "FounderMac",
                        "--expected-mac-host-id", "a" * 64, "--founder-id", "22222222-2222-2222-2222-222222222222", *mode)), \
                        patch.object(trainer.importlib.util, "spec_from_file_location", return_value=spec), \
                        patch.object(trainer.importlib.util, "module_from_spec", return_value=worker), \
                        patch.dict(trainer.sys.modules), \
                        patch.object(Path, "is_dir", side_effect=AssertionError("model inspection")) as inspect, \
                        patch.object(Path, "read_bytes", side_effect=AssertionError("artifact read")) as read, \
                        patch.object(Path, "mkdir", side_effect=AssertionError("artifact write")) as write, \
                        patch.object(trainer.subprocess, "run", side_effect=AssertionError("compute execution")) as compute, \
                        self.assertRaises(PermissionError):
                    trainer.main()
                guard.assert_called_once_with("FounderMac", "training", None, "a" * 64)
                for operation in (inspect, read, write, compute):
                    operation.assert_not_called()

    def test_mac_recipe_rejects_wrong_owner_legacy_schema_and_unsafe_budgets(self):
        args = SimpleNamespace(engine="mlx", host_kind="FounderMac", expected_mac_host_id="a" * 64,
                               founder_id="22222222-2222-2222-2222-222222222222")
        settings = dict(schema="controlled-mlx-training-v1", base_model="fixture", base_revision="a" * 40,
                        host_kind="FounderMac", mac_host_id=args.expected_mac_host_id, founder_id=args.founder_id,
                        trainer_sha256="b" * 64, iterations=100, learning_rate=0.00005, lora_rank=8,
                        max_sequence_tokens=512, deadline_seconds=600, seed=73)
        trainer.validate_recipe(settings, args, "b" * 64)
        for field, invalid in (("schema", "local-mlx-v1"), ("founder_id", "33333333-3333-3333-3333-333333333333"),
                               ("max_sequence_tokens", 2049), ("iterations", True), ("learning_rate", float("nan")),
                               ("deadline_seconds", 3601), ("trainer_sha256", "c" * 64), ("mac_host_id", "c" * 64)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                trainer.validate_recipe(dict(settings, **{field: invalid}), args, "b" * 64)

    def test_child_log_bound_and_deadline_do_not_leave_running_compute(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(engine="mlx", output=Path(directory),
                                   storage_authority=SimpleNamespace(verify_training_output_budget=Mock(return_value=0)))
            log = args.output / "bounded.log"
            with self.assertRaisesRegex(ValueError, "controlled_training_log_capacity"):
                trainer.run_bounded_child(args, [trainer.sys.executable, "-B", "-c", "import sys; sys.stdout.write('x' * 2097152)"], log, 5)
            self.assertLessEqual(log.stat().st_size, 1048576)
            with self.assertRaises(TimeoutError):
                trainer.run_bounded_child(args, [trainer.sys.executable, "-B", "-c", "import time; time.sleep(5)"], log, 0.1)
            self.assertGreater(args.storage_authority.verify_training_output_budget.call_count, 0)

    def test_target_mask_excludes_prompt_and_padding_and_retains_first_target(self):
        self.assertEqual([False, True, True, False, False],
                         [trainer.admitted_target_mask(index, 2, 4) for index in range(1, 6)])

    def test_exact_prompt_masking_rejects_prefix_rewrite_and_truncation(self):
        args = SimpleNamespace(output=Path("/not-written"))
        row = '{"messages":[{"role":"user","content":"fixture"},{"role":"assistant","content":"target"}]}\n'
        for tokens, prefix, limit, accepted in (([1, 2, 3], [1, 2], 3, True),
                                                ([1, 9, 3], [1, 2], 3, False),
                                                ([1, 2, 3], [1, 2], 2, False)):
            tokenizer = SimpleNamespace(apply_chat_template=Mock(side_effect=[tokens, prefix]))
            with self.subTest(tokens=tokens, limit=limit), patch.object(Path, "read_text", return_value=row):
                if accepted:
                    self.assertEqual([(tokens, 2)], trainer.encode_admitted_rows(args, {"max_sequence_tokens": limit}, tokenizer))
                else:
                    with self.assertRaises(ValueError):
                        trainer.encode_admitted_rows(args, {"max_sequence_tokens": limit}, tokenizer)
            for call in tokenizer.apply_chat_template.call_args_list:
                self.assertFalse(call.kwargs["enable_thinking"])


if __name__ == "__main__":
    unittest.main()
