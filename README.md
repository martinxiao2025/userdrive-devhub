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
| **API key 值**（存放在上面那一项命名的环境变量里） | — | 无 | **是**，缺失时 `scheduler_status` 显示 `openai:auth` |

- 客户端请求地址是 `{BaseUrl}/chat/completions`，因此 `BaseUrl` 必须是 OpenAI 兼容端点根地址（末尾 `/` 会被自动去掉）。
- 空字符串会回退到默认值；`RequestTimeoutSeconds` 超出 5–600 会被收敛到边界。
- 必填的只有 **API key 的值**；其余四项不写就用默认值。

#### 2.4.2 存放位置与优先级

1. **程序目录 `appsettings.json`**：只有日志与 `AllowedHosts`，**不含模型配置**。
2. **用户级配置文件**：`%LOCALAPPDATA%\userdrive-devhub\config.json`（用 `--data-dir` 或 `DEVHUB_DATA_DIR` 改数据目录时，路径变为 `<data-dir>\config.json`）。
3. **环境变量 `DEVHUB__...`（双下划线）**：后加载，覆盖第 2 步 JSON 中的同名键。
4. **API key 只从环境变量读取**：配置文件出现 `apiKey` / `openAiApiKey` / `api_key` 非空字符串会被忽略并记 Warning，key 不落盘、不进日志。
5. **改完必须重启守护进程**：配置在启动时读取一次（`reloadOnChange=false`），key 也来自进程启动时继承的环境块。
6. 注意：`config.json` 不存在时宿主直接使用内置默认值，`DEVHUB__...` 覆盖也不会被加载；先确保该文件存在（内容至少是 `{}`）。

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

在 Copilot Chat 中也可以直接调用 `scheduler_status`，检查 `providers[]` 里 `provider=openai` 的 `state` 与 `detail`：配置前是 `auth`（detail 为“未配置环境变量 DEVHUB_OPENAI_API_KEY”），配置并重启后变为 `ready`。仍为 `auth` 时，先确认当前终端是否继承到了新变量（新开窗口最稳妥），以及占位值是否已替换。

| state | 含义 | 处理 |
|---|---|---|
| `ready` | 探测通过，可派发 | — |
| `auth` | 缺少 API key，或 key 被端点拒绝（HTTP 401/403） | 设置/修正环境变量后重启 |
| `down` | 端点不可达；`vscode` 还包含扩展桥未启动或 pid 不一致 | 检查 `BaseUrl` 与网络；`vscode` 见 §4 第 5 条 |
| `no_model` | `vscode.lm` 没有可用模型 | 登录 GitHub Copilot 或选择模型后重载窗口 |

#### 2.4.4 BYOK（openai）与 vscode.lm（vscode）的启用条件与优先级

- 两个 provider **同时注册**（`openai` = BYOK 直连 OpenAI 兼容端点，`vscode` = 扩展的 `vscode.lm` 桥），执行时按队列项的 `preferred_provider` **精确取用**，不存在跨通道静默兜底。
- **定时任务固定走 `openai`**：扫描入队时 `preferred_provider` 直接写 `'openai'`，与 `vscode` 是否可用无关。
- **手动 `dispatch_now`**：`provider=auto` 时按 `openai` → `vscode` 选第一个 `ready` 且熔断关闭的；两者都不可用时回退 `openai` 入队；显式传 `openai` / `vscode` 时若该 provider 不可用会直接拒绝（`ProviderUnavailable`），不会自动换通道。
- **`vscode` 就绪条件**：VS Code 已安装并激活 `devhub-bridge` 扩展，且 `vscode.lm` 有可用模型（例如已登录 GitHub Copilot）→ 扩展写出 `lm-bridge.json` → 宿主 `GET /health` 成功且返回 pid 与发现文件 pid 一致。任一环节不满足即为 `down`。
- **单独说明**：`Execution:ScheduledProvider` 虽然存在于执行配置对象中，但当前**没有任何派发路径读取它**（定时任务入队时 provider 写死为 `openai`）。写它不会改变定时任务的 provider；要让某个任务走 `vscode`，只能用 `dispatch_now(provider:"vscode")` 手动派发。

### 2.5 日志

`appsettings.json` 的 `Logging` 节控制级别与格式，代码中不硬编码：

```json
{ "Logging": { "LogLevel": { "Default": "Information" }, "Console": { "FormatterName": "simple" } } }
```

机器解析场景把 `Logging:Console:FormatterName` 设为 `json`（可用环境变量 `Logging__Console__FormatterName=json`）。敏感字段（API key、令牌）不入日志。

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

端口固定 `127.0.0.1:3091`（与 `plugin/mcp.json` 一致），被占用即失败退出，不顺延到随机端口。

### 3.2 故障排查

| 现象 | 处理 |
|---|---|
| 退出码 2 | 已有实例在运行；`-Action status` 确认，必要时 `-Action stop` |
| 退出码 3 | `Get-NetTCPConnection -LocalPort 3091 -State Listen` 找到占用进程 |
| 退出码 4 | 检查数据目录权限与路径是否被文件占用 |
| 退出码 5 | 查看日志中的 `eventId=1103`，确认 `devhub.db` 未被其他进程锁死 |
| 扩展无通知 | 确认扩展设置 `devhub.notifications.enabled=true`（默认关闭）且 `runtime.json` 的 `apiEnabled=true`；上次已提醒的序号存在扩展 `globalState` 的 `devhub.noticeSeq` |
| 扩展桥未启动（`vscode:down`） | `vscode.lm` 没有可用模型：在 VS Code 中登录 GitHub Copilot 后重载窗口，`lm-bridge.json` 才会写出 |

