# V0.4 软件模拟验收

| 编号 | 场景 | 结果 | 证据 |
|---|---|---|---|
| G01 | Operator 无法进行手动调试 | PASS | Node + 浏览器 |
| G02 | Operator 不能创建配方草稿 | PASS | Node + 浏览器 |
| G03 | Engineer 可以创建和验证草稿 | PASS | Node + 浏览器 |
| G04 | Engineer 不能发布配方 | PASS | Node + 浏览器 |
| G05 | Admin 发布已验证配方 | PASS | Node + 浏览器 |
| G06 | 运行中阻止配方发布 | PASS | Node |
| G07 | 需要人工恢复时阻止发布 | PASS | Node |
| G08 | 回滚创建新的已发布版本，历史不被覆盖 | PASS | Node + 浏览器 |
| G09 | 失败操作写入模拟审计，标记拒绝 | PASS | Node |
| G10 | 11 步运行周期关联配方版本 | PASS | 浏览器 |
| G11 | 故障恢复清单显示未满足项及报警 | PASS | 浏览器 |
| G12 | 原 V0.3 自动流程、故障、取消回归 | PASS | Node |
| G13 | C# 编译与真实 REST API/RBAC 联调 | NOT RUN | 当前环境无 .NET 10 SDK |
| G14 | C# 配方 JSON 持久化、审计 JSONL 重启恢复 | NOT RUN | 需要 .NET 运行测试 |

只验收 Simulation 软件行为；演示身份和日志不满足生产认证、可靠审计要求。
