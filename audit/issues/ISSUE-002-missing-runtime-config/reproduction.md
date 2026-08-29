# ISSUE-002 复现记录：交接源码缺失运行配置目录

## 用户影响

交接源码根目录完全缺失 `config/`。项目文件仍声明将 `config/**/*` 复制到应用输出，
而 `App.OnStartup` 会立即加载 `navigation-flow.json`、
`page-recognition.1920x1080.json`、`community.json` 及页面模板。因此从交接源码重建的
程序不能完成启动，页面识别及依赖页面分类的测试也不能运行。

## 静态证据

- 工作副本 `config/`：不存在。
- `src/CurrencyWarsAssistant.App/CurrencyWarsAssistant.App.csproj:29`：
  `config/**/*` 应复制到输出目录。
- `src/CurrencyWarsAssistant.App/App.xaml.cs:113-130`：启动阶段从应用输出的
  `config/` 加载导航、页面识别及社区配置。
- 交接程序包的 `程序/config/` 存在，共 40 个文件（7 JSON、1 Markdown、32 PNG），
  总计 998,658 字节。
- 该程序包页面配置含 24 个页面、30 个锚点；30 个锚点引用的模板全部存在。
- 当前源码测试明确要求 `challenge_health_depleted` 页面；原项目旧配置不含该页面，
  交接程序包配置包含该页面，因此不能用 `D:/Codex-2/config` 的旧版本替代。

交接程序包三个启动必需配置的 SHA-256：

```text
community.json                         CEF1CBA3D305DA677D0E024EC7296BB8E2C69E3E99E499E2502375A4D6C3EFDB
navigation-flow.json                   48C07FFE8CFCF27F6885C6B775D38CE6DF0FEF004AE3A9DE4C42975D60F918A4
page-recognition.1920x1080.json        5B3FB70CF1FC2356C9DDF6683961636DC145EBA1A9621CA597F0CC4DE0FBED54
```

## 针对性测试复现

命令：

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --nologo `
  --filter "FullyQualifiedName=CurrencyWarsAssistant.Tests.GamePageClassifierTests.ClassifierRecognizesAllPrivacySafeReplayFrames"
```

结果：1 项失败。`GamePageRecognitionConfig.Load` 抛出
`DirectoryNotFoundException`，缺失路径为工作副本根目录下的
`config/page-recognition.1920x1080.json`。

## 真实启动链复现

为避免 UAC 干扰，使用构建出的 DLL 入口运行项目自带无界面批处理命令，输入为现有
`Fixtures/PageReplay` 图集。5 秒内进程没有正常退出，必须终止；
`batch-startup.log` 仅记录：

```text
command-parsed
wpf-started
```

用户日志记录两项启动异常：

```text
DirectoryNotFoundException: ...\config\navigation-flow.json
DirectoryNotFoundException: ...\config\page-recognition.1920x1080.json
```

异常发生在 `App.OnStartup` 配置加载阶段。由于全局 Dispatcher 处理器把异常标为已处理，
无界面模式既未进入批处理的异常退出分支，也未关闭 WPF 应用，表现为无输出并一直挂起。

## 根因

交接源码打包时漏掉了项目声明、启动代码和测试共同依赖的整个 `config/` 运行资产目录。
这是缺失构建/运行输入，不是识别算法本身的失败。
