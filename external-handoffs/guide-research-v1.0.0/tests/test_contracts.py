#!/usr/bin/env python3

from __future__ import annotations

import json
import subprocess
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class HandoffContractTests(unittest.TestCase):
    def test_full_validator_passes(self):
        result = subprocess.run(
            [sys.executable, str(ROOT / "tools/validate_all.py"), "--root", str(ROOT), "--include-output"],
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("deliberately invalid files rejected: 2", result.stdout)

    def test_valid_examples_repeat_read_identically(self):
        for path in sorted((ROOT / "examples/valid").glob("*.json")):
            first = json.loads(path.read_text(encoding="utf-8-sig"))
            second = json.loads(path.read_text(encoding="utf-8-sig"))
            self.assertEqual(first, second, path)

    def test_incomplete_and_conflict_are_preserved(self):
        evidence = json.loads(
            (ROOT / "examples/valid/02-incomplete-conflicted.research-evidence.v1.json").read_text(encoding="utf-8")
        )
        self.assertEqual("partial", evidence["extractionStatus"])
        self.assertTrue(evidence["unknowns"])
        self.assertEqual("unresolved", evidence["claims"][1]["conflicts"][0]["status"])
        playbook = json.loads(
            (ROOT / "examples/valid/05-incomplete-conflicted.guide-playbook.v1.json").read_text(encoding="utf-8")
        )
        self.assertEqual("conflicted", playbook["status"])
        self.assertEqual("never_auto_execute", playbook["missingInformationPolicy"]["highRiskDecisionBehavior"])

    def test_valid_playbooks_are_declarative(self):
        forbidden_keys = {"executeCode", "script", "javascript", "powershell", "python", "eval", "command"}

        def walk(value):
            if isinstance(value, dict):
                self.assertFalse(forbidden_keys.intersection(value), value)
                for item in value.values():
                    walk(item)
            elif isinstance(value, list):
                for item in value:
                    walk(item)

        for path in sorted((ROOT / "examples/valid").glob("*.guide-playbook.v1.json")):
            walk(json.loads(path.read_text(encoding="utf-8")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
