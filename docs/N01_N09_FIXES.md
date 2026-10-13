# N01–N09 修复说明（2026-10-13）

本轮变更针对软件仿真后端和本地工作室，不构成真实硬件、安全控制或工业认证。正式完整门禁仍以 `tools/release_gate_v20.py --profile release` 的最终结果为准。

## P1

| 编号 | 修复 | 可读回归 / 当前证据 | 限制 |
|---|---|---|---|
| N01 | 部署恢复只在指定站点、指定部署 ID 的同步恢复上下文中忽略该条未解决记录；同站其他部署仍阻断。恢复要求 Manual、Idle、备份 Verified，并校验可识别的 Before/After 版本。若 Interrupted 记录缺少 AfterFlowId 等关键证据，普通回滚拒绝；管理员必须显式提交与当前活动版本完全相同的 expected IDs 和 8–500 字核对说明，核对人、时间、观察版本与说明写回部署记录。Succeeded 版本漂移不能用该通道覆盖。嵌套配方和流程恢复沿用受控上下文。 | `RecoveryRegressions` 覆盖真实模板部署、Interrupted 回滚、AfterFlowId 缺失时无证据/错误 ID 拒绝、准确核对恢复并记录证据、Succeeded 漂移拒绝、快照刷新无缺失窗口。根代理另外执行了独立 HTTP 恢复和组合恢复检查，见 `reports/quality-fixes-20261013/`。 | 当前活动版本 ID 是人为观察输入，服务端精确比对；该机制不推断中断时究竟提交到哪一步，也不替代现场核验。不能据此宣称现场硬件恢复安全。 |
| N02 | 重启恢复在同一 SQLite 事务中持久化 Interrupted、恢复要求并删除 inflight。恢复要求拒写时整个事务回滚。 | `RecoveryRegressions` 覆盖成功重启恢复以及触发器注入的恢复写拒绝和重试。 | 未验证 72 小时、断电硬件和干净机器环境。 |
| N03 | 手动动作先提交持久意图；完成成功后才清意图，失败时将恢复要求和清意图同事务提交；写拒绝保留意图。当前 inflight 周期的 Failed/Stopped/Interrupted 结果与恢复要求及 marker 清理同事务，写拒绝会回滚终态结果并保留 marker。 | `RecoveryRegressions` 覆盖手动意图拒写后重启门禁、人工确认清理，以及周期 Failed 写拒绝后重试和 Stopped 持久门禁。 | 自动周期日志记录及 SQLite 属于软件仿真证据，不代表物理设备状态。 |
| N05 | `StationUpgradeManager` 用不可变快照原子替换未解决部署索引；`RunEligibilityRegistry` 以锁保护注册状态，未注册工站 fail closed。注册/配方初始化顺序调整，新站初始化失败会清理 journal 和注册状态。 | `RecoveryRegressions` 对升级索引并发刷新期间读取部署阻断做回归；严格门禁中的 `test_api_v14.py` 覆盖动态工站接口。 | 快照只保证进程内原子读取、注册表只保证进程内同步，不是跨进程协调协议。 |

## P2

| 编号 | 修复 | 可读回归 / 当前证据 | 限制 |
|---|---|---|---|
| N04 | 新增 `frontend/api-client.js` 的 `DeviceControlApiClient.create()`：同一个 deadline 覆盖响应头、完整限长正文和 JSON 解析；超时写操作标记结果未知且不自动重发。主界面探测、工作室和离线预览均使用共享客户端。 | `tests/test_api_client_v20.mjs` 覆盖慢正文、超时写只发一次、非法 JSON、成功响应、脚本加载顺序和各工作室接入。 | 服务端是否已完成超时写入需要通过运行记录和审计另行核对。 |
| N06 | 损坏模板目录和灰度记录启动时隔离并保留原文件与诊断；灰度读取仍可用，存在启动损坏时禁止创建或推进等写操作；readiness 报告损坏。 | `RecoveryRegressions` 覆盖损坏模板/rollout 的隔离、诊断和拒写；根代理独立 HTTP 验收覆盖 health/ready/diagnostics 与原文件保留。 | 隔离不会自动修复或删除损坏文件；须人工检查后处理。 |
| N07 | 审计根据 HTTP 结果区分 Accepted、Rejected、Cancelled、Failed；异常保存类型、消息和 `Exception.ToString()` 诊断详情。未开始的 500 响应返回统一非敏感 JSON 并恢复 command/trace/request 关联头；响应已开始则保留异常重抛语义。审计写入失败不掩盖原请求错误。 | C# contract 覆盖 500→Failed、异常详情；根代理对最终异常中间件补丁单独重建并独立 HTTP 验收 13 项，其中真实 500 响应头与审计 CommandId/TraceId 一致、ErrorDetail 持久。完整门禁基线在该最后补丁前为 46 PASS / 0 FAIL / 1 BLOCKED；最后补丁有单独构建和 HTTP 复验。 | 审计 SQLite 不是防篡改账本；诊断内容需避免传入敏感数据。 |
| N08 | 加入共享 `tests/dotnet_harness.py`，直接运行当前 Release DLL；源文件晚于 DLL 时先重建。测试服务器启动失败会清理进程树，正常退出先 graceful stop、超时后 Windows taskkill `/T`。恢复沙箱测试将符号链接能力不足用退出码 77 和 BLOCKED 输出报告，严格 gate 不当作 PASS；CI runner OS matrix 保持 Node 22。 | 已执行 API v10 真实 HTTP kill/restart；Windows 本机符号链接预检实际返回 `BLOCKED N08`、退出码 77。完整发布 gate 正由根代理运行。 | Windows 本机无 symlink 创建权限，无法在此环境验证符号链接通过路径；Linux CI 完整 symlink 执行尚无结果。目录 junction 不能代替此项。 |
| N09 | `/api/ready` 检查工站存储、恢复/部署阻断、启动历史损坏和失败工站；`/api/health` 维持 liveness。审计数据库 API 分页并对含下划线的站点 ID 做字面前缀比较。写 API 通过 `X-Command-Id` 响应/审计/runtime 事件关联；`X-Trace-Id` 与 runtime 事件使用同一 Activity trace ID，`X-Request-Id` 保留 ASP.NET 请求 ID。Runtime 事件使用有界 Channel；宿主与动态工站关闭时 drain，日志超过 8 MiB 时用临时文件原子替换并保留尾部；异常事件保留完整异常详情。 | C# contract 覆盖 A_B 与 AXB 的审计隔离、command correlation、错误审计详情和 recovery 状态故障；API v10 通过 helper 执行实际重启；根代理独立 HTTP 验收覆盖 readiness 503、损坏诊断和超过 2100 条历史分页。 | 日志队列满时事件计入丢失诊断，系统不保证不丢事件；SQLite 审计和本地日志均不是防篡改介质。轮转保留最近约 1 MiB，不是远程归档方案。 |

## 本轮未全面解决的架构主题

G01–G03 仅在支撑上述缺陷修复的范围内改动，没有进行整体重构，也没有声称达到工业安全、故障容错或认证要求。未执行长时间稳定性、真实硬件、干净机器或 Windows symlink 通过验证。Windows symlink 检查为 BLOCKED，完整门禁因此未获发布批准；最终异常中间件补丁单独完成构建与 HTTP 复验，但没有在补丁后重跑整套门禁。
