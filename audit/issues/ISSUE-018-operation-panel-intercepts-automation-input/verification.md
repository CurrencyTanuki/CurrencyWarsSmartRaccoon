# ISSUE-018 验证记录

## 结论状态

- 生产代码修复、定向测试、关联回归、全量回归和 Release 构建已完成；
- 2026-08-09 已使用最终 Release 候选程序和真实 2560×1440 `StarRail` 完成实机验收：从 `normal_hud` 启动自动刷后成功进入指南并继续到节点 1-1、1-2；
- 用户在实机观察后明确确认“现在可以自动刷了”，程序随后正常停止并关闭；
- 本项没有修改 Windows UAC、注册表、组策略、计划任务或旧版程序文件。

## 修前证据

- 用户设置：`inputs/user-settings-before.json`
  - SHA-256：`0E88BADDB08614CEE206EC2CFEF8F04F65916107A0B111FEFF206837B72265FA`
  - `IsLogOverlayClickThrough=false`
- 失败会话：`inputs/test-session-20260809-112335.jsonl`
  - SHA-256：`495B51F3CD4E1FD7771EEF576926C79E35206048E71840A7284EDC5D0ABDE625`
- 修前定向 TRX：`test-results/before-targeted.trx`
  - SHA-256：`72202E42840A57AED36E2D2EB45F95C76601E202C2DF707C9700115B62459F8E`
  - 结果：`0 Passed / 2 Failed / 0 Skipped`
  - 失败一：自动刷入口未在显示面板前强制穿透；
  - 失败二：组合点击目标被其他顶层窗口覆盖时，仍发送鼠标并返回成功。

## 最终生产修改

只修改以下三个生产文件：

1. `src/CurrencyWarsAssistant.App/MainViewModel.cs`
   - SHA-256：`4294AA3872D611842E71A4F9B9F4A6AE99228C27345AA2258DF1D97C3B80E584`
   - `RunFilterAsync` 在 `AssistanceActivated` 前设置
     `IsLogOverlayClickThrough = true`；
   - 该值会在正常退出时随现有用户设置机制持久化为 `true`，这是有意副作用；真实验收后会恢复验收前的用户设置文件。
2. `src/CurrencyWarsAssistant.Automation/Win32InputController.cs`
   - SHA-256：`80DEBBB0DC2C83EBA3E377BB03B2DA45339923C63236AF0AAB550BA6185D7D95`
   - 仅在 `ClickWithModifierAsync` 的鼠标按下前检查目标点实际根窗口；
   - 被其他顶层窗口覆盖时记录 `ModifierClickTargetCovered`、返回失败且不发送鼠标；
   - 原有 `finally` 保证 Alt 无论成功或失败都抬起。
3. `src/CurrencyWarsAssistant.Automation/Win32InputBackend.cs`
   - SHA-256：`30F128B35D0C8DE0DCACD6EED17A0B5C1F20EE4A0019AB184E9748B9C6EF2381`
   - 只新增 `GetAncestor(..., GA_ROOT)` 封装，允许游戏子 HWND 正确归属游戏顶层窗口。

没有修改普通 `ClickAsync`、拖动、导航坐标、页面模板、识别阈值、UAC 清单或操作面板布局。

## 自动化结果

### 定向测试

- `test-results/after-focused.trx`
- SHA-256：`1B7ADF1E1E3F571310C4325231271D1235881B0BA480B1BB7CD3C29952C5CFA6`
- 结果：`3 Passed / 0 Failed / 0 Skipped`
- 覆盖：
  - 其他顶层窗口遮挡时不发送鼠标，且 Alt 抬起；
  - 落点是游戏拥有的子 HWND 时正常放行；
  - 自动刷在显示面板前强制穿透。

### 关联回归

