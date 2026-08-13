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