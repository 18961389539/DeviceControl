# DeviceControl V1.5 — 已有工站的软件版本部署 / 回滚

## 范围

仅用于本地软件仿真。保留 V1.4 创建新工站的 API，本轮新增**已有工站**的已发布工作流与配方版本升级。**不允许热替换** 机构、IO 信号映射、轴、气缸、互锁和硬件驱动。完全不接入真实设备。

## 升级时序

1. Engineer 调用 `POST /api/upgrades/preflight`，传入 `{ "templateId": "tmpl-...", "stationId": "B" }`。检查模板 Published、目标 Idle+Manual、非 busy、无故障恢复标记、站点静态结构完全一致、无未解决的中断部署。成功生成 10 分钟有效且一次性使用的 `plan-...`，绑定当前 Flow/Recipe 版本 ID、模板 SHA-256；失败返回 `ready: false` 和原因，不下发授权令牌。
2. Admin 调用 `POST /api/upgrades/apply`，传入 `{ "planId": "plan-..." }`。在 `DeviceRuntime.WithConfigurationChange` 锁内**重新检查** 模板、站点状态和基线版本。拒绝重复消费、过期或旧版预检。
3. 先写入 `data/deployments/dep-...json`（`InProgress`）保存发布前 FlowRevision / RecipeRevision 逻辑快照，再由 `SqliteStore.CreateVerifiedBackup` 通过 SQLite Online Backup 生成 `data/deployments/backups/<station>/dep-....db`，执行 `PRAGMA quick_check` 并记下 SHA-256。备份失败时禁止执行版本发布。
4. 依次创建 Flow Draft → 服务端验证 → Publish，再创建 Recipe Draft → 验证 → Publish。保存新的版本 ID，将部署记录更新为 `Succeeded`。
5. 出错时尽最大努力回滚**本次已改变**的配方和流程。完全恢复则记录 `FailedCompensated`，否则记录 `ManualRecoveryRequired`。始终向调用者暴露失败，不能将补偿视为原子事务。
6. Admin 使用 `POST /api/upgrades/rollback` 和历史 `deploymentId` 人工回滚；`Succeeded` 状态必须确认当前活动 Flow/Recipe 就是此次发布结果，防止覆盖后续变更。历史成功时更新记录为 `RolledBack`，保留所有曾发布的不可变版本和备份。

## API / RBAC

| API | 方法 | 最低角色 | 语义 |
|---|---|---|---|
| `/api/upgrades/preflight` | POST | Engineer | 可执行预检，禁止读写真实设备 |
| `/api/upgrades/apply` | POST | Admin | 消费预检并逐站升级 |
| `/api/upgrades/rollback` | POST | Admin | 根据部署记录恢复已发布快照 |
| `/api/upgrades/history?stationId=B` | GET | Engineer | 查询持久化部署证据，可选工站筛选 |

常规服务端身份检查和审计中间件沿用 V1.4，但仅适合 `localhost` 演示账号；部署记录是可本地修改的 JSON，不构成防篡改证据。不要用于现场真实生产机器。

## 失败 / 重启边界

- 一次调用只升级**一台**现有工站。多工站顺序升级由管理员逐站执行；不提供虚假的跨站事务保证。
- 工作流 SQLite 与配方发布虽在同一站点，但不是同一事务，采用补偿策略。SQLite 在线备份是灾难恢复证据，**不是自动全库恢复命令**。自动回滚仅通过历史版本重新发布，保留现有数据和周期历史。
- 进程若在 `InProgress` 阶段异常退出，启动时历史记录仍在，服务**阻止该站新的部署**。Admin 必须检查状态/快照并通过回滚接口尝试恢复；失败的记录保留 `ManualRecoveryRequired`。目前尚未加入跨进程的强制恢复授权或双人审批。
- 预检计划只存在服务进程内，重启后失效；用户必须重新预检。历史审计和备份位于 `data/deployments/`，保留原路径。
- 在旧版本兼容界面中，A 仍可调用原 `/api/*` 单站路由；本轮新增部署路由不改变旧版流程、配方接口。

## 验收矩阵

- `node tests/test_upgrade_rules_v15.mjs`：离线结构兼容、空闲/模式/已发布检查。
- `python tests/test_upgrade_files_v15.py`：源码接缝与权限规则静态检查，**不是 C# 编译**。
- `python tests/test_browser_v15.py`：离线发布、部署、查看历史、回滚、自动模式拒绝。
- `python tests/test_api_v15.py`：需要 .NET 10 SDK；真实 HTTP 角色门禁、过期/重复预检、版本切换、SQLite 备份、回滚及重启后日志验证。
- 旧版 `test_browser_v13.py`、`test_browser_v14.py`：工站工作区与模板创建回归。

**本地验收限制：** 运行环境未安装 .NET 10 SDK，不能声称后端编译与 HTTP 测试已通过。完全软件发布门禁必须在 SDK 环境运行 `bash run-tests.sh --require-dotnet`。
