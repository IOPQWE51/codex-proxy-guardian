# 自动节点故障转移（Auto Node Failover）设计

日期：2026-08-14
状态：已批准（方案 A：故障转移式自动切换）

## 目标

当前节点不可用时，守护进程自动检测并切换到同一分组内最优可用节点，保持代理持续在线，减少手动切换。切换策略要「智能」：有触发门槛、择优、冷却、尝试上限、尊重手动选择，避免反复横跳。

## 现状

- 守护引擎（`GuardianDaemon.exe`，C#）每 35s 轮询 Clash API（默认 http://127.0.0.1:9090），已能读取 `/configs`（mode/mixed-port）、HTTP 探活、`/proxies` 的 GLOBAL.now 显示当前节点。
- 节点切换目前仅手动：托盘/主界面 `NodesForm` 调 `SwitchNode`（PUT /proxies/{分组}）。
- 节点挂了不会自动切，只会走现有宽限/下线逻辑（downSeconds 后清空环境变量与系统代理）。
- PowerShell 版守护（`codex-proxy-guardian.ps1`）与 C# 引擎共享配置/状态/日志格式与互斥锁，需同步实现。

## 行为设计

### 触发

- 前提：Clash API 可达、`mixed-port` 有效；且 HTTP 探活失败。
- 连续失败 `failoverTriggerCount`（默认 2）轮才触发自动切换；探活一恢复立即清零计数。
- 不触发：API 不可达（内核未启动，属环境问题非节点问题）；本会话从未在线过（沿用现有「立即下线，不残留死代理」逻辑）。
- 延迟高低不作为触发条件（仅作候选排序参考），避免抖动误切。

### 候选

1. **定位用户分组**：从 GLOBAL 沿 `now` 下钻；若 `GLOBAL.now` 是 Selector，取该 Selector 的 `now` 继续，直到非 Selector。最后一次出现的 Selector 即「用户分组」；若 GLOBAL.now 本身就是真实节点，用户分组 = GLOBAL。
2. **候选列表**：用户分组 `all` 中排除类型 Selector/Direct/Reject/URLTest/Fallback/LoadBalance/Compatible/Pass，排除名称 DIRECT/REJECT/REJECT-DROP/PASS，排除当前失败节点，去重。
3. **排序**：按内核 `history` 最近一次 delay 升序（无记录/失败排最后），取前 `failoverMaxProbes`（默认 5）个实测。

### 择优

- 对候选逐个 `GET /proxies/{name}/delay?timeout={failoverProbeTimeoutMs}&url={proxyTestUrls[0]}`。
- 有效延迟：0 < delay ≤ `failoverMaxDelayMs`（默认 8000ms）。
- 选延迟最低者切换；全无效则本轮不切（受尝试上限约束）。

### 切换

- `PUT /proxies/{用户分组}`，body `{"name": node}`（带 `clashApiSecret`）。
- 成功后日志：`自动故障转移：A -> B（组 G，延迟 Nms）`；不修改环境变量与系统代理，保持在线，下轮探活验证。
- state.json 记录：`lastFailoverAt`、`lastFailoverFrom`、`lastFailoverTo`、`failoverCount`；另加 `nodeDelay`（当前节点最近延迟，供展示）。

### 防抖

- 同一故障周期内最多自动切 `failoverMaxAttempts`（默认 3）次；已试过的节点本周期不重复（tried 集合），周期结束（探活恢复或尝试耗尽）即清空。
- 成功切换后 `failoverCooldownSeconds`（默认 300s）内不再自动切换。
- 健康恢复时若节点与上次记录不同（人为切换），重置失败计数与冷却，尊重手动选择。
- 故障周期内保持在线：从首次触发到尝试耗尽，视为宽限期延长（每轮探活失败但仍可再试时保持 Up，不清环境变量/系统代理，避免 90s 宽限先到期造成提前断线）。
- 尝试耗尽仍不可用 → 当轮判定下线并清配置（不再额外等 downSeconds，行为确定）；成功切换则恢复正常计数。

## 配置项（可热重载，均带钳制）

| 配置项 | 默认 | 范围 | 说明 |
| --- | --- | --- | --- |
| autoFailover | true | 布尔 | 总开关 |
| failoverTriggerCount | 2 | 1..10 | 连续探活失败几轮后触发 |
| failoverMaxAttempts | 3 | 1..5 | 一个故障周期最多自动切换次数 |
| failoverMaxProbes | 5 | 1..20 | 每轮实测候选数上限 |
| failoverProbeTimeoutMs | 3000 | 1000..15000 | 单节点延迟探测超时 |
| failoverMaxDelayMs | 8000 | 500..60000 | 候选可接受最大延迟 |
| failoverCooldownSeconds | 300 | 30..3600 | 成功切换后冷却 |

## 实现范围

- `src/GuardianDaemon.cs`（实际运行引擎）：新增配置字段/钳制/热重载；新增 `ResolveUserGroup`、`CollectCandidates`、`ProbeDelay`、`TryAutoFailover`；主循环在检测到探活失败时插入 `TryAutoFailover`；state.json 新字段；版本号 2.4.1 → 2.5.0。
- `scripts/codex-proxy-guardian.ps1`：同一算法镜像（函数名与逻辑一致），供 -Test/-DryRun 与自测。
- `scripts/self-test.ps1`：新增 mock 单测：配置钳制、分组下钻、候选过滤、触发计数、冷却、择优、尝试上限。
- `config/daemon.config.json`：补默认配置项。
- `docs/README.md`、`docs/USAGE.md`：说明自动故障转移行为与新配置项。
- 版本号：C# 引擎 v2.5.0；PS1 `DaemonVersion` 同步。

## 技术约束

- .NET Framework 4.8 / csc 命令行，C# 5 兼容，无第三方依赖。
- Clash API 面向 FlClash / Clash Meta 系；不支持 delay 接口的内核静默降级为「候选探测全失败 → 不自动切换」，维持原逻辑。
- 文件编码 UTF8 BOM + CRLF。

## 成功标准

1. 默认开启后，节点故障约 2 轮（≈70s）内自动切到最优可用节点；期间环境变量/系统代理不断线。
2. 全部候选不可用时当轮下线并清配置（与现有清配置行为一致），不留死代理。
3. 手动切换优先于自动；冷却与尝试上限生效，日志/状态可见故障转移记录，无反复横跳。
4. self-test 新增用例全绿，原 53/53 与 add-direct 14/14 不回归。
5. 守护重启后运行正常；构建产物与源码推 GitHub。

## 测试计划

- PS1 单测（mock /proxies 与 delay 结果）：分组下钻、候选过滤与排序、触发与冷却计数、择优选择、尝试上限、配置钳制。
- 构建验证：build-daemon.ps1 / build-tray.ps1 编译通过。
- 真机演练（可选，需用户配合）：临时设 failoverTriggerCount=1，把节点切到坏节点，观察自动切换日志与节点恢复。

## 不做（YAGNI）

- 不做延迟阈值触发切换、不做每轮自动测速选优（方案 C 内容）。
- 不改 GUI 界面（节点页仍可手动切换；state.json 新字段留待后续展示）。
- 不改直连白名单、日志上限、系统代理逻辑。
- 不做多内核适配（仅 FlClash / Clash Meta 系，不支持 delay 时静默降级）。