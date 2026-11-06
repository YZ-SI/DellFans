# 发布包

发布物是一个绿色包 `DellFans-<版本>-win-x64.zip`（附 `.sha256`），由 `Package.ps1` 从 `bin\` 生成：

```powershell
powershell -File build.ps1      # 构建 bin\
powershell -File Package.ps1    # dist\DellFans-<版本>-win-x64.zip 与 .sha256
```

压缩包里只有一个 `DellFans-<版本>\` 文件夹：

| 文件 | 说明 |
|---|---|
| `DellFans.exe` | 主界面（不提权）；双击即用 |
| `DellFans.Service.exe` | 后台控速程序，以 `--portable` 参数、管理员身份运行；不是 Windows 服务 |
| `DellFans.Cli.exe` | 命令行工具，也被监护进程用于恢复 |
| `DellFans.Tray.exe` / `DellFans.Watchdog.exe` | Rust 托盘与监护 |
| `*.exe.config` | .NET Framework 4.8 运行时声明 |
| `IntelMSR.bin` | PawnIO 的 MSR 模块（LGPL-2.1+），哈希在构建时固定 |
| `THIRD-PARTY-NOTICES.txt`、`licenses\` | 许可证 |
| `README.txt` | 中英文使用说明 |

不含任何 `.ps1`、`.cmd` 或测试程序（`tests\run-rust-packaging.ps1` 会检查）。PawnIO 驱动本身不随附，只在用户同意后从官方地址下载安装。

## 运行时行为

- 双击 `DellFans.exe`：没有控速程序在运行时，经 UAC 启动同目录的 `DellFans.Service.exe --portable`，并启动托盘。
- 控速程序首次运行时创建并保护 `%ProgramData%\DellFans`（属主 Administrators，仅 SYSTEM 与 Administrators 可写，拒绝连接点和符号链接）。
- 界面、托盘与命令行只连接同目录下、以管理员身份运行的 `DellFans.Service.exe`；验证不通过时请求不会发出。
- “登录后启动”只写当前用户的 `HKCU\...\Run\DellFans.Tray`；托盘登录后若发现控速程序未运行，会请求一次 UAC 启动它。
- 托盘“退出 DellFans”：控速程序交回 BIOS 自动后退出，托盘随之退出。
