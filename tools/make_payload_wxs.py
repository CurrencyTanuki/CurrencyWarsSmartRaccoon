#!/usr/bin/env python3
"""收割发布目录 → 生成 WiX v4 PayloadComponents 片段（递归目录树+确定性 GUID）。
用法: python make_payload_wxs.py <publish_dir> <out_wxs> <msi_version>
组件 GUID=uuid5(NAMESPACE_URL, "cw-installer:"+相对路径) —— 路径不变则 GUID 稳定。"""
import hashlib
import os
import sys
import uuid

publish = os.path.abspath(sys.argv[1])
out_wxs = sys.argv[2]

# 收集全部相对文件路径（/ 分隔）
all_files = []
for root, _dirs, fnames in os.walk(publish):
    rel = os.path.relpath(root, publish).replace("\\", "/")
    if rel == ".":
        rel = ""
    for fn in sorted(fnames):
        all_files.append((rel, fn))

# 目录树：children[""] = 根的直接子目录名集合；dir_ids = 相对路径 → wix Directory Id
children = {}
files_by = {}
dir_ids = {}


def dir_wix_id(rel):
    if rel == "":
        return "INSTALLFOLDER"
    if rel not in dir_ids:
        dir_ids[rel] = "D_" + hashlib.sha1(rel.lower().encode()).hexdigest()[:12]
    return dir_ids[rel]


for rel, fn in sorted(all_files):
    parts = rel.split("/") if rel else []
    acc = ""
    for part in parts:
        parent = acc
        acc = part if not acc else acc + "/" + part
        children.setdefault(parent, set()).add(part)
    files_by.setdefault(rel, []).append(fn)
    dir_wix_id(rel)  # 注册目录 Id

out = []


def component_xml(dir_rel, fname, indent):
    rel_path = (dir_rel + "/" + fname) if dir_rel else fname
    guid = str(uuid.uuid5(uuid.NAMESPACE_URL, "cw-installer:" + rel_path)).upper()
    abs_path = os.path.join(publish, rel_path.replace("/", os.sep))
    did = dir_wix_id(dir_rel)
    comp_id = "C_" + hashlib.sha1(rel_path.lower().encode()).hexdigest()[:12]
    pad = " " * indent
    return (pad + f'<Component Id="{comp_id}" Guid="{guid}" Directory="{did}">\n'
            + pad + f'  <File Source="{abs_path}" />\n'
            + pad + '</Component>\n')


def emit_dir(rel, indent):
    """递归生成目录树。根目录(rel="")生成 DirectoryRef，其余生成嵌套 Directory。"""
    pad = " " * indent
    if rel == "":
        out.append(pad + '<DirectoryRef Id="INSTALLFOLDER">')
    else:
        out.append(pad + f'<Directory Id="{dir_wix_id(rel)}" Name="{os.path.basename(rel)}">')
    inner = indent + 2
    for fn in sorted(files_by.get(rel, [])):
        out.append(component_xml(rel, fn, inner))
    for child in sorted(children.get(rel, [])):
        child_rel = child if not rel else rel + "/" + child
        emit_dir(child_rel, inner)
    if rel == "":
        out.append(pad + '</DirectoryRef>')
    else:
        out.append(pad + '</Directory>')


out.append('<?xml version="1.0" encoding="utf-8"?>')
out.append('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
out.append('  <Fragment>')
emit_dir("", 4)
out.append('    <ComponentGroup Id="PayloadComponents">')
for rel in sorted(files_by):
    for fn in sorted(files_by.get(rel, [])):
        rel_path = (rel + "/" + fn) if rel else fn
        comp_id = "C_" + hashlib.sha1(rel_path.lower().encode()).hexdigest()[:12]
        out.append(f'    <ComponentRef Id="{comp_id}" />')
out.append('    </ComponentGroup>')
out.append('  </Fragment>')
out.append('</Wix>')

with open(out_wxs, "w", encoding="utf-8") as f:
    f.write("\n".join(out) + "\n")
total = sum(len(v) for v in files_by.values())
print(f"payload wxs written: {out_wxs} ({total} files, {len(files_by)} dirs)")
