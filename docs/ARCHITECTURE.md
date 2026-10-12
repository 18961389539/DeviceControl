# Device Control V0.2 — 设计细节

## 系统拓扑

```text
Web HMI (HTML/CSS/JS) -> localhost REST -> DeviceRuntime (C#)
       |                                     | mode / busy / recovery
       |                                     | config-driven sequence
       |                                     | ordinary interlock monitor
       |                                     v
       |                              hardware abstractions
       |                                     |
       |                        Simulation IO / Axis / Cylinder / Camera
       |
       +--> 浏览器内置 MockRuntime（只在独立仿真模式）

StationConfig  <-  config/station.json
             \-> frontend/station-data.js (构建时同步副本)
```

## 设计边界

- 所有具体动作通过 `IIoController`、`IMotionController`、`ICylinderRegistry`、`IAxis`、`ICylinder`、`ICamera` 接口对接。`SimHardware.cs` 仅有 Simulation 实现。
- `StationConfig.Validate()` 在启动时校验重复 IO 地址、重用阀 DO、错误行程、无效工作步骤和缺失的普通 DI。
- 站点原型使用设备级单 Busy 标记，限制任意两个普通手动动作并发，后续引入资源锁才能支持真正的并行机构调度。
- 手动模式和自动流程都执行相同的普通互锁规则；轴模拟器和气缸模拟器在运动时持续检查动作普通联锁。
- 任意动作失败/取消会清除所有轴回零标志并要求人工确认；故障复位不等于恢复确认。恢复确认要求所有轴已使能、已回零且处于配置回零位，全部气缸缩回。
- `RequestStop()` 仅通知 CancellationToken，不能等价于硬件安全停止或 STO。
- 轴位置变化仅是仿真，无硬实时保证。轨迹插补、限位保护、伺服反馈必须落在真实板卡/驱动及独立安全层。
- 真实驱动不得在未评估的情况下使用本程序公开的仿真输入接口。

## 当前未完成

- 无实际 IO/运动控制卡 SDK / 相机 SDK；无安全硬件输入的实际设备层。
- 无独立用户认证、按角色授权、不可抵赖操作审计、持久化数据库、版本化配方。
- 无 PLC 软逻辑、任务并行、流程编辑器、硬件同步捕获、多轴插补、功能安全认证。
- C# 未执行编译验证；前后端 API 一致性还需要在真实 .NET SDK 环境做测试。

## 推荐下一次架构拆分

```text
DeviceControl.Domain                contracts, definitions, alarm IDs
DeviceControl.Hardware.Abstractions  transport API, board error semantics
DeviceControl.Hardware.Simulation    deterministic sim + fault injection
DeviceControl.Hardware.Vendor        per-vendor adapter, no unreviewed runtime enablement
DeviceControl.Runtime               machine state, commands, interlocks, scheduler
DeviceControl.Vision                acquisition / calibration / result mapping
DeviceControl.Api                   auth + REST / push status
DeviceControl.Tests                 unit, integration, HIL
```
