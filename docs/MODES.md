# 风扇模式

DellFans 提供五个模式。模式由后台控速程序执行，关闭窗口后继续按保存的模式运行；再次启动控速程序时按保存的模式恢复。

| 模式 | `Mode` 值 | 最低 RPM | 最高 RPM | 起调温度 | 说明 |
|---|---|---|---|---|---|
| 静音 | `quiet` | 1500 | 3000 | 55 °C | 转速上限 3000，不会更吵 |
| 平衡 | `balanced` | 2500 | 全速（5570） | 65 °C | |
| 高性能 | `performance` | 4000 | 全速 | 65 °C | |
| 自定义 | `custom` | 用户设定，1000–5570 | 全速 | 65 °C | 命令行 `--rpm N`、界面“最低 RPM” |
| BIOS 自动 | `auto` | — | — | — | 不接管，风扇完全由 BIOS 控制 |

## 调速规则

控速程序接管风扇时每 **1 秒**执行一次，参考温度是传感器层的 1 秒均值（CPU 封装与核心最高温中较高者）：

- 高于起调温度：目标每秒 **+50 RPM**，直到模式最高。
- 低于（起调温度 − 3 °C）并连续稳定 **20 秒**：目标每秒 **−100 RPM**，直到模式最低。
- 两者之间（起调温度 − 3 °C 至起调温度，含两端）：保持不变；回到这个区间会让“稳定 20 秒”重新计时。
- 接管时目标从风扇当前实测转速开始（夹到该模式的 [最低, 最高]；没有 3 秒内的有效转速读数时从模式最低开始；温度 ≥ 90 °C 则直接全速；控制温度 ≥ 起调温度 + 15 °C 或 ≥ 80 °C 时从该模式最高开始）；切换模式时当前目标被夹到新模式的 [最低, 最高]。
- 两次调速间隔异常长（控速程序繁忙）时一次最多按 2 秒推进，不会突然跳变。

## 保护（所有模式）

- CPU 温度 **≥ 90 °C** 立即全速，不受模式最高限制；全速至少保持 **10 秒**，且两个读数都 **≤ 85 °C** 后回到模式规则。释放时目标保持当前值（不会被压到模式最高，避免静音模式在 90/85 °C 间来回），之后只按上述降速规则（含 20 秒稳定）下降；已在运行的手动控制直接把目标改为全速，不先交回 BIOS。无效/越界的保护温度读数按“无”处理，用控制温度代替。保护使用传感器层的原始最高值（1 秒窗口内最大），控制使用 1 秒均值；两者取较高者。
- 同一组阈值（`FanSafetyPolicy.EmergencyC` / `ReleaseC`）同时用于控速程序的控制循环、硬件侧控制器（`ManualRpmController`）和 `FanSafetyPolicy.RequiresFullSpeed`。
- 保留：温度读数失效 8 秒交回 BIOS（仅温度原因的交回不锁定：有效读数连续 10 秒后自动恢复接管）；反馈异常、控制循环中断、资源持续超限、传感器缺失时交回 BIOS 自动，并保留已保存的模式；独立监护进程。
- `auto` 模式下控速程序不向风扇写入任何内容。

## 保存

`%ProgramData%\DellFans\settings.json`，`SchemaVersion` 2：

```json
{"SchemaVersion":2,"Mode":"custom","Enabled":true,"MinimumRpm":2500,"LastFault":null}
```

- `Mode` 是权威值；`Enabled` 只是为方便读取保留的派生字段（`Mode` 不是 `auto`）。
- `MinimumRpm` 是自定义模式的最低值，切到其他模式后仍保留，再切回自定义时沿用。
- 首次运行：`balanced`（能力检测通过后才开始接管）。
- 能力检测通过之前，控速程序拒绝除 `auto` 以外的所有模式（见 [CAPABILITY.md](CAPABILITY.md)）。
- `SchemaVersion` 不是当前版本（2）的文件视为无效：控速程序以 `auto` 启动并给出警告，不读取其中的任何值。
- 模式设置立即生效并写盘；控速程序再次启动后按保存的模式运行。写盘失败时撤销接管，不留下“运行中但未保存”的状态。

## 命令行

```
DellFans.Cli.exe --mode quiet|balanced|performance|custom|auto
DellFans.Cli.exe --mode custom --rpm 3000
DellFans.Cli.exe --rpm 3000          # 等同 --mode custom --rpm 3000
```

`custom` 必须带 `--rpm`；其他模式不能带 `--rpm`。没有其他调速参数。

## 界面

主界面的模式选择有五项；选择“自定义”时才显示“最低 RPM”输入。状态行显示已保存的模式、RPM 范围、起调温度和当前目标。

## 协议

`SetMode {"Mode": "...", "MinimumRpm": n}`（仅 `custom` 带 `MinimumRpm`）。`Snapshot.Background` 含 `Mode`（字符串）与 `ModeLimits {Min, Max, StartC}`（`auto` 时为 `null`）。详见 [CONTROL_PROTOCOL.md](CONTROL_PROTOCOL.md)。`Background.State` 运行中为 `FloorCurve`，保护为 `EmergencyFullSpeed`。
