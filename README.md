# 货币战争小助手（Currency Wars Smart Raccoon）

面向《崩坏：星穹铁道》货币战争玩法的免费、开源 Windows 桌面辅助工具。

## 功能

- 自动发现游戏窗口并截图
- 识别开局信息：敌方阵营、负面词条、投资环境
- 投资环境/敌人/词条筛选（支持"不限/必须出现/必须刷掉"）
- 自动重刷开局：条件不满足时退出重开
- 备战页自动部署与商店购买（保留/自动买名单）
- 银河学者羁绊策略、三仙舟+2持续伤害预设
- 自动战斗检测（0.2s 快速检查 + 2s 慢速）
- 对局状态记录、历史节点看板、挑战总结报告（HTML）

## 环境要求

- Windows 10/11 x64
- .NET 8 Desktop Runtime
- 游戏客户区需 16:9；模板标准分辨率为 1920×1080
- 已实测：1920×1080、2560×1440

## 构建

```powershell
dotnet build CurrencyWarsAssistant.sln -c Release
dotnet publish src/CurrencyWarsAssistant.App -c Release -r win-x64 --self-contained
```

## 测试

```powershell
dotnet test tests/CurrencyWarsAssistant.Tests -c Release
```

## 说明

- 本软件由项目官方免费提供，不收取任何费用。若你从任何渠道付费购买，请向商家申请退款。
- 本工具仅用于个人学习与研究，请遵守游戏用户协议。

## 联系方式

- QQ 群：726898246
- 官网：https://taskflowai.cn

## 许可证

本项目采用**非商业使用开源许可证**：任何人均可免费使用、修改和分发本软件，但必须**保留版权声明**，且**修改后的衍生作品必须同样开源**；同时**严禁任何形式的商业用途**（出售、集成到商业产品、营利活动）。详见 [LICENSE](LICENSE)。如需商业授权，请联系 QQ 群 726898246。
