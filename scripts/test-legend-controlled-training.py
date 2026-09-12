#!/usr/bin/env python3
"""Model-free admission checks: no training data, model files or compute."""
import contextlib
import importlib.util
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

_spec = importlib.util.spec_from_file_location("legend_controlled_training", Path(__file__).with_name("legend-local-train.py"))
trainer = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trainer)


class ControlledTrainingHostBoundaryTests(unittest.TestCase):
    def arguments(self, *extra):
        return ["trainer", "--base", "/never-read/base", "--data", "/never-read/data", "--output", "/never-write/output",
                "--run-key", "a" * 64, "--configuration-identity", "b" * 64, "--capacity-directory", "/never-write/capacity", *extra]

    def test_retired_mlx_engine_cannot_reach_any_path_or_compute(self):
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
                worker = SimpleNamespace(verify_controlled_azure_host=guard)
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
                guard.assert_called_once_with("fixture-vm", "training")
                for operation in (inspect, read, write, compute):
                    operation.assert_not_called()


if __name__ == "__main__":
    unittest.main()
