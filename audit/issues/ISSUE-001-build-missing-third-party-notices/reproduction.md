# ISSUE-001：交接源码缺少第三方通知文件，Release 构建失败

## 修改前复现

- 工作副本：`D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808`
- 原始来源：`C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808\源代码`
- 命令：`dotnet build CurrencyWarsAssistant.sln -c Release --nologo`
- 时间：2026-08-08（Asia/Shanghai）
- 结果：失败，0 个警告、1 个错误。

错误原文：

```text
Microsoft.Common.CurrentVersion.targets(5321,5): error MSB3030:
无法复制文件“D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808\THIRD_PARTY_NOTICES.md”，原因是找不到该文件。
```

## 根因证据

`src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj:32` 明确把仓库根目录的
`THIRD_PARTY_NOTICES.md` 作为发布内容复制，但交接源码根目录未包含该文件。

原项目和交接包内已编译程序各自包含同名文件，二者 SHA-256 完全一致：

```text
87DCF2CF15B2813A253244E343B2281A64B03F730D22F6C23BAEE462522A7755
```

核对来源：

- `D:\Codex-2\THIRD_PARTY_NOTICES.md`
- `C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808\程序\THIRD_PARTY_NOTICES.md`

## 计划中的最小修复

仅把上述双来源哈希一致的通知文件恢复到工作副本根目录；不删除项目引用，不修改构建逻辑，
也不同时处理版本号、测试或识别问题。
