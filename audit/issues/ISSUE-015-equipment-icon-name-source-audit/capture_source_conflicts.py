from __future__ import annotations

import hashlib
import html as html_module
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path


ISSUE = Path(__file__).resolve().parent
OUTPUT = ISSUE / "web-evidence"
OUTPUT.mkdir(parents=True, exist_ok=True)
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0 Safari/537.36"
)


def fetch(url: str, *, referer: str | None = None) -> bytes:
    headers = {"User-Agent": USER_AGENT, "Accept": "application/json,text/html;q=0.9,*/*;q=0.8"}
    if referer:
        headers["Referer"] = referer
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=45) as response:
        return response.read()


def write_bytes(name: str, payload: bytes) -> dict[str, object]:
    path = OUTPUT / name
    path.write_bytes(payload)
    return {
        "file": str(path),
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest().upper(),
    }


def strip_tags(fragment: str) -> str:
    fragment = re.sub(r"<br\s*/?>", "\n", fragment, flags=re.I)
    fragment = re.sub(r"<[^>]+>", "", fragment)
    fragment = html_module.unescape(fragment)
    return re.sub(r"[ \t\r\f\v]+", " ", fragment).strip()


summary: dict[str, object] = {"captured_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}

# 固定BWIKI总表修订，避免当前页后续变化；Referer可降低567响应概率。
bwiki_api = (
    "https://wiki.biligame.com/sr/api.php?action=parse&format=json&oldid=99085"
    "&prop=text%7Ctitle%7Crevid"
)
bwiki_bytes = fetch(
    bwiki_api,
    referer="https://wiki.biligame.com/sr/%E8%A3%85%E5%A4%87%E4%B8%80%E8%A7%88",
)
summary["bwiki_api"] = {"url": bwiki_api, **write_bytes("bwiki-equipment-oldid-99085.json", bwiki_bytes)}
bwiki_payload = json.loads(bwiki_bytes.decode("utf-8"))
bwiki_html = bwiki_payload["parse"]["text"]["*"]
divsort_rows = re.findall(r"<tr\b[^>]*class=[\"'][^\"']*\bdivsort\b[^\"']*[\"'][^>]*>(.*?)</tr>", bwiki_html, flags=re.I | re.S)
row_texts = [strip_tags(row) for row in divsort_rows]
wealth_rows = [
    text
    for text in row_texts
    if next((line.strip() for line in text.splitlines() if line.strip()), "") == "财富"
]
expert_rows = [text for text in row_texts if "专家邀请函" in text]
summary["bwiki_parse"] = {
    "revision_id": bwiki_payload["parse"].get("revid"),
    "title": bwiki_payload["parse"].get("title"),
    "divsort_row_count": len(row_texts),
    "wealth_row_count": len(wealth_rows),
    "wealth_rows": wealth_rows,
    "expert_invitation_row_count": len(expert_rows),
    "expert_invitation_rows": expert_rows,
    "contains_star_emblem_tome": "星徽秘典" in strip_tags(bwiki_html),
}

# 独立社区来源，用于确认Wealth双条并非BWIKI单站偶发。
fandom_api = (
    "https://honkai-star-rail.fandom.com/api.php?action=parse"
    "&page=Currency_Wars%3A_Zero-Sum_Game%2FEquipment"
    "&prop=text%7Cdisplaytitle%7Crevid&format=json&origin=*"
)
fandom_bytes = fetch(fandom_api)
summary["fandom_api"] = {"url": fandom_api, **write_bytes("fandom-equipment-current.json", fandom_bytes)}
fandom_payload = json.loads(fandom_bytes.decode("utf-8"))
fandom_html = fandom_payload["parse"]["text"]["*"]
summary["fandom_parse"] = {
    "revision_id": fandom_payload["parse"].get("revid"),
    "display_title": fandom_payload["parse"].get("displaytitle"),
    "wealth_occurrences": len(re.findall(r">\s*Wealth\s*<", fandom_html, flags=re.I)),
    "contains_destiny_component": "Destiny Component" in strip_tags(fandom_html),
    "contains_other": ">Other<" in fandom_html or "Other" in strip_tags(fandom_html),
}

# 中文官方米游社公告。保存API原始响应，并从正文中抽取本项所需的直接支持文本。
official_posts = {
    "v3.8": 71454150,
    "v4.4": 76641553,
    "v4.0": 73128301,
    "v4.2": 74751748,
    "gameplay_guide": 70242029,
}
official_summary: dict[str, object] = {}
for label, post_id in official_posts.items():
    url = f"https://bbs-api.miyoushe.com/post/wapi/getPostFull?post_id={post_id}"
    payload_bytes = fetch(url, referer=f"https://www.miyoushe.com/sr/article/{post_id}")
    evidence = write_bytes(f"miyoushe-{label}-{post_id}.json", payload_bytes)
    payload = json.loads(payload_bytes.decode("utf-8"))
    data = payload.get("data") or {}
    post = data.get("post") or {}
    post_info = post.get("post") or post
    content = post_info.get("content") or post_info.get("structured_content") or ""
    author = post.get("user") or data.get("user") or {}
    status = post_info.get("post_status") or post.get("post_status") or {}
    text = strip_tags(content if isinstance(content, str) else json.dumps(content, ensure_ascii=False))
    official_summary[label] = {
        "url": f"https://www.miyoushe.com/sr/article/{post_id}",
        "api_url": url,
        **evidence,
        "author_name": author.get("nickname") or author.get("name"),
        "certification": author.get("certification") or author.get("certification_info"),
        "is_official": status.get("is_official") if isinstance(status, dict) else None,
        "contains": {
            "命运圣杯星徽": "命运圣杯星徽" in text,
            "装备者加入命运圣杯羁绊": "装备者加入【命运圣杯】羁绊" in text,
            "垃圾袋": "垃圾袋" in text,
            "金垃圾袋": "金垃圾袋" in text,
            "专家邀请函": "专家邀请函" in text,
            "战利品": "战利品" in text,
            "骇客改件": "骇客改件" in text,
            "不占装备栏": "不占装备栏" in text,
            "欢愉星徽": "欢愉星徽" in text,
            "简易装备": "简易装备" in text,
            "进阶装备": "进阶装备" in text,
        },
    }
    time.sleep(0.4)
summary["miyoushe_official"] = official_summary

summary_path = OUTPUT / "source-conflict-summary.json"
summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(summary, ensure_ascii=False, indent=2))
