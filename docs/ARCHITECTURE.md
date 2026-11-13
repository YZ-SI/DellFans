# DellFans 0.2.0 架构与接口约定

本文件是各模块共同遵守的约定。改动接口时先改本文件。

## 1. 名称

| 项 | 名称 |
|---|---|
| 产品显示名 | DellFans |
| C# 命名空间 | `DellFans` |
| GUI exe | `DellFans.exe` |
| CLI exe | `DellFans.Cli.exe` |
| 控速程序 exe | `DellFans.Service.exe --portable` |
| 托盘/deck exe（Rust） | `DellFans.Tray.exe` |
| 监护 exe（Rust） | `DellFans.Watchdog.exe` |
| 管道 | `DellFans.Control.v2` |
| 硬件互斥与会话恢复 | 受保护目录锁文件（见 §5） |
| 单实例 / 显示信号 | `Local\DellFans.Window.v2` / `Local\DellFans.ShowWindow.v2` |
| 登录启动 | `HKCU\...\Run` 值 `DellFans.Tray` |
| 程序目录 | 用户解压的文件夹（绿色版，不安装） |
| 数据目录 | `%ProgramData%\DellFans` |
| 用户目录 | `%LocalAppData%\DellFans` |
| 用户注册表 | `HKCU\Software\DellFans\Ui` |

## 2. 进程

| 进程 | 语言 | 权限 | 常驻 | 职责 | 内存目标（私有） |
|---|---|---|---|---|---|
| `DellFans.Service.exe --portable` | C# .NET 4.8 | 当前用户，管理员身份（普通进程，不是 Windows 服务） | 是 | 传感器、模式控制、SMM/BIOS 写入、IPC 服务端、能力检测 | ≤ 30 MiB |
| `DellFans.Watchdog.exe` | Rust | 由控速程序启动（继承管理员身份） | 接管期间 | 监视控速程序存活与心跳；失效时启动 `DellFans.Cli.exe --recover <session>` | ≤ 3 MiB |
| `DellFans.Tray.exe` | Rust | 用户（普通权限） | 登录后 | 托盘、deck、模式快捷切换、打开主界面 | ≤ 5 MiB |
| `DellFans.exe` | C# WinForms | 用户（普通权限） | 否，关窗即退出 | 主界面、详情、设置 | 打开时 ≤ 40 MiB |
| `DellFans.Cli.exe` | C# | 当前用户（恢复时继承管理员身份） | 否 | CLI、`--recover`、`--install-sensor-driver`、`--capability-probe` | — |

恢复逻辑只有一份（C#，`FanWatchdog` 恢复部分）。Rust 监护只做判定和启动。

## 3. IPC：`\\.\pipe\DellFans.Control.v2`

- 帧：4 字节小端长度 + UTF-8 JSON，单帧 ≤ 64 KiB（请求）/ 4 MiB（回复）。
- 多实例（最多 4 个），首实例 `FILE_FLAG_FIRST_PIPE_INSTANCE`。
- DACL：SYSTEM、Administrators 完全控制；`INTERACTIVE` 读写；拒绝 `NETWORK`。管道带 `PIPE_REJECT_REMOTE_CLIENTS`，服务端另拒绝网络登录令牌。
- 客户端以 `TokenImpersonationLevel.Identification` 连接，用 `GetNamedPipeServerProcessId` 取对端进程，要求它的映像正是客户端同目录下的 `DellFans.Service.exe`，且令牌已提升（管理员）。普通权限程序无法启动提升的进程，因此无法冒充。
- `Exit`（交互用户）：控速程序交回 BIOS 自动后退出；托盘菜单“退出 DellFans”使用。

请求：`{"Command": "...", ...}`

| Command | 字段 | 权限 | 说明 |
|---|---|---|---|
| `Status` | `Detail`（bool，缺省 true） | 交互用户 | `Detail:false` 返回摘要行（`SummaryOnly:true`） |
| `SetMode` | `Mode`：`quiet`/`balanced`/`performance`/`custom`/`auto`；`MinimumRpm`（custom 时，1000–5570） | 交互用户 | 立即生效并持久化 |
| `Probe` | — | 管理员 | 15 s 全速响应检查后恢复；与 `SetMode` 同一能力门：本机型号/BIOS 未通过能力检测时拒绝（`--test-fan` 同） |
| `Capability` | — | 交互用户 | 返回能力检测结果与报告路径 |
| `RunCapabilityProbe` | `Consent`（必须为 `true`） | 交互用户 | 能力检测第 2 步（BIOS 全速 15 s 后恢复）。允许交互用户而非仅管理员：检测只做 15 s 全速（失败方向是风扇更吵、不会过热）然后恢复 BIOS 自动，且必须显式 `Consent=true`；服务拥有风扇、已有检测进行中或保存的模式不是 `auto` 时拒绝 |

