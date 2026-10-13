# R01–R08 修复说明（2026-10-13）

本轮变更针对软件仿真后端和本地工作室，不构成真实硬件、安全控制或工业认证。

**最终门禁（release profile，含浏览器）：53 项任务，52 PASS / 0 FAIL / 1 BLOCKED（`python:test_recovery_v20.py`，Windows 符号链接能力不足，退出码 77，语义与复审基线一致：不计通过）。`releaseApproved: false` —— 阻塞项与本机符号链接环境有关，非本轮代码缺陷。证据：`reports/quality-fix-r01r08-20261013/gate/`（release-gate.json / txt / logs）。**

本机符号链接能力实测：`Path.symlink_to` 与 `cmd mklink` 均报告成功但产物不是重分析点（Python `is_symlink()` False、`readlink` 报 WinError 4390），属环境级拦截，无法在本机完成“具备 symlink 能力”的验收项；需在 Linux CI 或具备真实符号链接能力的主机上执行。因此 `test_recovery_v20.py` 的探测逻辑已由“不抛异常即视为可用”修正为“必须生成真实符号链接”，保持 BLOCKED/77 的诚实语义。

复审来源：`reports/quality-reaudit-20261013/`（R01–R08，P1×2 / P2×6）。用户选定 R01 采用**保守策略**（保留严格原始基线，明确关闭重建引导）。

## P1

| 编号 | 修复 | 回归 / 证据 | 限制 |
|---|---|---|---|
| R02 | `DeviceRuntime.cs`：所有长步骤（含 Delay）统一由条件监视包装执行——40ms 轮询 `OrdinaryInterlockMonitor`，条件失效以 linked CTS 取消该步骤并抛出互锁错误；周期完成边界再次复核取消、普通工艺条件与运行状态，禁止无依据提交 Completed。不再只依赖各驱动内部轮询。 | 新增 `tests/test_api_v21.py`：末尾 Delay 期间防护门失效 → Failed + 需恢复 + 不计数；分支 Delay 同样；运行中停止 → Stopped；干净周期 → Completed。周期记录、步骤记录、事件与快照一致。 | 仿真实时余量有限（40ms 轮询窗口）；不替代任何硬件安全功能；未做长期老化。 |
| R03 | `frontend/api-client.js` 重写：断流、坏 JSON、响应过大、超时、5xx 一律标记 `resultUnknown` 并保留本地生成的 commandId（服务端回显优先）与 traceId；4xx 业务拒绝保持普通错误并携带同一关联 ID；写操作从不自动重发。`app.js`/4 个工作室统一经 `describe()` 展示具体 ID 与核对指引。新增 `GET /api/commands/{commandId}` 与 `/api/stations/{id}/commands/{commandId}`（Engineer+）：汇总审计、未完结手动意图、周期与运行事件；审计页新增“命令查询”入口。`X-Command-Id` 写入 `manual_action_intents`、`run_inflight`、`production_runs`（v5 列），周期记录带 commandId。 | 新增 `tests/test_api_client_v21.mjs`（断流/坏 JSON/超大/超时/500/409 分类 + ID 保留 + 不重发）与 `tests/test_api_v22.py`（按命令 ID 核对审计/周期结果；RBAC；无效 ID 拒绝）。 | 已完成的手动意图在结果提交后删除（设计使然），其历史由审计行承载；幂等键未引入（服务端回显 X-Command-Id 仅作关联，不阻止重发）；96 h 老化未做。 |

## P2

