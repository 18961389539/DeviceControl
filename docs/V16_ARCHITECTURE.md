# DeviceControl V1.6 — 多工站灰度发布、证据门禁与版本一致性

## 范围

V1.6 在 V1.5 `StationUpgradeManager` 之上新增 `FleetRolloutManager`，只管理**仿真工站的软件工作流及配方**，不涉及板卡、相机、运动驱动及真实设备。本地账号及 JSON 日志是演示实现，不适合作为生产认证或不可篡改审计。

## 关键决策

- **显式步进，不自动异步推送**：每次 `advance` 只执行试点（1 站）或后续最多 3 站，使用现有 V1.5 的“预检计划 → SQLite 在线备份 → 逐站版本发布 → 失败补偿”，不引入跨站分布式事务。
- **试点后才能扩批**：试点成功后进入 `AwaitingEvidence`；需在升级完成之后产生实际 `Completed` 仿真周期，且周期记录中的 `FlowVersion` 与 `RecipeId` 与这次部署记录相等。没有证据绝不自动放行。
- **每批都要验收**：后续每波次也先进入 `AwaitingEvidence`；当前批次全部验收才能进入 `WaveReady`。验证时检查空闲、无报警/恢复要求、流程和配方版本一致，且 `WorkflowQualityGate.Evaluate` 的确定性质量场景通过。后续周期出现失败、中断、停止则拒绝通过该批次。
- **基线乐观锁**：创建批次时记录每台工站当前发布的流程 ID、配方 ID；真正部署前重新检查，外部发布了新版本则暂停；已经验证的试点如发生版本漂移，后续扩批也会被拦截。
- **持久化操作意图**：文件 `data/rollouts/roll-*.json` 先记录 `Deploying`、每站 `Applying`，再执行单站部署。进程重启后发现未收尾 `Deploying` 时按 `Interrupted` 读取，禁止自动重试；管理员必须检查部署及备份记录，然后通过关闭批次结束本批操作。日志通过同目录临时文件再 `File.Move(..., overwrite:true)` 写入；不是跨文件系统原子事务，更不是防篡改日志。
- **失败暂停、手工终止**：一个工站部署失败，已经成功的工站**不会自动回滚**，未执行的工站仍是 `Pending`。出现 `Failed` / `Applying` 的批次不可点击“继续”，避免对不确定状态进行盲重试。管理员使用 V1.5 单站部署历史、日志及数据库备份核查之后，可终止本批，再针对各站按单站恢复规则处理。

## 状态机

```text
Create [PilotPending]
  └─ Advance → Deploying → AwaitingEvidence (pilot)
      └─ Verify with real run + quality gate → WaveReady
          └─ Advance → Deploying → AwaitingEvidence (1..3 stations)
              └─ Verify → WaveReady / Completed

PilotPending | WaveReady | AwaitingEvidence → Pause → Paused → Resume
Any active rollout → Abort (does NOT rollback stations)
Deploy failure or stale baseline → Paused (failed step: manual reconciliation)
Process exit during Deploying → Interrupted (no automatic resume)
```

## API 与权限

| 方法 | 路由 | 最低角色 | 作用 |
|---|---|---|---|
| GET | `/api/rollouts` | Engineer | 列出灰度发布记录 |
| GET | `/api/rollouts/{id}` | Engineer | 批次详情 |
| GET | `/api/rollouts/{id}/evidence` | Engineer | 只读验证，展示缺少的周期或质量证据 |
| POST | `/api/rollouts` | Admin | 创建计划 `{templateId,pilotStationId,stationIds,waveSize}`，2–12 台、每批最多 3 |
| POST | `/api/rollouts/{id}/advance` | Admin | 推进当前试点或波次，调用 V1.5 逐站升级 |
| POST | `/api/rollouts/{id}/verify` | Admin | 证据齐全时验收当前波次 |
| POST | `/api/rollouts/{id}/pause` | Admin | 暂停，参数 `{reason}` |
| POST | `/api/rollouts/{id}/resume` | Admin | 没有失败/中断步骤时继续 |
| POST | `/api/rollouts/{id}/abort` | Admin | 关闭批次，不自动回滚；参数 `{reason}` |

原 `/api/upgrades/*`、`/api/templates/*`、`/api/stations/*` 和旧 `/api/*` 接口保留。保守的灰度调度拒绝两个未关闭批次占用同一工站，防止并行部署写入版本冲突。批次记录不会修改已发布模板。

## UI 操作流程（无需硬件）

1. Admin 在「配置模板与部署」捕获并验证、发布 A 的模板；输入 `A,B`，选择 A 试点，设置每批数量后创建计划。
2. 点击「升级当前批次」，A 进入 `AwaitingEvidence`。此时「检查证据」显示没有使用新流程和新配方的成功周期，继续升级按钮禁用。
3. 切换 A 工作区，手动使能并回零轴，切换自动，启动一个正常仿真周期，结束后切回手动。返回灰度区执行「检查证据」→「确认批次验收」。
4. 状态进入 `WaveReady`，点击升级 B，重复仿真周期和验收，最终状态为 `Completed`。
5. 验证异常时保持 `AwaitingEvidence`；可暂停排查、在无失败步骤时恢复。升级失败进入 `Paused`，不自动重试或覆盖。关闭批次不会替各站回滚。

## 测试边界

- `tests/test_browser_v16.py`：在 Chromium 运行**实际**模拟周期，测试试点证据阻塞、双站批次、暂停恢复及全部完成。
- `tests/test_browser_v16_conflict.py`：Engineer 权限限制、自动模式部署阻塞、失败步骤拒绝恢复、手动关闭、创建批次后外部配方发布产生版本冲突。
- `tests/test_browser_v15_latest.py`：在**最新 V1.6 页面**回归 V1.5 单站部署与回滚。
- `tests/test_rollout_files_v16.py`：静态源码/API/RBAC 接缝检查，**不等于 C# 编译**。
- `tests/test_api_v16.py`：需要 .NET 10 SDK，检查 HTTP RBAC、试点证据阻塞、暂停/恢复/关闭、持久化重启。这些 .NET 检查在当前环境**未运行**。
- CI `bash run-tests.sh --require-dotnet`：强制 C# 构建和合同/HTTP 门禁；在没有 SDK 的机器上失败，不得将跳过的测试算通过。

## 仍未实现

完整 .NET 后端编译及 HTTP 验收、真正的批量原子回滚、跨进程锁、生产身份认证、防篡改签名日志、自动运行测试工站、报警趋势定量阈值以及可配置维护窗口。都不在 V1.6 RC 已验收范围。
