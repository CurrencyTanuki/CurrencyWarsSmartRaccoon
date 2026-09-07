# Windows 操作标准与指南(货币战争智能狸)

> 适用环境:Windows 11,软件 = `C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前\CurrencyWarsAssistant.App.exe`(WPF/.NET 8,**需要管理员权限运行**),由计划任务 `CWSmartRaccoonCmdTest`(**管理员权限,RL HIGHEST**)拉起、免 UAC。AI 通过 Git Bash + PowerShell 操作本机。
>
> 本文全部条目来自 2026-09-07 实际发生的操作失败案例,非理论推演。每条 = 标准命令 + 失败症状 + 处理。
> 精简版规则清单见记忆文档 `windows-ops-standard.md`(触发条件:凡涉及启动/停止/部署本软件、杀进程、powercfg、诊断进程——先读)。

---

## 一、启动/停止软件(标准命令序列)

### 1.1 启动:只能走计划任务,禁止直接 start exe

**标准命令**(Git Bash 下必须用 `powershell -Command` 包裹):

```bash
powershell -Command "Start-ScheduledTask -TaskName 'CWSmartRaccoonCmdTest'"
```

- **背景**:软件 exe 需要管理员权限。直接 `start exe` 触发 UAC 提权弹窗,**无人值守时 2 分钟超时自动取消**,报"操作已被用户取消",启动失败。
- **失败症状**:`start exe` 后无进程 / 报"操作已被用户取消"。
- **处理**:一律改用 `Start-ScheduledTask`(计划任务已注册,免 UAC)。无人值守场景**绝对禁止**任何形式的直接启动 exe。

### 1.2 启动前置检查:确认无旧实例(单实例 mutex)

软件有单实例 mutex(EventWaitHandle+Mutex)。**新实例撞锁 → 向旧实例写 exit.txt 请退 → 每秒重试最多 12 秒 → 超时 = 新实例自退**(测试台模式专有,表现为"起不来 / jsonl 0 字节")。挂死旧实例持锁时,所有新实例 12 秒自退。

**标准命令**(启动前必查):

```bash
powershell -Command "Get-Process -Name 'CurrencyWarsAssistant.App' -ErrorAction SilentlyContinue | Select Id,StartTime"
```

- 有旧实例 → 先按 1.3 停止;有挂死实例 → 先按第三章清理。**不清旧实例直接启动 = 白白浪费 12 秒后自退**。

### 1.3 停止:写 exit 指令 → 轮询 → (挂死时)一次性 Kill → 失败升级用户

**标准命令序列**:

```bash
# 1) 写退出指令(文件名是中文,写文件用 PowerShell)
powershell -Command "Set-Content -Path 'C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前\指令测试-exit.txt' -Value 'stop' -Encoding UTF8"

# 2) 轮询进程退出(每 2 秒查一次,最多 30~40 秒)
powershell -Command "Get-Process -Name 'CurrencyWarsAssistant.App' -ErrorAction SilentlyContinue"

# 3) 仍在 → 一次性强杀(只试一次)
powershell -Command "Stop-Process -Name 'CurrencyWarsAssistant.App' -Force"
# 或: taskkill /F /PID <pid>

# 4) 仍在 → 立即升级为用户操作(任务管理器结束)或重启电脑,禁止反复重试
```

- **失败症状**:
  - 挂死实例消费不了 exit.txt(轮询 40 秒不退);
  - `Stop-Process`/`taskkill -F` 对提权进程报"拒绝访问"或无效。
  - 实测:`Process.Kill()` 有时成功(PID 43428),有时失败(PID 32992/41608)——失败时**唯一可靠手段 = 用户任务管理器结束,或重启电脑**。
- **处理**:kill 只试一次;失败立即停止重试,升级用户。
- **收尾**:停止后删除 exit.txt 残留(残留会导致下次启动/验收时序错乱,发布脚本第 0 步也是删残留)。

### 1.4 启动成功的判据(不许只看进程存在)

- **进程启动验证** = 等 jsonl 首事件(SessionStarted)+ 写 STATUS 指令验证消费(标准端到端验收脚本:`artifacts\verify_deployment.ps1`,写 `指令测试-command.txt` = `STATUS`,60 秒内等到 `指令测试-result.txt` 出现 `STATUS ⇒ OK` 回执)。
- **jsonl 0 字节或静止(停在启动 4 事件)≠ 正常**——很可能撞单实例锁自退中、或已挂死、或系统已睡眠冻结。
- 进程存在但长时间无事件 → 转第三章诊断决策树。

---

## 二、部署新版本(构建 → 停旧 → 复制 → 版本验证)

**铁律:发布脚本 `artifacts\deploy_stable_1271.ps1` 不构建!** 它只 robocopy `bin\Debug` 产物。**部署前必须先 `dotnet build`**,否则版本戳/新代码根本不会进产物——robocopy 照样"成功",部署的是旧二进制。

### 2.1 标准流程(五步,顺序不可乱)

