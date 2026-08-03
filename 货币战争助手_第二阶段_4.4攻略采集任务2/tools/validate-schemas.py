#!/usr/bin/env python3
"""Dependency-free validator for the JSON Schema keywords used by this delivery.

This is intentionally a bounded offline validator, not a replacement for a general
Draft 2020-12 implementation. It supports every keyword present in schemas/1.0.0
and adds the Observed<T> semantic invariant that JSON Schema alone cannot express
concisely. A full validator can be used independently against the same schemas.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path
from urllib.parse import urljoin, urlparse


class ValidationFailure(Exception):
    pass


def load_json(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


class OfflineSchemaValidator:
    def __init__(self, schema_dir: Path):
        self.schema_dir = schema_dir
        self.by_name = {path.name: load_json(path) for path in schema_dir.glob("*.schema.json")}
        self.by_id = {schema.get("$id"): schema for schema in self.by_name.values() if schema.get("$id")}

    def validate(self, instance, schema_name: str):
        schema = self.by_name[schema_name]
        self._validate(instance, schema, schema.get("$id", schema_name), "$")
        self._validate_observed_semantics(instance, "$")

    def _resolve(self, reference: str, base_uri: str):
        document_ref, _, fragment = reference.partition("#")
        if document_ref:
            resolved_uri = urljoin(base_uri, document_ref)
            schema = self.by_id.get(resolved_uri) or self.by_name.get(Path(document_ref).name)
        else:
            schema = self.by_id.get(base_uri) or self.by_name.get(Path(urlparse(base_uri).path).name)
        if schema is None:
            raise ValidationFailure(f"unresolved $ref {reference!r} from {base_uri!r}")
        target = schema
        if fragment:
            for part in fragment.lstrip("/").split("/"):
                part = part.replace("~1", "/").replace("~0", "~")
                target = target[part]
        return target, schema.get("$id", base_uri)

    def _validate(self, value, schema, base_uri: str, path: str):
        if "$ref" in schema:
            target, target_base = self._resolve(schema["$ref"], base_uri)
            self._validate(value, target, target_base, path)
        for child in schema.get("allOf", []):
            self._validate(value, child, base_uri, path)
        if "const" in schema and value != schema["const"]:
            self._fail(path, f"expected constant {schema['const']!r}")
        if "enum" in schema and value not in schema["enum"]:
            self._fail(path, f"value {value!r} is not in enum")
        expected = schema.get("type")
        if expected and not self._is_type(value, expected):
            self._fail(path, f"expected type {expected}, got {type(value).__name__}")
        if isinstance(value, dict):
            required = schema.get("required", [])
            for name in required:
                if name not in value:
                    self._fail(path, f"missing required property {name!r}")
            properties = schema.get("properties", {})
            for name, child_value in value.items():
                if name in properties:
                    self._validate(child_value, properties[name], base_uri, f"{path}.{name}")
                elif schema.get("additionalProperties") is False:
                    self._fail(path, f"unexpected property {name!r}")
        if isinstance(value, list):
            if len(value) < schema.get("minItems", 0):
                self._fail(path, "array has too few items")
            if "maxItems" in schema and len(value) > schema["maxItems"]:
                self._fail(path, "array has too many items")
            if schema.get("uniqueItems") and len({json.dumps(item, sort_keys=True, ensure_ascii=False) for item in value}) != len(value):
                self._fail(path, "array items are not unique")
            if "items" in schema:
                for index, item in enumerate(value):
                    self._validate(item, schema["items"], base_uri, f"{path}[{index}]")
        if isinstance(value, str):
            if len(value) < schema.get("minLength", 0):
                self._fail(path, "string is too short")
            if "pattern" in schema and re.search(schema["pattern"], value) is None:
                self._fail(path, f"string does not match {schema['pattern']!r}")
            if schema.get("format") == "uri" and not urlparse(value).scheme:
                self._fail(path, "string is not an absolute URI")
            if schema.get("format") == "date-time":
                try:
                    datetime.fromisoformat(value.replace("Z", "+00:00"))
                except ValueError as error:
                    self._fail(path, f"invalid date-time: {error}")
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if "minimum" in schema and value < schema["minimum"]:
                self._fail(path, "number is below minimum")
            if "maximum" in schema and value > schema["maximum"]:
                self._fail(path, "number is above maximum")

    def _validate_observed_semantics(self, value, path: str):
        if isinstance(value, dict):
            status = value.get("status")
            if status in {"known", "unknown", "conflict", "stale"} and "confidence" in value:
                if status == "known" and "value" not in value:
                    self._fail(path, "known observation must contain value")
                if status != "known" and not value.get("uncertainty"):
                    self._fail(path, "non-known observation must explain uncertainty")
            for name, child in value.items():
                self._validate_observed_semantics(child, f"{path}.{name}")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                self._validate_observed_semantics(child, f"{path}[{index}]")

    @staticmethod
    def _is_type(value, expected):
        return {
            "object": isinstance(value, dict),
            "array": isinstance(value, list),
            "string": isinstance(value, str),
            "integer": isinstance(value, int) and not isinstance(value, bool),
            "number": isinstance(value, (int, float)) and not isinstance(value, bool),
            "boolean": isinstance(value, bool),
            "null": value is None,
        }[expected]

    @staticmethod
    def _fail(path, message):
        raise ValidationFailure(f"{path}: {message}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    root = args.root.resolve()
    validator = OfflineSchemaValidator(root / "schemas" / "1.0.0")
    valid_dir = root / "samples" / "valid"
    invalid_dir = root / "samples" / "invalid"
    expected = [
        "source-record", "guide-raw", "guide-playbook", "guide-runtime", "run-event",
        "run-snapshot", "node-record", "recommendation", "situation-report",
        "run-summary-report", "screenshot-analysis-request", "analysis-result",
    ]
    failures = []
    for name in expected:
        schema_name = f"{name}.schema.json"
        try:
            validator.validate(load_json(valid_dir / f"{name}.valid.json"), schema_name)
        except Exception as error:  # report all cases in one run
            failures.append(f"VALID sample failed: {name}: {error}")
        try:
            validator.validate(load_json(invalid_dir / f"{name}.invalid.json"), schema_name)
            failures.append(f"INVALID sample unexpectedly passed: {name}")
        except ValidationFailure:
            pass
    if failures:
        print("\n".join(failures), file=sys.stderr)
        return 1
    print(f"Schema validation passed: {len(expected)} valid samples accepted; {len(expected)} invalid samples rejected.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