`Probe` 需要已提升的管理员。服务端权限表与字段校验见 `src/ControlPipe.cs`（`ControlPolicy`），细节见 `docs/CONTROL_PROTOCOL.md`。`DellFans.Cli.exe --recover <会话文件>` 退出码：0 已恢复或无需处理，1 失败需重试，2 路径被拒绝，3 会话恢复正常（继续监视）。监护进程启动命令行：`DellFans.Watchdog.exe "<会话文件>"`（存在于服务同目录时优先），否则 `DellFans.Cli.exe --watchdog "<会话文件>"`；两者都需在监视就绪后写入 `<会话文件>.ready`。

回复：`{"Success": bool, "Error": string|null, "Snapshot": {...}}`。Snapshot 字段只增不删；托盘只依赖：
`PolledAtUtc`、`SummaryOnly`、`Sensors[].{Id,HardwareType,Name,Type,Value,Quality,PolledAtUtc}`、`Background.{State,Mode,EffectiveTargetRpm,ControlTemperatureC,Settings}`、`Warnings`。

## 4. 传感器

| 量 | 主来源 | 备用 | 都不可用 |
|---|---|---|---|
| CPU 温度 | PawnIO IntelMSR：`0x1A2` TjMax，`0x1B1` Package，`0x19C` 逐物理核（`GetLogicalProcessorInformationEx` 枚举，固定到各核掩码的首个逻辑核，支持处理器组；混合架构按 EfficiencyClass 命名 `P-core n` / `E-core n`，否则 `Core n`，Id `cpu/core/<n>`，n 从 0 起；核心最大/平均按物理核计算） | ESIF `root\wmi:EsifDeviceInformation` 实例 1–3 最大值（去 0） | 服务不接管，BIOS 自动 |
| 风扇转速 | SMM `0x02A3` | BFn/SMBIOS | — |
| 硬盘温度 | NVMe：`IOCTL_STORAGE_QUERY_PROPERTY` StorageDeviceTemperatureProperty；SATA：`SMART_RCV_DRIVE_DATA`（读写句柄，仅 SMART 读命令） | — | 显示不可用 |

- 采样 250 ms；控制用 1 s 均值；90 °C 保护用原始最大值，立即生效。保护值 = 自上次读取以来与最近 1 s 内的最大原始读数（单次尖峰不会因读取间隔而丢失，只报告一次）；过期则为空。
- 时间：样本的年龄、顺序、过期与调度一律用单调时钟（`Stopwatch.GetTimestamp`）；墙钟仅用于显示（`PolledAtUtc`），墙钟跳变 ±1 h 不影响过期判断。存储读数同理，负年龄视为过期。
- ESIF 交叉校验在独立线程运行，最多每 5 s 一次，上次耗时 > 500 ms 则跳过下一个周期；阻塞的 WMI 调用不会让 MSR 样本过期。
- ESIF 为唯一来源时：值连续 30 s 不变会给出警告（可能冻结）。ESIF 仍可用于控制，但保护权威性降低，优先使用 MSR。
- `0x1B1` 无“读数有效”位（SDM：22:16 为 Pkg Digital Readout，63:23 保留；位 31 仅 `0x19C` 有），封装温度解码不要求位 31。
- 逐核读取相互隔离：单核失败记为不可用（NaN），仅当全部读取失败时该次读取才失败。CPU 采样线程优先级 AboveNormal；`Dispose` 在采样线程仍处于慢读取时由该线程在返回后释放句柄，不会重新创建。
- 控制温度只来自传感器层的 1 s 均值（`CpuControlTemperatureC`）。
- PawnIO：不随附。检测失败（含驱动运行但 `\Device\PawnIO` 不存在）时，主页提示卡（Missing／HalfUninstalled；NeedsRestart 只提示重启）与托盘菜单项“安装 CPU 温度驱动…”（`DellFans.exe --sensor-driver`，直接弹出同意对话框）；用户同意后由 GUI 经 `runas` 启动 `DellFans.Cli.exe --install-sensor-driver --result-file <tmp>`（CLI 把结果 JSON 写入该临时空文件，GUI 据退出码 0/3010/其他 与 UAC 取消 1223 显示结果）下载固定版本、校验 SHA-256 与签名者 `CN=namazso.eu`、`-install -silent`。`IntelMSR.bin`（LGPL-2.1+）作为单独文件随附，附许可证与源码地址。
- 序列化不用 `JavaScriptSerializer`（+5–10 MiB）；IPC 与报告用自写的最小 JSON 读写器。

