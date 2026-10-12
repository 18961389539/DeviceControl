# V1.9 隔离恢复沙箱设计

## 边界

- 用户输入为部署 ID，不能输入备份文件路径；后端要求备份路径精确对应 `data/deployments/backups/<station>/<dep-id>.db`。
- 原始备份来自 SQLite Online Backup，不能从正在写入的 WAL 数据库直接做裸文件复制。
- V1.9 复制**已完成的部署备份**而非生产数据库；校验复制出的字节，不对运行数据库发 SQL。
- 读取沙箱副本时使用 SQLite ReadOnly 模式，仅执行 `PRAGMA integrity_check` 和活动版本 SELECT。
- 必须同时满足：路径、大小、SHA-256、完整性、活动流程/配方与部署前基线匹配；否则 `backupValidated=false`。即使通过也始终 `requiresManualReview=true`。
- 不暴露磁盘路径给 API 客户端，不向 Runtime 写入，不自动推进灰度、不更改部署历史。
- 诊断临时副本应定期在运维中检查是否残留；失败清理会在报告里留下阻断项。

## 测试矩阵

| 用例 | Python 独立沙箱 | C# HTTP 门禁 |
|---|---|---|
| 正常 SQLite 快照 | 已验证 | 待 SDK |
| 备份哈希不匹配 | 已验证 | 待 SDK |
| 路径越界 | 已验证 | 待 SDK |
| 部署前流程/配方版本不匹配 | 已验证 | 待 SDK |
| 损坏数据库，即使哈希一致 | 已验证 | 待 SDK |
| 源文件与部署日志不改变 | 已验证 | 待 SDK |
| Operator/未登录拒绝 | 静态授权检查 | 待 SDK |
| Chromium 离线界面提醒无备份可验 | 已验证 | 不适用 |

## 后续 P0

1. 在已装 .NET 10 SDK 的 Windows/CI 机器上实际编译，执行 `bash run-tests.sh --require-dotnet` 和 `tests/test_api_v19.py`。
2. 完成真实服务重启、SQLite/WAL 恢复、双站并发服务停止、强制中断场景的可复现集成验收。
3. 在模拟数据沙箱独立实现人工审批后的“恢复操作建议”，但任何真实数据写回须另立安全设计与审核。