```bash
# 第 1 步:构建(必须先于 robocopy)
cd "C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826" && dotnet build

# 第 2 步:停旧实例(见 1.3;发布脚本内置此步:删 exit 残留 → 写 exit → 轮询 40 秒不退即中止 rc=6)
#   旧实例运行会锁 App.dll;robocopy 无 /R 参数 = 默认百万次×30 秒重试 = 发布挂死 30 分钟(1.2.97 实测)

# 第 3 步:robocopy 复制(必须带 /R /W;发布脚本已含)
robocopy "<bin\Debug\net8.0-windows10.0.19041.0>" "C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前" /E /NJH /NFL /NDL /R:5 /W:2

# 第 4 步:验证部署版本(必做,不可省)
powershell -Command "(Get-Item 'C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前\CurrencyWarsAssistant.App.dll').VersionInfo.ProductVersion; (Get-Item 'C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前\CurrencyWarsAssistant.App.dll').LastWriteTime"

# 第 5 步:端到端验收(启动 + STATUS 回执,脚本内置)
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\artifacts\verify_deployment.ps1" -TargetDir "C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前"
```

### 2.2 robocopy 返回码与文件锁失败识别

- `rc=3` = 正常(部分复制+额外文件);**`rc≥8` = 失败**。
- **关键陷阱:`rc<8` 也可能跳过了被锁文件**(挂死进程锁住 exe/dll → robocopy 跳过这些文件,rc 仍是 3 "成功")→ **稳定目录出现新旧混搭文件 → 新实例行为异常**。
- **处理**:rc<8 不等于部署成功,必须执行第 4 步版本验证:比对 `App.dll` 的 `ProductVersion` 与 `LastWriteTime` 是否等于本次构建时间与目标版本。对不上 = 有文件被锁跳过,回第三章清理进程后重新部署。
- 挂死旧实例 40 秒不退时发布脚本会主动中止(rc=6,"勿带锁覆盖")——这是正确行为,不要绕过。

---

## 三、进程诊断决策树(正常/挂死/提权的判别与处理)

```
Get-Process 查进程
│
├─ 进程不存在 → 按一.1 启动;启动后 12 秒内自退 → 单实例锁,查旧实例(一.2)
│
├─ 进程存在 → 第 0 步:date 对时 + 查 StartTime(第五章 5.4)
│   └─ 系统时钟跳变(睡眠唤醒 NTP 校时)→ 按"睡眠冻结"处理(第四章),
│      不要基于时间戳做任何日志推理
│
├─ Responding=False → UI 线程阻塞 = 挂死
│   └→ 写 exit.txt 轮询 → 一次性 Kill → 失败即升级用户/重启(禁止反复重试)
│
└─ Responding=True 但零事件、不消费指令 → "假正常"
    ├─ 时钟跳变/用户离开过 → 睡眠冻结(第四章):全部进程被冻结,表现为
    │   "静默/杀不掉/dump 拒绝",先禁睡眠再处理
    ├─ 新实例起不来(jsonl 0 字节)→ 旧挂死实例持单实例锁,先清挂死实例
    └─ 排除以上 → 按挂死处理(UI 线程阻塞时 Responding 仍可为 True)
```

### 3.1 提权进程(UIPI 权限墙)——普通权限 AI 无法诊断/强杀/自动化

软件以管理员权限运行,普通权限的 ZCode/AI 对它:

| 操作 | 症状 |
|---|---|
| UI 自动化(get_app_state/点击) | 被 UIPI 阻止("runs elevated, so Windows UIPI blocks") |
| Process.MainModule / ExecutablePath / CIM ExecutablePath | 读取为空 |
| dotnet-dump collect / taskkill | "拒绝访问" |

- **结论**:提权软件进程无法被普通权限 AI 诊断、强杀、自动化。**识别特征 = 同一进程连续多种工具失败**。
- **处理**:立即升级为用户操作(任务管理器结束)或重启电脑。**不要反复重试浪费轮次**(见第六章)。

### 3.2 挂死进程

- **特征**:窗口显示 + Responding=True 但零事件、不消费指令;Stop-Process/taskkill -F 失败("拒绝访问"或无效);dotnet-dump collect 同样拒绝。
- **Kill 结果不可预测**:有时成功(43428),有时失败(32992/41608)。**失败 = 唯一可靠手段是用户任务管理器,或重启电脑**。
- **连锁危害**:挂死进程锁住 exe/dll → robocopy 部署跳过这些文件(rc=3 仍"成功")→ 稳定目录新旧混搭 → 新实例行为异常(见 2.2)。**部署前必须确保旧实例已干净退出**。

---

## 四、系统睡眠防护(挂机前必做的 powercfg 四项)

**今天最大坑**:用户离开后 Windows 空闲自动睡眠 → **全部进程冻结** → 表现为"软件静默(Responding=True 但零事件/不消费指令)"、"杀不掉"、"dump 拒绝"、"时钟跳变"(唤醒后 NTP 校时跳几小时)。以上症状与挂死/提权完全同貌,**先查睡眠再下结论**。

**标准命令**(挂机/无人值守前必做,四项缺一不可):

