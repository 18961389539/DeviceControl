# DeviceControl V1.7 — 多工站维护窗口、批次诊断与人工恢复

> **SIMULATION ONLY**: 纯软件演示；不存在真实硬件驱动、运动安全回路、生产身份认证和不可篡改审计。状态/诊断并不构成工业现场安全许可。

## 核心设计

V1.7 继续使用 V1.6 `FleetRolloutManager` 和 V1.5 `StationUpgradeManager`。仍然是逐站显式发布，不引入后台任务或跨站原子事务。已经成功部署的站点在暂停/中断时不会自动回滚。

**维护窗口（可选）**
- 创建批次可提交 `windowStartUtc` 与 `windowEndUtc`，必须同时存在，必须是含 `Z` 或 `+00:00` 的 UTC ISO-8601 字符串，窗口满足 `start < end`、最长 7 天。
- 时间区间为 **[start, end)**；未设置表示允许人工随时开始（仍受所有旧权限/预检控制）。
- 管理员点击 `advance` 前，**以及每一个待升级工站开始部署前**，服务端都检查当前 UTC 时间。窗口关闭时拒绝或暂停后续部署，但不杀死已经开始的单站升级。
- 到达窗口时间不会自动创建计划、重启服务或执行任何部署；窗口结束不会强行停止已经在执行的步骤。证据查看、人工处理和批次验收仍可在窗口外执行。

**诊断（只读）**
- `Diagnose` 同时展示批次维护窗口状态与各站的 `Mode/State/Busy/Alarm/RecoveryRequired`、当前 Flow/Recipe 版本、和 V1.5 `InProgress`/`Interrupted`/`ManualRecoveryRequired` 日志。
- 对 `Pending`/`Failed` 工站比对部署前版本；对 `AwaitingEvidence`/`Verified` 工站比对目标版本。诊断不会修改 Runtime，不会自动重试/解除报警。

**人工恢复（显式、保守）**
- 仅 Admin 能在批次 `Paused`/`Interrupted` 时 `reconcile` 一个 `Failed`/`Applying` 步骤。
- 需要 8～500 字备注，当前工站空闲手动、无报警/恢复标志，**当前 Flow/Recipe 与最初预检基线完全一致**，并且没有未解决的 V1.5 单站部署日志。
- 仅将对应步骤记为 `Pending`，把 `recoveryNote`、`recoveryActor`、`reconciledAt` 写入持久化批次日志，保持 `Paused`。必须显式 `resume` 然后 `advance` 才能重试；重新走 V1.5 预检及备份。若有部分版本已写入或日志仍未恢复，坚决拒绝；需人工单站回滚/排查，必要时关闭批次。`abort` 仍然不回滚已部署工站。
- 进程中断时读取原有 `Deploying` 状态为 `Interrupted`，拒绝自动继续。JSON 批次日志仅本地持久化，非强一致分布式事务日志。

## API / 权限

| 方法 | 路由 | 最低角色 | 功能 |
|---|---|---|---|
| POST | `/api/rollouts` | Admin | 原有创建字段 + 可选 `windowStartUtc`,`windowEndUtc` |
| GET | `/api/rollouts/{id}/diagnostics` | Engineer | 只读批次/站点诊断 |
| POST | `/api/rollouts/{id}/reconcile` | Admin | `{stationId,note}` 手工核对后解除一个失败步骤 |
| POST | `/api/rollouts/{id}/advance` | Admin | 保留旧协议，增加每站窗口校验 |
| GET | `/api/rollouts/{id}/evidence` | Engineer | 原有运行证据，不受维护窗口限制 |
| POST | `/api/rollouts/{id}/verify` | Admin | 原有验收，不受维护窗口限制 |

所有新接口服从 `Permissions.Required` 的 `/api/rollouts` 路由规则：GET ≥ Engineer，POST ≥ Admin。旧单站接口和配方/流程版本管理保持不变。

## 人工操作与验收场景

1. 用 Admin 登录，捕获/验证/发布模板。创建包含 A/B 的批次，维护窗口设为未来时间；点击升级应拒绝，诊断报告显示窗口关闭，工站不变。
2. 关闭上述批次。创建当前开放的窗口，B 做试点；先把 B 设为自动模式，再执行升级，批次因预检失败暂停。
3. 诊断显示 B 模式问题。把 B 切回手动模式，在“人工核对说明”写入足够清晰的备注，执行核对；它只变为 `Pending`，批次依然 `Paused`。
4. Admin 显式恢复，显式升级 B 后进入 `AwaitingEvidence`；仍需 V1.6 的实际仿真周期和质量门禁才能向 A 扩批。
5. 如单站实际版本不等于原始基线，或 V1.5 日志存在未解决的部署，必须拒绝核对；通过单站升级记录人工恢复，不能绕过。

## 测试与发布边界

- `tests/test_browser_v17.py`：已运行真实 Chromium 离线流程：窗口关闭、诊断、失败暂停、非法短备注、恢复、显式重启与部署。
- `tests/test_browser_v17_compat_v16.py` 等：在 **V1.7 最新 HTML** 上跑 V1.6 灰度证据和 V1.5 单站回滚兼容回归。
- `tests/test_rollout_v17_static.py`：代码/API/UI 静态接缝，不是 C# 编译。
- `tests/test_api_v17.py`：.NET 10 的实际 HTTP 和重启持续性门禁，已加入 CI；**当前生成环境没有 dotnet，未运行**。
- `bash run-tests.sh --require-dotnet` 是强制发布门禁；无 SDK 时失败。本阶段不可标注为生产就绪。

本轮仍不涉及真实硬件、上线发布、自动重试策略、生产可靠性 SLA、工业安全认证或真实时区维护排程服务。
