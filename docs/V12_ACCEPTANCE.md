# V1.2 运行与验收记录

## 执行方式

- `bash run-tests.sh`：Node 浏览器引擎单元、SQLite schema/migration、资源 FIFO/超时/取消/原子申请、静态 C# 接缝校验；有 dotnet 时运行 C# 编译与接口测试。
- `python tests/test_browser_v12.py`：在 Chromium 加载 V1.2 独立 HTML，准备 A/B、同时启动周期、验证实时共享资源监视面板、软件停止、质量门禁 4/4。
- `bash run-tests.sh --require-dotnet`：生产发布门禁应开启，**缺 SDK 会失败**。GitHub Actions 配置 .NET 10。
- 后端：`dotnet run --project backend/DeviceControl.Api`（仅本机 loopback），Web 前端参阅根目录 README。

## 本次容器中的真实测试范围

- ✅ Node 老版本流程、权限、调试、回放、质量模型等回归。
- ✅ Node V1.2 FIFO、双工站关键节点串行、取消/超时、多资源锁测试。
- ✅ SQLite schema、备份与 v1→v4 迁移回归。
- ✅ Chromium 离线交互测试：两个工站同处 `Running`、资源看板渲染、软件停止和流程质量检查 4/4。
- ⚠️ **未运行**：.NET 10 C# 编译、C# 契约测试和实际 ASP.NET HTTP 接口集成（本环境未安装 SDK）。对应源码与 CI 用例已准备，不宣称已验收。

注意：离线 HTML 是浏览器模拟，不代表 C# API 已成功编译。
