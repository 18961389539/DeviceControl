# Q01–Q08 整改说明（2026-10-13）

本轮变更针对 `reports/quality-comprehensive-20261013/` 全面复审提出的 8 项问题（P1×2 / P2×6）实施修复，范围包含软件仿真后端、前端状态栏、发布门禁工具与发布证据链。

**最终门禁与证据绑定**：本轮最终 release profile 门禁结果、清单摘要与源修订均绑定在 `DeviceControl_V2.0_ReleaseStatus.json`（`sourceRevision` / `manifest` / `gateEvidence` 三节），原始证据归档在 `reports/quality-fix-q01q08-20261013/gate/`（release-gate.json / release-gate.txt / 逐例日志）。文档不复制数字，数字只以机器可读证据为准，避免再次出现"发布状态与源包不一致"的问题（即 Q08 本身）。

修复过程中第一轮门禁暴露一处过时静态断言（`tests/test_station_files_v12.py` 仍匹配旧版 `_shared.AcquireAsync(` 直呼与 `sharedLease?.Dispose()` 文本），已按新契约更新后重新运行；过程记录保留在本节与归档日志中。

## P1

| 编号 | 修复 | 回归 / 证据 | 限制 |
|---|---|---|---|
| Q01 | `Governance.cs`：待补审计队列改为**磁盘队列 + 提交确认**。`data/audit-pending.jsonl` 及其两个轮转槽（`.1` / `.2`）是队列本体；内存 `_pending` 只是有界视图（2000 条），视图裁剪**不再丢弃未确认事实**。补录按批幂等执行（`INSERT OR IGNORE` 按行 ID），只有数据库确认成功后才截断/轮转 spool；部分失败保留未确认行；启动加载不再受 10000 上限静默截断。 | 新增 `tests/test_api_v25.py` 场景 A：预置 10000 条 spool + 触发数据库拒写后再登录，恢复后 10000 条**全部且仅一次**落库、`probe-00000` 存在、spool 在全部确认后被清空、健康恢复（pending=0）。 | 队列容量上限 100000 条（超出转入显式丢失计数并停止新写入）；未做真实磁盘满/介质故障。 |
| Q02 | `Governance.cs` + `Program.cs`：①`TrySpool` 失败不再静默——轮转槽失败转入有界救援缓冲并写入 `data/audit-loss.json` 丢失标记（含未落盘数/不可恢复数与最后错误）；②健康分别报告 db/spool/memoryOnly/lost；③命令查询合并待补视图、救援缓冲与磁盘 spool，新增 `pendingAudit`/`uncertain`/`lost` 字段，未命中时明确说明"无法排除属于丢失范围"，**不再用'未找到'暗示未到达**；④无可用持久通道时（数据库与全部轮转槽失败、存在仅内存记录、或存在未确认丢失）**拒绝需要留痕的新变更**（503 `auditUnavailable`），停止/故障复位/恢复确认/丢失确认仍可用；⑤新增 `POST /api/audit/acknowledge-loss`（Admin）：在仍有未补齐记录时拒绝，确认动作本身先经数据库通道持久化后才清除丢失标记；⑥中间件对每条已认证请求做限流恢复探测（1s），通道恢复后无需新写入即可自动补录。 | `tests/test_api_v25.py` 场景 B：主槽被目录替代时轮转槽接管，注销记录在重启前已可查（pendingAudit）、重启后补录确认；场景 C：全部通道不可写 → 留痕变更 503、重启后 ready 保持 503 且查询报告 uncertain/lost、人工确认后才恢复 200。既有 `tests/test_api_v22.py` 降级/补录门禁全部保持通过。 | 通道全部失效后的**第一个**请求无法在受理前获知失效（记录进入救援缓冲，不静默丢弃）；此后 fail-closed。若连丢失标记文件都不可写（全盘不可写），跨重启的丢失事实无法持久化——运行期内仍降级并拒绝新变更，已记录为固有边界。 |

## P2

