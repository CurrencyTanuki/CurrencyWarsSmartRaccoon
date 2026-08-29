from __future__ import annotations

import csv
import hashlib
import io
import json
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

from PIL import Image


ROOT = Path(r"D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808")
ISSUE = ROOT / "audit" / "issues" / "ISSUE-015-equipment-icon-name-source-audit"
EQUIPMENT_DIR = ROOT / "data" / "runtime" / "1.0.0" / "4.4" / "equipment"
JSON_OUTPUT = ISSUE / "remote-icon-verification.json"
CSV_OUTPUT = ISSUE / "remote-icon-verification.csv"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 Chrome/126.0 Safari/537.36 "
    "CurrencyWarsAssetAudit/1.0"
)


def verify(record: dict) -> dict:
    icon = record["icon"]
    local_path = EQUIPMENT_DIR / icon["asset_path"]
    local_bytes = local_path.read_bytes()
    local_hash = hashlib.sha256(local_bytes).hexdigest()
    base = {
        "id": record["id"],
        "name": record["name"],
        "category": record["category"],
        "source_page_url": record["source_extensions"]["source_page_url"],
        "source_url": icon["source_url"],
        "local_path": str(local_path.relative_to(ROOT)).replace("\\", "/"),
        "declared_sha256": icon["sha256"].lower(),
        "local_sha256": local_hash,
        "local_bytes": len(local_bytes),
        "local_width": icon["width"],
        "local_height": icon["height"],
    }

    last_error = None
    for attempt in range(1, 4):
        try:
            request = urllib.request.Request(
                icon["source_url"],
                headers={
                    "User-Agent": USER_AGENT,
                    "Accept": "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
                    "Referer": "https://wiki.biligame.com/",
                },
            )
            with urllib.request.urlopen(request, timeout=30) as response:
                remote_bytes = response.read()
                status = getattr(response, "status", 200)
                content_type = response.headers.get("Content-Type", "")
                final_url = response.geturl()
            with Image.open(io.BytesIO(remote_bytes)) as image:
                width, height = image.size
                image_format = image.format or ""
            remote_hash = hashlib.sha256(remote_bytes).hexdigest()
            return {
                **base,
                "http_status": status,
                "content_type": content_type,
                "final_url": final_url,
                "remote_sha256": remote_hash,
                "remote_bytes": len(remote_bytes),
                "remote_width": width,
                "remote_height": height,
                "remote_format": image_format,
                "declared_matches_local": icon["sha256"].lower() == local_hash,
                "remote_matches_local": remote_hash == local_hash,
                "dimensions_match": (width, height) == (icon["width"], icon["height"]),
                "error": "",
                "attempts": attempt,
            }
        except (OSError, ValueError, urllib.error.URLError) as error:
            last_error = f"{type(error).__name__}: {error}"
            time.sleep(0.5 * attempt)

    return {
        **base,
        "http_status": None,
        "content_type": "",
        "final_url": "",
        "remote_sha256": "",
        "remote_bytes": None,
        "remote_width": None,
        "remote_height": None,
        "remote_format": "",
        "declared_matches_local": icon["sha256"].lower() == local_hash,
        "remote_matches_local": False,
        "dimensions_match": False,
        "error": last_error or "unknown error",
        "attempts": 3,
    }


def main() -> None:
    payload = json.loads((EQUIPMENT_DIR / "equipment.json").read_text(encoding="utf-8"))
    records = payload["records"]
    results: list[dict] = []
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = [pool.submit(verify, record) for record in records]
        for index, future in enumerate(as_completed(futures), start=1):
            result = future.result()
            results.append(result)
            print(
                f"[{index:03d}/{len(records)}] {result['id']} "
                f"status={result['http_status']} match={result['remote_matches_local']}"
            )

    results.sort(key=lambda item: item["id"])
    JSON_OUTPUT.write_text(
        json.dumps(results, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    with CSV_OUTPUT.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(results[0]))
        writer.writeheader()
        writer.writerows(results)

    summary = {
        "records": len(results),
        "http_success": sum(item["http_status"] == 200 for item in results),
        "remote_matches_local": sum(item["remote_matches_local"] for item in results),
        "dimensions_match": sum(item["dimensions_match"] for item in results),
        "errors": sum(bool(item["error"]) for item in results),
        "mismatched_ids": [
            item["id"] for item in results if not item["remote_matches_local"]
        ],
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
