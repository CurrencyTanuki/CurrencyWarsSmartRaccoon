# ISSUE-020 验证记录

## 当前状态

- 真实归档、排行顺序不变量和代码调用路径已经确认；
- 修前真实分析路径：`3,545` 与 `8,863` 两项均失败，实际分别为 `35,450,000` 与 `88,630,000`；
  - `test-results/before-targeted.trx`
  - SHA-256：`C9E0101C307E7115FAADFA67B7B517D2764D4E0002862252DA75F7E15ED6D7BE`
- 修前可靠性合同：新增规范千分位 `1,001` 期望 true，实际 false；其余四行通过；
  - `test-results/before-reliability.trx`
  - SHA-256：`B97D64E5D8A2751F73D9E2C80EF91EBEC024C066AA3E875C44D684346849F431`
- 第一版过宽实现虽然 focused 10/10，却使七张既有 live 战斗图的伤害缩小一万倍；该版本被拒绝并保留原始 TRX：
  - `test-results/after-phase2-operational-class.trx`
  - SHA-256：`251511719F8C338ED46A8304367B1A56F38E875C8A904B423B343B6F6AD9BE26`
  - 结果：`171 Passed / 7 Failed`
- 最终混合规则：规范整数千分位按基础值；无单位小数继续按万位兼容。
- 独立审查指出 `0,001`/`01,234` 不应被当作规范千分位；新增两条保护测试修前均失败：
  - `test-results/before-leading-zero-guard.trx`
  - SHA-256：`56D18188B8CA6E901602B36D6C62C04B406DE5D5287C27D729026DD9FE476116`
  - 结果：`5 Passed / 2 Failed`
- 最终 focused：`19/19 Passed`
  - `test-results/after-focused-final.trx`
  - SHA-256：`8532B203BAC9D7E4D361D8DCE29B13E7058085F7F221029B81EF8A4E8BBCD104`
- 最终 `Phase2OperationalCollectionTests`：`180/180 Passed`
  - `test-results/after-phase2-operational-class-final.trx`
  - SHA-256：`A83D4868F7CD7EACC6C84A28C5408557042C71F91E20FB46DC5856CC6E171052`
- 中间全量（前导零规则收紧前）：`818 = 815 Passed / 1 Failed / 2 NotExecuted`
  - `test-results/after-full.trx`
  - SHA-256：`E89B40E59AF112B94D6E5437B0CCBC199F4D2DFAF7B9B1C6AE8ABD232E3BE292`
  - 唯一失败为刻意保留的 ISSUE-019 修前队列红灯；其余既有与本项测试全部通过。
  - 相对 ISSUE-018 的 814 项全绿基线：新增 4 项（本项 3 项 Passed、ISSUE-019 红灯 1 项 Failed），缺失 0、既有结果变化 0。
- 最终 Release build：`0 warnings / 0 errors`
  - log SHA-256：`A0F3AED2260E66F06C76DF6D0AA1CA61F828A25F47FD91BED711406A81819171`
  - binlog SHA-256：`E37B55B919DCC3905801855654BDACEFF435E24F4DA20C957C291E5BFE03DF32`
- 最终生产源码 SHA-256：`63F8492BC8FB16FBA475A13DE863040EF7E487F09D9294F4CCA53D21011C9C95`
- 最终测试源码 SHA-256：`0A5CB0486E0D42FED4911871025B699685BC6656914F97C10327BC095CCF310A`
- 最终全量将在 ISSUE-019 队列红灯修复后统一重跑；在该全量完成前不宣称整个项目全绿。

## 独立终审

独立只读审查已批准 ISSUE-020 的限定范围：三组先红、拒绝过宽补丁、最终源码/测试差异、19/19、180/180、Release build 及结算兼容边界均已复核。批准不包含 ISSUE-019、行动值、战后血量、历史 UI 或项目级可交付结论。
