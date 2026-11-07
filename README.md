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

### 2.4 日志

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
| 扩展无通知 | 确认 `lm-bridge.json` 存在、`runtime.json` 的 `apiEnabled=true` |

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
4. **真实模型调用未验证**：本机未配置 `DEVHUB_OPENAI_API_KEY`，真实 OpenAI 兼容端点回合未实测；无凭据时 `scheduler_status` 会显示 `openai:auth`，这是预期行为，不用假 provider 冒充通过。
5. **`vscode` provider 依赖窗口**：未激活扩展时 `vscode:down`，Chat 内手动触发不可用；定时任务默认走 `openai`，不依赖窗口。
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