- `test-results/after-related.trx`
- SHA-256：`A98B6211F17CE567ED41806F95665B30821DBF74187CBBF71B1A47C7B678DDA8`
- 结果：`17 Passed / 0 Failed / 0 Skipped`
- 覆盖 `Win32InputControllerTests`、`RealtimeRecognitionEntryTests`、
  `CurrencyWarsNavigationConfigTests`、`OpeningNavigationRetryPolicyTests`。

### 全量回归

第一次全量：

- `test-results/after-full.trx`
- SHA-256：`23E2288B61079C028CDBBD5EAF642114AE1189EA61B092BE34B77464F2BC1DB3`
- 结果：`811 Passed / 1 Failed / 2 NotExecuted`，总计 814；
- 唯一失败是实时捕获 5 秒计数 `Expected 35..70 / Actual 34`，与本次三个生产改动没有调用依赖；
- 该测试随后在五个独立进程中 `5/5` 通过，原始五份 TRX 均保留。

最终全量复跑：

- `test-results/after-full-rerun.trx`
- SHA-256：`37469DB855E7738D8937DB18FE35546EDA96D432535AEA51754476C05C0D8393`
- 结果：`812 Passed / 0 Failed / 2 NotExecuted`，总计 814；
- 相对 ISSUE-017 基线：新增 3 个测试且全部通过、缺失 0；13 个既有壁钟性能测试由 Failed 转为 Passed，既有 Passed 转 Failed 为 0；
- 两个 NotExecuted 均为此前已经明确静态跳过的性能测试，本项未改变它们。

### 测试诊断数据隔离

- 遮挡测试按生产路径写入了 4 条合成 `ModifierClickTargetCovered` 诊断；
- 已将原始文件完整保存为 `inputs/input-diagnostics-synthetic-tests.jsonl`；
- SHA-256：`A0DA3878EA2E5A613758C9EE432DB7541F264F349A19E43D80BC5DD6334CBEB7`；
- 四条记录的虚拟句柄均为 `0x3E7 / 0x7B`、测试坐标均为 `(410,160)`，不得冒充真实游戏遮挡；
- 修前用户日志目录不存在该文件；保存证据后已仅删除此测试新建文件，其他用户日志和设置未动。

## Release 构建

- 命令：`dotnet build CurrencyWarsAssistant.sln -c Release --no-restore --nologo`
- 结果：退出码 0，`0 warnings / 0 errors`
- 日志：`test-results/release-build.log`
  - SHA-256：`55A65136A53BE1506ED2BCC57BA492646EAAAE9B75A4DD83F5BF2B09F0F65B50`
- Binlog：`test-results/release-build.binlog`
  - SHA-256：`1BCD9B6EBF64BC9307324BE673C7B3D9C5A3FD15E17024835DDAFA1F5910CE74`
- App DLL SHA-256：`B6BF83FBA5B0E6B1D441DDC80BD5C914DC3829CA11FD60F36FE736032FCF6DEB`
- Automation DLL SHA-256：`0CD367C0E7FCA02D769955B479D6F157ABDFFF5AE297E34D23BE9B2B1B188729`

## 真实程序 + 真实游戏验收

### 环境与启动对象

- 验收日期：`2026-08-09`
- 游戏：真实 `StarRail.exe`，PID `29560`，游戏画面 `2560×1440`（16:9）；
- 候选程序：
  `src/CurrencyWarsAssistant.App/bin/Release/net8.0-windows10.0.19041.0/CurrencyWarsAssistant.App.exe`
  - 验收时 PID：`16264`
  - SHA-256：`21077587844BAD023D370BBA6C056E1E45AB61877AAB19656F2420DD3C8C9D5C`
- 启动前共享设置为 `IsLogOverlayClickThrough=false`，输入文件 SHA-256：
  `0E88BADDB08614CEE206EC2CFEF8F04F65916107A0B111FEFF206837B72265FA`。

### 实际结果

