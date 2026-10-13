# DeviceControl V2.0 RC — Release acceptance matrix

Version: 2.0-rc. Scope: software simulation only; excludes actual hardware, actuators, safety certification.

## 当前状态：证据绑定（Q08 之后）

本项目的发布结论**不再写在文档正文里冒充验收**。当前状态由三个机器可读产物互相绑定，任何源码、测试、配置或文档修改都会使它们失效并要求重新生成：

| 产物 | 内容 | 生成 / 校验命令 |
|---|---|---|
| `MANIFEST_SHA256.txt` | 当前交付集（git tracked + 未忽略的未跟踪文件，剔除生成证据目录）逐文件 SHA-256 | `python tools/release_manifest.py --write` / `--verify`（`--verify` 是发布门禁任务 `manifest:verify`，零缺失/零差异/零未登记才通过） |
| `DeviceControl_V2.0_ReleaseStatus.json` | 绑定 git 修订（HEAD + 工作树脏标志 + 工作树摘要）、清单摘要、门禁证据路径与结果、`releaseApproved` | `python tools/release_manifest.py --bind-status <release-gate.json>` |
| `reports/quality-fix-q01q08-20261013/gate/release-gate.json` | 逐任务状态、日志路径、源码指纹、耗时 | `python tools/release_gate_v20.py --profile release` |

**严格状态契约（Q07）**：门禁任务状态由 `classify_output()` 判定——`RESULT: PASS|FAIL|BLOCKED|SKIP` 显式标记优先、退出码 77 固定为 BLOCKED、输出含 UNVERIFIED/SKIP/BLOCKED 而退出 0 一律记 BLOCKED。**未执行的任务永远不会被计为 PASS**；release profile 只有在全部任务 PASS（无 FAIL / BLOCKED / SKIP / TIMEOUT、无缺失 SDK 任务）时才置 `releaseApproved = true`。

读取当前结论请使用：

```bash
python -c "import json;print(json.load(open('DeviceControl_V2.0_ReleaseStatus.json'))['releaseApproved'], '|', json.load(open('DeviceControl_V2.0_ReleaseStatus.json'))['gateEvidence'])"
```

**已知环境阻塞（非代码缺陷）**：本机文件系统不生成真实符号链接（`symlink_to`/`mklink` 报告成功但无重分析点），`python:test_recovery_v20.py` 因此以退出码 77 报告 BLOCKED，正式门禁不将其记作 PASS。该专项需在具备真实符号链接能力的环境（Linux CI）执行；详见 `docs/R01_R08_FIXES.md`。

## 历史记录（2026-10-13 早期，当时环境）

> 以下表格记录的是修复前/早期环境下的一次验收，其中"dotnet not installed / cannot run without SDK"等结论属于**当时执行环境**，不代表当前工具可用性或当前状态。当前状态只以上节绑定证据为准，不再维护文档正文中的数字。

| Gate | Evidence this run | Release decision |
|---|---|---|
| Node runtime, workflow, governance, replay, stations, resources | 13 distinct PASS entries in first offline run | PASS (selected scope) |
| SQLite schema, migration, WAL/backup, integrity | five individual scripts returned 0 | PASS |
| Recovery v20 provenance fixture | controlled cross-station backup and symlink failures rejected; original untouched | PASS |
| Browser isolated sandbox V1.9 / V1.8 drills / V1.7 maintenance / V1.6 rollout | Chromium scenarios returned 0 | PASS |
| Full uninterrupted offline suite | aborted/exceeded execution window before completion; old version assertions patched | UNVERIFIED |
| .NET 10 compilation of backend | dotnet not installed; installation host unavailable | BLOCKED |
| .NET contract runner | cannot run without SDK | BLOCKED |
| HTTP authentication, two-station upgrades, restart recovery | cannot run without SDK | BLOCKED |

**发布未批准即不得打 V2.0 GA/stable 标签**——除非绑定证据中的 `releaseApproved` 为 true（即 release profile 在无 FAIL/BLOCKED 的干净运行中通过并完成 `--bind-status`）。

Exact commands:

```bash
python tools/release_manifest.py --write          # 冻结交付集后重新生成清单
python tools/release_gate_v20.py --profile release
python tools/release_manifest.py --bind-status reports/<本轮>/gate/release-gate.json
```

The full release profile includes `dotnet build -c Release`, the .NET contract runner, the Python live HTTP suites, and Chromium smoke. Each case has its own bounded execution, log, and JSON status. A fast targeted offline run is diagnostics only and must never set `releaseApproved = true`.

Known technical debt:
- JSON deployment journal updates and separate workflow/recipe SQL publishing are not a cross-store atomic transaction. Operators must review interrupted upgrades manually.
- Journal SHA-256 checks corruption, not an authenticated origin guarantee against privileged filesystem attackers.
- A source-only C# check does not prove successful compilation. CI must be executed in an environment with SDK and NuGet access.
- Audit durability fixes (Q01/Q02) do not cover a genuinely full failure of every storage medium; inspect `audit-loss.json` and the `/api/audit/acknowledge-loss` flow in that case.
