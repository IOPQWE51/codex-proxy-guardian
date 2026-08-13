# Codex 代理守护脚本

检测 FlClash（`127.0.0.1:9090` Clash API）并维护 Codex 的代理配置：用户级环境变量
（`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`）与 WinINET 系统代理。
FlClash 下线时自动清空代理配置，使 DeepSeek 等可直连；不自动重启 Codex，
代理配置变化时重启 Codex 后生效。

## 文件

- `codex-proxy-daemon.ps1`：主脚本。`-Test` 单次检测并应用；`-Test -DryRun` 只检测不应用。
- `install-daemon.ps1`：注册计划任务 `CodexProxyDaemon`（登录时静默启动）。
- `uninstall-daemon.ps1`：移除任务；`-ClearEnv` 清空用户代理变量；`-DisableSystemProxy` 关闭系统代理。
- `daemon.config.json`：轮询间隔（默认 35 秒）、Clash API 地址、探活 URL、NO_PROXY 等。
- `state.json` / `logs\daemon.log`：当前状态与日志。

## 使用

```powershell
.\install-daemon.ps1                        # 安装（注册计划任务）
Start-ScheduledTask -TaskName 'CodexProxyDaemon'   # 立即启动
.\codex-proxy-daemon.ps1 -Test -DryRun      # 只检测，预览状态
.\codex-proxy-daemon.ps1 -Test              # 单次检测并应用
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo
.\uninstall-daemon.ps1 -ClearEnv            # 卸载并清空用户代理变量
```

## 说明

- 默认每 35 秒检测一次；连续 3 次失败（约 105 秒）判定 FlClash 下线。
- Codex 需重启后才会继承最新的用户环境变量；守护脚本不会自动重启 Codex。
- 后续切换国外模型只需改 Codex 的 `config.toml` provider，无需改本脚本。
- 若更换代理软件（非 FlClash），把 `daemon.config.json` 的 `clashApiUrl` 改为对应 Clash 内核管理地址即可。