# 1920×1080 识别模板

`pages/` 存放由游戏截图裁出的页面锚点。运行以下命令可以重新生成模板与测试用的脱敏回放帧：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\tools\Extract-RecognitionTemplates.ps1
```

生成脚本会先把 16:9 截图统一缩放到 1920×1080，再按标准坐标裁图。测试回放帧会遮盖左下角 UID 区域及可能包含自定义角色名的队伍文字区，模板本身也不得包含 UID、账号名或其他个人信息。

页面定义位于 `config/page-recognition.1920x1080.json`。新增模板时应同时记录：

- 游戏版本、画面比例和 UI 缩放；
- 锚点在标准画布上的搜索区域；
- 经过真实截图回放验证的匹配阈值。
