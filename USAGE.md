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
