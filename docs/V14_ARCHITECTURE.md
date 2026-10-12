# DeviceControl V1.4 — 多工站模板与受控创建

## 范围与非目标

纯软件/浏览器仿真。不接入现场总线、实际 IO、运动控制器、相机或工业安全链路。

V1.4 是一个带权限和状态门禁的 **不可变模板快照库**，用于从已有工站捕获可复制的软件配置，并创建新的 **独立仿真工站**。模板状态流转：`Draft → Validated → Published`。不允许发布后编辑，不允许直接在线覆盖已经存在/运行中的工站；后者只提供只读差异比较。

## 组件

- `StationTemplateCatalog.cs`：快照库，包含软件站点配置（信号/轴/气缸/互锁/当前已发布工作流）与当前已发布配方；不包含运行历史、事件、操作用户或身份凭证。写入 `data/templates/catalog.json`，采用临时文件+同目录替换。SHA-256 校验快照与模板发布前质量门禁检查。模板库与运行历史不共用写表。
- `MultiStationRegistry.cs`：将固定 A/B Runtime 扩展成最多 12 个动态站点。保留 V1.0 A 路由与 V1.2 节点级进程内资源调度器；新建工站经 `Sim*` 工厂构造，并拥有独立 `SqliteStore`、`RecipeStore`、`FlowRevisions`、`RunJournal`、`DeviceRuntime`。A/B 的实例配置和历史不变。
- `config/managed-stations/<ID>.json`：只保存经管理员批准且 ID 校验合格的新工站配置；服务重启时自动加载。每个动态工站的历史目录 `data/stations/<ID>/` 独立。ID 只能是 1～16 个 ASCII 字母/数字/下划线/短横线且字母开头；内部统一大写；拒绝已存在 ID。
- `StationTemplateStudio`：独立的模板管理 UI，包含捕获、验证、审批、差异比较与批量新建，支持服务器模式和离线只读硬件的浏览器仿真模式。前端 `StationWorkspace.register()` 动态增加工作区 ID，并继承编辑器/回放分站隔离。

## API 和角色

| API | Method | Minimum role | 说明 |
|---|---|---|---|
| `/api/templates` | GET | Operator | 快照列表 |
| `/api/templates/{id}` | GET | Operator | 模板版本 |
| `/api/templates/capture` | POST | Engineer | `{stationId, name}` 创建草稿 |
| `/api/templates/{id}/validate` | POST | Engineer | 静态配置 + 流程质量门禁 |
| `/api/templates/{id}/publish` | POST | Admin | 标记不可变已发布 |
| `/api/templates/{id}/diff/{stationId}` | GET | Operator | 只读差异，最多 150 项 |
| `/api/templates/{id}/deploy` | POST | Admin | `{stationIds:["C","D"]}` 新建 1～5 个 |

HTTP API 接口继续仅绑定 localhost，依赖演示角色、演示密码，不能用于真正工业部署的身份认证。未识别修改端点继续 fail-closed。通过原 V1.3 RBAC/Audit 中间件记录 POST 操作，数据不是防篡改审计。

## 预检、安全和失败语义

1. 动态创建前，全部 ID 都通过服务端合法性、唯一性和容量检查；请求内不能有重复 ID，已有 ID 不允许覆盖。工站历史目录存在也拒绝复用，避免错误关联旧数据。
2. Snapshot SHA-256 与当前配置/配方一致；模板静态验证与 `WorkflowQualityGate.Evaluate` 通过才可发布或创建。
3. 使用 `FileMode.CreateNew` 持久化仿真软件配置，后台动态工站加载只接受固定配置目录；不会启用任何真实驱动。
4. 批量创建 **不是跨文件/数据库事务**。逐站顺序创建，如中途失败会报告已经创建的站点和失败 ID；不能声称一键回滚。模板版本本身保持不可变。
5. 不可对已有工站热更新配置；对比 API 只返回差异。后续如果要支持已有工站迁移，应新增“预检→停机→导出备份→逐站确认→回滚窗口”流程。
6. 软件资源协调器只在当前进程内提供 FIFO 节点级互斥。不是实时安全调度器，也不是分布式锁。

## 数据隔离 / 兼容性

- A 仍写入原 `data/` 根目录；B 仍写入 `data/stations/B/`；新站 C/D 写入 `data/stations/C/`、`data/stations/D/`，配置目录 `config/managed-stations/`。
- FlowStudio 浏览器演示的草稿缓存采用每工站命名空间。旧 A 工站仍兼容原浏览器 localStorage 键，B 和后续工站新增独立键，避免旧版跨站编辑器缓存串扰。
- 动态新站加入 `/api/stations` 后，原 `/api/stations/{id}/...` 工作区查询/控制继续有效；旧单站 `/api/*` 始终指向 A。

## 验收状态

已运行：`run-tests.sh` 中的 Node、SQLite、静态接口接缝检查，以及 `tests/test_browser_v14.py` 离线 Chromium 端到端（审批权限、模板发布、C/D 批量创建、输入隔离、重复 ID 拒绝）。

**尚未运行：** `dotnet build`、`tests/test_api_v14.py` 和 C# ContractTests：当前环境没有 .NET 10 SDK。CI 的 .NET 10 安装、编译和 HTTP 集成测试是后续发布门禁，不能把静态检查或浏览器测试当成后端验收。