### 3.3 性能与体积基线（实测）

| 项 | 实测值 |
|---|---|
| 1000 个任务文件 reconcile | 约 319 ms（基线 ≤ 500 ms，稳态约 204 ms） |
| `queue_list` 一页（50/1000） | 约 5.5 ms（基线 ≤ 50 ms） |
| 自包含单文件体积 | 103.7 MiB（`PublishSingleFile` + `SelfContained`） |
| 冷启动到 `/health` 就绪 | 约 864 ms |
| 压缩版（`EnableCompressionInSingleFile=true`） | 50.0 MiB，冷启动约 1355 ms |

---

## 4. 已知限制

1. **机器级单实例**：同一台机器同时只允许一个宿主；多工作区由 `workspaces.json` 承载，不是多进程。
2. **MCP 端点无静态令牌**：Agent Plugins 规范不作用 `url`/`headers` 占位符，只能依赖"仅回环 + Host/Origin 校验"。
3. **扩展需单独安装**：插件规范无法下发 VS Code 扩展，`vscode.lm` 桥与通知依赖 `devhub-bridge` VSIX。
4. **真实模型调用未验证**：本机未填入真实 key，真实 OpenAI 兼容端点回合未实测；配置步骤见 §2.4。注意占位值（如 `CHANGE_ME`）也算“已配置”，启动探测会显示 `openai:ready`，但真实回合会因 HTTP 401 失败并转回 `openai:auth`，所以必须以一次真实回合的结果为准，不用假 provider 冒充通过。
5. **`vscode` provider 依赖窗口与模型**：未激活扩展、或 `vscode.lm` 没有可用模型（例如未登录 GitHub Copilot）时 `vscode:down`，不会写出 `lm-bridge.json`；定时任务默认走 `openai`，不依赖窗口。
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

- `dotnet build DevHubd.sln`：0 警告 0 错误；`dotnet test`：92/92 通过（xUnit v3 + Microsoft.Testing.Platform，0 跳过）。
- `node scripts/validate-plugin.mjs`（4 项 PASS）、`node scripts/check-encoding.mjs`（126 个文件）、`node scripts/smoke-mcp.mjs`（真实握手 + `tools/list` + `tools/call` + `resources/read`）全部通过。
- 按 §1.1 命令自包含发布：产物 103.7 MiB，`-Action start/status/stop` 与 MCP 冒烟连续走通（`startupMs=1966`，停止后 `runtime.json` 被移除、`status` 返回 3）。
- 退出码真机复跑：已有实例运行再启动 → 2；先占用 3091 再启动 → 3。
- VSIX 安装到 VS Code Stable：`code --install-extension` 成功，扩展宿主日志出现 `_doActivateExtension userdrive.devhub-bridge, activationEvent: 'onStartupFinished'`，无报错。
- 通知闭环：向 `notice` 表造 3 条新通知，VS Code 通知中心各弹一次且标题不重复；ack 后数据库 `acked_at` 写入、`globalState["devhub.noticeSeq"]` 从 0 推进到 3；随后用 `sinceSeq=0` 查询不再返回已确认项。
- 守护进程侧 `/health` + pid 校验：发现文件 pid 与桥 pid 一致 → `providers = vscode:ready`；把发现文件 pid 改为 30929 → `provider=vscode state=down detail=发现文件 pid=30929 与桥实际 pid=30928 不一致，判定为残留文件`。

### 6.2 仅单测覆盖

- 退出码 4（数据目录不可写）与 5（迁移失败）：`StartupExitCodeTests` 覆盖，未在真机复现。
- 单实例租约续期/接管、停机租约、调度扫描增量与 reconcile、队列流转、回合超时与取消、熔断状态机、路径守卫、工具 schema 快照、日志 EventId 与字段断言。
- 性能基线（1000 任务 reconcile 约 319 ms、`queue_list` 一页约 5.5 ms）由 `P4PerformanceTests` 固化，属测试环境测量值。

### 6.3 缺外部条件（未验证）

- **真实 OpenAI 兼容回合**：本机只配置了占位值（`DEVHUB_OPENAI_API_KEY=CHANGE_ME`），未填入真实 key，真实回合未执行；`scheduler_status` 的探测结果会因占位值显示 `openai:ready`，但不能作为端到端通过的证据。按 §2.4 换成真实 key 后重跑一次真实回合，记录输入输出与耗时，并确认不再是 `openai:auth`。
- **真实 `vscode.lm` 回合桥**：扩展已安装并激活，但本机 VS Code 未登录 GitHub Copilot，`vscode.lm` 无可用模型，因此未写出 `lm-bridge.json`；上文的 `/health` + pid 校验是用本地桩端点验证守护进程侧逻辑，不代表真实桥可用。登录 Copilot 并重载窗口后可复测。
- **插件包安装**：`Chat: Install Plugin from Source` 无法通过 CLI 自动化，未在真机执行；`plugin/` 已通过 `validate-plugin.mjs` 静态校验。
