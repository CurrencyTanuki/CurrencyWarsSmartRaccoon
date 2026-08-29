# 货币战争智能狸 0.2.757 更新说明（候选审计版）

> 修复用户实机反馈的第一个错误（0.2.756 测试中发现的界面布局回归）。

## 背景

用户在 0.2.756 实机测试中发现：开局词条按钮区**从原来的多行多列网格变成单列**，
并出现"加载并编辑"手动加载按钮。用户判断：该手动加载实际是瞬间完成的，
应用启动慢的原因根本不在此，要求撤销该改动。

## 根因

该回归由两个候选修改叠加造成：

1. **0.2.752-2 虚拟化改动**：开局筛选列表的 ItemsPanel 从 `WrapPanel`（多列网格）
   改为 `VirtualizingStackPanel`（纵向单列）——导致词条按钮"只剩一列"。
2. **0.2.755-21 按需加载改动**：开局词条编辑区默认折叠（`Collapsed`），
   需点击"加载并编辑"按钮才展开，并显示"为保证软件立即可操作…"占位说明。

## 0.2.757 修复内容

1. **恢复多列网格布局**：筛选列表 ItemsPanel 恢复为 `WrapPanel`（一排排、一列列），
   移除 `CanContentScroll=True` 与虚拟化相关 Setter（`IsVirtualizing`/`Recycling`/`CacheLength`）。
2. **撤销手动加载**：删除"加载并编辑"按钮与占位说明（`OpeningFiltersPlaceholder`），
   开局词条编辑区（`OpeningFiltersSection`）恢复启动即显示（默认可见）。
3. **清理代码**：删除 `OnLoadOpeningFiltersClick` 事件与 `_openingFiltersLoaded` 字段。
4. **契约测试同步更新**：`OpeningFilterListsUseMultiColumnWrapLayout`（断言 WrapPanel 多列、
   无纵向虚拟化）与 `OpeningFilterEditorIsVisibleImmediatelyWithoutManualLoad`（断言启动即显示、
   无手动加载按钮）。

## 说明

- 词条筛选数据本身仍是启动时从保存配置加载（`LoadUserSettings`），只是界面不再折叠。
- 本版本不改变识别栈、数据结构和任何识别/结算逻辑。

## 测试

- 全量回归：**557/557 通过，0 失败，0 跳过**（含更新后的界面契约测试）。
- 启动冒烟：单实例保护在已有实例运行时正确拦截新实例退出（实测验证）。
- 无实机对局验证（本机未安装游戏）。

## 交付物

- 候选发布包：`artifacts/CurrencyWarsSmartRaccoon-0.2.757-win-x64-portable.zip`（162.8 MB，1736 条目）
  SHA-256：`691056DF94CA86DFC0463419E428FF741389666FE23392D5AEFADFAB574887C6`
- 回退基线：`release-baseline/CurrencyWarsSmartRaccoon-0.2.751-win-x64-portable.zip` 保持不动。
- 上一候选：0.2.756 zip 保留在 artifacts/（如需回退比对）。
