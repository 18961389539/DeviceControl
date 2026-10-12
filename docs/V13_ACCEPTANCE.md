# V1.3 软件验收清单

1. `node tests/test_workspace_v13.mjs`：工站路由白名单，审计路径映射，stale revision 判定。
2. `python tests/test_browser_v13.py`：顶部 A/B 切换，画布未保存变更不串站、B 独有配方草稿、输入仿真分隔、工站跳转与质量门禁。
3. `bash run-tests.sh`：保留 V1.2 Node、SQLite、配置静态和旧版流程检查。
4. `bash run-tests.sh --require-dotnet`：必须完成 C# 编译、合同与 API V0.9–V1.3 集成；没有 SDK 时明确失败，不能把 Node 静态检查算作编译。
5. `python tests/test_api_v13.py`：要求 Engineer 读取工站审计，Operator 无权查看；A/B 流程独立发布，审计不串站。

### 安全与部署门槛

所有演示账号、模拟普通互锁、动态资源锁**不构成**现场机械安全控制。当前只有本机 Simulation 模式。不要用于生产线控制，也不要开放公网端口。
