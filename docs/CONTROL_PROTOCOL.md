# 控制管道与本机风扇控制实现

## 管道权限与请求校验

管道 `\.\pipe\DellFans.Control.v2`，帧为 4 字节小端长度 + UTF-8 JSON（请求 ≤ 64 KiB，回复 ≤ 4 MiB）。

**创建：** 首个实例使用 `FILE_FLAG_FIRST_PIPE_INSTANCE`（名称被抢占时服务记录 `pipe-error.json` 并退避重试，不退出）；最多 4 个实例同时服务，因此长耗时命令不阻塞 `Status`。读取请求有总期限（5 s），空闲连接不会占住实例。

**DACL：** NETWORK 拒绝；SYSTEM、Administrators 完全控制；INTERACTIVE 仅读写（没有创建实例、改 DACL、取得所有权的权利）。服务端读到请求后在 `RunAsClient` 下检查调用者令牌：含 NETWORK SID 的令牌（网络登录）、匿名、来宾，以及既非 INTERACTIVE 又非管理员/SYSTEM 的令牌（服务、批处理）一律拒绝。未提升的管理员令牌里 Administrators 只是“仅拒绝”，按交互用户处理。

| Command | 允许的调用者 | 字段（其余字段必须为空） |
|---|---|---|
| `Status` | 交互用户或管理员 | `Detail`（可选） |
| `SetMode` | 交互用户或管理员 | `Mode`（必填）：`quiet`、`balanced`、`performance`、`custom`、`auto`（小写，区分大小写）；`MinimumRpm`（仅 `custom`，必填，1000–5570；其他模式不得携带） |
| `Capability` | 交互用户或管理员 | 无；回复快照的 `Background.Capability` 为能力检测状态（[CAPABILITY.md](CAPABILITY.md)） |
| `RunCapabilityProbe` | 交互用户或管理员 | `Consent`（必须为 `true`，否则拒绝）；运行 15 s 全速响应测试，控速程序正在控制风扇或已有检测进行中时拒绝 |
| `Probe` | 已提升的管理员 | 无 |

`SetMode` 立即生效并写入 `settings.json`；服务端校验（`ControlPolicy.Validate`）在处理程序之前拒绝未知模式、`custom` 缺少或越界的 `MinimumRpm`、以及非 `custom` 携带的 `MinimumRpm`。能力检测未通过时，除 `auto` 以外的 `SetMode` 与 `Probe` 都被拒绝。

`Snapshot.Background` 含 `Mode`（字符串，当前保存的模式）与 `ModeLimits`（`{"Min":1500,"Max":3000,"StartC":55}`，`auto` 时为 `null`）。`Background.Settings` 为 `{SchemaVersion:2, Mode, Enabled(派生), MinimumRpm(自定义最低值，其他模式下也保留), LastFault}`。规则见 [MODES.md](MODES.md)。

未知命令（区分大小写）、超长命令名、缺失或越界的字段在到达处理程序前即返回 `Success:false`；权限不足的回复以 `Access denied:` 开头。

**客户端：** 以 `TokenImpersonationLevel.Identification` 连接；用 `GetNamedPipeServerProcessId` 取对端进程，只有它的映像正是客户端同目录下的 `DellFans.Service.exe`、且令牌已提升时才发送请求。管道不存在时抛出 `ServiceNotRunningException`；只有主界面（以及托盘经主界面）会在用户同意后经 UAC 启动控速程序。

**`Exit`：** 交互用户；控速程序回复后交回 BIOS 自动并退出（托盘“退出 DellFans”）。

**监护恢复：** `DellFans.Cli.exe --recover <fan-watchdog-*.json>` 执行一次恢复：退出码 0 = 已恢复或无需处理，1 = 失败需重试，2 = 路径被拒绝，3 = 父进程与心跳已恢复正常（调用方继续监视）。恢复前的互斥是 `%ProgramData%\DellFans\locks\recover-<记录名>.lock` 独占文件。

**未知字段：** 读取请求时忽略未知键（`Status` 因此对新旧客户端兼容）；写命令（`SetMode`、`Probe`、`RunCapabilityProbe`）带未知键会被拒绝并列出键名。JSON 读写用 `src/MiniJson.cs`（深度 ≤ 16、重复键拒绝、请求 ≤ 64 KiB、回复 ≤ 4 MiB），控速程序不加载 System.Web.Extensions。

## Dell SMM 接口

实现位于 `src/SmmFanControl.cs`。不同 Dell 电脑的可用命令不能互相推定，见 [DELL_INTERFACE_COMPATIBILITY.md](DELL_INTERFACE_COMPATIBILITY.md)。

