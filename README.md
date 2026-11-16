# CodexIdleHibernate · Codex 空闲后休眠

Windows 托盘小工具：每 60 秒读取一次本机 Codex 桌面会话状态，全部空闲后倒计时 30 秒，复查后休眠电脑。监测在本地执行，不调用模型，不消耗 token。

[下载 Windows x64 程序](https://github.com/Kokkorylien/CodexIdleHibernate/releases/latest)

## 使用

1. 解压下载的 `CodexIdleHibernate-v1.0.0-win-x64.zip` 到可写目录。
2. 保持 Codex 桌面打开，并至少创建过一个本机会话。
3. 双击 `CodexIdleHibernate.exe`，点击 **开始监测**。

需要 Windows 10/11 x64、.NET Framework 4.8，以及已启用的 Windows 休眠功能。程序不修改系统电源配置。Windows 11 通常已包含所需运行时。

- 全部空闲后倒计时 30 秒；再次读取状态，仍空闲才执行 `shutdown.exe /h`。每次开始监测只执行一次休眠，唤醒后不会自动再次休眠。
- 点 **X** 只隐藏窗口，后台继续监测，右下角月亮托盘图标保留。Windows 可能将它放在「^」内。
- 双击托盘图标显示窗口；右键可显示窗口、开始/取消监测、退出。**退出**会取消尚未执行的自动休眠。
- **取消监测**只停止监测；再次打开程序可找回已有窗口，不重复启动实例。
- 勾选 **仅测试（不休眠）** 可演练监测流程。
- 连接失败、超时、未知状态或数据来源不完整时继续等待，不当作空闲。
- `idle` 表示空闲，`notLoaded` 表示未加载，`active` 包含思考和工具执行。通过分页检查全部未归档会话，不局限于最近 50 个。
- 不设置开机启动或系统定时任务。

## 会话上下文与日志

默认依次使用程序目录中的可选 `context.json`、当前进程的 `CODEX_THREAD_ID` / `CODEX_SESSION_ID`、当前用户的 Codex `session_index.jsonl` 来取得本地调用所需的会话标识。支持 `CODEX_HOME`；默认目录是 `%USERPROFILE%\.codex`。

此标识只用作调用上下文，不排除该会话，也不依据索引判断忙闲；状态始终来自 Codex 桌面的实时连接。正常情况下无需填写配置。自动发现失败时，将 `context.example.json` 复制为 `context.json`，填写自己的一个本地 Codex 会话 ID。

日志保存在程序旁的 `logs/日期.jsonl`，包含时间、会话标题/标识/状态与操作结果，不保存消息正文。`--check` 的结果存入 `last-check.json`；启动错误存入 `last-error.txt`。这些是本地个人数据，已从 Git 和发行包中排除。

## 命令行

```text
CodexIdleHibernate.exe                   打开窗口，等待手动开始
CodexIdleHibernate.exe --arm             打开后立即监测
CodexIdleHibernate.exe --dry-run --arm   只演练，不休眠
CodexIdleHibernate.exe --stop            取消已有实例的监测
CodexIdleHibernate.exe --check           只读查询并写入 last-check.json
```

## 兼容范围

仅支持 Windows 本机 Codex 会话，不将 ChatGPT 对话当作 Codex 会话。发现远程 Codex 会话或不可用来源时不进入休眠判定。需要保持 Codex 桌面打开。

工具使用 Codex 桌面的内部本地管道接口；客户端更新可能要求适配。接口版本不符或查询失败时显示错误并继续等待。首次验证的客户端版本为 `26.1029.7803.0`；这不是 OpenAI 官方工具。

## 从源码构建

无需安装额外 SDK。Windows PowerShell 5.1 或 PowerShell 7 中执行：

```powershell
.\build.ps1 -OutputDirectory .\build\local
```

使用 Windows 自带 .NET Framework 编译器构建 WinForms 程序，并运行针对监测决策、分页、通信和取消的检查，测试不执行电源操作。

打包：

```powershell
.\package.ps1
```

发行 ZIP 及 SHA-256 校验文件输出到 `dist/`。程序版本在 `AssemblyInfo.cs` 中声明。`LifecycleChecks.cs` 用于独立检查窗口隐藏、托盘恢复与退出；需在正常程序关闭后单独编译运行，测试强制使用不休眠模式。
