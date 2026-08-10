# Codex 代理守护脚本 设计文档

日期：2026-08-10

## 背景与目标

当前 Codex（Windows 商店版桌面应用，`OpenAI.Codex`）没有稳定走本地代理：

- FlClash 内核监听 `127.0.0.1:7890`（mixed 代理端口）与 `127.0.0.1:9090`（Clash API）。
- Windows 系统代理（WinINET）虽已指向 `127.0.0.1:7890`，但桌面版 Codex 不会稳定使用系统代理（openai/codex #10555、#15447、#20844）；可靠方式是让 Codex 启动时继承 `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` 环境变量。
- 当前用户环境变量中代理变量为空，因此 Codex 直连 `api.deepseek.com`，代理是否生效"碰运气"。
- 后续会切换其他国外模型，届时所有对外流量都需要稳定走本地代理。

目标：做一个开机自启、静默运行的守护脚本，持续检测 FlClash 代理状态，确保 Codex 始终连接当前代理；代理变化时自动校准，Codex 重启后立即生效（不自动重启 Codex）。

## 方案选型

采用**方案一：PowerShell 守护脚本 + Windows 计划任务**。

对比过的其他方案：

- 编译为 exe 守护程序：更原生但维护成本高，当前需求没有必要。
- 仅做启动器包装 Codex 图标：开始菜单直接启动时仍绕过，治标不治本。

## 目录结构

项目根目录：`G:\AGENT\proxy\codex-proxy-daemon\`（独立本地 git 仓库，不推送到远端）。

```text
codex-proxy-daemon/
├── codex-proxy-daemon.ps1      # 主守护脚本
├── install-daemon.ps1          # 注册计划任务（登录时静默启动）
├── uninstall-daemon.ps1        # 移除计划任务（可选恢复环境变量）
├── daemon.config.json          # 配置：轮询间隔、API 地址、探活 URL、NO_PROXY 等
├── state.json                  # 最近一次检测/应用状态（脚本维护）
├── logs/
│   └── daemon.log              # 运行日志（按大小轮转）
└── docs/superpowers/specs/     # 设计文档
```

## 组件与职责

1. **检测器（Detector）**：按固定间隔查询 Clash API，输出"代理是否在线、mixed 端口、当前节点、模式"。
2. **应用器（Applier）**：根据检测结果计算期望状态（用户环境变量 + WinINET 系统代理），与实际状态比对，只应用差异，广播 `WM_SETTINGCHANGE`。
3. **状态记录器（State/Log）**：写入 `state.json` 与 `daemon.log`，只记录状态转换与错误。
4. **安装器（Installer）**：注册/移除计划任务，负责开机自启。

## 检测逻辑

- 轮询间隔：**35 秒**（`daemon.config.json` 中可调）。
- 请求 `http://127.0.0.1:9090/configs`：读取 `mixed-port`（当前 7890，端口变化自动跟随）与 `mode`。
- 请求 `http://127.0.0.1:9090/proxies`：读取 `GLOBAL` 选择器当前节点（如 `美国洛杉矶2号`），仅用于日志。
- 探活：经 `http://127.0.0.1:<mixed-port>` 请求 `https://www.gstatic.com/generate_204`，返回 204 视为健康。
- 判定下线：**连续 3 次**（约 105 秒）API 不可达或探活失败，才判定 FlClash 下线，避免瞬时抖动。
- 判定上线：API 可达且探活成功一次即恢复。

## 应用与回滚

FlClash 在线且健康：

- 设置用户级环境变量（持久化到 `HKCU\Environment`）：
  - `HTTP_PROXY` = `http://127.0.0.1:<mixed-port>`
  - `HTTPS_PROXY` = `http://127.0.0.1:<mixed-port>`
  - `ALL_PROXY` = `http://127.0.0.1:<mixed-port>`
  - `NO_PROXY` = `localhost,127.*,10.*,192.168.*,*.local`（可配置）
- 确保 WinINET 系统代理：`ProxyEnable=1`，`ProxyServer=127.0.0.1:<mixed-port>`；保留已有 `ProxyOverride` 绕过列表，若为空则写入与 `NO_PROXY` 对应的默认值。
- 广播 `WM_SETTINGCHANGE`（环境变量与 Internet 设置），使新启动的程序立即继承。

FlClash 下线：

- 将上述用户环境变量清空，使 Codex 下次启动可直连 DeepSeek。
- 若系统代理仍指向已失效的本地端口（`ProxyEnable=1` 且 `ProxyServer=127.0.0.1:*`），则置 `ProxyEnable=0` 关闭残留代理，避免程序卡在死代理；不动其他配置。
- 广播 `WM_SETTINGCHANGE`。

节点切换（如洛杉矶换东京）不需要改任何配置，只记日志，因为 Codex 始终走本地 mixed 端口。

## 不自动重启 Codex

按用户选择：**不自动重启 Codex**。若 Codex 进程正在运行且代理配置发生了变化，日志与 `state.json` 记录提示"代理配置已更新，重启 Codex 后生效"，由用户择机重启。

## 开机自启

- 计划任务名称：`CodexProxyDaemon`。
- 触发条件：用户登录时。
- 动作：`powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File codex-proxy-daemon.ps1`。
- 无窗口、无弹窗；无需管理员权限（仅写 HKCU）。

## 日志与状态

- `daemon.log`：记录启动、配置应用/回滚、端口变化、节点变化、下线/上线、错误；按大小轮转（默认 2 MB，保留 3 份）。
- `state.json`：最近一次检测结果（proxyUp、port、node、mode、lastApplied、lastChange、nextScheduledCheck）。

## 错误处理

- 每次轮询整体 try/catch，错误写入日志后继续下一轮，不中断守护进程。
- HTTP 请求全部带超时（默认 8 秒）。
- 环境变量/注册表写入失败时记录错误并保持上次状态，下一轮重试。
- 脚本启动时先读取配置；配置缺失或损坏时使用内置默认值并写日志。

## 验收标准

1. 安装后计划任务存在，登录后自动静默运行。
2. FlClash 在线时：`[Environment]::GetEnvironmentVariable('HTTPS_PROXY','User')` 返回 `http://127.0.0.1:7890`；系统代理开启且指向 7890。
3. 关闭 FlClash：约 105 秒后变量被清空、残留系统代理被关闭；日志记录"代理下线"。
4. 重新打开 FlClash：变量与系统代理自动恢复；日志记录"代理上线"。
5. 修改 FlClash mixed 端口（如 7891）：守护脚本自动跟随新端口。
6. 更换国外模型提供商（仅改 `config.toml` 的 provider）不需要改守护脚本。

## 后续扩展（YAGNI，暂不实现）

- 自动切换 Clash 节点/自动测速。
- Windows 通知（Toast）提醒"重启 Codex 生效"。
