# Usage

## Commands

```powershell
# Dry run: detect only
.\scripts\codex-proxy-guardian.ps1 -Test -DryRun

# Apply once
.\scripts\codex-proxy-guardian.ps1 -Test

# Daemon mode
# Handled by scheduled task: CodexProxyDaemon
```

## Behavior

- Online: sets user env vars `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`
- Offline: clears env vars and disables stale local system proxy
- Does not restart Codex automatically
- Logs: `logs/daemon.log`
- State: `state.json`

## Uninstall

```powershell
.\scripts\uninstall-daemon.ps1 -ClearEnv -DisableSystemProxy
```
## Tray console

`dist\GuardianTray.exe` provides a system-tray UI: status details (task, proxy up/down, node,
env vars, system proxy), read-only detect, start/stop daemon, pause/resume logon autostart,
tray autostart, log/config folders, install/uninstall. It refreshes every 15 s and shows a
balloon when the proxy state flips.
## 添加新的国内 API 直连（入口）

```powershell
# 一步添加：自动归一化域名并写入 config\daemon.config.json，守护 <=35s 热重载生效
.\scripts\add-direct.ps1 https://api.longcat.chat

# 已带通配符时原样使用
.\scripts\add-direct.ps1 *.xxx.com

# 同时同步 PS / C# 默认清单与 README 家数（换环境/重装也带上）
.\scripts\add-direct.ps1 https://api.xxx.com -SyncDefaults

# 只预览归一化结果，不写入
.\scripts\add-direct.ps1 https://api.xxx.com -DryRun
```

> 只有国内站才建议加直连；国外 API 走代理通常更稳。
> 域名范围想更精确时，直接传 `*.api.xxx.com` 形式。