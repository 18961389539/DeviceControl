# V1.1 多工站运行隔离与共享资源调度

## 运行拓扑

```text
Browser V1.1   -- token + station id -->  /api/stations/*
       |                                        |
       |                              MultiStationRegistry (singleton)
       |                                        |
       |           +----------------------------+--------------------------+
       |           |                                                       |
       |           v                                                       v
       |     StationContext A                                          StationContext B
       |     V1.0 legacy API                                           Scoped API
       |     DeviceRuntime A                                           DeviceRuntime B
       |     SimIo + Flow A                                            SimIo + Flow B
       |     Recipe A + SQLite A                                       Recipe B + SQLite B
       |     data/devicecontrol.db                                     data/stations/B/devicecontrol.db
       |
       +--- offline mode --> MockRuntime A, MockRuntime B (separate objects)
```

**隔离原则**：同名信号和机构允许分别出现在 A、B，因为它们属于不同软件实例。共享同一个 `StationConfig`、控制器、流程引擎或持久化库则不允许。服务端返回 `profileId`，前端所有站点操作使用显式 `stationId`。A/B 单独持有 `_recoveryRequired`、`_busy`、报警、历史和版本；B 的配方和流程使用独立库。业务审计保持设备级共享审计，并通过请求路径区分工站。

## 共享资源

`MultiStationRegistry.Start` 在 `_gate` 下做三件事：

1. 查找并核对 `CELL-SHARED` 当前拥有者是否仍忙碌；完成后才释放旧拥有者。
2. 如果仍被另一个未结束的工站占用，拒绝新启动（HTTP 409）。
3. 同一锁下调用被选工站 `StartSingleCycle()`；该方法先持久化 in-flight 标记、再安排异步执行。只有成功返回后登记当前拥有者。

成功终止、异常中止、软件停止都不由 UI 释放资源，而是在后续管理操作读取 Runtime 非 busy 状态后回收拥有者。旧版 `/api/cycle/start` 也转发此调度器，而不是直接操作 `DeviceRuntime`，避免两个 API 入口的并发绕过。

**权衡**：为了界定隔离与异常边界，锁覆盖完整周期，而不是具体 `VisionLocate` 等节点。下一版可改为由每个节点显式申请/释放共享资源，并实现公平等待队列、取消、超时和死锁检查。当前逻辑只模拟资源排他性，不是安全联锁或确定性实时控制。

## 持久化路径与启动恢复

A 延用 V1.0 数据位置，以便旧库、历史 API 和测试不迁移。B 在 `data/stations/B/` 生成独立 v4 SQLite、JSONL 和配方兼容文件。储存 scope 为应用内固定值并通过标识符白名单校验，不接受用户自定义绝对路径。每个 `RunJournal` 在构造时分别恢复自己数据库残留的 `run_inflight` 为 `Interrupted`，只让对应工站进入待恢复状态，不自动恢复动作。

## 权限边界

- `GET` 工站状态/历史：已登录 Operator 及以上。
- `POST /mode`：Operator 可切 Auto，只有 Engineer 及以上可切 Manual。
- `POST /cycle/start|stop`：Operator 及以上，受资源仲裁。
- `POST /manual/*`、`/sim/*`、`/fault/reset`、`/recovery/confirm`：Engineer 及以上。
- 流程/配方草稿、验证：Engineer 及以上；发布、回滚：Admin。

HTTP RBAC 将站点前缀归一化后复用原有动作权限矩阵；未知新修改指令默认 Admin，不能只靠按钮 disabled 授权。

## 兼容性与下一阶段

仍保留既有 `/api/snapshot`、`/api/mode`、`/api/runs` 等单站 API，它们代表 A。`/api/stations` 返回两站汇总及共享资源 owner；`/api/stations/{id}/runs/{runId}` 和 `/snapshots` 只访问指定站数据库。下一阶段建议迁移现有完整设计器/配方/回放 UI 成独立工站上下文，并补齐真实 .NET API 运行后站点级热重启、流程事务与共享节点锁契约。