| 编号 | 修复 | 回归 / 证据 | 限制 |
|---|---|---|---|
| Q03 | `MultiStationRegistry.cs`：动态工站加载把**文件名规范化、重复 ID（含大小写等价）、容量上限、目录创建/读取失败**全部纳入逐文件隔离——失败文件跳过并记录原因，原文件一律保留（不猜测、不自动删除）；最小诊断宿主不再因管理目录问题退出。`Program.cs`：启动期 fleet 初始化/资格注册包入防御块；`/api/ready` 与 `/api/diagnostics/stations` 改为防御式解析依赖（注册表不可构造时回答 503/降级字段而不是 500）。 | 新增 `tests/test_api_v26.py`：非法文件名 / 大小写重复 / 超容量 / 合法名坏内容四类同时存在 → health 可达、诊断含各原因、A/B/正常动态站可用、受影响路由 503 `stationUnavailable`、全部原文件保留；`managed-stations` 被普通文件占位时同样启动成功。既有 `tests/test_api_v23.py` 三场景保持通过。 | 目录"拒读"以路径被文件占位场景近似（Windows 无 ACL 注入）；未验证运行中并发破坏目录。 |
| Q04 | `Program.cs`：CORS `WithExposedHeaders` 增加 `X-Audit-Status`（白名单仍仅四个标识头）；`frontend/app.js`：状态栏三态化（ok / degraded / unavailable），降级显示"审计降级 · 数据可能过期"，不可用显示"审计不可用 · 变更已受限"。 | `tests/test_browser_v21.py` 扩展为真实 Chromium 跨端口验证：SQLite 触发器制造确定性降级，浏览器读到 `degraded`（此前为 null），恢复后读回 `ok`；`Access-Control-Expose-Headers` 含 `X-Audit-Status`；另以静态断言保证 UI 存在两种提示文案。`tests/test_api_v25.py` 场景 B 重复断言暴露头。 | 未执行完整操作员任务可用性测试；头在请求执行前设置，首次造成故障的写响应仍可能为 ok（已在文档与查询指引中说明以持久记录为准）。 |
| Q05 | `FlowEngine.cs` + `DeviceRuntime.cs`：新增暂停监视回调（`pauseMonitor`）。断点暂停、人工异常门等待、共享资源获取等待**全部**在 40ms 周期内继续执行普通条件互锁检查；期间条件失效立即**锁存**——释放等待门、以故障终止周期（Fault + 需恢复），恢复信号不会把暂停等待悄悄变回 Completed。资源获取等待在监视失败时主动取消待决请求。 | 新增 `tests/test_api_v27.py`：断点暂停期间防护门短暂失效（150ms 内恢复）→ 周期 Fault、resume 409、run=Failed、计数不增；人工门等待期间条件失效 → 门自动中止、事后 retry 409。 | 40ms 轮询窗口为仿真实时余量；不替代硬件安全功能。 |
| Q06 | `FlowEngine.cs`：人工 error gate 改为**先释放全部物理/共享 lease 再等待**（可重复调用的幂等释放），retry 时按节点超时重新获取——不再让无期限的人工决策持有 CELL-SHARED 拖垮其它工站；快照 `resources` 在等待期间为空。`DeviceRuntime.cs`：**未开始任何动作**的资源争用超时按业务原因区分——不置 Fault、不清除回零、不要求机构恢复，仅以 409 明确拒绝并记录意图结果。 | 新增 `tests/test_api_v28.py`：B 进入人工等待时不再持有 CELL-SHARED，A 手动视觉 2.5s 内快速成功（原缺陷为 2.534s 超时 409 + A 进 Fault）；停止后无残留 lease；占用场景下 A 的争用超时保持 Idle、回零有效、无恢复要求。 | 保留"需要保留资源"的工艺需求留待节点级策略配置（当前统一释放）；未做真实机构资源语义。 |
| Q07 | `tools/release_gate_v20.py`：新增统一状态契约 `classify_output()`——显式 `RESULT: PASS\|FAIL\|BLOCKED\|SKIP` 标记优先，退出码 77 固定 BLOCKED，**输出含 UNVERIFIED/SKIP/BLOCKED 且退出 0 一律 BLOCKED，绝不 PASS**；PASS 标记与失败退出码矛盾按 FAIL；严格 release 继续要求全部 PASS。`tests/test_browser_v21.py`、`test_api_v21/v22/v23/v24` 显式使用 `RESULT:` + 退出 77 契约。 | 新增 `tests/test_gate_classification.py`（登记进门禁）：合成输出矩阵断言（含"UNVERIFIED + 0"、"SKIP + 0"、77、"PASS 标记 + 非 0"、多标记取最后）；并静态核验门禁已登记 manifest/契约任务。 | 未改造全部历史任务为显式标记（存量任务经统一分类器按退出码 + 输出关键字判定，语义上不可能再出现"未执行计 PASS"）。 |
| Q08 | 新增 `tools/release_manifest.py`：`--write` 从 git 交付集（tracked + 未忽略的未跟踪文件，剔除 artifacts/reports/运行时 data/生成夹具/根目录浏览器回归截图/两个生成证据文件）重算 `MANIFEST_SHA256.txt`；`--verify` 对当前工作树执行**零缺失/零差异/零未登记**校验并作为门禁任务 `manifest:verify` 运行；`--bind-status` 把 `DeviceControl_V2.0_ReleaseStatus.json` 绑定到 git 修订、工作树摘要、清单摘要与门禁证据 JSON（不再出现"仍写 dotnetSdkAvailable=false"这类过期结论）。`docs/V20_ACCEPTANCE.md` 与 README 重写为"当前绑定 + 历史记录"两段，历史环境结论明确标记为历史。 | 门禁输出 `reports/quality-fix-q01q08-20261013/gate/`；`python tools/release_manifest.py --verify` 在门禁内与收尾均通过（零差异；收尾清单 165 项——首轮运行发现根目录回归截图会被 Chromium 测试重写，已将其归入生成证据排除）。ReleaseStatus 由 `--bind-status` 生成，历史 ReleaseStatus 被明确取代（不再叠加伪造验收）。 | 清单摘要绑定"工作树内容"而非 git commit 对象（允许检出未提交的整改轮）；修改任何交付文件都会使清单失败，需重新生成——这是设计使然（证据失效）。 |

