# 能力检测

DellFans 不按机型区分。在每台 Dell 电脑上，控速程序都先做只读检查；检查通过后，用户同意并通过一次全速响应测试，才会控制风扇。
通过之前控速程序拒绝所有非 `auto` 模式，风扇保持 BIOS 自动（`Background.State = "CapabilityRequired"`）。
总体约定见 [ARCHITECTURE.md](ARCHITECTURE.md) 第 7 节。

## 步骤

1. **只读检查**（不写任何固件，控速程序启动后自动做一次）
   - Dell WMI 类与 SMBIOS 令牌（含风扇令牌 `0xF600`、BFn 接口、BIOS 属性 `FanCtrlOvrd`）
   - SMM 转速读取
   - BIOS 风扇模式读取（`Enabled` / `Disabled`）
   - CPU 温度来源（PawnIO IntelMSR 或 ESIF）
   - 硬盘温度（仅提示，缺失不算失败）
2. **全速响应测试**（只在只读检查全部通过后，且需要用户同意）
   - 首次打开 DellFans 窗口时弹出“检测风扇控制接口？”。同意后风扇全速约 15 秒，转速必须上升 **≥ 300 RPM**；
     随后恢复 BIOS 自动并读回确认为 `Disabled`。命令行 `DellFans.Cli.exe --capability-probe`（回答 `y`）做同样的测试。
   - 拒绝则什么都不改，风扇保持 BIOS 自动；下次打开窗口会再次询问。
3. **通过**：写入 `%ProgramData%\DellFans\capability.json`

```json
{"Model":"...","BiosVersion":"...","PassedAtUtc":"2026-01-01T08:30:00.0000000Z","Checks":[{"Name":"...","Result":"Pass","Error":null,"Detail":"..."}]}
```

   记录绑定 **Model + BiosVersion**；更新 BIOS 后记录失效，需要重新检测。只有全速测试通过的记录才算通过。控速程序重启后立即按记录判断，不再重复读取。
4. **失败**（只读检查或测试失败，或不是 Dell / 缺少该接口）：不写 `capability.json`，风扇保持 BIOS 自动，并生成匿名报告：
   - 窗口弹出“当前电脑不支持”对话框，同一型号 + BIOS 版本只弹一次（用户刚做完测试的那一次一定会弹），可直接打开报告所在文件夹，并请你到 <https://github.com/YZ-SI/DellFans/issues> 提交；
   - 报告写入 `%LocalAppData%\DellFans\reports\capability-report-*.json`；控速程序另在 `%ProgramData%\DellFans\reports` 保留一份；
   - 状态行持续显示“当前电脑不支持 · 风扇保持 BIOS 自动”。

## 状态

`Snapshot.Background.Capability`（只增字段）：`State`、`Model`、`BiosVersion`、`ReportPath`、`Checks[]`、`WriteCheckRequired`。

| State | 含义 |
|---|---|
| `Unknown` | 尚未检查（控速程序刚启动或型号未读到） |
| `ReadPassed` | 只读检查通过，等待用户同意全速测试；仍不接管 |
| `Passed` | 全速测试已通过，允许控速 |
| `Failed` | 某项检查或测试失败，`ReportPath` 指向报告 |
| `Unsupported` | 不是 Dell 系统，或缺少所需接口 |

## 命令行与 IPC

```
DellFans.Cli.exe --capability          显示状态与各项检查
DellFans.Cli.exe --capability-probe    先显示状态；仅当 ReadPassed 时在控制台询问 [y/N]，回答 y 才发送同意
```

IPC（`\\.\pipe\DellFans.Control.v2`，见 [CONTROL_PROTOCOL.md](CONTROL_PROTOCOL.md)）：

| Command | 字段 | 权限 | 说明 |
|---|---|---|---|
| `Capability` | — | 交互用户 | 返回快照，`Background.Capability` 为当前状态 |
| `RunCapabilityProbe` | `Consent`（必须为 `true`） | 交互用户 | 运行第 2 步；缺失或为 `false` 一律拒绝。控速程序正在控制风扇或已有检测进行中时拒绝 |

通过后控速程序重建引擎，转速控制器按该型号打开；`DellSmmFan` 只在 `capability.json` 匹配当前 Model + BIOS 时才允许写入（只读检测除外）。
独立监护进程使用同一规则恢复。

## 报告内容与隐私

报告含：型号、BIOS 版本、Windows 版本号、CPU 名称、各检查的名称/结果/错误、传感器来源。
**不含**序列号、服务标签、用户名、计算机名：错误文本中出现计算机名或用户名会被替换为 `<redacted>`，
`serial`/`service tag`/`s/n` 后面的值同样替换。报告只保存在本机，不会自动上传。

## 已知限制

- 全速测试**没有**独立监护会话（不登记 Rust 监护，不会由监护进程恢复）。它的保护有三层：
  1. 写 BIOS 全速之前先写持久标记 `%ProgramData%\DellFans\full-override.json`，只有 BIOS 读回确认已恢复自动后才删除；
  2. 控速程序每次启动时：只要标记存在，就恢复 BIOS 自动一次并写 `reports\full-override-restored.json`；没有标记时，BIOS 里的 `Enabled`
     视为用户自己的设置，不会改动；
  3. 失败方向是 BIOS 全速（吵但不会过热）。
  所以控速程序在测试中被终止时，风扇可能停在全速，直到控速程序下次启动才被恢复（托盘发现控速程序退出后会请求重新启动一次）。控速程序不运行时没有任何程序会恢复它。
- SMM 协议（`0x02A3` 读转速、`0x01A3` 设档位）在不同 Dell 电脑上是否相同未经验证，这正是此检测要回答的问题；
  全速测试只用 BIOS 属性写入，不发 SMM 写命令。

## 提交结果

检测失败，或你的电脑能用，欢迎在 <https://github.com/YZ-SI/DellFans/issues/new> 提交，附上
`capability-report-*.json`（或 `capability.json`）的内容。不要附加序列号或服务标签。
