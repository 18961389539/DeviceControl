# V0.9 软件架构：确定性门禁与版本证据

## 1. 为什么在后端重新检查

V0.8 的浏览器质量场景只在浏览器内存中运行，可能被绕过。V0.9 让服务端 `FlowRevisions` 在草稿验证、发布和回滚流程中调用 `WorkflowQualityGate.Evaluate()`：

`工作区 -> Draft -> Check -> Evaluate -> [报告归档 + Validated] -> Publish 再 Evaluate -> [报告归档 + Supersede + Active 指针更新]`

模型构造一份独立 DI 输入快照、假定初始机构复位、模拟节点条件和动态反馈，不使用 `DeviceRuntime`、IO 控制器、相机服务或系统定时器。输出默认场景预期对比和静态风险提示。**这不取代真实 Runtime，也不提供实时或安全保证。**

## 2. 质量报告与哈希

`FlowRevision.Hash` 是 `SequenceStep` 的 .NET JSON 序列化结果的 SHA-256；报告的 `DefinitionSha256` 必须与之匹配。`ReportSha256` 是由版本 ID、流程摘要、评估时间、PASS 标记、场景明细和诊断信息构成的规范 JSON 负载的 SHA-256。读取时 `WorkflowQualityGate.Verify()` 重新计算报告摘要；这仅是完整性检查，不是公钥签名或防管理员篡改审计。

SQLite v3 的 `workflow_quality_reports` 有流程 FK、`passed` 约束及阻止 UPDATE/DELETE 的触发器。`SqliteStore.SaveFlowVersion` 可在一个事务中保存状态变化、报告证据并更新活动流程。失败场景报告在草稿状态不变的情况下另行留存；结构校验在门禁前失败时，只返回错误，尚不会生成报告。

V0.8 迁移过来的现存 Published 流程如未绑定合格质量报告，将在服务启动时执行一次门禁；失败则拒绝启动该仿真流程。初次创建的默认流程也按同一策略验证。

## 3. 双运行时的契约与差异

固定基线 `tests/fixtures/quality_contract.json` 包含五组流程：正常、前置视觉变量缺失、资源竞争提示、永远不满足的等待条件、条件分支。JS 脚本默认**只读核对**契约；必须显式添加 `--write` 才能更改基线。

`backend/DeviceControl.ContractTests` 在有 .NET SDK 时读取同一个 JSON，对 C# 门禁的 PASS、场景 outcome、warning 数量做逐条断言。这里对照的是**模型质量门禁结果**，不是实际动作调度时序。HTTP 集成脚本覆盖失败草稿报告、成功验证与发布、角色权限、过期版本冲突、SQLite 结果。

## 4. 事务、异常与隔离

验证阶段门禁通过后，质量报告和 `Validated` 状态原子落库。发布阶段重新运行门禁，在同一事务中写报告、变更版本状态与指向 Published 版本的单例指针；事务失败时目标仍是旧版本。发布动作仍受 DeviceRuntime 的空闲和恢复状态保护。

API 默认 `127.0.0.1:5088`，允许通过 `SimListenUrl` 使用其他 loopback HTTP 地址用于测试，拒绝绑定非本机地址。仍只有演示级令牌，不允许暴露到非本机网络。演示离线 HTML 仍在内存里运行模拟器，不代表后端 API 已连接。

## 5. 已知缺口

- 需要真实 C# 编译及运行验证；当前环境不具备 .NET SDK。
- 目前门禁不会预测调度竞态、真实设备事件，也不能保证特定工作周期的可靠性。
- 自定义 DI 场景尚未成为服务端持久化、管理员签核的发布必选项。
- 数据库完整性校验无法保护具有数据库文件最高权限的恶意操作者。
- 尚未补齐多工站互斥、事务型节点变量快照和负载回归。
