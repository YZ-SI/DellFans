# 传感器读数验证

目标：证明风扇转速、CPU 封装温度、核心温度是**实时**且**准确**的。

## 方法

| 层次 | 做法 |
|---|---|
| 解码正确 | MSR 按 Intel SDM 公式（TjMax = `0x1A2` 位 23:16；温度 = TjMax − `0x19C`/`0x1B1` 位 22:16，位 31 有效）做表驱动单元测试；自洽检查：核心最大 = 各**物理核**最大，核心平均 = 各物理核平均（0x19C 是每物理核一份，超线程同胞读数相同，不重复计入；混合架构按 P/E 核命名，例如 6 P + 8 E = 14 核 / 20 逻辑核时行名为 `P-core 0..5`、`E-core 6..13`，与 HWiNFO 一致），封装与核心最大相差几度以内 |
| 准确 | 同一时刻与独立实现比对：HWiNFO64（自带驱动，独立读同一片内 DTS，期望 ≤ 1 °C）；ESIF（Intel 平台固件，路径完全不同，期望平均 ≤ 2 °C）；风扇：SMM 与 BFn 两条 Dell 固件接口互比 |
| 实时 | 每个读数带采集时刻；记录 传感器→控速程序→管道→界面 每跳延迟并设上限；数据源停更时标记 Stale 并退出控制 |

## `DellFans.Cli.exe --verify-sensors`（只读）

```
DellFans.Cli.exe --verify-sensors [--seconds 120] [--load 30] [--output <目录>]
```

- 本进程内起一份传感器层，每 **250 ms** 取一次；同时每秒向控速程序要一次 `Status`（`Detail:true`，控速程序没有应答时报告 `service: not found`）。
- `--load N`：从第 `min(10, 秒数/4)` 秒起对全部逻辑核加 N 秒负载（BelowNormal 优先级；封装/最高读数 ≥ 85 °C 时立即撤销）。不带 `--load` 完全不产生负载。
- 若存在 HWiNFO 共享内存 `Global\HWiNFO_SENS_SM2`（需在 `HWiNFO64.INI` 的 `[Settings]` 加 `SensorsSM=1`，只读），加入与 HWiNFO 的差值。
- 风扇 SMM 与 BFn 互比**只在控速程序已应答且没有接管风扇时**进行；接管时写 `skipped: service owns firmware`，控速程序没有应答时一律不碰固件。
- 输出目录里的 `report.json`（逐项结果、每个来源的刷新间隔、差值统计、逐跳延迟、不变量、过期事件）和 `samples.csv`（每 250 ms 一行；控速程序样本与风扇样本各占一行），并在控制台给出 PASS/WARN/SKIP 摘要。

| 项 | 判据（超出即 WARN） |
|---|---|
| 与 HWiNFO | 封装温度差的中位数 ≤ 1 °C |
| 与 ESIF | 封装温度差的平均 ≤ 2 °C（传感器层本身用 ESIF 时跳过） |
| 控速程序数据年龄 | p95 ≤ 1.5 s（行采集时刻到客户端收到） |
| 控速程序与直读 | 封装温度差平均 ≤ 2 °C |
| 不变量 | 核心最大 = 各核最大、平均 = 平均值（容差 0.15 °C，行已取整到 0.1）、封装与核心最大相差 ≤ 6 °C |
| 过期事件 | 0 次 |
| 风扇 SMM vs BFn | 差的中位数 ≤ 200 RPM |

纯分析部分（`src/SensorVerificationAnalysis.cs`）有离线夹具测试：`tests/run-verify.ps1`。
