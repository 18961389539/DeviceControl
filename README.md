# DeviceControl V2.0 RC — 软件稳定性验收基线（未批准正式发布）

**仍是软件 Simulation。没有真实硬件控制或安全评级。** V2.0 不扩展设备功能，主要改善发布门禁与部署备份来源校验。

## V2.0 修正

- `tools/release_gate_v20.py`：Node 工作流、Python/SQLite、Chromium、.NET 10 编译、契约和真实 HTTP 集成分项验收。每例独立超时、日志；`release-gate.json` 和 `release-gate.txt` 为机器/人工可读报告；执行中 `progress.json` 持续落盘。完整模式遇到缺失 SDK **失败，不允许把跳过当通过**。
- `StationUpgradeManager.InspectBackup`：部署日志只能引用本次部署与目标工站的**精确**备份文件，并检查路径内符号链接，防止跨工站有效备份被错误引用。
- `RecoverySandboxInspector` 与 Python `recovery_sandbox.py`：补充 `backups/` 上级目录符号链接限制。检查仍只读，不写回。
- `tests/test_recovery_v20.py`：验证正常副本、跨工站引用、文件/目录/上级符号链接；`tests/test_v20_recovery_contract.py` 为 C# 静态检查（**不是编译证明**）。
- `.github/workflows/software-check.yml`：CI 安装 .NET 10 后运行严格发布门禁；保留扩展浏览器回归。
- 健康接口版本为 `2.0-rc`，旧接口路径不变；V2.0 交互预览继续沿用离线 V1.9 全功能页面。

## 立即运行

```bash
python tools/release_gate_v20.py --profile offline   # 离线功能回归，不等于正式发布
python tools/release_gate_v20.py --profile release   # 正式发布门禁，缺 SDK 必然失败
python tools/release_gate_v20.py --profile offline --no-browser --only 'recovery_v20,recovery_sandbox_v19' # 快速诊断
```

**发布结论：V2.0 RC / RELEASE APPROVED: NO。** 此环境 `dotnet` 命令不存在且安装源不可达，尚未执行真实 C# 编译、HTTP 集成和服务重启验收。当前已分别通过 Node、SQLite 五组、恢复沙箱与四条浏览器回归，但没有将这些结果冒充全部 .NET 验收。参见 `docs/V20_ACCEPTANCE.md`。

---

# DeviceControl V1.9 RC — 隔离备份检查与恢复验收门禁

继承 V1.8 全部软件仿真功能，不接入 IO 板卡、运动卡、相机或任何真实硬件。

## 本轮新增

1. `backend/DeviceControl.Api/RecoverySandboxInspector.cs`：只通过部署 ID 读取指定部署备份；复制到隔离临时目录后，按顺序核验受控路径、容量限制（128 MiB）、副本 SHA-256、SQLite `PRAGMA integrity_check`、部署前流程和配方活动版本。读操作结束清理临时副本，**不恢复数据库、不改变工站运行状态、不改变部署日志状态**。结果始终标记人工核查要求。
2. `GET /api/upgrades/{deploymentId}/sandbox`：Engineer/Admin 可读，Operator/未认证拒绝；依赖 .NET 服务。V1.9 不提供写回沙箱或一键恢复接口。
3. `tools/recovery_sandbox.py`：无 .NET SDK 时可在本机用 Python 标准库对**已有备份**进行同类检查。数据目录必须由调用方可信指定；只复制备份到系统临时目录，输出 JSON 结果。示例：

   ```bash
   python tools/recovery_sandbox.py --data-dir backend/DeviceControl.Api/data --deployment-id dep-<32位小写hex>
   ```
4. `DeviceControl_V1.9_Preview.html`：在「配置模板与部署」的第 ⑨ 区域新增只读沙箱诊断。离线 HTML **没有真实 SQLite 备份**，只能说明“未执行数据库检查”，不伪造成功的完整性结论。
5. 新增 `tests/test_recovery_sandbox_v19.py`、`tests/test_recovery_v19_static.py`、`tests/test_browser_v19.py`、`tests/test_api_v19.py`，并更新 CI 版本契约及入口。历史 HTTP 测试允许新的 `1.9` 健康接口版本。

## 本轮明确限制

