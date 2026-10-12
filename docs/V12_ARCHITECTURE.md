# V1.2 共享资源：节点级 FIFO 协调器

## 边界

本版本是 **software-only / Simulation**。A、B 拥有独立的 `DeviceRuntime`、`FlowEngine`、模拟控制器、SQLite 存储和故障恢复状态。`SharedResourceCoordinator` 是应用进程内的共享服务；它不控制实际设备、不是安全互锁，也不是分布式锁。

```
/api/stations/{A|B}/cycle/start --+-- Runtime A -- FlowEngine A --+
                                   |                              |
                                   +-- Runtime B -- FlowEngine B --+--> SharedResourceCoordinator
                                         independent DB              |     keys: CELL-SHARED, ...
                                         independent recovery        |     atomic FIFO queue
                                                                  acquire / dispose
```

### 已实现

1. 新的 `SequenceStep.SharedResources` 字符串列表（最多 8 项，1~32 位合法名称）以及 `ResourceWaitTimeoutMs`（100~30000ms，默认 2500ms）。为兼容旧 V1.1 发布版本，**所有 `VisionLocate` 隐式占用 `CELL-SHARED`**，即使历史版本没有此字段。
2. 进入叶子节点时一次性申请全部共享资源，FIFO 排队，不能持有某个共享资源再等待另一个。获得锁才执行节点；执行成功、失败、取消都会在 `finally` 中释放。
3. 超时产生确定的资源等待异常，不使用旧 `Retry` / `WaitForOperator` 自动重试资源超时；进入设备的正常故障/恢复流程。取消中的“授权同时完成”竞争由服务端回收未交到调用方的 lease。
4. 两个自动周期可以同时启动。跨工站冲突只发生在配置的共享叶子节点，而不是整周期。手动视觉触发也使用 `CELL-SHARED`。
5. `GET /api/stations/resources` 返回 `policy`, `leases`, `queue`, `events`，供多工站看板展示；`GET /api/stations` 返回 `coordination=NodeScopedFIFO`，同时保留旧 `owner` 供 UI 兼容。
6. Web 流程节点检查器可编辑共享资源和超时；离线网页使用镜像逻辑（并非实际 C# 服务）。普通本地机构资源 `Axis:X` 等继续由每个工站自己的 FlowEngine 处理。

### 明确限制

- 严格 FIFO 可能导致 *head-of-line blocking*，这是本版为公平性和可解释性作出的设计取舍。
- 对共享资源的标记是**业务配置**：未标记的跨工站物理冲突不会自动推断。任何未来的硬件场景都必须重新进行安全、互锁、运动区域检查；此版本不适合真实设备控制。
- `SharedResourceCoordinator` 不持久化等待队列，也不跨进程；进程崩溃后的站点会按原 `RunJournal` 规则标记 `Interrupted`，但不会继续旧动作。
- V1.2 **未**将整套现有流程设计器/配方/回放视图按选择的 B 工站切换；这些原视图仍以 A 工站为主，B 的启动/停止/状态/版本信息由多工站控制台展示。

### 未来工作

工站级流程编辑/回放上下文切换、跨站序列追溯、调度事件持久化、离线资源利用率报表、站点配置管理，以及已接入 .NET SDK 环境下的实际 C# 编译/HTTP 集成验收。
