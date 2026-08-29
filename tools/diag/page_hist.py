# -*- coding: utf-8 -*-
import json, os, glob, collections

base = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941"
files = sorted(glob.glob(os.path.join(base, "analysis-*.json")))
page_counter = collections.Counter()
page_conf = collections.defaultdict(list)
board_empty = collections.Counter()
for f in files:
    try:
        d = json.load(open(f, encoding="utf-8-sig"))
    except Exception:
        continue
    snap = d.get("snapshot", {})
    page = snap.get("pageId", {})
    pid = page.get("value")
    conf = page.get("confidence", 0)
    page_counter[str(pid)] += 1
    page_conf[str(pid)].append(round(conf, 3))
    # board 是否空
    board = snap.get("boardCharacterIds", {})
    if str(pid) == "reward_shop":
        board_empty["shop-pid-board-known"] += 1 if board.get("status") == "known" and len(board.get("value", [])) == 0 else 0
        board_empty["shop-pid-board-nonempty"] += 1 if len(board.get("value", [])) > 0 else 0

print("=== 全 run pageId 判定分布 ===")
for k, v in page_counter.most_common():
    confs = page_conf[k]
    print(f"{v:4d} 帧 | pageId={k} | conf均值={sum(confs)/len(confs):.3f} 范围{min(confs)}-{max(confs)}")
print("\n=== reward_shop 判定下的 board(前台) ===")
for k, v in board_empty.items():
    print("  ", k, "=", v)
