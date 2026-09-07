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

## 七、AI 工具常见 Windows 错误对照表(网络调研)

> 2026-09-07 网络调研沉淀:AI 编程工具(Claude Code/Copilot/国产 Agent 等)在 Windows 命令行上反复犯的错。每行 = 错误现象 → 根因(带来源)→ 一步到位正确做法。与第一~六节交叉引用;本文档第一~六节的案例与下述外部根因互为印证。

### 7.1 执行策略与编码类(对应第五节)

| 错误现象 | 根因(来源) | 一步到位正确做法 |
|---|---|---|
| 跑 .ps1 报 "running scripts is disabled on this system" | Windows 客户端默认执行策略 Restricted(所有 scope 未定义时生效);执行策略是纵深防御非安全边界([about_Execution_Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)) | 单次(首选,零系统改动):`powershell -NoProfile -ExecutionPolicy Bypass -File x.ps1`(Process 级仅当前会话);长期:`Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser`。勿全局 Bypass/Unrestricted;改 LocalMachine 需管理员 PowerShell |
| PowerShell 写出的中文文件/脚本内容乱码 | Windows PowerShell 5.1 的 `Set-Content`/`Add-Content` 在无显式 `-Encoding` 且目标为空/新建时默认 **Default(系统 ANSI 旧代码页,中文系统=GBK/CP936)**;`Out-File` 与 `>` 重定向默认 UTF-16LE([about_Character_Encoding](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)) | 读写一律显式 `-Encoding UTF8`(见 1.3/1.4 标准命令);不要依赖默认编码。AI 工具实测同坑:Agent 管线把 GB2312 输出按错误编码处理导致乱码([LobsterAI issue #455](https://github.com/netease-youdao/LobsterAI/issues/455)) |
| 含中文的 .ps1 脚本本身被解析成乱码 | 无 BOM 的 UTF-8 脚本会被 Windows PowerShell 按 ANSI 代码页误读;官方原话:"If you need to use non-Ascii characters in your scripts, save them as UTF-8 with BOM"([about_Character_Encoding](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)) | 含中文的 .ps1 必须 **UTF-8 with BOM** 保存(与记忆 `scripted-edit-shell-pitfalls.md` 的 .ps1 必须 BOM 条款互证) |
| 控制台输出中文显示乱码(读文件正确、显示层错) | 控制台活动代码页非 UTF-8(中文系统 OEM 936);`chcp 65001` 只对**之后启动的程序**生效,之前启动的程序继续用旧代码页([chcp 官方文档 Remarks](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/chcp)) | 判断文件内容真伪用 PowerShell 读(见 5.2),不要信显示层;需要修显示层时 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`(会话内)或 `chcp 65001`(对后续进程);PowerShell 与外部程序管道编码由 `$OutputEncoding` 控制 |
| 把"Beta: Use Unicode UTF-8 for worldwide language support"当乱码万能解 | 该选项把系统 ANSI/OEM 代码页整体改成 65001,依赖旧代码页的软件会坏:工业 SCADA 停摆([Levexsa](https://www.levexsa.com/en/blog/enabling-beta-use-unicode-utf-8-for-worldwide-language-support-in-windows-10-1803-or-later-may-lead-to-citect-scada-not-functioning-properly-si-experimenta-una-situacion-similar-le-recomendamos-ponerse-en-contacto-con-un-especialista-de-levex?elem=887849))、商业软件声明不兼容([Febooti](https://www.febooti.com/products/automation-workshop/online-help/events/workshop-service/2273.html))、OpenJDK 帮助文本反乱码([microsoft/openjdk#43](https://github.com/microsoft/openjdk/issues/43));原理见 [StackOverflow 56419639](https://stackoverflow.com/questions/56419639/what-does-beta-use-unicode-utf-8-for-worldwide-language-support-actually-do) | 不为解决脚本乱码开此选项(机器级副作用不可控);脚本层显式编码(上一行)是唯一正解;已开且出问题的软件,解法=取消勾选+重启([MS Q&A](https://learn.microsoft.com/en-au/answers/questions/4230605/i-want-to-uncheck-the-tick-of-beta-use-unicode-utf)) |

### 7.2 UAC/管理员权限自动化类(对应第一节、第三章)

| 错误现象 | 根因(来源) | 一步到位正确做法 |
|---|---|---|
| 无人值守 `Start-Process -Verb RunAs`/`start exe` 报"操作已被用户取消" | UAC 提示显示在安全桌面,**2 分钟无交互硬超时自动取消**([SuperUser 1264363](https://superuser.com/questions/1264363/prevent-timeout-for-uac-popup-on-windows-10)、[ScreenConnect 社区同证](https://screenconnect.product.connectwise.com/communities/1/topics/579-fix-uac-should-never-lose-ability-to-control-machine-remotely));提级提示与安全桌面机制见 [微软 UAC 官方文档](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works) | 无人值守免 UAC 首选**计划任务**:`schtasks /create ... /rl HIGHEST`(官方:"HIGHEST = highest level of privileges, such as Superuser accounts",[schtasks create](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create))+ `Start-ScheduledTask`(本文档 1.1);交互场景可用 gsudo(凭证缓存默认 5 分钟,`gsudo -k` 清除,提权失败退出码 999,[gsudo](https://github.com/gerardog/gsudo)) |
| 想关 UAC 提示省事 | 注册表 `ConsentPromptBehaviorAdmin=0`(Elevate without prompting)可免提示([Server Fault 1148724](https://serverfault.com/questions/1148724/best-practice-of-uac-behavior-of-the-elevation-prompt-for-administrators-in-adm)、[4sysops](https://4sysops.com/archives/why-and-how-to-disable-the-uac-elevation-prompts-secure-desktop-prompting/)) | **本项目不做此配置**——安全代价大,且计划任务方案已彻底绕开 UAC(见上),风险更小且零安全降级 |
| 普通权限 Stop-Process/taskkill 杀提权进程报 "Access is denied" | 官方明文:"to stop a process that is not owned by the current user, you must start PowerShell by using the Run as administrator option"(非本人进程必须管理员 PowerShell;lsass 示例同样报 Access denied,[Stop-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/stop-process)) | 这不是 bug 是设计。普通权限 AI **无法强杀提权进程**→按本文档三升级用户任务管理器/重启,不要反复重试(禁止事项 2) |
| UI 自动化点不了提权软件窗口 | 完整性级别隔离(UIPI):"Applications with lower integrity levels can't modify data in applications with higher integrity levels"([微软 UAC 官方文档](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)) | 同上,识别为提权墙后立即升级用户操作(本文档 3.1),勿当工具 bug 修 |

### 7.3 进程强杀与睡眠类(对应第三章、第四节)

| 错误现象 | 根因(来源) | 一步到位正确做法 |
|---|---|---|
| `taskkill /F` 杀掉主进程但子进程残留 | `/F` 只强制结束指定进程;`/T` 才会连子进程:"Ends the specified process and any child processes started by it"([taskkill 官方文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/taskkill)) | 树杀标准命令:`taskkill /T /F /PID <pid>`(本项目停止序列仍以 exit.txt 优先,见 1.3;强杀兜底时补 /T) |
| 所有睡眠项都设"从不",唤醒后 2 分钟又睡 | UNATTENDSLEEP(无人值守空闲睡眠)是独立设置,官方定义:"duration of inactivity before the system automatically enters sleep **after waking from sleep in an unattended state**",默认 2 分钟([Sleep unattended idle timeout - MS Learn](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-sleep-unattended-idle-timeout)、[MS Answers 同症案例](https://learn.microsoft.com/en-us/answers/questions/3866858/windows-11-pc-goes-back-to-sleep-even-though-all-s)) | `powercfg /setacvalueindex scheme_current sub_sleep unattendsleep 0; powercfg /setactive scheme_current`(本文档四第三项,缺它全盘皆输) |
| 以为 `powercfg /change` 能设所有超时 | `/change` 只支持 monitor/disk/standby/hibernate-timeout(ac/dc)八项;其余设置须 `/setacvalueindex scheme sub setting value` 且 **/setactive 后才生效**;别名(sub_sleep/unattendsleep)可用 `powercfg /aliases` 查([powercfg 官方文档](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options)) | 标准四项见第四节;/change 覆盖不到的一律 setacvalueindex+setactive 成对执行 |
| 排查"软件静默/杀不掉"从进程入手绕圈子 | 现代待机/睡眠期间进程整体冻结,症状与挂死/提权同貌;官方提供 `/sleepstudy`、`/systemsleepdiagnostics` 报告工具([powercfg 官方文档](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options),两者需管理员) | 诊断流程先问"系统睡过吗"(本文档三决策树第 0 步对时);需要证据时 `powercfg /sleepstudy` 看睡眠迁移记录 |

### 7.4 文件锁/部署类(对应第二节)

| 错误现象 | 根因(来源) | 一步到位正确做法 |
|---|---|---|
| robocopy 默认参数发布挂死几十分钟 | 官方默认:`/r` = **1,000,000 次**重试、`/w` = **30 秒**([robocopy 官方文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)) | 必带 `/R:5 /W:2`(本文档 2.1;1.2.97 挂死 30 分钟事故的官方根因) |
| rc<8 就当部署成功,结果文件新旧混搭 | 官方退出码表:0~7 均非失败("No failure was encountered"),**"Any value equal to or greater than 8 indicates that there was at least one failure"**;rc=3="Some files were copied. Additional files were present"——被跳过的锁文件不进退出码([robocopy 官方文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)) | rc 只做失败筛查;成功判定=部署后验证 App.dll ProductVersion+LastWriteTime(本文档 2.2 第 4 步)+端到端验收 |
| 只看屏幕输出复盘发布 | 官方建议:"It's highly recommended when running the robocopy command to create a log file that can be viewed once the process completes verifying its integrity"([robocopy 官方文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)) | 大批量/排障时加 `/LOG:<file>`(或 `/LOG+` 追加、`/V` 显示 skipped 文件),用日志核对跳过清单 |

### 来源清单(本章全部外部引用)

1. [robocopy - Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)(退出码、/R /W 默认值、/LOG 建议)
2. [taskkill - Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/taskkill)(/T /F 定义)
3. [powercfg command-line options - Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options)(/change 支持范围、/setacvalueindex 语法、/aliases、/sleepstudy)
4. [Sleep unattended idle timeout - Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-sleep-unattended-idle-timeout)(UNATTENDSLEEP 官方定义)
5. [Windows 11 PC goes back to sleep - Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/3866858/windows-11-pc-goes-back-to-sleep-even-though-all-s)(unattendsleep=0 修法)
6. [chcp - Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/chcp)(代码页与生效范围)
7. [about_Character_Encoding - Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)(PS5.1 默认编码、BOM、$OutputEncoding)
8. [about_Execution_Policies - Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)(Restricted/RemoteSigned 默认值、Bypass 会话级)
9. [Stop-Process - Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/stop-process)(非本人进程须管理员)
10. [How User Account Control works - Microsoft Learn](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)(安全桌面、完整性级别/UIPI)
11. [schtasks create - Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create)(/RL HIGHEST)
12. [gsudo - GitHub](https://github.com/gerardog/gsudo)(凭证缓存、退出码 999)
13. [Prevent timeout for UAC popup - Super User](https://superuser.com/questions/1264363/prevent-timeout-for-uac-popup-on-windows-10)(UAC 2 分钟超时)
14. [ScreenConnect 社区:UAC 2 分钟自动取消](https://screenconnect.product.connectwise.com/communities/1/topics/579-fix-uac-should-never-lose-ability-to-control-machine-remotely)(无人值守同症)
15. [Server Fault:UAC 提示行为最佳实践](https://serverfault.com/questions/1148724/best-practice-of-uac-behavior-of-the-elevation-prompt-for-administrators-in-adm)、[4sysops:安全桌面提示](https://4sysops.com/archives/why-and-how-to-disable-the-uac-elevation-prompts-secure-desktop-prompting/)(ConsentPromptBehaviorAdmin)
16. [StackOverflow 56419639:Beta UTF-8 选项原理](https://stackoverflow.com/questions/56419639/what-does-beta-use-unicode-utf-8-for-worldwide-language-support-actually-do)
17. [microsoft/openjdk#43](https://github.com/microsoft/openjdk/issues/43)、[Febooti 不兼容声明](https://www.febooti.com/products/automation-workshop/online-help/events/workshop-service/2273.html)、[Levexsa SCADA 案例](https://www.levexsa.com/en/blog/enabling-beta-use-unicode-utf-8-for-worldwide-language-support-in-windows-10-1803-or-later-may-lead-to-citect-scada-not-functioning-properly-si-experimenta-una-situacion-similar-le-recomendamos-ponerse-en-contacto-con-un-especialista-de-levex?elem=887849)(Beta UTF-8 副作用实锤)
18. [LobsterAI issue #455:Agent 管线 GB2312 乱码](https://github.com/netease-youdao/LobsterAI/issues/455)(AI 工具 Windows 编码坑实证)
19. [MS Q&A:取消 Beta UTF-8 勾选](https://learn.microsoft.com/en-au/answers/questions/4230605/i-want-to-uncheck-the-tick-of-beta-use-unicode-utf)

## 八、启动死实例(挂死)处置标准流程(2026-09-08 事故沉淀)

> 事故:1.2.119 DI 死锁实例启动即永久卡死(闪屏冻结/jsonl 0 字节),值守 84 分钟五种方式杀不掉,最终用户亲手结束。
> 本节=此类场景的标准处置,违反=重复 84 分钟空转。

### 8.1 判定签名(启动挂死,区别于运行期挂死)

- 进程存在但 **jsonl 创建后 60 秒仍 0 字节**(健康启动 ≤3 秒必有首事件)= 启动挂死签名。
- 伴随:互斥量已持有(新实例 12 秒自退)、App.dll/依赖 DLL 全部被锁(部署 robocopy 跳过=新旧混搭风险)、exit.txt/abort.txt/激活事件全部无效(指令轮询 timer 在死锁点之后,从未启动)。
- **对外零接口**:启动期死锁的实例没有任何软件内通道可用,唯一解=外部强杀;强杀唯一解=用户(见 8.2)。

### 8.2 处置顺序(严格照此,总耗时目标 <10 分钟)

1. **确认签名**:jsonl 0 字节 60 秒 + 进程 StartTime 晚于本次部署 → 定性启动挂死。
2. **一次预告 + 一次 UAC**(必须先在消息里预告"将弹出 UAC,请点是",占屏铁律):`powershell -Command "Start-Process -Verb RunAs -FilePath 'cmd.exe' -ArgumentList '/c taskkill /F /IM CurrencyWarsAssistant.App.exe'"`。UAC 无人点击 2 分钟自动取消。
3. **同时给出任务管理器指引**(用户不想点 UAC 时):任务管理器→详细信息→结束 CurrencyWarsAssistant.App(PID)。
4. **挂后台监视器自动恢复**(关键!已验证有效):`bash artifacts/watch_and_deploy.sh`——每分钟检测,进程消失即自动执行部署脚本(robocopy+版本核对+启动+STATUS 验收)全自动恢复。用户结束进程的瞬间,系统自愈,无需值守 AI 在场。
5. **禁止事项**:禁止无预告弹 UAC;禁止反复弹 UAC 刷屏(>2 次);禁止对死实例反复 Stop-Process(拒绝访问是 UICI 设计,重试无意义);禁止绕过锁强行 robocopy(新旧混搭)。

### 8.3 为什么杀不掉(原理,勿再试错)

- 软件经计划任务(RunLevel=HighestAvailable)拉起=高完整性进程;AI 宿主=中完整性。**同用户也不行**:中完整性进程 OpenProcess(PROCESS_TERMINATE) 高完整性进程被拒(UAC 强制完整性控制,设计而非故障)。
- 计划任务反向利用的三种形态全部受控:①`/create /rl HIGHEST` 新提权任务=非提权上下文被拒;②`/change /tr` 改现有提权任务=要求账户密码(AI 无);③`/run` 免密可行但 ACTION 固定为应用本体(只能拉起应用,不能执行任意命令)。
- **解锁方案(待办,需用户最后一次 UAC 配合)**:注册一个"AI 可写 wrapper"辅助任务——ACTION 指向稳定目录下 AI 可写的 `ai-elevated-runner.cmd`,以后提权=改脚本内容(免密码)+`schtasks /run`(已实测免密) 。安全代价=可写该脚本者即可以最高权限执行,须用户知情同意后方可注册。

### 8.4 预防(让死实例不再出现)

- **DI 注册变更**(工厂/Func/可选参数注入)必须子代理对抗审查+跑 `DiResolutionCycleRegressionTests` 守卫(30 秒解析不上=死锁形状,已入套件)。
- **启动看门狗(待办)**:OnStartup 在 DI 解析前起独立看门狗线程,90 秒未完成启动→写 `startup-stuck` 标记+自我退出释放互斥量——死实例占用从无限降为 90 秒,且标记文件给远程 AI 直接证据。
- 每次发布后立即验收(jsonl 首事件+STATUS);验收失败=立即按 8.2 处置,**绝不留死实例观察**。

## 附:相关脚本与文件

| 文件 | 用途 |
|---|---|
| `artifacts\deploy_stable_1271.ps1` | 发布脚本(删残留→停旧→robocopy→原生dll核对→版本核对→端到端验收;**不含构建**) |
| `artifacts\verify_deployment.ps1` | 验收脚本(Start-ScheduledTask 启动→进程存活→STATUS 端到端回执) |
| `artifacts\watch_and_deploy.sh` | 死实例自动恢复监视器(8.2 第4步;进程消失→自动部署+启动,已 02:53 实战验证) |
| `指令测试-exit.txt` / `指令测试-command.txt` / `指令测试-result.txt` | 停止指令 / 注入指令 / 回执文件(均位于稳定目录) |
| 记忆文档 | `C:\Users\zzz81\.zcode\cli\memories\projects\_-ai_20260826-8ac61dadbe406f5b\memory\windows-ops-standard.md`(精简规则清单) |
