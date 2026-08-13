# Codex 代理守护（codex-proxy-guardian）

检测 FlClash 并维护 Codex 的本地代理配置：FlClash 在线时写入用户级代理环境变量
（`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`）与 WinINET 系统代理；
下线时自动清空，让 DeepSeek、通义、Moonshot 等境内官方 API 直连不绕代理。
带托盘图形控制台，可查看状态、启停守护、管理开机自启。

## 特性

- **FlClash 联动**：轮询 Clash 内核 API（默认 `127.0.0.1:9090`），取 `mixed-port` 作为代理端口；
  上线即时恢复，下线有 90 秒时间窗滞后，频繁开关不会误清配置。
- **境内直连白名单**：`directDomains` 自动并入 `NO_PROXY` 与系统代理绕过列表，
  DeepSeek 等官方 API 不经过代理。
- **日志防写爆**：单文件上限 + 自动轮转（默认 2 MB × 3 份），日志写入连续失败时自动静默降级。
- **单轮容错**：单轮检测异常只记录继续运行，不退出守护。
- **静默常驻**：计划任务登录自启、隐藏窗口；崩溃时弹窗提示并写日志。
- **托盘控制台**：`GuardianTray.exe`（约 15 KB，.NET Framework 4.8 免安装运行时）。
- **配置热重载**：修改 `config\daemon.config.json` 后无需重启守护，下次检测自动生效并记录日志。
- **崩溃自动重启**：计划任务启用 RestartCount（最多 3 次 / 1 分钟内），守护意外退出后自动恢复。
- **多 URL 探活**：支持多个探活 URL，任一成功即判定在线，降低单点故障导致的误判。
- **多实例互斥**：守护与托盘均有互斥锁，不会重复运行。

## 目录结构

```text
codex-proxy-guardian/
├── scripts/
│   ├── codex-proxy-guardian.ps1   # 守护核心（-Test 单次检测，无参常驻）
│   ├── tray-helper.ps1            # 托盘控制台的 PowerShell 后端
│   ├── install-daemon.ps1         # 注册计划任务 CodexProxyDaemon（登录自启）
│   ├── uninstall-daemon.ps1       # 移除任务（-ClearEnv 清变量 / -DisableSystemProxy 关系统代理）
│   └── build-tray.ps1             # 用系统 csc.exe 编译托盘程序
├── src/GuardianTray.cs            # 托盘控制台源码（WinForms，C#）
├── dist/GuardianTray.exe          # 已编译托盘程序
├── config/daemon.config.json      # 守护配置
├── docs/                          # 设计与实现计划
├── state.json                     # 实时状态（git 忽略）
└── logs/daemon.log                # 运行日志（git 忽略，自动轮转）
```

## 快速开始

```powershell
git clone https://github.com/IOPQWE51/codex-proxy-guardian.git
cd codex-proxy-guardian

.\scripts\install-daemon.ps1              # 注册计划任务（登录自启、静默）
Start-ScheduledTask -TaskName 'CodexProxyDaemon'   # 立即启动
.\dist\GuardianTray.exe                   # 可选：托盘控制台
```

验证：

```powershell
[Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
Get-Content state.json                    # proxyUp / node / message
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo
```

## 使用

### 命令行

```powershell
.\scripts\codex-proxy-guardian.ps1 -Test -DryRun   # 只检测不修改
.\scripts\codex-proxy-guardian.ps1 -Test            # 单次检测并应用
# 无参数 = 常驻模式（由计划任务调用）
```

### 托盘控制台

右键托盘图标：

- 状态详情：任务状态、代理在线/节点/端口、环境变量、系统代理
- 只读检测代理：手动跑一次检测，不改配置
- 启动守护 / 停止守护
- 暂停开机自启 / 恢复开机自启（不打断正在运行的守护）
- 托盘开机自启（写入 HKCU Run）
- **切换节点**：显示所有 Clash 选择器组及其节点列表，当前节点加粗标记，点击即可切换
- **重启 Codex 应用**：确认后关闭并重启 Codex（仅在确认后执行，对话会断开）
- 打开日志/配置目录；安装 / 卸载守护任务

托盘每 15 秒刷新一次状态，代理上下线切换会弹出气泡通知。节点列表每次展开时通过 Clash API 实时查询，支持实时切换。

## 配置（config/daemon.config.json）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `clashApiUrl` | `http://127.0.0.1:9090` | Clash 内核管理地址（换代理软件只改这里） |
| `clashApiSecret` | （空） | Clash 内核密钥，需要认证时填写（Bearer） |
| `pollIntervalSeconds` | `35` | 轮询间隔（钳制 5–600） |
| `requestTimeoutSeconds` | `8` | API / 探活超时（钳制 2–30） |
| `downSeconds` | `90` | 连续失败判定下线的秒数（钳制 15–600） |
| `proxyTestUrl` | gstatic generate_204 | 单个探活地址（兼容旧配置） |
| `proxyTestUrls` | 3 条 URL | 探活 URL 数组，任一成功即判定在线；降低单点被墙导致的误判 |
| `noProxy` / `proxyOverride` | 本机/内网默认 | 基线绕过列表，`directDomains` 会自动并入 |
| `directDomains` | 8 家境内 API | 直连白名单（逗号分隔，`*.deepseek.com` 等） |
| `nodeLogCooldownSeconds` | `60` | 节点切换日志节流 |
| `maxLogBytes` / `maxLogFiles` | 2 MB / 3 | 日志上限与轮转份数 |

## 常见问题

- **DeepSeek 直连**：确保 `directDomains` 含 `*.deepseek.com`；守护在线时会把它写入
  `NO_PROXY`，Clash 重启或换节点不影响直连。
- **换国外模型**：改 Codex provider 即可，代理端口由守护自动写入，无需改脚本。
- **换环境部署**：整目录拷贝（`dist\GuardianTray.exe` 连同 `scripts/`、`config/`），
  或设置环境变量 `CODEX_PROXY_GUARDIAN_HOME` 指向项目根目录。
- **代理软件非 FlClash**：只要暴露 Clash API，改 `clashApiUrl` 即可。

## 卸载

```powershell
.\scripts\uninstall-daemon.ps1 -ClearEnv -DisableSystemProxy   # 完整清理
```

托盘程序退出即移除；`托盘开机自启` 可在托盘菜单取消。

## 自测

```powershell
.\scripts\self-test.ps1     # 隔离沙盒验证：配置钳制、白名单合并、宽限时间窗、日志轮转与防爆
```

不修改真实配置、环境变量或注册表。

## 开发与构建

```powershell
.\scripts\build-tray.ps1 -Force    # 重新编译 GuardianTray.exe（系统自带 csc.exe，无需 SDK）
```

MIT License。