- 不进行生产/仿真 Runtime 数据库的自动覆盖，也不自动回滚；人工核对是强制策略。
- SQLite 数据库/日志在非安全介质中，SHA-256 只能发现损坏/不一致，无法提供防恶意篡改的可信来源保证。
- .NET API 的正确性**仍待** .NET 10 SDK 编译与真实 HTTP 集成测试；当前容器没有 `dotnet`，不得把 Python/静态测试当成 C# 编译验证。
- 真实后端测试脚本使用隔离临时目录，会创建/篡改**测试专用**的部署备份，不会操作现有工站。

## 快速验证

```bash
python tests/test_recovery_sandbox_v19.py       # 离线备份行为测试
python tests/test_recovery_v19_static.py        # 静态接口接缝
python tests/test_browser_v19.py                # Chromium 离线交互
bash run-tests.sh                               # 旧 Node/SQLite + 当前测试；无 .NET 会明确 UNVERIFIED
bash run-tests.sh --require-dotnet              # 正式发布门禁，缺 .NET 10 SDK 会失败
python tests/test_api_v19.py                    # 必须在带 SDK 的 CI/本地运行，缺 SDK 输出 UNVERIFIED
```

CI 文件：`.github/workflows/software-check.yml`。既往架构记录可见 `docs/V18_ARCHITECTURE.md` 及之前版本的文档。

---

# DeviceControl V1.8 RC — 软件部署重启恢复与一致性诊断

基于 V1.7 的纯软件仿真。新增重启后部署意图 **Interrupted 持久化**、单站备份 SHA-256 只读核验、批次恢复一致性报告、隔离故障演练和回归测试。**不接入硬件**，未自动重试或跨站回滚。

- 独立浏览器预览：`DeviceControl_V1.8_Preview.html`（Admin / demo123），进入「配置模板与部署」页面的「⑧ 重启恢复与一致性演练」；无后端时演练不修改真实工站。
- 截图：`DeviceControl_V1.8_Recovery.png`；最新设计：`docs/V18_ARCHITECTURE.md`。
- 本地验证：`node tests/test_recovery_v18.mjs`、`python tests/test_browser_v18.py`；CI 强制执行 `bash run-tests.sh --require-dotnet`。
- **发布门禁未通过**：这里没有 .NET 10 SDK，C# 编译和真实 HTTP 重启验证尚未执行；不能称为生产就绪。测试与限制详见 `DeviceControl_V1.8_TestResults.txt`。

---

# DeviceControl V1.7 RC — 维护窗口 / 批次诊断 / 人工恢复

**纯软件仿真，不控制真实硬件，不替代任何工业安全功能。** V1.7 继承 V1.6 的试点、证据验收及分批升级，在灰度部署页面加入可选 UTC 维护窗口、逐站诊断与失败步骤人工核对。批次始终由 Admin 显式推进；窗口开放不会触发后台自动执行。失败恢复必须满足原始版本基线、无报警和无未解决单站部署日志，并记录核对人/备注；不会自动重试或回滚。

- 双击 `DeviceControl_V1.7_Preview.html`，使用本地演示账号 `admin / demo123` 登录，打开「配置模板与部署」的 V1.7 灰度部署区域。
- `DeviceControl_V1.7_Maintenance.png` 为本轮真实 Chromium 界面截图。
- `python tests/test_browser_v17.py` 为维护窗口、诊断和恢复回归，`python tests/test_browser_v17_compat_v16.py` 为旧版灰度证据门禁在新界面上的回归。
- `bash run-tests.sh` 运行 Node、SQLite、静态检查；`bash run-tests.sh --require-dotnet` 强制 .NET 10 编译与实际 HTTP 测试。**当前环境没有 .NET SDK，C# 编译与 HTTP 集成尚未验收。**
- 维护窗口和核对接口的准确规则与限制：`docs/V17_ARCHITECTURE.md`。

---

# DeviceControl V1.6 RC — 多工站灰度部署与质量验收

**纯软件仿真，不接入硬件，不是安全控制系统。**

本轮在 V1.5 逐站预检/备份/发布/回滚的基础上，增加**试点放行、逐批部署、质量与仿真周期证据验证、版本一致性阻断、失败暂停、关闭及部署历史**。后端 `FleetRolloutManager` 采用持久化 JSON 批次记录；浏览器离线版仅会话内保存。最多 12 台工站，每批最多 3 台。

