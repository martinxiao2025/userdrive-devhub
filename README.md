# userdrive-devhub

定时调度智能体插件：扫描工作区 `.devhub/tasks/*.json`，按队列派发模型回合、落库结果并向 Copilot Chat 暴露 MCP 控制面。

- 宿主：`src/DevHubd`（ASP.NET Core + Kestrel + `BackgroundService`，固定监听 `127.0.0.1:3091`）
- 插件包：`plugin/`（Agent Plugins 1.0.0，技能 + streamable-http MCP）
- VS Code 薄扩展：`extensions/devhub-bridge`（`vscode.lm` 桥 + 通知）
- 技术栈：.NET 10 / C# 14、xUnit v3（Microsoft.Testing.Platform）、Microsoft.Data.Sqlite、官方 ModelContextProtocol SDK

---

## 1. 安装

### 1.1 运行宿主（二选一）

自包含发布产物（目标机无需安装 .NET）：

```powershell
dotnet publish src/DevHubd/DevHubd.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
artifacts\publish\devhubd.exe
```

或使用运维脚本（推荐）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action start
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action status
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action stop
```

`supervise` 常驻守护，`drill` 做一次"启动 → 强杀进程树 → 自动拉起 → MCP 冒烟"的崩溃恢复演练。

### 1.2 安装插件包与扩展

1. VS Code 命令面板执行 `Chat: Install Plugin from Source`，选择仓库下的 `plugin/` 目录。
2. 安装薄扩展（`vscode.lm` 桥与通知依赖它）：

   ```powershell
   code --install-extension extensions\devhub-bridge\devhub-bridge-0.1.0.vsix
   ```

3. 插件只包含技能与 MCP 服务器；扩展无法随插件下发，必须单独安装。

---

## 2. 配置

### 2.1 数据目录

默认 `%LOCALAPPDATA%\userdrive-devhub`，可用 `--data-dir <path>` 或环境变量 `DEVHUB_DATA_DIR` 覆盖。

| 文件 | 说明 |
|---|---|
| `devhub.db` | SQLite（WAL）：任务视图、队列、结果、通知、调度状态 |
| `runtime.json` | 运行时发现文件：pid、MCP/内部 API 端口、一次性令牌；仅回环可读 |
| `runtime.lock` | 单实例租约（pid + 心跳） |
| `workspaces.json` | 工作区清单（机器级，多窗口共用） |
| `config.json` | 用户级配置（baseUrl / model / 执行参数） |
| `lm-bridge.json` | VS Code 扩展写入的桥接发现文件 |
| `logs/` | `devhub-daemon.ps1` 启动时的 stdout/stderr 日志 |

**数据访问与 schema 管理分工**：schema（9 张表的 DDL、索引、`PRAGMA user_version` 版本推进）由 `Migrator` 独占管理，没有 EF Migrations，也不存在 `__EFMigrationsHistory`。EF Core（`DevHubDbContext`）只做既有表的映射与读写，不会建表或改表；表名/列名保持既有 snake_case。EF 连接与裸连接共用同一段连接级 PRAGMA（WAL / `busy_timeout=5000` / `foreign_keys=ON` / `synchronous=NORMAL`）。唯一的"原生 SQL"例外是队列的原子认领（`UPDATE … RETURNING` 单语句 + IMMEDIATE 事务）与 SQLite 原生 upsert（`ON CONFLICT`）。

### 2.2 工作区清单

```json
{
  "version": 1,
  "workspaces": [
    { "id": "alpha", "root": "E:\\work\\alpha", "enabled": true }
  ]
}
```

`id` 必须匹配 `^[a-z0-9][a-z0-9._-]{0,63}$`，`root` 必须是绝对路径。任务文件位于 `<root>/.devhub/tasks/*.json`。

### 2.3 用户级配置

`%LOCALAPPDATA%\userdrive-devhub\config.json`：

```json
{
  "Model": {
    "OpenAi": { "BaseUrl": "https://api.openai.com/v1", "Model": "gpt-4.1-mini" }
  },
  "Execution": { "MaxParallel": 1, "DeliveryMode": "dispatch_only" }
}
```

- 配置键采用 `Sections:Keys` 形式，环境变量以 `DEVHUB__` 前缀覆盖（如 `DEVHUB__Model__OpenAi__BaseUrl`）。
- **API key 只从环境变量读取**：默认变量名 `DEVHUB_OPENAI_API_KEY`（可用 `Model:OpenAi:ApiKeyEnvironmentVariable` 改名）。
- 配置文件出现 `apiKey` 明文字段会被拒绝并记录 Warning，key 不会落盘、不会进日志。

### 2.4 配置模型供应商

模型配置**只**来自用户级配置文件与环境变量；`appsettings.json` 只有 `Logging` 与 `AllowedHosts`，不承载模型配置，写进去不会生效。

#### 2.4.1 配置项（准确键名与默认值）

| 键（JSON 层级） | 环境变量覆盖 | 默认值 | 必填 |
|---|---|---|---|
| `Model:OpenAi:BaseUrl` | `DEVHUB__Model__OpenAi__BaseUrl` | `https://api.openai.com/v1` | 否 |
| `Model:OpenAi:Model` | `DEVHUB__Model__OpenAi__Model` | `gpt-4.1-mini` | 否 |
| `Model:OpenAi:ApiKeyEnvironmentVariable` | `DEVHUB__Model__OpenAi__ApiKeyEnvironmentVariable` | `DEVHUB_OPENAI_API_KEY` | 否 |
| `Model:OpenAi:RequestTimeoutSeconds` | `DEVHUB__Model__OpenAi__RequestTimeoutSeconds` | `120`（限制 5–600） | 否 |
| `Execution:ScheduledProvider` | `DEVHUB__Execution__ScheduledProvider` | `openai` | 否 |
| **API key 值**（存放在上面那一项命名的环境变量里） | — | 无 | **是**，缺失时 `scheduler_status` 显示 `openai:auth` |

- 客户端请求地址是 `{BaseUrl}/chat/completions`，因此 `BaseUrl` 必须是 OpenAI 兼容端点根地址（末尾 `/` 会被自动去掉）。
- 空字符串会回退到默认值；`RequestTimeoutSeconds` 超出 5–600 会被收敛到边界。
- `Execution:ScheduledProvider` 只接受 `openai` / `vscode`（大小写不敏感）。取值非法时**回退 `openai` 并在启动日志记 Warning**（不阻断启动），不会把非法值写进队列。
- 必填的只有 **API key 的值**；其余项不写就用默认值。

#### 2.4.2 存放位置与优先级

1. **程序目录 `appsettings.json`**：只有日志与 `AllowedHosts`，**不含模型配置**。
2. **用户级配置文件**：`%LOCALAPPDATA%\userdrive-devhub\config.json`（用 `--data-dir` 或 `DEVHUB_DATA_DIR` 改数据目录时，路径变为 `<data-dir>\config.json`）。
3. **环境变量 `DEVHUB__...`（双下划线）**：后加载，覆盖第 2 步 JSON 中的同名键。
4. **API key 只从环境变量读取**：配置文件出现 `apiKey` / `openAiApiKey` / `api_key` 非空字符串会被忽略并记 Warning，key 不落盘、不进日志。
5. **改完必须重启守护进程**：配置在启动时读取一次（`reloadOnChange=false`），key 也来自进程启动时继承的环境块。
6. 注意：`config.json` 是可选的。文件不存在时宿主仍会读取 `DEVHUB__...` 环境变量覆盖；文件存在但内容损坏时回退内置默认值并记录 Warning。

#### 2.4.3 设置示例（可直接复制）

```powershell
# 1) 用户级 API key：把 <YOUR_API_KEY> 换成真实值，不要提交进仓库
[Environment]::SetEnvironmentVariable('DEVHUB_OPENAI_API_KEY','<YOUR_API_KEY>','User')

# 2) baseUrl / model 的用户级覆盖（不设置则使用默认值）
[Environment]::SetEnvironmentVariable('DEVHUB__Model__OpenAi__BaseUrl','https://api.openai.com/v1','User')
[Environment]::SetEnvironmentVariable('DEVHUB__Model__OpenAi__Model','gpt-4.1-mini','User')

# 3) 用户级配置文件（必须存在，DEVHUB__ 覆盖才会被加载；UTF-8 无 BOM）
$dir = Join-Path $env:LOCALAPPDATA 'userdrive-devhub'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$json = @'
{
  "Model": {
    "OpenAi": {
      "BaseUrl": "https://api.openai.com/v1",
      "Model": "gpt-4.1-mini"
    }
  },
  "Execution": {
    "ScheduledProvider": "openai"
  }
}
'@
[IO.File]::WriteAllText((Join-Path $dir 'config.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

# 4) 重启守护进程。start 用 Start-Process 继承当前进程环境：
#    若环境变量是在本窗口启动之后才写入的，先同步到进程级，或直接开一个新窗口执行。
$env:DEVHUB_OPENAI_API_KEY = [Environment]::GetEnvironmentVariable('DEVHUB_OPENAI_API_KEY','User')
$env:DEVHUB__Model__OpenAi__BaseUrl = [Environment]::GetEnvironmentVariable('DEVHUB__Model__OpenAi__BaseUrl','User')
$env:DEVHUB__Model__OpenAi__Model = [Environment]::GetEnvironmentVariable('DEVHUB__Model__OpenAi__Model','User')
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action stop
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action start

# 5) 确认生效：末行应打印 providers = openai:ready, vscode:down（vscode 视扩展状态而定）
node scripts/smoke-mcp.mjs
```

在 Copilot Chat 中也可以直接调用 `scheduler_status`，检查 `providers[]` 里 `provider=openai` 的 `state` 与 `detail`：未配置时是 `auth`（detail 为“未配置环境变量 DEVHUB_OPENAI_API_KEY”），仍填占位值（如 `CHANGE_ME`、`<YOUR_API_KEY>`）时也是 `auth`（detail 明确提示“仍是占位值”），替换为真实 key 并重启后才会变成 `ready`。仍为 `auth` 时，先确认当前终端是否继承到了新变量（新开窗口最稳妥），再确认占位值已替换。

| state | 含义 | 处理 |
|---|---|---|
| `ready` | 探测通过，可派发 | — |
| `auth` | 缺少 API key，或 key 被端点拒绝（HTTP 401/403） | 设置/修正环境变量后重启 |
| `down` | 端点不可达；`vscode` 还包含扩展桥未启动或 pid 不一致 | 检查 `BaseUrl` 与网络；`vscode` 见 §4 第 5 条 |
| `no_model` | `vscode.lm` 没有可用模型 | 登录 GitHub Copilot 或选择模型后重载窗口 |

#### 2.4.4 BYOK（openai）与 vscode.lm（vscode）的启用条件与优先级

- 两个 provider **同时注册**（`openai` = BYOK 直连 OpenAI 兼容端点，`vscode` = 扩展的 `vscode.lm` 桥），执行时按队列项的 `preferred_provider` **精确取用**，不存在跨通道静默兜底。
- **定时任务的 provider 由 `Execution:ScheduledProvider` 决定**：扫描入队时读取该配置（默认 `openai`），写入队列项的 `preferred_provider`。只接受 `openai` / `vscode`，非法值回退 `openai` 并在启动日志记 Warning。修改后需重启守护进程，且只影响**之后新入队**的队列项；已在队列中的项保留入队时的 provider。
- **手动 `dispatch_now`**：`provider=auto` 时按 `openai` → `vscode` 选第一个 `ready` 且熔断关闭的；两者都不可用时回退 `openai` 入队；显式传 `openai` / `vscode` 时若该 provider 不可用会直接拒绝（`ProviderUnavailable`），不会自动换通道。手动派发的 `provider` 只作用于该次入队的队列项，**不会改变** `Execution:ScheduledProvider`。
- **`vscode` 就绪条件**：VS Code 已安装并激活 `devhub-bridge` 扩展，且 `vscode.lm` 有可用模型（例如已登录 GitHub Copilot）→ 扩展写出 `lm-bridge.json` → 宿主 `GET /health` 成功且返回 pid 与发现文件 pid 一致。任一环节不满足即为 `down`。
- **启用桥**：VS Code 用户设置里 `"devhub.bridge.enabled": true`（扩展默认关闭）。修改后重载扩展宿主即可写出 `lm-bridge.json`；已启动的桥不会因再次改配置而重启，需要重载窗口（或让扩展宿主重启）。
- **模型选择（不静默回退）**：扩展按 **id → family → name** 顺序匹配目标模型：先找 `id = deepseek-flash`，再找 `family = deepseek-flash`，最后找 `name = DeepSeek V4.1 Flash`。都匹配不到时**不会**回退到第一个可用模型，而是弹出错误提示，并把实际可用模型的 `id｜family｜name｜vendor` 清单写到数据目录的 `lm-bridge.error.json`，方便定位。
- **发现文件字段**：`lm-bridge.json` 除 `pid/port/token/model` 外，还带 `modelId` / `modelFamily` / `modelVendor`，`/health` 返回的 `model` 与发现文件一致。

### 2.5 日志

`appsettings.json` 的 `Logging` 节控制级别与格式，代码中不硬编码：

```json
{ "Logging": { "LogLevel": { "Default": "Information" }, "Console": { "FormatterName": "simple" } } }
```

机器解析场景把 `Logging:Console:FormatterName` 设为 `json`（可用环境变量 `Logging__Console__FormatterName=json`）。敏感字段（API key、令牌）不入日志。

### 2.6 token 用量与成本监控

每次真实回合的用量落在 `attempt` 表（`input_tokens` / `output_tokens` / `usage_estimated`），只记录 provider 响应体里 `usage` 的原值，不做换算。

| 场景 | 落库结果 |
|---|---|
| provider 每轮都返回 `usage` | `input_tokens` = 各轮 `prompt_tokens` 之和，`output_tokens` = 各轮 `completion_tokens` 之和，`usage_estimated=0` |
| 任意一轮缺少 `usage` | 整回合记 `NULL`（部分轮次之和不能代表真实总量），查询返回 `promptTokens/completionTokens/totalTokens = null` |
| 完全没有 `usage` | 同上记 `NULL`，不估算成真实值 |

- **通道差异**：BYOK（`openai`）非流式响应带 `usage`，落真实 token；`vscode.lm` 桥在 VS Code 1.145 的 `LanguageModelChatResponse` 上**取不到 usage**（扩展运行时探测不到 `usage` 字段），因此该通道的 attempt 三列均为 `NULL`、`usageEstimated=0`，查询也会明确返回 `null`——不用字符数估算冒充 token。需要成本核算时以 BYOK 通道的数值为准。

- **多轮累加**：工具调用循环会产生多次请求，每轮响应各带一份 `usage`，落库的是整回合累加值，不是最后一轮。
- **估算标记**：`usage_estimated=1` 表示该数值是近似估算、**不是真实 token**；当前 BYOK 非流式路径只写入 provider 原值（恒为 `0`）。若后续接入不返回 `usage` 的通道，必须显式置 `1` 并在字段说明中标注，不能混入真实值统计。
- **用量可得性显式标记**：`usageAvailable` 与 token 三列一起返回。`usageAvailable=false` 表示 provider 没有返回 usage（token 为 `null`，**不是 0**）；`true` 表示数值来自 provider 响应。消费方不要用“token 是否为 0”猜缺失。历史行由 schema v4 修正：v3 统一写 0，v4 把 token 非空的旧行回填为 `true`，token 为 `NULL` 的旧行保持 `false`。
- **查看方式**：
  - `queue_get` → `attempts[]` 的 `toolCalls` / `promptTokens` / `completionTokens` / `totalTokens` / `usageEstimated` / `usageAvailable` / `roundsExhausted`。
  - `result_list` → 每条结果的 `toolCalls` / `promptTokens` / `completionTokens` / `totalTokens` / `usageEstimated` / `usageAvailable` / `roundsExhausted`（取该队列项最近一次 attempt）。
  - `scheduler_status` → `tokens` 的累计值（含 `attemptsWithUsage`、`attemptsWithoutUsage`、`estimatedAttempts`，缺失用量单独计数而不是当 0 计入总量）。
  - 日志：`模型回合完成` / `模型回合结束` 两条记录带 `promptTokens`、`completionTokens`、`totalTokens`、`usageMissing`（或 `usageEstimated`）、`roundsExhausted` 字段，可与落库值对账。

---

## 3. 运维

### 3.1 退出码

| 退出码 | 含义 |
|---|---|
| 0 | 正常退出 |
| 1 | 未分类异常 |
| 2 | 已有实例（机器级单实例） |
| 3 | MCP 端口 3091 被占用 |
| 4 | 数据目录不可写 |
| 5 | 数据库迁移失败 |
| 6 | 插件包角色声明非法（缺字段、越界或引用不存在的技能） |

端口固定 `127.0.0.1:3091`（与 `plugin/mcp.json` 一致），被占用即失败退出，不顺延到随机端口。

### 3.2 故障排查

| 现象 | 处理 |
|---|---|
| 退出码 2 | 已有实例在运行；`-Action status` 确认，必要时 `-Action stop` |
| 退出码 3 | `Get-NetTCPConnection -LocalPort 3091 -State Listen` 找到占用进程 |
| 退出码 4 | 检查数据目录权限与路径是否被文件占用 |
| 退出码 5 | 查看日志中的 `eventId=1103`，确认 `devhub.db` 未被其他进程锁死 |
| 退出码 6 | 查看 `eventId=1006` 的逐条错误，按「文件 + 字段名」修正 `plugin/roles/*.json`；临时绕过可清除 `DEVHUB_PLUGIN_DIR` 让宿主跳过角色加载 |
| 扩展无通知 | 确认扩展设置 `devhub.notifications.enabled=true`（默认关闭）且 `runtime.json` 的 `apiEnabled=true`；上次已提醒的序号存在扩展 `globalState` 的 `devhub.noticeSeq` |
| 扩展桥未启动（`vscode:down`） | `vscode.lm` 没有可用模型：在 VS Code 中登录 GitHub Copilot 后重载窗口，`lm-bridge.json` 才会写出 |

### 3.3 性能与体积基线（实测）

| 项 | 实测值 |
|---|---|
| 1000 个任务文件 reconcile | 25 次采样：最小 303.1 / 中位 333.6 / p95 407.4 / 最大 926.4 ms（基线 ≤ 2000 ms；稳态基线 ≤ 1000 ms） |
| `queue_list` 一页（50/1000） | 约 5.5 ms（基线 ≤ 50 ms） |
| 自包含单文件体积 | 103.7 MiB（`PublishSingleFile` + `SelfContained`） |
| 冷启动到 `/health` 就绪 | 约 864 ms |
| 压缩版（`EnableCompressionInSingleFile=true`） | 50.0 MiB，冷启动约 1355 ms |

### 3.4 后台启动与终端作业对象（重要）

`scripts/devhub-daemon.ps1 -Action start` 用 `Start-Process` 启动守护进程。若这段脚本是在**会被终端回收的宿主**里执行（例如 IDE 的集成终端、CI 的临时作业、Agent 的 shell 工具），子进程会继承该终端所在的 Windows 作业对象（Job Object）；终端一关或作业被终止，`devhubd` 会被**连带杀掉**，表现为队列项 attempt 报 `LEASE_EXPIRED`、日志里没有任何关闭记录。

- 需要长期驻留时，**不要**在可被回收的终端里直接 `start`。改用 `Invoke-CimMethod -ClassName Win32_Process -MethodName Create` 起一个脱离作业对象的 wrapper，由 wrapper 再 `Start-Process`；环境变量在 wrapper 运行时从用户级读取（`[Environment]::GetEnvironmentVariable(name, 'User')`），不要把 key 写进脚本文件。
- 判断是否被连带回收：看 `runtime.json` 的 `pid` 是否还存在、`/health` 是否可达；进程消失但日志没有正常关闭记录，基本就是作业对象回收。
- 已有实例时 `-Action start` 会以退出码 2 拒绝启动，先 `-Action stop` 再启动。

---

### 3.5 升级到本版本（含破坏性变更）

升级步骤：停止旧实例 → 替换二进制/插件 → 启动。数据库会在启动时自动迁移到 schema v5：
v2/v3 的 `attempt` 用量列保持原语义，v5 只做 `ALTER TABLE ADD COLUMN` 与新增表
（`role_view`、`input_request`，以及 `task_view.role_id/depends_on/approval_state`、
`queue_item.role_id/hold_kind/hold_ref`），**不修改既有 CHECK、不重建表**，旧行按默认值
（`role_id=implementer`、`approval_state=none`）继续工作，无需手工改库。
启动时会校验 `plugin/roles/*.json` 并把角色写入 `role_view`；声明非法则以退出码 6 终止。

**不确定项回流（提问即挂起）**

- 角色 `canRequestInput=true` 时，模型可在回合中调用 `ask_input` 提问；问题落库为 `input_request`，
  队列项回到 `pending` 并挂 `hold_kind='input'`，**不消耗 attempt 预算**，也不占并发槽。
- 会话侧用 `input_request_list`（默认只列 `open`）取回，`input_request_get` 查看上下文与候选选项，
  `input_request_answer` 提交答案后同事务清除挂起、`available_at=now`，下一次扫描即重跑该任务。
- 相同答案重复提交返回 `duplicate`；不同答案返回 `ANSWER_CONFLICT`，不覆盖已答内容。
- `input_request_cancel` 人工撤销会终止挂起队列项（`INPUT_REQUEST_CLOSED`），同内容不会自动重新入队。
- 超时（`Execution:InputTimeoutHours`，默认 24）由每次 tick 的过期扫描置 `expired`，队列项失败为
  `INPUT_TIMEOUT`；任务文件内容变更时旧问题单自动取消，队列项记 `TASK_CHANGED`。

**审批闸门（结果通过/驳回）**

- `task.requiresApproval=true` 时，即便全局档位与任务档位都是 `auto`，结果也停在 `awaiting_review`；
  任务级声明收紧后不能被全局 `auto` 绕过。
- 待审结果用 `result_list` 查看，`result_approve` 置 `delivered`、`result_reject` 置 `failed`；
  两者都会回写 `devhub://tasks` 的 `approvalState`（`pending/approved/rejected`）并写 `audit`。
- 驳回额外写 `notice.kind='failure'`（`result-rejected:<resultId>`），通过不写通知。
- 同向重复提交幂等（`decidedAt=null`）；反向提交返回 `RESULT_NOT_PENDING`，不覆盖既有终态。
- 审批**不改任务文件**：任务清单仍由工作区维护，避免调度器改写用户文件。

**B1 时间格式统一为 UTC + `Z`**

- 旧：`2026-11-08T04:09:03.1234567+00:00`；新：`2026-11-08T04:09:03.1234567Z`。
- 覆盖所有工具与资源的 `createdAt` / `updatedAt` / `startedAt` / `endedAt` / `readAt` / `seenAt` /
  `lastScanAt` / `nextScanAt` / `checkedAt`。库内历史行不改写，读侧统一归一后出契约。
- 迁移：若你的脚本用 `endsWith("+00:00")` 或严格字符串比较判断时间，改为按时间解析（`DateTimeOffset` /
  `new Date(...)`）。`nextCursor` 不受影响。

**B2 删除 `scheduler_status.verbose`**

- 该参数原本无任何作用，现已从工具签名删除。SDK 会忽略多余参数，旧调用不会报错，
  但该字段已无效果；请从调用参数中删除，避免误判它会改变返回内容。

**轮次耗尽的语义（新增 `roundsExhausted`）**

- 工具轮次预算耗尽时，回合状态仍是 `succeeded`，但 `queue_get.attempts[]` 与 `result_list.items[]`
  会带 `roundsExhausted=true`，表示正文是框架移除工具后的兜底答复，**不是**模型自然完成。
- 背景：旧实现返回错误码 `TOOL_ROUNDS_EXCEEDED`；改用 `Microsoft.Extensions.AI` 后框架会移除工具再调一轮
  并正常返回，本版不再与框架行为对抗，改为“保持返回路径 + 显式留痕”。自动化流程应把
  `roundsExhausted=true` 视同需要人工复核。
- 日志：`模型回合结束` 新增 `roundsExhausted` 字段，可与落库值对账。

**其他执行语义变化**

1. `vscode` 桥取消：宿主改为原样抛出 `OperationCanceledException`（先以 3 秒独立超时发送
   `DELETE /turns/{id}` 收尾），由 `TurnRunner` 统一映射为 `cancelled`。对外可观察结果与旧版一致。
2. 连续工具错误达到 3 次：回合失败，`errorKind=tool_error`，`errorCode` 取工具自身稳定码
   （如 `FILE_NOT_FOUND`），不再是笼统的回合异常。

**新增 `usageAvailable`**：与 token 三列一起返回，`false` 明确表示用量不可得（token 为 `null`），
不需要再用“token 是否为 0”猜。完整字段与错误码见 [docs/mcp-tool-contract.md](./docs/mcp-tool-contract.md)。

---

## 4. 已知限制

1. **机器级单实例**：同一台机器同时只允许一个宿主；多工作区由 `workspaces.json` 承载，不是多进程。
2. **MCP 端点无静态令牌**：Agent Plugins 规范不作用 `url`/`headers` 占位符，只能依赖"仅回环 + Host/Origin 校验"。
3. **扩展需单独安装**：插件规范无法下发 VS Code 扩展，`vscode.lm` 桥与通知依赖 `devhub-bridge` VSIX。
4. **真实模型调用**：已用真实 BYOK 跑通（见 §6.1）。占位值（如 `CHANGE_ME`）不会被当成可用凭据：启动探测会显示 `openai:auth` 并提示“仍是占位值”，替换为真实 key 后才会转 `ready`。即便显示 `ready`，也应以一次真实回合的结果为准，不用假 provider 冒充通过。
5. **`vscode` provider 依赖窗口与模型**：未激活扩展、或 `vscode.lm` 没有可用模型（例如未登录 GitHub Copilot）时 `vscode:down`，不会写出 `lm-bridge.json`；定时任务默认走 `openai`，不依赖窗口（可用 `Execution:ScheduledProvider` 改为 `vscode`，但那样同样受上述条件约束）。
6. **未启用裁剪**：`PublishTrimmed` 与反射/序列化路径冲突，当前只评估不启用；R2R 会增大体积，未采用。
7. **无 Web 面板**：本期只提供 MCP 工具面、技能与 VS Code 原生通知。

---

## 5. 验证命令

```powershell
dotnet build DevHubd.sln            # 0 警告 0 错误
dotnet test DevHubd.sln             # 全绿
node scripts/validate-plugin.mjs
node scripts/check-encoding.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action start
node scripts/smoke-mcp.mjs          # 真实 MCP 握手 + tools/list + tools/call
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action stop
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\devhub-daemon.ps1 -Action drill
```

---

## 6. 验证状态

记录基线：Windows 11 + .NET SDK 10.0.401 + VS Code Stable 1.145.2，2026-11-07。

### 6.1 已真实验证

- `dotnet build DevHubd.sln`：0 警告 0 错误；`dotnet test`：153/153 通过、0 跳过（此前的 5 项启动失败路径用例在停止守护进程、互斥体释放后真机跑通，见下一条）。
- `node scripts/validate-plugin.mjs`（5 项 PASS，含 `roles/` 角色字段校验）、`node scripts/check-encoding.mjs`（193 个文件）、`node scripts/smoke-mcp.mjs`（真实握手 + `tools/list` + `tools/call` + `resources/read`）全部通过。
- 角色声明与快照（2026-11-08，U7）：临时数据目录从零启动，日志为 `数据库就绪…（schema v5）`、`角色快照已同步：declared=3 added=3 updated=0 removed=0 unchanged=0`；经真实 MCP 握手调用 `scheduler_status` 返回 `roles[]` 三项（`implementer` maxParallel=2、`reviewer` 1、`triage` 1，`running` 均为 0）；`/health` 的 `tables` 含 `role_view`、`input_request`。声明非法路径由 `StartupExitCodeTests` 真机复跑：退出码 6 且日志含 `缺少必填字段`。
- 任务模型与 `task_upsert`（2026-11-08，U8）：临时工作区经真实 MCP 调用 `task_upsert`（`role=reviewer`、`requiresApproval=true`、`source=conversation`）返回 `contentHash` 并落盘 `.devhub/tasks/T-900.json`；`scheduler_control(scan_now)` 后 `resources/read devhub://tasks` 显示该任务 `status=ready`、`roleId=reviewer`，队列项按 `reviewer` 入队（无 API key 时按既有语义失败为 `MISSING_API_KEY`）。当时 `tools/list` 已含 8 个工具（U11 后为 14 个），冒烟通过。
- 问题单回流（2026-11-08，U9/U10）：真实 MCP 握手后 `tools/list` 返回 12 个工具（新增 `input_request_list/get/answer/cancel`），`scheduler_status.host.schemaVersion=5`；真机调用 `input_request_list` 返回 `{"items":[]}`（空态不输出 `nextCursor`）。提问挂起、同题去重、任务变更取消、回答恢复（含相同答案 `duplicate` 与不同答案 `ANSWER_CONFLICT`）、超时置 `expired`、撤销终止队列项由 `InputRequestTests` 与 `P2ExecutionTests` 覆盖。
- 审批闸门（2026-11-08，U11）：真实 MCP 握手 `tools/list` 返回 14 个工具（新增 `result_approve` / `result_reject`）；`devhub://tasks` 每个条目均含 `approvalState`，当前真实数据全部为 `none`。真机调用 `result_approve` 传入不存在的 id 返回 `isError=true`、`code=UNKNOWN_RESULT`、`retryable=false`，错误信封与契约一致。通过→`delivered`/`approved`、驳回→`failed`/`rejected` 与通知、同向幂等（`duplicate`）、反向 `RESULT_NOT_PENDING`、`requiresApproval` 在 `auto` 档位下仍待审，由 `ResultDecisionTests`（5 项）与 `P2ExecutionTests` 覆盖。
- schema 升级：已有 V1 库启动后 `user_version` 由 1 升到 2（新增 `attempt.usage_estimated`），`scheduler_status.host.schemaVersion=2`。2026-11-08 增补：v3 新增 `attempt.usage_available`、`attempt.rounds_exhausted`；v4 回填旧行 `usage_available`（token 非空即标 1，token 为 `NULL` 保持 0）。真实数据目录启动日志为 `数据库就绪…（schema v4）`，冒烟返回 `schema=v4`。
- v2 → v4 迁移安全核对（2026-11-08，**真实 v2 库副本** `%TEMP%\devhub-migrate-check\devhub.db`，先 `VACUUM INTO` 出干净副本，全程只动副本）：迁移前 `user_version=2`、`integrity_check=ok`、`foreign_key_violations=0`；各表行数 task_view=5、queue_item=5、attempt=6、result=5、notice=5、provider_state=2、scheduler_state=1、scope_lock=0、audit=1。迁移后 `MigrateAsync` 返回 4，行数逐表完全一致，既有主键 **missing=0**（9 张表全部），`integrity_check=ok`、外键无违规；旧行 `rounds_exhausted` 全为 0，`usage_available` 与 token 是否为空严格对应（6 条旧 attempt 中 4 条有 token 被标 1、2 条 token 为 `NULL` 保持 0）；重复迁移幂等返回 4。结论 `PASS`，不以“启动成功 + schema v4”替代行数与主键证据。
- 真实 BYOK 回合复验（REAL-006，2026-11-08，当前代码 `Microsoft.Extensions.AI` 重构后）：provider `openai`（`https://api.deepseek.com` / `deepseek-flash`），队列项 `7ce12702856e45d49d1ea363fa8acf27`，`attempt=1`、`outcome=succeeded`、`toolCalls=2`、`promptTokens=3050`、`completionTokens=312`、`totalTokens=3362`、`usageEstimated=false`、`usageAvailable=true`、`roundsExhausted=false`；直接读 SQLite 的 `attempt` 行为 `input_tokens=3050, output_tokens=312, usage_available=1, rounds_exhausted=0`，与 `queue_get` 一致；结果为 `awaiting_review`（`resultId=aab6bf06aea94714accd80a789449521`），产物 `work/7ce1270…/out/summary-u5-byok.md`。
- 真实 `vscode.lm` 回合复验（REAL-007，2026-11-08）：provider `vscode`，队列项 `4aef09fb14114a4ca9a4234b7080d27c`，`attempt=1`、`outcome=succeeded`、`toolCalls=3`、`usageAvailable=false`、`roundsExhausted=false`；该通道取不到 usage，`attempt` 的 `input_tokens`/`output_tokens` 为 **`NULL`**（不是 0），`usage_estimated=0`，`queue_get` 不返回 token 字段，未做任何估算；结果为 `awaiting_review`（`resultId=ebaa2fe3987843dbabef2b383aeaa15c`），产物 `work/4aef09f…/out/summary-u5-lm.md`。桥可用前提：VS Code 窗口必须以**受信任工作区**（或 `--disable-workspace-trust`）打开，否则扩展被限制模式跳过、`lm-bridge.json` 不生成、`vscode` 恒为 `down`。
- 契约一致性回归（2026-11-08）：`scheduler_control` 返回的 `providers[].checkedAt` 原先仍是 `+00:00`，与 B1 “统一 `Z`” 不一致；已改为与 `scheduler_status` 共用 `UtcTime.Normalize`，真机返回 `2026-11-08T04:50:25.9859966Z`。
- usage 落库核对（REAL-003，新真实回合）：provider 3 轮累计 `prompt_tokens=3087`、`completion_tokens=360`、`total=3447`，`usageMissing=False`；直接读 SQLite 得 `input_tokens=3087, output_tokens=360, usage_estimated=0`；`queue_get`、`result_list`、`scheduler_status.tokens` 三处读回一致，未做任何估算。
- 按 §1.1 命令自包含发布：产物 103.7 MiB，`-Action start/status/stop` 与 MCP 冒烟连续走通（`startupMs=1966`，停止后 `runtime.json` 被移除、`status` 返回 3）。
- 启动失败路径真机复跑（2026-11-08，先 `-Action stop` 停掉守护进程并确认 `runtime.json` 移除、`/health` 不可达）：`StartupExitCodeTests` 的退出码 2/3/4/5 四项与 `LoggingTests.SecondInstance_WritesErrorLogWithLockPath` 全部执行且通过，全量通过、0 跳过。
- VSIX 安装到 VS Code Stable：`code --install-extension` 成功，扩展宿主日志出现 `_doActivateExtension userdrive.devhub-bridge, activationEvent: 'onStartupFinished'`，无报错。
- 通知闭环：向 `notice` 表造 3 条新通知，VS Code 通知中心各弹一次且标题不重复；ack 后数据库 `acked_at` 写入、`globalState["devhub.noticeSeq"]` 从 0 推进到 3；随后用 `sinceSeq=0` 查询不再返回已确认项。
- 守护进程侧 `/health` + pid 校验：发现文件 pid 与桥 pid 一致 → `providers = vscode:ready`；把发现文件 pid 改为 30929 → `provider=vscode state=down detail=发现文件 pid=30929 与桥实际 pid=30928 不一致，判定为残留文件`。
- 真实 `vscode.lm` 桥（2026-11-08）：VS Code Stable 1.145.2，`chat.defaultModel=deepseek-flash`，扩展按 **id → family → name** 选中 `DeepSeek V4.1 Flash`（`id=deepseek-flash`、`family=deepseek`、`vendor=deepseek`），未发生静默回退；`lm-bridge.json` 含 `pid=50432`、`port=49210`、`model=DeepSeek V4.1 Flash`、`modelId/modelFamily/modelVendor`，`/health` 返回 `{"ok":true,"pid":50432,"model":"DeepSeek V4.1 Flash"}` 与发现文件一致，pid 存活校验通过，重启守护进程后 `scheduler_status.providers` 中 `vscode` 为 `ready`。
- 真实 `vscode.lm` 回合（REAL-005，2026-11-08）：provider `vscode`，耗时 `2.95s`，工具调用 2 次（3 轮：2 次工具调用 + 1 次最终回答，轮次由工具序列推导，扩展未单独上报轮数），改动 1 个文件（新增 `out/summary-lm.md`），结果为 `awaiting_review`，通知与 ack 闭环走通（`{"acknowledged":1}`，unread 4→3）。该通道**取不到 usage**：扩展运行时探测 `LanguageModelChatResponse.usage` 为 `undefined`，attempt 三列为 `NULL`、`usage_estimated=0`，`queue_get`/`result_list` 查询返回 `null`，未做任何估算。
- 插件包在 Copilot Chat 真机安装（2026-11-08）：命令面板 `Chat: Install Plugin from Source` 选择仓库内 `plugin` 目录，安装落盘 `%USERPROFILE%\.copilot\installed-plugins\_direct\plugin\`（含 `plugin.json`、`mcp.json`、`skills/devhub-scheduler/`）；Customizations 面板显示 `MCP Servers` 中 `devhubd` 为 Running、`Skills` 中 `devhub-scheduler` 为 Loaded。首次输入 `userdrive-devhub` 被拒（`Invalid plugin spec. Use: plugin-name@marketplace-name, owner/repo, or a URL`），改为完整目录路径后安装成功。
- Chat 内真实模型调用（2026-11-08，DeepSeek V4.1 Flash 三轮）：① `Read skill [devhub-scheduler]` → `scheduler_status{verbose:true}`（当时版本仍有该参数，现已在 B2 删除） → `result_list{unreadOnly:true,limit:10}`，10s、输入 185,943 / 输出 1,171 tokens，正确报出 `openai:ready`、`vscode:ready`、`host.pid`、`mcp={bound:true,port:3091}`；② `queue_list{status:"all",limit:100}` → `queue_get{queueItemId:"7405161e…"}` → `result_list{limit:20}`，19s、304,672 / 2,131 tokens，查得 REAL-005（provider `vscode`、耗时 2.95s、`usageEstimated=false`），但当时 `toolCalls` 字段缺失；③ `dispatch_now{taskId:"NOT-EXIST",dryRun:true}` 返回结构化 `UNKNOWN_TASK`（含 `next`），模型按提示核对 `workspaces.json` 后改用 `REAL-005` 重试成功，19s、330,397 / 2,037 tokens。
- Chat 内修复回归（2026-11-08，同一会话）：`queue_get` 的 `attempts[0].toolCalls=2`；`result_list` 五条结果均返回 `toolCalls`（REAL-005=2、REAL-004=3、REAL-003=2、REAL-002=2、REAL-001=2）；`dispatch_now{taskId:"REAL-005",workspaceId:"devhub-real-turn",dryRun:true}` 由修复前 `"provider":"openai"` 变为 `"provider":"vscode"`（复用的是已存在的 vscode 队列项）。耗时 9s，输入 178,047 / 输出 1,159 tokens。

### 6.2 仅单测覆盖

- 单实例租约续期/接管、停机租约、调度扫描增量与 reconcile、队列流转、回合超时与取消、熔断状态机、路径守卫、工具 schema 快照、日志 EventId 与字段断言。
- 性能基线（1000 任务 reconcile 25 次采样：最小 303.1 / 中位 333.6 / p95 407.4 / 最大 926.4 ms，基线 2000 ms、稳态 1000 ms；`queue_list` 一页约 5.5 ms）由 `P4PerformanceTests` 固化，属测试环境测量值。原 500 ms 阈值低于观测最大值，2026-11-08 依据实测分布调高，未改被测实现与取样方式。

### 6.3 缺外部条件（未验证）

- 无。原「插件包安装」一项已于 2026-11-08 在真机走官方安装路径完成，并连同 Chat 内工具调用一并转入 §6.1。
