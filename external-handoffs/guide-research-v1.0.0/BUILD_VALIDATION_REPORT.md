# 构建与独立验证报告

验证日期：2026-08-01（Asia/Shanghai）

## JSON 全量校验

命令：

```powershell
python tools/validate_all.py --root . --include-output
```

实际结果：PASS。

- 合法 evidence：2
- 合法 playbook：3
- 故意非法样本被拒绝：2
- 标准 ID 目录：7
- 所有合法文件重复读取一致：通过

## 合同测试

命令：

```powershell
python tests/test_contracts.py
```

实际结果：4 项通过，0 项失败。覆盖全量校验、重复读取、unknown/冲突保留和“仅声明式数据”检查。

## 干净解压复验

命令：

```powershell
python tools/build_package.py
```

构建器创建 ZIP 后解压到新的临时目录，再从解压目录运行同一组 4 项测试。实际结果：4 项通过，0 项失败；临时目录随后删除。

## 内容审计

构建器检查全部包内文件：

- 只允许 JSON、Markdown、TXT 和 Python 源码；
- 不允许超过 5 MiB 的异常文件；
- 扫描 OpenAI 风格密钥、私钥、UID 数字和 Windows 用户私有路径；
- 不包含 EXE、DLL、截图、图标、视频、账号数据或项目构建物。

实际结果：通过，未发现阻塞项。
