# V1.3 工站工作区架构

## 范围

基于 V1.2 的双 Runtime + 节点级共享资源 FIFO，引入“当前工站工作区”视图。仍是单机、纯仿真；本版本绝不引入工业硬件、安全 PLC 或现场控制。

## 前端隔离

- `frontend/station-workspace.js` 维护白名单 A/B、局部 revision 与站点路由映射。业务 GET/POST 在浏览器和 C# 后端中统一调用站点限定 API（旧式 `/api/*` 在后端继续仅表示 A）。
- 切换工站前拒绝正在运行的主工作区命令或流程编辑器修改。切换期间锁住新的操作；对切换前发起的异步读取，以 revision 判断并丢弃旧响应，避免 A 快照覆盖 B 视图。
- `FlowEditor` 在 A/B 分别保存未发布的画布与 Undo/Redo 栈，不会隐式写入后端；切换时中断过期加载。`QualityDashboard` 在工站维度隔离场景与质量结果，localStorage key 为 `devicecontrol.v13.scenarios.{id}`。
- `ReplayViewer.switchStation` 会停止播放并清除已选 runId，历史详细变量必须来自实际已存证据，绝不借用另一站的实时变量。
- 离线版 A/B 分别实例化 `MockRuntime`、`GovernanceMock` 和工站配置，只有 `SharedResourceCoordinator` 是共享服务。通过 `tools/build_preview.py` 在一个 HTML 内打包前端，无需服务即可操作。
- 多工站调度控制台保留，同时提供「打开工作区」按钮，与顶部站点选择器操作同一上下文。

## 服务端 API

- 新增 `GET /api/stations/{id}/audit?page=&pageSize=`，基于全局追加日志筛选带站点路径的审计项。A 对旧单站 API 兼容（不含登录登出、全局审计或调度接口）；B 绝不会包含 A 记录。
- 资源、流程、配方、周期历史、快照、调试、恢复等此前已有的 `/api/stations/{id}/*` 均保持原行为；`UnknownStationException` 继续返回 404。
- GET 审计仍要求 Engineer 及以上，由 `Permissions.Required` 在路由前缀规范化后统一判定。写权限、发布前空闲检查由后端执行，而非由 UI 禁用按钮代替。

## 已知限制

- A/B 编辑器未保存变更只在当次页面会话内缓存；关闭页面会丢失。未要求跨设备工作区持久化。
- 审计记录当前仍写入单机全局审计数据库，使用字符串路由前缀进行站点筛选；不是对多租户的安全隔离边界。
- 工站切换时自动轮询最多等待当前轮 API 完成后刷新；revision 保证旧响应不会污染新站。后台资源页调度仍可独立刷新。
- 真正发布需要在有 .NET 10 SDK 的 CI 环境运行 C# 编译、合同及 HTTP 集成测试。