- 独立 HTML：`DeviceControl_V1.6_Preview.html`，用 `admin / demo123` 登录，进入「配置模板与部署」的「⑥ 多工站灰度部署」。
- 实际界面：`DeviceControl_V1.6_Rollout.png`。
- 运行本机 Node/SQLite/静态检查：`bash run-tests.sh`。
- V1.6 浏览器验收：`python tests/test_browser_v16.py`、`python tests/test_browser_v16_conflict.py`。
- V1.5 兼容性在最新界面验收：`python tests/test_browser_v15_latest.py`。
- **正式发布门禁：`bash run-tests.sh --require-dotnet`，需要 .NET 10 SDK；当前环境没有，C# 编译、HTTP 合同和后端重启持久化尚未验收。**

接口、状态机、断电/失败边界和验收矩阵详见 `docs/V16_ARCHITECTURE.md`。V1.6 批次停止不是跨站统一回滚，不会自动执行任何仿真周期，也不能绕过普通互锁。

---

# DeviceControl V1.5 RC — 已有工站的软件配置部署、备份与回滚

**纯软件仿真，没有真实硬件驱动，不能替代工业安全控制。** 版本基础为 V1.2 RC，保留完整旧软件模块与独立工站数据库，进一步实现 A/B 工作区级界面隔离。


## V1.5 本轮交付

- **现有工站预检**：仅接受已发布模板；目标必须 `Idle + Manual`、无报警和待恢复；机构/信号/轴/气缸/互锁完全兼容；基于当前流程/配方版本生成 10 分钟一次性计划。
- **逐站受控发布**：Admin 以预检计划确认当前活动版本并发布新的工作流与配方；不覆盖机构配置。保持历史发布的不可变版本。
- **双重备份证据**：发布前持久化包含旧版 Flow/Recipe 快照的 `data/deployments/dep-*.json`；使用 SQLite Online Backup 创建并校验部署前数据库副本 `data/deployments/backups/<station>/dep-*.db`，保存 SHA-256。
- **失败补偿**：部署任一步骤失败则尝试重新发布旧 Flow/Recipe 版本；如果恢复失败，持久化 `ManualRecoveryRequired` 并阻止后续升级。跨两次版本发布**不是数据库原子事务**。
- **人工回滚和版本冲突门禁**：Admin 根据历史部署编号回滚，阻止在其他人后来发布了新版本时直接覆盖。
- **前端**：原「配置模板与部署」页面新增 `⑤ 已有工站 · 受控软件升级与回滚`，离线浏览器版可完整操作并查看内存历史；浏览器离线记录不会写盘。

建议体验：下载独立 HTML，用 `admin / demo123` 登录，在「配置模板与部署」中捕获 A → 验证 → 发布模板，然后选择工站 B，点击「执行升级预检」→「发布到已有工站」→「回滚到部署前」。切换 B 到自动模式后重新预检应拒绝。**所有控制都只作用于软件模拟对象。**

详细设计与限制见 [`docs/V15_ARCHITECTURE.md`](docs/V15_ARCHITECTURE.md)。

## V1.4 本轮交付

- **版本化工站模板库**：从工站 A/B（或新建工站）当前已发布流程和已发布配方捕获 **Draft**；工程师验证，管理员发布，发布版本不可修改。
- **服务端门禁**：校验 `StationConfig.Validate`、SHA-256 快照与工作流质量场景，通过后才能创建工站。
- **只读差异比较**：支持模板与任意工站的软件配置/配方字段差异，已有工站不能在线被模板覆盖。
- **动态批量创建**：单次 1～5 个（总量最多 12），ID 白名单、拒绝覆盖。动态实例拥有独立的流程、配方、数据和故障状态，继续共享软件节点级 FIFO 调度器。
- **持久化**：后端 `config/managed-stations/` 和 `data/stations/<ID>/`；重启恢复新增工站。离线预览仅保留本页面会话的新增工站。
- **前端**：新增「配置模板与部署」页面。顶部工站选择器、原有多工站控制台支持新工站；修复离线 FlowStudio B/C/D 缓存跨站复用问题。

建议体验：使用 `engineer / demo123` 在「配置模板与部署」中捕获并验证模板；退出后使用 `admin / demo123` 批准发布，再输入 `C,D` 创建工站，切换到 C/D 的各个页面核对隔离。**仅仿真**，无真实硬件输出。

详见 `docs/V14_ARCHITECTURE.md`。

## V1.3 软件工作区交付