## 回归登记与基础设施

- `tools/release_gate_v20.py` 新增任务：`manifest:verify`、`python:test_gate_classification.py`、`http:test_api_v25/v26/v27/v28.py`；状态判定改用 `classify_output()`。
- 新增：`tests/test_api_v25.py`（Q01/Q02）、`test_api_v26.py`（Q03）、`test_api_v27.py`（Q05）、`test_api_v28.py`（Q06）、`test_gate_classification.py`（Q07）、`tools/release_manifest.py`（Q08）。
- 更新：`tests/test_browser_v21.py`（Q04 + Q07 契约）、`test_api_v21/v22/v23/v24.py`（Q07 契约）、`test_station_files_v12.py`（适配 Q05/Q06 的受监视获取与幂等释放）。
- 文档：本文件；`docs/V20_ACCEPTANCE.md`、`README.md` 的当前绑定说明。

## 未覆盖 / 保留项

- 未执行：72h+ 仿真老化、目标负载 P95/P99 复测、实机、真实介质断电、干净机安装/升级/回滚、Linux CI 结果（本轮在 Windows 本机执行）。
- Q01/Q02 未做：真实磁盘满、扇区损坏、并发多进程写入同一审计存储。
- Q05/Q06 未做：真实机构时序与安全语义验证；暂停策略的可配置化（当前统一"暂停即监视、等待即释放"）。
- Q07 未做：全部历史任务的显式标记化（依赖统一分类器语义兜底）。
- 本轮修复不改变"Simulation、非安全控制"定位；发布是否批准以绑定门禁证据与用户裁定为准。