- 在真实游戏 `normal_hud` 页面启动自动刷；
- 操作面板显示后，“鼠标穿透”实际变为选中；
- 自动化成功越过原先失败的 `open_guide` 组合点击，继续识别和操作节点 1-1、节点 1-2；
- 主窗口活动日志可见“进入节点 1-2”等后续状态，证明不是仅把鼠标事件送入 Windows 后停在原页；
- 用户实机确认“现在可以自动刷了”；
- 验收停止后 `CurrencyWarsAssistant.App.exe` 进程不存在，`StarRail.exe` 保持运行，未观察到崩溃或残留助手进程；
- 清理过合成测试诊断后，真实验收没有重新生成 `input-diagnostics.jsonl`，因此没有本轮真实
  `ModifierClickTargetCovered` 或 Win32 输入失败记录。

### 截图证据

- `screenshots/ui-after-main-window-20260809-1331.jpg`
  - SHA-256：`47C7A0B8CAC4998DA33B52E17FBF401B4D5695CC221B49C7FE4E1A14045DCED0`
  - 显示真实游戏已进入节点 1-2 战斗阶段，操作面板同时可见；
- `screenshots/ui-after-status-log-20260809-1331.jpg`
  - SHA-256：`E4860B9854C8BC28F412AEF39A5DBCC4B33685800E7705FCD4017C5EF978179E`
  - 显示自动刷已完成节点 1-1 战斗识别并进入节点 1-2；
- `screenshots/ui-after-operation-panel-20260809-1331.jpg`
  - SHA-256：`13FDD9D65041509827E425FF813E49E48FD596BD0827513B660DC103F3048B04`
  - 显示操作面板“鼠标穿透”已选中。

### 实际运行归档

- RunId：`run-20260809-133022-r2-73793ac9e84b47a8b8d7366a69e24d02`
- `events.jsonl` SHA-256：`9674DD9FA515204349C2D6E7CD19818DF39426FB87E2F702B586C8258413ABFD`
- `checkpoint.v1.json` SHA-256：`35625902E36798FB69B0FF196C3CB63CE5E31547EE8274D8DF3903066A741286`
- `nodes/node-1-1-final.json` SHA-256：`F9C7371F31EE61F9466BEC23A6F9EFEF1321496AA27CCED2CA1A32C785581AB5`
- `nodes/node-1-2-final.json` SHA-256：`2360CE7844C23D2BA172E5E72103EDFDD4949CB9E682B7D0A471F5AFC5EF9151`
- 该归档保留为下一项“节点历史字段未显示”问题的真实修前证据；本项只据此验证自动刷输入与后续导航，不声称整局已完成。

### 设置恢复与用户数据清理

- 正常退出后，程序按设计把穿透设置持久化为 `true`；原始退出状态已保存为
  `inputs/user-settings-after-success-before-restore.json`，SHA-256：
  `59E9C0A8D774E3F17C9FE59CEBDFB8C1BB95042A1E57E9B87B7CBAD3CED2FC01`；
- 随后已把共享 `user-settings.json` 按字节恢复为验收前文件；恢复后 SHA-256 精确为
  `0E88BADDB08614CEE206EC2CFEF8F04F65916107A0B111FEFF206837B72265FA`；
- 游戏进程和真实运行归档未删除、未修改；合成测试诊断仅保留在本问题审计目录。

## 验收边界

- 本次实机验收确认的是：操作面板不会再拦截自动刷的开局组合点击，且自动化能够继续进入后续节点；
- 没有把本次未跑完整局的过程外推为所有自动化分支均已实机覆盖；
- 节点历史除金币外字段显示为空是本次实跑发现的独立问题，已另行排查，不属于本项输入修复范围。

## 独立终审

- 结论：`APPROVE`
- 独立审查员以只读方式复核生产/测试差异、修前红灯、定向/关联/全量 TRX、Release 构建、三张实机截图、真实 RunId、退出状态和设置恢复；未发现阻断点；
- 批准范围严格限定为“自身置顶操作面板不再拦截自动刷开局的 `open_guide` 组合点击，且自动化实际继续进入后续页面和节点”；
- 不把本批准外推为整局、所有自动化分支或节点历史字段均已完成。
