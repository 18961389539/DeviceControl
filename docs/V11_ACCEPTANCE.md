# DeviceControl V1.1 RC — 当前验证结论

## 已执行并通过（本环境）

- `bash run-tests.sh`：V0.3～V1.0 Node 仿真与流程质量回归、V1.1 A/B 离线对象隔离、SQLite v1～v4 迁移/完整性、站点工厂静态接缝检查，结果通过。
- `python tests/test_browser_v11.py`：Chromium 登录、Tab 展示、双工站操作、轴准备、共享资源互斥、分别停止与恢复标志、原流程质量 4/4 检查，结果通过；截图 `DeviceControl_V1.1_Stations.png`。
- `python tests/test_browser_v10.py`：V1.0 原有流程质量 4/4、工作流、回放界面回归，通过。
- 前端 `node --check` 与单文件预览构建，通过。

## 仍未验证，不可宣称通过

由于当前环境没有 .NET 10 SDK：

1. `dotnet build backend/DeviceControl.Api/DeviceControl.Api.csproj` 实际编译。
2. `dotnet run --project backend/DeviceControl.ContractTests/DeviceControl.ContractTests.csproj` C# 流程契约。
3. `python tests/test_api_v09.py`、`python tests/test_api_v10.py`、**`python tests/test_api_v11.py`** HTTP 集成。
4. B 站进程意外结束/重启后 `Interrupted` 状态与站点隔离的真实 C# 行为。
5. CI 远端 .NET/Chromium 完整门禁。

在 SDK 不可用时，`bash run-tests.sh --require-dotnet` 应退出码 2。不能用离线浏览器 Mock 通过替代 C# 后端的正确性。

## 明确不在本版范围内

- 真实硬件、PLC、IO 板卡、机械功能安全或设备调试。
- 多工站动态创建/删除、跨设备节点级共享资源调度、排队与公平性、确定性时间界限。
- 生产级账户认证和不可篡改审计。仅本机 loopback + 演示身份。
- 将旧 A 站全量配置编辑器、故障向导、流程回放 UI 自动绑定到任意工站；本版 B 站通过新调度页和专用 API 操作。

**版本状态：V1.1 RC，仅软件仿真，正式后端验收仍待完成。**
