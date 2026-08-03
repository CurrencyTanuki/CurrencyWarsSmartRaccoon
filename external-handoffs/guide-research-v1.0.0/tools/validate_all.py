#!/usr/bin/env python3
"""Validate every evidence/playbook JSON with no third-party dependencies.

The validator implements the JSON Schema keywords used by the two frozen
schemas and then applies cross-file and canonical-ID checks. Errors always name
the file, JSON path and reason.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse


EVIDENCE_SCHEMA = "research-evidence.v1"
PLAYBOOK_SCHEMA = "guide-playbook.v1"


@dataclass(frozen=True)
class ValidationError:
    file: Path
    path: str
    reason: str

    def __str__(self) -> str:
        return f"{self.file}: {self.path}: {self.reason}"


def load_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read JSON: {exc}") from exc


def canonical(value) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def matches_type(value, expected: str) -> bool:
    if expected == "null":
        return value is None
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    if expected == "string":
        return isinstance(value, str)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if expected == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    return False


class SchemaValidator:
    def __init__(self, schema: dict, file: Path):
        self.schema = schema
        self.file = file
        self.errors: list[ValidationError] = []

    def add(self, path: str, reason: str) -> None:
        self.errors.append(ValidationError(self.file, path, reason))

    def resolve(self, ref: str) -> dict:
        if not ref.startswith("#/"):
            raise ValueError(f"unsupported external $ref {ref!r}")
        current = self.schema
        for component in ref[2:].split("/"):
            current = current[component.replace("~1", "/").replace("~0", "~")]
        return current

    def validate(self, value, schema: dict | None = None, path: str = "$") -> None:
        schema = self.schema if schema is None else schema
        if "$ref" in schema:
            self.validate(value, self.resolve(schema["$ref"]), path)
            return

        if "const" in schema and value != schema["const"]:
            self.add(path, f"must equal {schema['const']!r}; got {value!r}")
        if "enum" in schema and value not in schema["enum"]:
            self.add(path, f"must be one of {schema['enum']!r}; got {value!r}")

        allowed = schema.get("type")
        if allowed is not None:
            types = [allowed] if isinstance(allowed, str) else allowed
            if not any(matches_type(value, item) for item in types):
                self.add(path, f"must have type {types!r}; got {type(value).__name__}")
                return

        if isinstance(value, str):
            if len(value) < schema.get("minLength", 0):
                self.add(path, f"must contain at least {schema['minLength']} character(s)")
            pattern = schema.get("pattern")
            if pattern and re.fullmatch(pattern, value) is None:
                self.add(path, f"does not match pattern {pattern!r}: {value!r}")
            fmt = schema.get("format")
            if fmt == "uri":
                parsed = urlparse(value)
                if parsed.scheme not in {"http", "https"} or not parsed.netloc:
                    self.add(path, f"must be an absolute HTTP(S) URI; got {value!r}")
            elif fmt == "date":
                try:
                    dt.date.fromisoformat(value)
                except ValueError:
                    self.add(path, f"must be an ISO date; got {value!r}")
            elif fmt == "date-time":
                try:
                    dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
                except ValueError:
                    self.add(path, f"must be an ISO date-time; got {value!r}")

        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if "minimum" in schema and value < schema["minimum"]:
                self.add(path, f"must be >= {schema['minimum']}; got {value}")
            if "maximum" in schema and value > schema["maximum"]:
                self.add(path, f"must be <= {schema['maximum']}; got {value}")

        if isinstance(value, list):
            if len(value) < schema.get("minItems", 0):
                self.add(path, f"must contain at least {schema['minItems']} item(s)")
            if schema.get("uniqueItems"):
                seen: set[str] = set()
                for index, item in enumerate(value):
                    token = canonical(item)
                    if token in seen:
                        self.add(f"{path}[{index}]", "duplicates an earlier array item")
                    seen.add(token)
            item_schema = schema.get("items")
            if item_schema:
                for index, item in enumerate(value):
                    self.validate(item, item_schema, f"{path}[{index}]")

        if isinstance(value, dict):
            properties = schema.get("properties", {})
            for name in schema.get("required", []):
                if name not in value:
                    self.add(path, f"missing required field {name!r}")
            for name, item in value.items():
                child_path = f"{path}.{name}"
                if name in properties:
                    self.validate(item, properties[name], child_path)
                else:
                    additional = schema.get("additionalProperties", True)
                    if additional is False:
                        self.add(child_path, "unknown field is not allowed")
                    elif isinstance(additional, dict):
                        self.validate(item, additional, child_path)


def load_catalogs(root: Path) -> tuple[dict[str, set[str]], list[ValidationError]]:
    errors: list[ValidationError] = []
    index_path = root / "standard-ids/catalog-index.v1.json"
    try:
        index = load_json(index_path)
    except ValueError as exc:
        return {}, [ValidationError(index_path, "$", str(exc))]
    catalogs: dict[str, set[str]] = {}
    for entry in index.get("catalogs", []):
        path = index_path.parent / entry.get("file", "")
        try:
            raw = path.read_bytes()
            actual_hash = hashlib.sha256(raw).hexdigest()
            if actual_hash != entry.get("sha256"):
                errors.append(ValidationError(path, "$", "SHA-256 differs from catalog index"))
            data = json.loads(raw.decode("utf-8-sig"))
            records = data.get("records", [])
            ids = [item.get("id") for item in records if isinstance(item, dict)]
            if len(ids) != entry.get("recordCount"):
                errors.append(ValidationError(path, "$.records", "record count differs from catalog index"))
            if len(ids) != len(set(ids)):
                errors.append(ValidationError(path, "$.records", "contains duplicate IDs"))
            catalogs[entry["catalog"]] = set(ids)
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            errors.append(ValidationError(path, "$", f"cannot load catalog: {exc}"))
    return catalogs, errors


CATALOG_FIELDS = {
    "characterIds": "characters",
    "coreCharacterIds": "characters",
    "optionalCharacterIds": "characters",
    "frontCharacterIds": "characters",
    "backCharacterIds": "characters",
    "benchCharacterIds": "characters",
    "characterId": "characters",
    "equipmentIds": "equipment",
    "substituteEquipmentIds": "equipment",
    "bondIds": "bonds",
    "investmentEnvironmentIds": "investment-environments",
    "investmentStrategyIds": "investment-strategies",
    "enemyAffixIds": "enemy-affixes",
}


def check_standard_ids(value, catalogs: dict[str, set[str]], file: Path, path: str = "$") -> list[ValidationError]:
    errors: list[ValidationError] = []
    if isinstance(value, dict):
        for key, item in value.items():
            child = f"{path}.{key}"
            catalog_name = CATALOG_FIELDS.get(key)
            if catalog_name:
                candidates = item if isinstance(item, list) else [item]
                for index, candidate in enumerate(candidates):
                    item_path = f"{child}[{index}]" if isinstance(item, list) else child
                    if candidate not in catalogs.get(catalog_name, set()):
                        errors.append(
                            ValidationError(file, item_path, f"unknown canonical {catalog_name} ID {candidate!r}")
                        )
            if key == "minimumStarsByCharacterId" and isinstance(item, dict):
                for character_id in item:
                    if character_id not in catalogs.get("characters", set()):
                        errors.append(
                            ValidationError(file, f"{child}.{character_id}", "unknown canonical character ID")
                        )
            errors.extend(check_standard_ids(item, catalogs, file, child))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            errors.extend(check_standard_ids(item, catalogs, file, f"{path}[{index}]"))
    return errors


def discover_documents(root: Path, include_output: bool) -> list[Path]:
    bases = [root / "examples/valid"]
    if include_output:
        bases.extend([root / "output-template/evidence", root / "output-template/playbooks"])
    paths: list[Path] = []
    for base in bases:
        if base.exists():
            paths.extend(base.rglob("*.json"))
    return sorted(paths)


def validate_semantics(
    documents: dict[Path, dict], catalogs: dict[str, set[str]]
) -> list[ValidationError]:
    errors: list[ValidationError] = []
    evidence_sets: dict[str, tuple[Path, dict, set[str]]] = {}
    playbook_ids: dict[str, Path] = {}
    for file, data in documents.items():
        version = data.get("schemaVersion")
        if version == EVIDENCE_SCHEMA:
            evidence_id = data.get("evidenceSetId")
            claim_ids = [item.get("claimId") for item in data.get("claims", [])]
            if evidence_id in evidence_sets:
                errors.append(ValidationError(file, "$.evidenceSetId", f"duplicate ID also used by {evidence_sets[evidence_id][0]}"))
            if len(claim_ids) != len(set(claim_ids)):
                errors.append(ValidationError(file, "$.claims", "claimId values must be unique within an evidence set"))
            evidence_sets[evidence_id] = (file, data, set(claim_ids))
            if data.get("source", {}).get("contentType") == "video":
                for index, claim in enumerate(data.get("claims", [])):
                    if claim.get("topic") != "source_metadata" and claim.get("locator", {}).get("kind") != "timestamp":
                        errors.append(
                            ValidationError(file, f"$.claims[{index}].locator.kind", "video content claim must use a timestamp locator")
                        )
        elif version == PLAYBOOK_SCHEMA:
            guide_id = data.get("guideId")
            if guide_id in playbook_ids:
                errors.append(ValidationError(file, "$.guideId", f"duplicate guideId also used by {playbook_ids[guide_id]}"))
            playbook_ids[guide_id] = file
        errors.extend(check_standard_ids(data, catalogs, file))

    for file, data in documents.items():
        if data.get("schemaVersion") != PLAYBOOK_SCHEMA:
            continue
        action_ids = [item.get("actionId") for item in data.get("actions", [])]
        phase_ids = [item.get("phaseId") for item in data.get("phases", [])]
        if len(action_ids) != len(set(action_ids)):
            errors.append(ValidationError(file, "$.actions", "actionId values must be unique"))
        if len(phase_ids) != len(set(phase_ids)):
            errors.append(ValidationError(file, "$.phases", "phaseId values must be unique"))
        known_actions = set(action_ids)
        known_phases = set(phase_ids)
        for path, refs in collect_named_lists(data, {"actionIds", "thenActionIds", "otherwiseActionIds", "fallbackActionIds"}):
            for index, ref in enumerate(refs):
                if ref not in known_actions:
                    errors.append(ValidationError(file, f"{path}[{index}]", f"unknown actionId {ref!r}"))
        for index, branch in enumerate(data.get("branches", [])):
            phase_ref = branch.get("transitionToPhaseId")
            if phase_ref is not None and phase_ref not in known_phases:
                errors.append(ValidationError(file, f"$.branches[{index}].transitionToPhaseId", f"unknown phaseId {phase_ref!r}"))
        for path, ref in collect_evidence_refs(data):
            evidence_id = ref.get("evidenceSetId")
            claim_id = ref.get("claimId")
            target = evidence_sets.get(evidence_id)
            if target is None:
                errors.append(ValidationError(file, path + ".evidenceSetId", f"unknown evidence set {evidence_id!r}"))
            elif claim_id not in target[2]:
                errors.append(ValidationError(file, path + ".claimId", f"unknown claim {claim_id!r} in {evidence_id!r}"))
    return errors


def collect_named_lists(value, names: set[str], path: str = "$"):
    found = []
    if isinstance(value, dict):
        for key, item in value.items():
            child = f"{path}.{key}"
            if key in names and isinstance(item, list):
                found.append((child, item))
            found.extend(collect_named_lists(item, names, child))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            found.extend(collect_named_lists(item, names, f"{path}[{index}]"))
    return found


def collect_evidence_refs(value, path: str = "$"):
    found = []
    if isinstance(value, dict):
        for key, item in value.items():
            child = f"{path}.{key}"
            if key == "evidenceRefs" and isinstance(item, list):
                found.extend((f"{child}[{index}]", ref) for index, ref in enumerate(item) if isinstance(ref, dict))
            found.extend(collect_evidence_refs(item, child))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            found.extend(collect_evidence_refs(item, f"{path}[{index}]"))
    return found


def validate_document(path: Path, schemas: dict[str, dict]) -> tuple[dict | None, list[ValidationError]]:
    try:
        first = load_json(path)
        second = load_json(path)
    except ValueError as exc:
        return None, [ValidationError(path, "$", str(exc))]
    if canonical(first) != canonical(second):
        return None, [ValidationError(path, "$", "repeated read produced a different canonical result")]
    version = first.get("schemaVersion") if isinstance(first, dict) else None
    schema = schemas.get(version)
    if schema is None:
        return first, [ValidationError(path, "$.schemaVersion", f"unsupported schema version {version!r}")]
    validator = SchemaValidator(schema, path)
    validator.validate(first)
    return first, validator.errors


def validate_invalid_examples(root: Path, schemas: dict[str, dict]) -> tuple[int, list[ValidationError]]:
    directory = root / "examples/invalid"
    failures: list[ValidationError] = []
    rejected = 0
    for path in sorted(directory.rglob("*.json")):
        _, errors = validate_document(path, schemas)
        if errors:
            rejected += 1
        else:
            failures.append(ValidationError(path, "$", "invalid example was unexpectedly accepted"))
    return rejected, failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--include-output", action="store_true", help="also validate JSON placed in output-template")
    args = parser.parse_args()
    root = args.root.resolve()
    schema_paths = {
        EVIDENCE_SCHEMA: root / "schemas/research-evidence.v1.schema.json",
        PLAYBOOK_SCHEMA: root / "schemas/guide-playbook.v1.schema.json",
    }
    schemas = {}
    errors: list[ValidationError] = []
    for version, path in schema_paths.items():
        try:
            schemas[version] = load_json(path)
        except ValueError as exc:
            errors.append(ValidationError(path, "$", str(exc)))
    catalogs, catalog_errors = load_catalogs(root)
    errors.extend(catalog_errors)

    documents: dict[Path, dict] = {}
    for path in discover_documents(root, args.include_output):
        data, document_errors = validate_document(path, schemas)
        errors.extend(document_errors)
        if data is not None and not document_errors:
            documents[path] = data
    errors.extend(validate_semantics(documents, catalogs))
    rejected, invalid_errors = validate_invalid_examples(root, schemas)
    errors.extend(invalid_errors)

    if errors:
        print(f"FAILED: {len(errors)} validation error(s)", file=sys.stderr)
        for error in errors:
            print(f"  {error}", file=sys.stderr)
        return 1
    evidence_count = sum(1 for item in documents.values() if item.get("schemaVersion") == EVIDENCE_SCHEMA)
    playbook_count = sum(1 for item in documents.values() if item.get("schemaVersion") == PLAYBOOK_SCHEMA)
    print("PASS: all JSON validation checks succeeded")
    print(f"  valid evidence files: {evidence_count}")
    print(f"  valid playbook files: {playbook_count}")
    print(f"  deliberately invalid files rejected: {rejected}")
    print(f"  standard ID catalogs loaded: {len(catalogs)}")
    print("  repeated-read determinism: passed for every valid file")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