Windows 提供 `root\dcim\sysman\diagnostics:LegacyDiags.Execute`，活动端点为 `ACPI\PNP0C14\WMDG_0`，GUID 为 `{F1DDEE52-063C-4784-A11E-8A06684B9B01}`。四个寄存器分别以 4 字节、小端序传入 `EaxVal/EbxVal/EcxVal/EdxVal`，相应 Len 为 4。

| EAX 命令 | 行为 |
|---|---|
| `0xFEA3` | 签名：EAX=`0x44494147`，EDX=`0x44454C4C` |
| `0x02A3`，EBX=0 | 风扇 0 的 RPM 反馈 |
| `0x04A3`，EBX=`state << 8` | 名义档位 1=1000、2=4000、3=5600 RPM |
| `0x01A3`，EBX=`state << 8` | 只写运行档位 1 / 2 |
| `0x34A3` | 禁用固件自动调速，允许设置运行档位 |
| `0x35A3` | 恢复固件调速；最终转速还取决于 BIOS 全速覆盖属性 |

EAX 原样返回命令或低 16 位为 `0xFFFF` 时视为拒绝。读取名义档位 3 成功不代表写入成功，生产代码只写档位 1 / 2，不写 0 / 3。程序进入手动控制前检查机型、BIOS、签名及档位 1 / 2 名义值。

协议来源：[Linux dell-smm-hwmon 文档](https://github.com/torvalds/linux/blob/master/Documentation/hwmon/dell-smm-hwmon.rst)。

## 覆盖 1000–5570 RPM

风扇最低运行约 1012 RPM，中档约 3980 RPM，BIOS `FanCtrlOvrd=Enabled` 全速约 5570–5580 RPM（一台测试机上的实测值）。软件将 1000 / 4000 / 5570 作为名义控制端点，目标值存在机械惯性和反馈波动。

每次接管会话仅在入口开启一次全速覆盖，退出接管时恢复 BIOS 自动。中间目标每轮等待 120 ms，固定端点每轮等待 750 ms；只使用运行时 SMM 操作，不反复写 BIOS 属性：

- 1000–4000 RPM：禁用固件自动控制，在运行档位 1 / 2 之间反馈切换。
- 4000–5570 RPM：低侧使用 `0x34A3` 后设置中档；高侧使用 `0x35A3`，让已开启全速覆盖的固件驱动最高转速。
- 控制死区为目标上下 15 RPM；窗口“保持中”的判定为当前反馈与目标相差不超过 100 RPM，该文字不代表误差始终受限于 100 RPM。

比例表示区间位置：`rpm = round(1000 + 4570 × percent / 100)`，不是 PWM 占空比。0% 保持最低运行转速，50%=3285 RPM，100%=5570 RPM。

BIOS 配置读取偶尔耗时约 2 秒，并会争用固件接口。因此手动调速时使用入口已确认的配置，暂停周期性 BIOS 属性查询，继续实时采集 RPM、温度和其他传感器。切回自动后恢复属性轮询。不要同时运行其他调速软件。

## 退出与异常恢复

入口首先启动独立监护进程并等待就绪，然后修改控制状态。监护进程检查父进程 PID、启动时间和心跳，父进程退出或心跳超过 8 秒未更新时，执行 `0x35A3` 并恢复 BIOS 自动。正常恢复成功后才解除监护；恢复失败时尝试开启全速覆盖。

各模式在 CPU ≥ 90 °C 时全速、≤ 85 °C 后回到模式规则（见 [MODES.md](MODES.md)）。温度反馈超过 8 秒未更新时交回 BIOS 自动。控制循环间隔超过 3 秒、时钟倒退或调用出错时尝试恢复自动；1 秒以前的 RPM 控制反馈不会覆盖新采集的固件读数。整机无法执行代码（死锁、断电）时无法提供软件恢复保证。

恢复动作不修改最低转速配置。控速程序再次启动后，仍按保存的模式执行（`auto` 即 BIOS）。控速程序启动时会先处理上一会话尚未完成的恢复；每条恢复记录有独立跨进程互斥，防止旧监护在新控速程序接管后才恢复 BIOS。固件版本变化后，不再发送仅针对旧版 BIOS 验证的原始命令。

`tests/run.ps1` 检查换算、持久化、温控延迟 / 斜率和保护判定；`tests/run-recovery.ps1` 检查监护、会话恢复与入口时序。会写固件的真机检查只在用户在场时手动进行。
