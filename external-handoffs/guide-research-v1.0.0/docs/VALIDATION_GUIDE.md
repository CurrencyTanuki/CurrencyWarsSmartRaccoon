# 校验工具使用说明

环境：Python 3.10 或更新版本；不需要安装第三方包，不需要联网。

## 校验包内示例

```powershell
python tools/validate_all.py --root .
```

## 同时校验外部 AI 的输出

```powershell
python tools/validate_all.py --root . --include-output
```

校验器会执行：

1. 两份冻结 Schema 的必填字段、类型、枚举、范围、未知字段和格式检查；
2. 标准 ID 存在性检查；
3. evidence set、claim、action、phase 和引用一致性检查；
4. 视频内容结论的时间点检查；
5. 重复 ID 检查；
6. 标准 ID 目录的数量和 SHA-256 检查；
7. 每个合法文件重复读取后的规范化结果一致性检查；
8. 故意非法样本必须被拒绝。

错误示例：

```text
output-template/playbooks/example.json: $.actions[0].evidenceRefs[0].claimId: unknown claim 'x' in 'evidence-y'
```

这表示具体文件、字段路径和失败原因。不要删除字段或改成空值规避错误；修正真实数据或显式写 unknown。

## 输出约定

- 成功：退出码 0，首行 `PASS`。
- 失败：退出码 1，首行 `FAILED`，随后逐项错误。
- `examples/invalid` 中的文件是测试夹具，不应复制到 output。