## 5. 安全

- 硬件与会话锁使用 `%ProgramData%\DellFans\locks\` 下的锁文件（`FileShare.None` 独占打开，目录 ACL 仅 SYSTEM/Administrators）。
- 监护：等待父进程句柄（`WaitForSingleObject`），心跳每 1 s 检查，失效判据 8 s 不变；保持“每次固件写入前可被监护中止”。
- 数据目录：控速程序启动时创建并保护 `%ProgramData%\DellFans`（属主 Administrators，仅 SYSTEM/Administrators 可写；路径或内容中有重解析点则拒绝）。
- 登录启动只写 `HKCU\...\Run`（托盘，普通权限）；托盘发现控速程序未运行时经 UAC 请求启动。不使用最高权限计划任务，因为解压目录普通用户可写。
- 外部皮肤/语言/偏好文件解析失败只跳过该文件，设置页列出“已忽略：文件名（原因）”。
- GUI 与托盘普通权限运行（GUI 单独 manifest `asInvoker`）；只有控速程序和 PawnIO 安装需要管理员。

## 6. 模式

| 模式 | 最低 RPM | 最高 | 起调温度 |
|---|---|---|---|
| quiet 静音 | 1500 | 3000 | 55 °C |
| balanced 平衡 | 2500 | 全速 | 65 °C |
| performance 高性能 | 4000 | 全速 | 65 °C |
| custom 自定义 | 1000–5570 | 全速 | 65 °C |
| auto BIOS 自动 | — | — | 不接管 |

- 高于起调温度：每 1 s 目标 +50，直到上限。低于（起调 −3 °C）且稳定 20 s：每 1 s −100，直到最低。之间保持。
- 任何模式下 CPU ≥ 90 °C 立即全速，≤ 85 °C 后回到模式规则。温度失效 8 s 交回 BIOS。
- `settings.json` 的 `SchemaVersion` 为 2，含 `Mode`；首次运行为 balanced（检测通过后才接管）；文件无效时为 auto。
- CLI：`--mode quiet|balanced|performance|custom|auto`；`--rpm N` = custom + 最低 N。

## 7. 能力检测（所有 Dell 电脑）

不按机型区分：每台 Dell 电脑都走同一流程，详见 [CAPABILITY.md](CAPABILITY.md)：

1. 控速程序启动后自动做只读检查：Dell WMI 类与令牌、SMM 转速读取、BIOS 风扇模式读取、CPU 温度来源；硬盘温度仅作信息。不写固件。
2. 读检查全部通过后，用户首次打开 DellFans 窗口时弹出同意对话框；同意后 BIOS 全速约 15 s，转速须上升 ≥ 300 RPM，随后恢复 BIOS 自动并读回确认。CLI `--capability-probe` 等价。
3. 通过则把 `{Model, BiosVersion, PassedAtUtc, Checks}` 写入 `%ProgramData%\DellFans\capability.json`，绑定型号与 BIOS 版本；只有全速测试通过的记录有效。
4. 只读检查或测试失败（或不是 Dell / 缺少接口）：窗口提示“当前电脑不支持”，写匿名报告，状态行保持“当前电脑不支持 · 风扇保持 BIOS 自动”。
5. 通过之前控速程序拒绝除 `auto` 以外的所有模式，不控制风扇。
6. 全速测试没有独立监护会话；保护是写前标记 `full-override.json`、控速程序启动时标记存在则恢复 BIOS 自动一次、失败方向是 BIOS 全速（不会过热）。同一规则覆盖所有 `SetFullPersistent` 写入。
