# DeviceControl V1.0 — 软件可靠性候选版验收

## 已在当前无 .NET SDK 环境中执行

- JavaScript 设备仿真、权限、流程调度、流程设计、历史回放和质量门禁 V0.3～V0.8 Node 分项回归：通过。
- 浏览器 V1.0 质量门禁与页面切换：单独运行通过；V0.9 HTML 浏览器分项未作为本轮发布门禁重新宣称通过。
- V1.0 历史快照与并行轨迹序号一致性测试：通过；缺失历史变量时不推测数据。
- SQLite 引擎中的 v3→v4 幂等升级、旧数据保留、外键、快照不可修改触发器、进程中断标记事务及回滚：通过。
- SQLite v1→v2、v2→v3、WAL/备份恢复和基本 Schema 验证：通过。
- `bash run-tests.sh --require-dotnet` 在未安装 SDK 的当前环境中正确失败（退出码 2），没有给出虚假通过结果。

## 仍需在 .NET 10 SDK 环境中验证

1. `dotnet build backend/DeviceControl.Api/DeviceControl.Api.csproj` 实际编译。
2. `dotnet run --project backend/DeviceControl.ContractTests/DeviceControl.ContractTests.csproj` C# 流程质量契约。
3. `python tests/test_api_v09.py` 旧发布质量门禁接口。
4. `python tests/test_api_v10.py` 完整周期变量快照、历史接口、模拟进程突然退出与重启恢复。
5. CI 中的自动构建、API 测试和真实 `Microsoft.Data.Sqlite` 客户端行为。

当前交付为 **V1.0 软件可靠性候选版（RC）**，不是已完成 .NET 编译验收的正式版，也不具备机械或设备安全控制资格。

## 使用边界

- 仅演示账号 + 本地 HTTP loopback + 软件仿真。
- 未完成多工站隔离、生产环境身份认证、运行中热备、数据库损坏自动修复、真正的确定性实时控制。
- 快照是节点结束时的观察值，不是运动中的高频采样；并行工况下不能从节点快照推断连续轨迹。
- `Interrupted` 表示数据库中残留执行中标记（进程异常退出），不代表能够知道最后执行到哪一步；恢复需要人工确认。
- 故障恢复不自动重放先前动作。