- **工站级 UI 上下文**：顶部 A/B 工站选择器在总览、手动调试、IO、流程调试、流程设计、配方、报警、恢复、分析及历史回放中生效。多工站调度页新增跳转指定工站工作区的按钮。
- **工站独立编辑缓存**：A/B 流程设计器拥有独立的未保存画布、撤销/重做栈；切站不会自动保存或发布。质量门禁自定义场景按工站独立保存。
- **版本与历史隔离**：读写请求自动添加 `/api/stations/{id}/...`，旧 `/api/*` 继续指向 A。回放、周期选中项在切站时重置，防止相同 ID 的历史记录混淆。
- **工站审计筛选**：新增 `GET /api/stations/{id}/audit`，保留全局追加审计记录，显示时只返回所选站点操作；Engineer 权限。
- **并发多工站周期**：A/B 可以同时处于 Running，`/api/cycle/start` 和 `/api/stations/{id}/cycle/start` 统一执行节点级调度，不再整周期独占 `CELL-SHARED`。
- **原子多资源 FIFO**：单个流程叶子节点可以声明多个 `sharedResources`；必须一次性获得全部资源才执行。严格 FIFO、公平但可能发生队头阻塞；等待超时后进入原有故障/人工恢复路径，取消与异常保证释放。
- **兼容旧流程**：所有 `VisionLocate` 均自动声明 `CELL-SHARED`，避免旧 V1.1 SQLite 中缺少新字段的已发布流程绕过共享镜头资源。手动视觉触发也使用此逻辑资源。新流程可配置 `resourceWaitTimeoutMs`（100～30000ms，默认 2500ms）。
- **资源监控与编辑**：多工站控制台显示当前拥有者、已分配 lease 和 FIFO 等待队列，`GET /api/stations/resources` 返回 `leases`, `queue`, `events`。流程设计器节点检查器可编辑共享资源与超时。
- **保持分站隔离**：A 继承 `data/devicecontrol.db`，B 使用 `data/stations/B/devicecontrol.db`，A 的旧版接口和 V1.0 用户界面保持兼容；B 流程/配方/历史/故障仍独立。

历史详情参阅 `docs/V11_ARCHITECTURE.md` 与 `docs/V10_ARCHITECTURE.md`。本轮架构及限制参阅 `docs/V12_ARCHITECTURE.md`。

## HTML 离线预览

双击 `DeviceControl_V1.5_Preview.html`，使用 `engineer / demo123` 登录。在顶部切换「工站 A/B」，各页面只显示当前站点数据；「多工站调度」可以跳转到指定工站工作区。依次给 A、B 分别点击「准备轴 / 回零」→「切换自动」，随后分别「运行周期」：两个工站可以同时执行；遇到共享视觉节点时 FIFO 排队，监控板块会显示 `CELL-SHARED` 归属与等待请求。

其它演示角色：`operator`、`admin`，密码均为 `demo123`。该凭证只用于本地 Simulation 演示。

## .NET 本地服务

需要 **.NET 10 SDK**（也需能够恢复 `Microsoft.Data.Sqlite` NuGet 依赖）。在项目根目录：

```bash
dotnet run --project backend/DeviceControl.Api
```

再开启 `python -m http.server 5173 --directory frontend`，访问 `http://127.0.0.1:5173`。仿真 API 默认绑定 `http://127.0.0.1:5088`。不要将演示凭证或接口暴露到公网。

## 测试

```bash
bash run-tests.sh                       # Node、SQLite、静态源码接缝；SDK 存在则自动进行 C# 验收
python tests/test_browser_v15.py         # Chromium V1.5 部署/回滚回归
python tests/test_browser_v14.py         # Chromium V1.4 模板/动态工站回归
python tests/test_browser_v13.py         # Chromium V1.3 工作区隔离回归
bash run-tests.sh --require-dotnet       # 强制编译/HTTP 验收门禁，缺 SDK 时明确失败
```

V1.3 测试范围参阅 `docs/V13_ACCEPTANCE.md`。**当前执行环境没有 .NET 10 SDK**，因此仅确认 Node/SQLite/Chromium 通过，不能把后端 C# 编译或真实 HTTP 测试说成已通过。CI 已设置安装 SDK 并运行这些测试。

## 未完成与下一步

- V1.3 已实现 A/B 工站主工作区、流程设计、配方、报警、历史回放独立显示。本轮 V1.5 已推进到逐站软件部署与回滚，下一阶段可继续做分批灰度、部署依赖和停机维护窗口。
- 共享资源调度器不是分布式锁，不跨进程，也没有持久化等待队列；进程中断依赖独立工站 `RunJournal` 恢复状态。
- 尚不做任何真实硬件驱动、工业运动安全和实机控制。
