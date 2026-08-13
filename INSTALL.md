# Install

## Prerequisites

- Windows 10/11
- PowerShell 5.1 or later
- FlClash with Clash API enabled at `http://127.0.0.1:9090`
- Codex desktop app installed

## Steps

1. Clone this repository:

```powershell
git clone https://github.com/IOPQWE51/codex-proxy-guardian.git
cd codex-proxy-guardian
```

2. Register the scheduled task:

```powershell
.\scripts\install-daemon.ps1
```

3. Start the daemon now:

```powershell
Start-ScheduledTask -TaskName 'CodexProxyDaemon'
```

4. Verify:

```powershell
[Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo
```

## Notes

- The scheduled task runs at logon and starts hidden.
- Restart Codex after first install if you want it to use the proxy immediately.
