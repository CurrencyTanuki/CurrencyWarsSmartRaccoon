# ISSUE-018 修前复现：悬浮操作面板拦截自动化输入

## 用户现象

- 独立部署的旧版本仍能启动，自动刷开局也没有显示输入异常；
- 日志显示已执行“按住 LeftAlt 点击：打开星际和平指南”，但游戏页面没有切换；
- 现象看起来与 UIPI/管理员权限阻断相同。

## 保存的原始证据

- `inputs/user-settings-before.json`
  - SHA-256：`0E88BADDB08614CEE206EC2CFEF8F04F65916107A0B111FEFF206837B72265FA`
  - 保存值：`IsLogOverlayClickThrough=false`
- `inputs/test-session-20260809-112335.jsonl`
  - SHA-256：`495B51F3CD4E1FD7771EEF576926C79E35206048E71840A7284EDC5D0ABDE625`
  - `11:23:35`：`SessionStarted` 明确记录管理员权限为 `True`；
  - `11:24:52`：发现本地 `StarRail`，客户区 `2560x1440`；
  - `11:24:54`：游戏获得前台焦点；
  - `11:24:55.542`：输入层报告“已按住 LeftAlt 点击：打开星际和平指南”；
  - `11:25:00.343`：页面仍未切换，指南页锚点只有 `31.5%`；
  - `11:25:03.813`：之后才出现 `GameFocusPaused`。
- 同目录没有 `input-diagnostics.jsonl`，未见 `SendInput` 返回 0 或 Win32 输入异常。

## 几何复现

1. `config/navigation-flow.json` 的 `open_guide` 参考点为 `(1598,45)`，参考尺寸为
   `1920x1080`。
2. 正式导航映射在 `CurrencyWarsNavigation.MapStandardPoint` 中按窗口尺寸缩放并取整；
   对 `2560x1440`，目标约为 `(2131,60)`。
3. `OperationPanelWindow` 是 `600x620`、`Topmost=true`；主窗口将其定位在工作区右上：
   `left = 2560 - 600 - 24 = 1936`、`top = 20`，覆盖
   `[1936,2536) x [20,640)`。
4. `(2131,60)` 确定落在该面板内。
5. 当前共享设置为 `IsLogOverlayClickThrough=false`，面板移除
   `WS_EX_TRANSPARENT`，整窗参与命中测试。
6. `ClickWithModifierAsync` 修前只依据 `SendInput` 返回计数宣告成功，不读取
   `WindowFromPoint`，所以“输入已排队”会被误报成“游戏已收到点击”。

## 系统配置排除

只读检查没有发现本项目写入 UAC、组策略、注册表输入策略、服务、全局输入钩子或
`BlockInput` 的生产逻辑。现机 UAC 关键值为：

- `EnableLUA=1`
- `ConsentPromptBehaviorAdmin=5`
- `PromptOnSecureDesktop=1`
- `EnableSecureUIAPaths=1`

本次日志又明确记录管理员权限为 `True`，因此当前证据不支持“本轮修改了 Windows
系统安全配置”这一解释。

## 版本边界

- `11:23` 这次原始日志对应的快捷方式当时实际启动了交接包中的 `0.2.839` 命名包，
  不能冒充 `0.2.836` 稳定包的直接运行证据。
- 但 `0.2.836` 以及 `D:\Codex-2` 的旧源码同样包含：右上置顶操作面板、跨版本共享
  `%LOCALAPPDATA%\CurrencyWarsSmartRaccoon\user-settings.json`、以及不校验目标 HWND 的
  组合点击实现。因此共享的 `false` 设置足以让独立旧包出现同一故障，而无需修改旧包
  的任何程序文件。

### 11:23 会话版本归属的可复核原始记录

1. `Microsoft-Windows-Shell-Core/Operational`，Event ID `28115`：
   - Record `6475`，`2026-08-09 11:22:54.0263577 +08:00`，快捷方式
     `货币战争智能狸-新版` 的 `AppID` 指向
     `CurrencyWarsSmartRaccoon-0.2.839-win-x64-portable/`
     `CurrencyWarsAssistant.App.exe`；
   - Record `6480`，`2026-08-09 11:25:21.7090190 +08:00`，同一入口才被改指
     `CurrencyWarsSmartRaccoon-0.2.836-win-x64-portable/`
     `CurrencyWarsAssistant.App.exe`；
   - Event `28115` 只证明入口登记/改指，不是新进程创建事件，后一个记录不能证明
     `0.2.836` 随后真正启动。
2. UserAssist：
   - 键 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\`
     `{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count` 中，解码后的
     `0.2.839` EXE 值运行计数为 1、LastRun 为
     `2026-08-09 11:23:20.783 +08:00`；
   - `{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}\Count` 中，同一桌面快捷方式
     LastRun 也是 `11:23:20.783`。
3. `Microsoft-Windows-Application-Experience/Program-Compatibility-Assistant`：
   - Event ID `17`、Record `202`，`2026-08-09 11:31:28.1268164 +08:00`；
   - `ExePath` 再次明确为 `0.2.839-win-x64-portable` 目录中的 EXE。

因此硬证据时间链为：`11:22:54` 入口指向 `0.2.839` 命名包，`11:23:20`
该 EXE 实际运行，应用会话日志在 `11:23:35` 开始，`11:31:28` PCA 仍记录同一
`0.2.839` 路径。`11:25:21` 只是运行期间把快捷方式改指 `0.2.836`，没有对应的
`0.2.836` UserAssist/PCA 运行证据。曾只读观察到 PID 4976 路径相同，但该瞬时进程
快照未落盘，不作为唯一硬证。这里证明的是包目录归属，不把其内部程序集 FileVersion
错误宣称为 `0.2.839`。

## 修前自动化红灯要求

在修改生产代码前增加确定性测试：

- 组合键点击的目标点由另一个顶层窗口覆盖；
- Windows 后端仍让移动和键盘按下成功；
- 修前实现会发送鼠标按下/抬起并返回成功；
- 正确契约应为：不发送鼠标点击、返回明确失败，并保证修饰键最终抬起。
