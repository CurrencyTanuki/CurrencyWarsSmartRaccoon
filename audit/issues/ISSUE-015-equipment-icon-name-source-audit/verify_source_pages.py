from __future__ import annotations

import csv
import html
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(r"D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808")
ISSUE = ROOT / "audit" / "issues" / "ISSUE-015-equipment-icon-name-source-audit"
EQUIPMENT_DIR = ROOT / "data" / "runtime" / "1.0.0" / "4.4" / "equipment"
JSON_OUTPUT = ISSUE / "source-page-verification.json"
CSV_OUTPUT = ISSUE / "source-page-verification.csv"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 Chrome/126.0 Safari/537.36 "
    "CurrencyWarsAssetAudit/1.0"
)


def normalize(value: str) -> str:
    return re.sub(r"\s+", "", html.unescape(value)).replace("·", "•")


def verify(record: dict) -> dict:
    page_url = record["source_extensions"]["source_page_url"]
    image_url = record["icon"]["source_url"]
    image_basename = Path(urllib.parse.urlparse(image_url).path).stem
    last_error = None
    for attempt in range(1, 4):
        try:
            request = urllib.request.Request(
                page_url,
                headers={"User-Agent": USER_AGENT, "Accept": "text/html,*/*;q=0.8"},
            )
            with urllib.request.urlopen(request, timeout=30) as response:
                body_bytes = response.read()
                status = getattr(response, "status", 200)
                content_type = response.headers.get("Content-Type", "")
                final_url = response.geturl()
            charset_match = re.search(r"charset=([\w-]+)", content_type, re.I)
            charset = charset_match.group(1) if charset_match else "utf-8"
            body = body_bytes.decode(charset, errors="replace")
            decoded = html.unescape(body)
            title_match = re.search(r"<title>(.*?)</title>", decoded, re.I | re.S)
            title = re.sub(r"\s+", " ", title_match.group(1)).strip() if title_match else ""
            normalized_name = normalize(record["name"])
            return {
                "id": record["id"],
                "name": record["name"],
                "category": record["category"],
                "source_page_url": page_url,
                "final_url": final_url,
                "http_status": status,
                "content_type": content_type,
                "bytes": len(body_bytes),
                "title": title,
                "title_contains_name": normalized_name in normalize(title),
                "body_contains_name": normalized_name in normalize(decoded),
                "body_references_image_basename": image_basename in decoded,
                "source_revision_id": record["source_extensions"]["source_revision_id"],
                "fetched_at_utc": datetime.now(timezone.utc).isoformat(),
                "attempts": attempt,
                "error": "",
            }
        except (OSError, ValueError, urllib.error.URLError) as error:
            last_error = f"{type(error).__name__}: {error}"
            time.sleep(0.5 * attempt)

    return {
        "id": record["id"],
        "name": record["name"],
        "category": record["category"],
        "source_page_url": page_url,
        "final_url": "",
        "http_status": None,
        "content_type": "",
        "bytes": None,
        "title": "",
        "title_contains_name": False,
        "body_contains_name": False,
        "body_references_image_basename": False,
        "source_revision_id": record["source_extensions"]["source_revision_id"],
        "fetched_at_utc": datetime.now(timezone.utc).isoformat(),
        "attempts": 3,
        "error": last_error or "unknown error",
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
                f"status={result['http_status']} "
                f"name={result['title_contains_name']} "
                f"image={result['body_references_image_basename']}"
            )

    results.sort(key=lambda item: item["id"])
    JSON_OUTPUT.write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    with CSV_OUTPUT.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(results[0]))
        writer.writeheader()
        writer.writerows(results)

    summary = {
        "records": len(results),
        "http_success": sum(item["http_status"] == 200 for item in results),
        "title_contains_name": sum(item["title_contains_name"] for item in results),
        "body_contains_name": sum(item["body_contains_name"] for item in results),
        "body_references_image_basename": sum(
            item["body_references_image_basename"] for item in results
        ),
        "errors": sum(bool(item["error"]) for item in results),
        "exceptions": [
            {
                "id": item["id"],
                "name": item["name"],
                "title": item["title"],
                "name_match": item["title_contains_name"],
                "image_match": item["body_references_image_basename"],
                "error": item["error"],
            }
            for item in results
            if item["http_status"] != 200
            or not item["title_contains_name"]
            or not item["body_contains_name"]
            or not item["body_references_image_basename"]
        ],
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