| 编号 | 修复 | 回归 / 证据 | 限制 |
|---|---|---|---|
| R01 | **保守策略**：`FleetRolloutManager.Reconcile` 基线不一致的 409 改为完整可执行指引（单站回滚恢复内容会发布新版本 ID、内容恢复≠ID 恢复 → 关闭批次 + 重建）；Advance 的基线冲突步骤错误与批次原因同样给出重建指引；灰度工作台在收到该指引时展示①②③替代步骤。不放宽任何 ID 比较。 | 新增 `tests/test_api_v24.py`：批次建立 → 单站部署+回滚（内容恢复、新 ID）→ Advance 步骤 Failed、批次 Paused 且带重建原因 → Reconcile 409 且消息含关闭/重建/禁止盲目重试 → 批次保持 Paused → Abort 200 → 重建批次 200 且基线为新 ID。 | 未提供受控重基线通道；需人工关闭重建（按 V1.7 文档语义）。 |
| R04 | `AuditTrail`：写入失败不再静默——失败计数 + 内存待补队列 + `data/audit-pending.jsonl` 跨重启补录（下次成功写入时回放）；`Record` 自身不抛入请求路径。新增 `X-Audit-Status` 响应头（ok/degraded/unavailable）；审计降级或不可用时，`/api/flows`、`/api/recipes`、`/api/upgrades`、`/api/rollouts`、`/api/templates` 的非 GET 请求返回 503 拒绝；`/api/ready` 与 `/api/diagnostics/stations` 暴露 audit 健康（degraded/pending/failures/lastError，不可用时 unavailable）；主界面状态栏显示“审计降级”。 | 新增 `tests/test_api_v22.py` 段：SQLite 触发器拒绝审计 INSERT → 非关键写仍 200（审计计 pending、spool）、后续请求头 degraded、关键变更 503、诊断/ready 可见 → 移除触发器后成功写入自动补录（pending 归零、503 行回放到审计）→ 关键变更恢复放行。 | 待补队列有界（10000 条内存 + 追加 spool）；spool 本身无轮转上限；未验证磁盘满/介质故障场景。 |
| R05 | v5 迁移：`audit_events` 增加 `station_id`/`command_id`/`deployment_id` 列（含索引），`AuditEntry` 同步扩展；端点经 `AuditAttribution` 显式归属——`upgrades/apply|rollback|preflight` 记录目标工站与部署 ID，`rollouts/*` 按批次目标集**逐站**记录行（含 rolloutId），`templates/capture|deploy` 记录来源站/目标集；中间件优先使用显式归属，其次路径推导（仅旧式单站路由归 A）。`PageAudits` 以 station_id 列匹配，旧行（NULL）回退路径规则并排除 fleet 路径，全局 rollback/advance 不再误归 A。 | `tests/test_api_v22.py`：B 的单站回滚（全局路由）在 B 的审计视图可查、A 视图不可见；命令查询返回 stationId=B 与 deploymentId。`test_sqlite_schema.py` 更新至 v5 并校验新列/索引与旧行 NULL 回退。 | 历史误归属行保留原样（不再出现在 A 视图，但可从全局审计查询）；批量操作按目标集逐站记录同一请求结果码（逐站结果仍以批次记录为准）。 |
| R06 | `StationBootstrap` 抽取主站配置加载（失败不致命）；`MultiStationRegistry` 构造改为经服务容器在 try/catch 内构建固定 A/B（与动态站同一 fail-closed 隔离），坏站进入 `FailedStations` 且原因含具体加载错误；`/api/health` 零依赖；中间件对该站路由返回 503 `stationUnavailable`（含原因），审计/登录/诊断读取均韧性化（审计存储不可用时 `X-Audit-Status: unavailable`、诊断报 unavailable）。 | 新增 `tests/test_api_v23.py` 三场景：B+动态 C 损坏 → A 与诊断存活、B/C 路由 503、ready 503；A 配置损坏 → 遗留路由 503 带原因、B 存活、login 可用；A 数据库损坏 → 诊断/health/login 可达（audit unavailable）、A 拒绝、B 正常。契约测试 `RecoveryRegressions` 适配新构造。 | 仅覆盖配置/数据库损坏类；模板/部署/灰度损坏为既有隔离（同测试内含部署损坏诊断断言）。未验证并发破坏（运行中写坏文件）。 |
| R07 | 客户端新鲜度按域跟踪（`snapshot:{station}`、`flow:{station}`）：仅“成功 + 契约校验通过”的对应读取可清除过期标志；超时、503、坏 JSON、结构不符均保持/置为过期；其他域的新鲜不能掩盖已过期域。`app.js` 快照/流程读取附带契约校验；跨站旧响应仍由 `workspace.isCurrent` 拦截。 | `tests/test_api_client_v21.mjs`：超时→503 不清除、坏结构保持过期、跨域不互相掩盖、恢复读取后归位。 | 新鲜度域目前仅覆盖主轮询的快照/流程；其他读取（治理/审计）沿用原有错误路径。 |
| R08 | CORS 增加 `WithExposedHeaders("X-Command-Id","X-Trace-Id","X-Request-Id")`；错误对象、toast、工作室错误行与“命令查询”入口均保留具体 ID。 | 新增 `tests/test_browser_v21.py`：真实 Chromium 从 `http://127.0.0.1:5173` 跨端口请求隔离 API，健康响应头可读、401 业务拒绝携带 commandId、成功写入携带 commandId+traceId。 | 暴露仅限白名单三头；未暴露任何敏感头；5173 被占用时该探针显式 SKIP（环境冲突）。 |

## 回归登记与基础设施

- `tools/release_gate_v20.py` 新增：`node:test_api_client_v21.mjs`、`http:test_api_v21/v22/v23/v24.py`、`browser:test_browser_v21.py`。
- `test_sqlite_schema.py`、`test_sqlite_v10.py` 更新至 schema v5；旧断言点同步修正（版本号、v5 列与索引、旧行 NULL 回退）。
- 新增 `docs/R01_R08_FIXES.md`（本文件）作为 R 轮追踪；上一轮 N01–N09 见 `docs/N01_N09_FIXES.md`。

## 未覆盖 / 保留项

- 未执行：72h+ 仿真老化、目标负载 P95/P99、实机、真实介质断电、干净机安装/升级/回滚、Linux CI 结果（本轮在 Windows 本机执行）。
- G01–G03 保留：历史/备份容量策略、配方全表重写、商业身份、二进制交付、schema 版本向前/向后兼容验收（v5 迁移为向前兼容，未做降级路径）。
- 审查与修复不改变“Simulation、非安全控制”定位；发布是否批准以最终门禁与用户裁定为准。