```bash
powershell -Command "powercfg /change standby-timeout-ac 0"
powershell -Command "powercfg /change hibernate-timeout-ac 0"
powershell -Command "powercfg /setacvalueindex scheme_current sub_sleep unattendsleep 0; powercfg /setactive scheme_current"
powershell -Command "powercfg /change monitor-timeout-ac 0"   # 可留,建议一并禁
```

- **第三项(unattendsleep,无人值守睡眠超时)最关键且最易漏**:它是独立计时器,表现为**唤醒后 2 分钟自动再睡**;只禁 standby 不禁 unattendsleep = 无效。
- **命令转义**:`powercfg` 的 `/参数` 在 Git Bash 直跑会被当路径解析(报"参数无效"),**必须用 `powershell -Command` 包裹**(详见第五章 5.1)。

---

## 五、命令转义与编码规则

### 5.1 powercfg 必须 powershell 包裹

- Git Bash 直跑 `powercfg /change ...` → `/change` 被当路径 → "参数无效"。
- **标准**:`powershell -Command "powercfg /change standby-timeout-ac 0"`。

### 5.2 中文文件名/文件存在性验证必须用 PowerShell

- Git Bash 的 `ls` 输出中文是**显示层乱码**,但文件名本身可能正确;反之也可能真错。**PowerShell 是唯一判据**:

```bash
powershell -Command "Get-ChildItem 'C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前' -Filter '指令测试-*' | Select Name,Length,LastWriteTime"
```

- 不要依据 Git Bash 的 ls 输出对中文文件名做存在性判断,更不要基于它做删除/重建动作。

### 5.3 含中文的写文件/路径操作走 PowerShell

- 写 `指令测试-exit.txt` / `指令测试-command.txt` 等中文文件名一律 `Set-Content -Encoding UTF8`(见 1.3、1.4)。
- 相关既有坑(见记忆 `scripted-edit-shell-pitfalls.md`):.ps1 必须 BOM(无 BOM 中文按 GBK 乱码);内层 `powershell -File` 中文路径从 Bash 相对路径直跑必乱码。

### 5.4 时钟与时间戳推理

- 查进程启动时间:`powershell -Command "Get-Process -Name 'CurrencyWarsAssistant.App' | Select Id,StartTime"`。
- **系统时钟跳变**(睡眠唤醒后 NTP 校时跳几小时)会破坏基于时间戳的日志推理——**分析日志前先 `date` 对时**,发现跳变先按"发生过睡眠"处理(第四章)。

---

## 六、绝对禁止事项

1. **禁止无人值守时直接 `start exe` 启动软件**——UAC 弹窗 2 分钟超时=启动失败。只能 `Start-ScheduledTask -TaskName 'CWSmartRaccoonCmdTest'`。
2. **禁止对挂死/提权进程反复重试 kill**——同一进程连续多种工具(taskkill/Stop-Process/dump/UIA)失败即定性,立即升级用户任务管理器或重启。反复重试=浪费轮次,且结果不可预测(43428 成/32992、41608 败)。
3. **禁止部署前不 build**——`deploy_stable_1271.ps1` 只 robocopy 不构建;不 build = 部署旧二进制还报"成功"。
4. **禁止部署后不验证版本**——robocopy rc<8 也可能跳过被锁文件(新旧混搭);必须核验 `App.dll` 的 ProductVersion + LastWriteTime。
5. **禁止挂机前不做睡眠四项禁用**——少做 unattendsleep 一项,唤醒后 2 分钟自动再睡,全部进程冻结,症状与挂死无法区分。
6. **禁止用 Git Bash ls 判断中文文件名**——显示层乱码,PowerShell 是唯一判据。
7. **禁止把 Responding=True 当健康判据**——挂死与睡眠冻结时 Responding 均可为 True;唯一判据 = 事件流(jsonl)在动 + STATUS 指令能消费。
8. **禁止在时钟未对时前做时间戳推理**——睡眠唤醒 NTP 校时会让日志时间线整体漂移。
9. **禁止带锁覆盖部署**——旧实例(尤其挂死实例)未退出前不跑 robocopy;发布脚本 rc=6 中止是保护,不许绕过。
10. **禁止留下 exit.txt 残留**——停止/验收后即删,否则污染下次启动与指令时序。

---

## 附:相关脚本与文件

| 文件 | 用途 |
|---|---|
| `artifacts\deploy_stable_1271.ps1` | 发布脚本(删残留→停旧→robocopy→原生dll核对→版本核对→端到端验收;**不含构建**) |
| `artifacts\verify_deployment.ps1` | 验收脚本(Start-ScheduledTask 启动→进程存活→STATUS 端到端回执) |
| `指令测试-exit.txt` / `指令测试-command.txt` / `指令测试-result.txt` | 停止指令 / 注入指令 / 回执文件(均位于稳定目录) |
| 记忆文档 | `C:\Users\zzz81\.zcode\cli\memories\projects\_-ai_20260826-8ac61dadbe406f5b\memory\windows-ops-standard.md`(精简规则清单) |
