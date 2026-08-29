from __future__ import annotations

import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path


PROJECT = Path(r"D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808")
HANDOFF = Path(r"C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808")
ISSUE = PROJECT / "audit" / "issues" / "ISSUE-015-equipment-icon-name-source-audit"
RELATIVE_RUNTIME = Path("data/runtime/1.0.0/4.4/equipment")
LOCATIONS = {
    "work": PROJECT / RELATIVE_RUNTIME,
    "handoff_source": HANDOFF / "源代码" / RELATIVE_RUNTIME,
    "handoff_program": HANDOFF / "程序" / RELATIVE_RUNTIME,
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


payloads = {
    name: json.loads((root / "equipment.json").read_text(encoding="utf-8"))
    for name, root in LOCATIONS.items()
}
records = payloads["work"]["records"]
ids = [record["id"] for record in records]
names = [record["name"] for record in records]
id_set = set(ids)
declared_paths = {record["icon"]["asset_path"] for record in records}

rows: list[dict[str, object]] = []
hash_groups: dict[str, list[str]] = defaultdict(list)
for record in records:
    icon_relative = Path(record["icon"]["asset_path"])
    location_hashes: dict[str, str | None] = {}
    location_exists: dict[str, bool] = {}
    for location, root in LOCATIONS.items():
        path = root / icon_relative
        location_exists[location] = path.is_file()
        location_hashes[location] = sha256(path) if path.is_file() else None
    work_hash = location_hashes["work"]
    if work_hash:
        hash_groups[work_hash].append(record["id"])
    rows.append(
        {
            "id": record["id"],
            "name": record["name"],
            "category": record["category"],
            "icon_path": record["icon"]["asset_path"],
            "declared_sha256": record["icon"]["sha256"],
            "work_exists": location_exists["work"],
            "work_sha256": work_hash,
            "handoff_source_exists": location_exists["handoff_source"],
            "handoff_source_sha256": location_hashes["handoff_source"],
            "handoff_program_exists": location_exists["handoff_program"],
            "handoff_program_sha256": location_hashes["handoff_program"],
            "all_three_match_declared": bool(
                work_hash
                and work_hash == record["icon"]["sha256"]
                and location_hashes["handoff_source"] == work_hash
                and location_hashes["handoff_program"] == work_hash
            ),
            "file_name_matches_id": icon_relative.stem == record["id"],
            "base_equipment_id": record.get("base_equipment_id"),
            "base_reference_exists": record.get("base_equipment_id") is None
            or record.get("base_equipment_id") in id_set,
            "component_ids": record.get("component_ids", []),
            "component_references_exist": all(component in id_set for component in record.get("component_ids", [])),
        }
    )

actual_icons = {
    str(path.relative_to(LOCATIONS["work"])).replace("\\", "/")
    for path in (LOCATIONS["work"] / "assets" / "currency_wars_equipment_icons").glob("*.png")
}
numeric_ids = sorted(int(record_id.rsplit("_", 1)[-1]) for record_id in ids)
expected_numeric = set(range(min(numeric_ids), max(numeric_ids) + 1))
missing_numeric = sorted(expected_numeric - set(numeric_ids))

summary = {
    "equipment_json_sha256": {name: sha256(root / "equipment.json") for name, root in LOCATIONS.items()},
    "record_count": len(records),
    "unique_id_count": len(set(ids)),
    "unique_name_count": len(set(names)),
    "declared_icon_count": len(declared_paths),
    "actual_icon_count": len(actual_icons),
    "missing_icon_paths": sorted(declared_paths - actual_icons),
    "orphan_icon_paths": sorted(actual_icons - declared_paths),
    "duplicate_ids": sorted(item for item, count in Counter(ids).items() if count > 1),
    "duplicate_names": sorted(item for item, count in Counter(names).items() if count > 1),
    "duplicate_paths": sorted(item for item, count in Counter(record["icon"]["asset_path"] for record in records).items() if count > 1),
    "filename_id_mismatch_count": sum(not bool(row["file_name_matches_id"]) for row in rows),
    "all_three_hash_match_count": sum(bool(row["all_three_match_declared"]) for row in rows),
    "base_reference_count": sum(record.get("base_equipment_id") is not None for record in records),
    "missing_base_reference_count": sum(not bool(row["base_reference_exists"]) for row in rows),
    "component_reference_count": sum(len(record.get("component_ids", [])) for record in records),
    "missing_component_reference_count": sum(not bool(row["component_references_exist"]) for row in rows),
    "numeric_id_min": min(numeric_ids),
    "numeric_id_max": max(numeric_ids),
    "missing_numeric_ids": missing_numeric,
    "unique_icon_hashes": len(hash_groups),
    "duplicate_hash_clusters": sum(len(group) > 1 for group in hash_groups.values()),
    "duplicate_hash_members": sum(len(group) for group in hash_groups.values() if len(group) > 1),
    "category_counts": dict(sorted(Counter(record["category"] for record in records).items())),
}

json_path = ISSUE / "local-asset-verification.json"
csv_path = ISSUE / "local-asset-verification.csv"
json_path.write_text(json.dumps({"summary": summary, "rows": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=list(rows[0].keys()))
    writer.writeheader()
    for row in rows:
        normalized = dict(row)
        normalized["component_ids"] = ";".join(row["component_ids"])
        writer.writerow(normalized)

print(json.dumps(summary, ensure_ascii=False, indent=2))

