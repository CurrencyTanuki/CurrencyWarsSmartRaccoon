#!/usr/bin/env python3
"""Export compact, canonical Currency Wars identifiers from the current project.

This maintainer tool is not needed by the external researcher after the package
has been built. It deliberately exports identifiers and short classification
metadata only; it does not copy images or long source text.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


PACKAGE_VERSION = "guide-research-v1.0.0"
GAME_VERSION = "4.4"


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "standard-ids",
    )
    args = parser.parse_args()
    source_root = args.source_root.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    source_files = {
        "characters": source_root / "data/4.4/currency-wars-characters.json",
        "equipment": source_root / "data/runtime/1.0.0/4.4/equipment/equipment.json",
        "investment-environments": source_root / "data/4.4/investment-environments.json",
        "investment-strategies": source_root / "data/4.4/investment-strategies.json",
        "enemy-affixes": source_root / "data/4.4/enemy-affixes.json",
        "bond-states": source_root / "data/4.4/phase2-icon-assets/source-taxonomy.json",
    }
    missing = [str(path) for path in source_files.values() if not path.is_file()]
    if missing:
        raise SystemExit("Missing source catalog(s):\n" + "\n".join(missing))

    character_data = read_json(source_files["characters"])
    bond_by_name = {
        item["name"]: item["id"] for item in character_data["bond_catalog"]
    }
    characters = [
        {
            "id": item["id"],
            "name": item["name"],
            "position": item.get("position", "unknown"),
            "costs": item.get("costs", []),
            "bondIds": [
                bond_by_name[name] for name in item.get("bonds", []) if name in bond_by_name
            ],
        }
        for item in character_data["characters"]
    ]
    bonds = [
        {
            "id": item["id"],
            "name": item["name"],
            "type": item.get("type", "unknown"),
            "activationThresholds": [
                tier.get("required_members")
                for tier in item.get("tier_effects", [])
                if isinstance(tier.get("required_members"), int)
            ],
        }
        for item in character_data["bond_catalog"]
    ]
    equipment_data = read_json(source_files["equipment"])
    equipment = [
        {
            "id": item["id"],
            "name": item["name"],
            "category": item.get("category", "unknown"),
            "equippable": bool(item.get("equippable", False)),
            "occupiesEquipmentSlot": bool(item.get("occupies_equipment_slot", False)),
        }
        for item in equipment_data["records"]
    ]
    environments = [
        {"id": item["id"], "name": item["name"]}
        for item in read_json(source_files["investment-environments"])
    ]
    strategies = [
        {
            "id": item["id"],
            "name": item["name"],
            "rarity": item.get("rarity", "unknown"),
            "availablePlanes": item.get("available_planes", []),
        }
        for item in read_json(source_files["investment-strategies"])
    ]
    affixes = [
        {"id": item["id"], "name": item["name"], "tier": item.get("tier")}
        for item in read_json(source_files["enemy-affixes"])
    ]
    state_data = read_json(source_files["bond-states"])
    bond_states = [
        {
            "id": item["id"],
            "name": item["name"],
            "population": item.get("population"),
            "available": item.get("available", False),
        }
        for item in state_data["bond_matrix"]
    ]

    datasets = {
        "characters": characters,
        "equipment": equipment,
        "bonds": bonds,
        "bond-states": bond_states,
        "investment-environments": environments,
        "investment-strategies": strategies,
        "enemy-affixes": affixes,
    }
    index_entries = []
    for name, records in datasets.items():
        target = output / f"{name}.json"
        write_json(
            target,
            {
                "schemaVersion": "standard-id-catalog.v1",
                "gameVersion": GAME_VERSION,
                "catalog": name,
                "records": sorted(records, key=lambda item: item["id"]),
            },
        )
        index_entries.append(
            {
                "catalog": name,
                "file": target.name,
                "recordCount": len(records),
                "sha256": sha256(target),
            }
        )

    write_json(
        output / "catalog-index.v1.json",
        {
            "schemaVersion": "standard-id-index.v1",
            "packageVersion": PACKAGE_VERSION,
            "gameVersion": GAME_VERSION,
            "sourceFiles": {
                name: {
                    "projectRelativePath": str(path.relative_to(source_root)).replace("\\", "/"),
                    "sha256": sha256(path),
                }
                for name, path in source_files.items()
            },
            "catalogs": sorted(index_entries, key=lambda item: item["catalog"]),
        },
    )
    print("Generated standard ID catalogs:")
    for item in sorted(index_entries, key=lambda entry: entry["catalog"]):
        print(f"  {item['catalog']}: {item['recordCount']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
