# -*- coding: utf-8 -*-
import json, glob, collections, os

base = r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941'
# 按时间排序 analysis，统计每帧的 pageId + board/bench/lineup 识别情况 + 商店
rows = []
for f in sorted(glob.glob(base + r'\analysis-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    snap = d.get('snapshot', {})
    page = snap.get('pageId', {}).get('value')
    pconf = snap.get('pageId', {}).get('confidence', 0)
    board = snap.get('boardCharacterIds', {})
    bench = snap.get('benchCharacterIds', {})
    shop = snap.get('shopCharacterIds', {})
    bstat, bval = board.get('status'), len(board.get('value', []))
    beStat, beVal = bench.get('status'), len(bench.get('value', []))
    sVal = len(shop.get('value', []))
    rows.append((os.path.basename(f)[:26], page, round(pconf,2), bstat, bval, beStat, beVal, sVal))

print("文件 | pageId | pageconf | board状态/数 | bench状态/数 | shop数")
import os as _os
for r in rows:
    print(f"{r[0]} | {r[1]} | {r[2]} | {r[3]}/{r[4]} | {r[5]}/{r[6]} | {r[7]}")

# 汇总
print("\n=== 汇总 ===")
print("有 board(前台 前)识别到角色(≥1) 的帧数:", sum(1 for r in rows if r[4] >= 1))
print("board 空(or unknown) 的帧数:", sum(1 for r in rows if r[4] == 0))
print("其中判为 reward_shop 的帧数:", sum(1 for r in rows if r[4]==0 and r[1]=='reward_shop'))
print("其中判为 preparation 且 board空:", sum(1 for r in rows if r[4]==0 and r[1]=='preparation_generic'))
print("总计帧数:", len(rows))
