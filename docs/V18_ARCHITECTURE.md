# DeviceControl V1.8 RC — 重启恢复、备份校验与只读演练

**范围**：单进程、多工站、仅仿真。非真实硬件控制，不承担功能安全职责。

## 已实现

1. **启动恢复持久化**：`StationUpgradeManager` 构造时扫描 `data/deployments/dep-*.json`，将 `InProgress` 原子重写为 `Interrupted`。`FleetRolloutManager` 构造时扫描 `data/rollouts/roll-*.json`，将 `Deploying` 原子重写为 `Interrupted`。`Program.cs` 在对外提供服务前显式实例化两个 manager。重复启动不会反复更新已处理记录。扫描遇到损坏 JSON 时启动失败（fail closed），不会忽略未知状态。
2. **不自动变更**：启动路径不触发预检、发布、周期执行或回滚；旧 `Advance` 对 `Interrupted` 拒绝；旧 `Reconcile` 仍要求原始流程/配方版本、空闲手动无报警、没有未解决的单站日志。
3. **备份哈希只读核验**：`GET /api/upgrades/{id}/integrity` 验证备份路径在部署目录内、文件存在、SHA-256 与日志一致，返回 `Verified / Missing / InvalidPath / Mismatch / Unreadable`。`Verified` **不等于**通过整个数据库内容/业务一致性恢复演练；SQLite 在线备份时的完整性检查由既有 `CreateVerifiedBackup` 执行。此处不复制、不恢复数据库。
4. **灰度只读恢复报告**：`GET /api/rollouts/{id}/recovery-report` 汇总每站原始/目标版本匹配、步骤状态、最新相关部署记录、备份哈希与手动恢复阻断。`ReadOnly=true`，不能证明跨站事务完成，不能自动审批。
5. **权限**：备份检查与灰度恢复报告均要求 Engineer 或更高，其他修改仍要求 Admin；UI 禁用并不替代服务端校验。
6. **离线故障注入**：三个纯 JS 独立测试场景：写入意图后服务退出；流程/配方版本分裂；备份校验不一致。不会写入真实批次，也不修改 A/B 配方或状态。浏览器刷新后离线会话内容丢失。

## 关键状态与人工恢复

```text
[InProgress / Deploying] --应用进程重启--> [Interrupted: 持久化]
                                             |
                                             v
                                   只读诊断 / 哈希核验
                                             |
                   原始版本不匹配或日志未完结 → 拒绝盲目重试
                                             |
            人工复核日志、SQLite 备份、版本和设备仿真状态
                                             |
            现有 V1.7 人工核对（核对 != 自动部署）
```

批次不是跨站原子事务；程序退出期间可能存在已写入单站数据但批次日志未记录结果。当前策略将其视作**不确定结果**，而不是推断成功。需要独立人工处理和后续明确操作。

## API / 测试

```bash
node tests/test_recovery_v18.mjs
python tests/test_recovery_v18_static.py
python tests/test_browser_v18.py
python tests/test_browser_v18_compat_v17.py
python tests/test_browser_v18_compat_v16.py
# 必须在拥有 .NET 10 SDK 的环境执行
bash run-tests.sh --require-dotnet
# 包括 tests/test_api_v18.py：真实 HTTP + 进程重启 + 篡改备份哈希
```

## 未完成与风险（发布门禁）

- **当前环境未安装 .NET 10 SDK**：C# 编译、DI 实例化、HTTP、真实服务重启和 SHA-256 API 的端到端验证均未在本环境通过；CI 配置了真实验收脚本，但配置不代表通过。
- 只读备份哈希无法单独证明 SQLite 文件本身可恢复、运行时数据与操作系统 flush 完整性。
- 日志采用临时文件原子替换，未实现跨 SQLite 与多个 JSON 文件的分布式事务或显式 `fsync` 级断电保证。
- 仅在**启动时**标记中断；同进程的 I/O 异常、写盘中断仍可能要求人工核查。单进程锁不能解决多个服务器同时写同一目录的问题。
- 没有自动回滚整批灰度，也不会直接使用备份进行数据库覆盖。

下一步应先解决 .NET 构建/HTTP 发布门禁，再建立服务运行时故障注入、人工审核后的可重复恢复工单以及备份恢复隔离沙箱。保持 RC，不可视为生产上线版本。
