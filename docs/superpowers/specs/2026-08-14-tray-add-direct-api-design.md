# 托盘图形化「添加直连 API」设计

日期：2026-08-14
状态：已批准（方案 A）

## 目标

在托盘图标右键菜单中提供图形化入口，让用户填写一个或多个 API Base URL，
一键加入 `config\daemon.config.json` 的 `directDomains` 直连白名单，守护 35 秒内热重载生效。
多个 Base URL 以空格（或换行）分隔。

## 现状

- `GuardianTray.cs`（682 行）：托盘菜单 + 状态窗体，操作统一通过 `tray-helper.ps1` 分派。
- `tray-helper.ps1`：`switch ($Action)` 动作分派，UTF-8 输出。
- `add-direct.ps1`：命令行入口，接受单个 Base URL，支持 `-SyncDefaults` / `-DryRun`。
- 守护 `GuardianDaemon.exe` 每 35 秒热重载 `config\daemon.config.json`。

## 方案（A：托盘表单 + 复用 add-direct.ps1）

写配置逻辑只保留一份（add-direct.ps1），托盘负责界面与调用，避免逻辑漂移。

### 1. add-direct.ps1 支持多 URL

- `[string[]]$BaseUrls`（Position 0，至少 1 个）。
- 逐条归一化（`api.xxx.com` -> `*.xxx.com`；带 `*.`、IP、localhost 原样）。
- 现有 `-SyncDefaults`、`-DryRun` 语义不变。
- 输出逐条结果（新增 / 已存在 / 规则）。退出码 0 表示至少一条成功写入。

### 2. tray-helper.ps1 新增动作 AddDirect

- 参数：`-Value`（空格分隔的原始输入）。
- 解析为 URL 数组，调用 add-direct.ps1。
- 输出 JSON：`{ ok: true, added: [...], skipped: [...], rules: [...], message: "..." }`。
- 失败时 `ERR=...`（与现有动作一致）。

### 3. GuardianTray.cs 新增表单 AddDirectForm

- 菜单项「添加直连 API」，放在「编辑配置」上方。
- 表单内容：
  - 标题「添加直连 API」
  - 多行输入框（空格或换行分隔）
  - 实时预览行：显示将添加的规则列表（`*.longcat.chat` 等）
  - 复选框「同步默认清单（换环境/重装也生效）」默认不勾
  - 「添加」「取消」按钮
- 提交：调 helper AddDirect；成功气泡提示（新增 N 条、已存在 M 条）；失败弹窗。
- 输入为空或没有可解析 URL 时禁用「添加」按钮。

### 4. 边界与提示

- 空格/换行/逗号均视为分隔符（兼容粘贴）。
- 仅显示归一化后的规则，不修改用户原始输入；预览仅作预估，实际以 add-direct.ps1 输出为准。
- 单个条目无法解析（如空段、非法 host）时报告该条并继续处理其余条目；全部失败才判定整体失败。
- 国外 API 不加提示由用户自行判断（表单内一行灰色提示：仅建议国内站直连）。

## 成功标准

1. 托盘右键可打开表单，输入多个 URL（空格分隔）提交后，config 的 directDomains 增加对应规则，守护热重载。
2. 重复提交幂等，不产生重复条目。
3. 命令行 `add-direct.ps1 a b -SyncDefaults` 与托盘行为一致。
4. 自测 53/53 保持全绿，托盘/守护进程正常重启。
5. 文档同步（README/USAGE）。

## 不做（YAGNI）

- 不做直连规则管理界面（删除/编辑已有条目）。
- 不做"国外模型代理"表单。
- 不做拖动排序等富交互。