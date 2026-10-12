# V1.0 软件可靠性架构

## 生命周期

```text
StartSingleCycle
  -> SQLite run_inflight (commit first)
  -> FlowEngine executes nodes
  -> trace ordinal + NodeVariableSnapshot captured on callback
  -> RunJournal.AppendedCycle / SQLite transaction
       production_runs + run_node_traces + run_variable_snapshots
       DELETE run_inflight
  -> next idle cycle
```

每个节点的快照和轨迹使用执行器生成的 `traceOrdinal` 对齐，不依赖异步回调的执行先后。完整周期与子节点记录在同一事务内持久化；若任一快照写入失败，则本次完整周期记录不作为成功持久化。下一次服务初始化会扫描 `run_inflight` 并建立 `Interrupted` 周期，恢复标记不会触发新的流程动作。

## 持久化模型

- SQLite v4 延续 v3 的 `workflow_quality_reports` 质量门禁记录。
- `run_inflight`：执行中的唯一周期 ID、起始时刻、流程与配方版本。
- `run_variable_snapshots`：周期 ID + 节点轨迹序号、节点 ID、输入状态、轴状态、气缸状态、视觉结果、配方及流程版本。
- `run_variable_snapshots` 外键连接到 `run_node_traces` 的 `(run_id,ordinal)`；触发器禁止直接 UPDATE/DELETE。SHA-256 用于读取时的内容损坏检查，不是签名或防篡改系统。
- `production_runs.payload_json` 不再保存另一份变量快照。历史查询通过受校验的快照表还原变量证据。

## 本地 API（演示授权）

- `GET /api/runs` 最近 100 个周期，包含快照证据。
- `GET /api/runs/{id}` 单个周期；不存在返回 404。
- `GET /api/runs/{id}/snapshots` 返回该周期有 SHA-256 校验的节点快照；不存在返回 404。

## 发布检查

CI 的 `run-tests.sh --require-dotnet` 必须具备 .NET 10 SDK；缺失 SDK 即失败，而非将跳过解释成验收成功。Playwright 作为 CI 独立步骤执行，结果同样是发布门禁。保留 `run-tests.sh` 作为可在无 .NET 环境下运行的部分软件检查。
