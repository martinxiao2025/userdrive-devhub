# AGENTS.md — 仓库约定（所有实现必须遵守）

本文件是 `userdrive-devhub` 的项目级约束，优先级高于个人习惯。三条约束均已在工程配置中落点，改动代码时不得绕过。

---

## 1. 优先官方开源 package，降低自研代码

**原则**：能用成熟官方库解决的不自己写；自研只保留官方库覆盖不到的部分（业务领域逻辑、调度语义、MCP 工具定义）。

**当前技术栈的优先级**

| 能力 | 首选官方包 |
|---|---|
| 宿主 / 配置 / DI / 选项 / 日志 | `Microsoft.Extensions.*`（Hosting / Configuration / DependencyInjection / Options / Logging） |
| SQLite 访问与迁移执行 | `Microsoft.Data.Sqlite`（DDL 手写属于业务 schema，允许自研） |
| MCP 服务端 | 官方 `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` |
| 测试框架 | `xunit.v3`（+ `Microsoft.Testing.Platform` 运行器） |
| 日志断言 | `Microsoft.Extensions.Diagnostics.Testing`（`FakeLogger`） |

**执行要求**

- 新增依赖必须是官方（Microsoft / .NET Foundation / 项目官方组织）或高活跃度开源项目，且在 `*.csproj` 中**锁定显式版本**，不留 `*`、浮动区间或未固定版本。
- 每条自研实现若存在可替代的成熟库，必须在代码注释或设计文档中写明"为何未采用"，避免后续反复讨论。
- 升级依赖前先用 `dotnet list package --outdated` 实际枚举；只升稳定版，不引预览版。若最新稳定版破坏现有行为，不静默降级，记录版本、现象与取舍依据。

## 2. 文件统一 UTF-8

- 所有新增与修改文件使用 **UTF-8（无 BOM）**，不得出现乱码。
- [.editorconfig](./.editorconfig) 已声明 `charset = utf-8`；`.cs` 编译按 UTF-8 读取由 `Directory.Build.props` 的 `<CodePage>65001</CodePage>` 显式指定。
- [scripts/check-encoding.mjs](./scripts/check-encoding.mjs) 是日常校验门禁，必须保持通过；新增文本文件类型要同步加入该脚本的扫描范围。

## 3. 标准化日志输出

- 一律使用 `Microsoft.Extensions.Logging`：通过 `ILogger<T>` / `ILoggerFactory` 注入，**禁止 `Console.WriteLine` 手工拼 `[devhubd]` 日志**。交互式 CLI 输出（如 `--help`）如需保留，必须与日志分离。
- 结构化日志统一由 `[LoggerMessage]` 源生成（见 [DevHubLog.cs](./src/DevHubd/Logging/DevHubLog.cs)），固定 EventId、字段名与级别，禁止在业务代码里拼消息字符串。
- 控制台默认人类可读单行格式；JSON 格式由配置开关切换，供机器解析：
  - 配置文件：`Logging:Console:FormatterName = simple | json`
  - 环境变量覆盖：`Logging__Console__FormatterName=json`
  - 日志级别由 `Logging:LogLevel` 控制（`appsettings.json` + 环境变量 `Logging__LogLevel__*`），**不在代码里硬编码级别**。
- 关键路径必须打日志并带字段：启动/关闭、端口绑定与冲突、单实例互斥、数据库迁移、调度扫描与派发、模型回合开始/结束/失败、进程退出码。
- 敏感信息（API key、令牌、Authorization 头）**绝不能进日志**；只允许记录"是否已配置"与来源（环境变量名），不记录值。
- 异常必须带上下文记录（`ILogger` 的 exception 参数 + 关键字段），不允许 `catch` 后静默吞掉；确实需要忽略的收尾异常，必须写注释并至少 `Debug`/`Warning` 记录。

**日志验证方式**

- 自动化：`tests/DevHubd.Tests/LoggingTests.cs` 用 `FakeLogger` 断言关键路径的 EventId、级别与结构化字段；并断言 `appsettings.json` 保留 `Logging:Console:FormatterName` 与 `Logging:LogLevel`。
- 手工：`dotnet run --project src/DevHubd` 观察单行人类可读格式；`$env:Logging__Console__FormatterName='json'` 后启动，确认每行是合法 JSON。

## 4. 命名遵照 .NET 命名规范（分析器门禁）

**规则**

| 元素 | 约定 |
|---|---|
| 类型 / 方法 / 属性 / 事件 / 常量 | PascalCase |
| 参数、局部变量 | camelCase |
| 私有字段 | `_camelCase` |
| 接口 | `I` 前缀 |
| 异步方法 | `Async` 后缀（入口点 `Main` 除外） |
| 泛型参数 | `T` 前缀 |

**缩略词按单词处理**：正确写法是 `DevHubd` / `DevHubdOptions` / `DevHubdPaths`，不是 `Devhubd` / `DevhubdOptions` / `DevhubdPaths`。项目名、目录名、命名空间、程序集名、`.sln` 中的项目名必须一致；当前落位：

| 项 | 值 |
|---|---|
| 解决方案 | `DevHubd.sln` |
| 目录 / 项目 / 命名空间 | `src/DevHubd`、`src/DevHubd.Core`、`src/DevHubd.Mcp`、`tests/DevHubd.Tests` |
| 程序集名（对外可执行文件） | `devhubd.dll` / `devhubd.exe`（对外标识允许小写） |

**文件名与主类型同名，一个文件一个主类型**；小的强相关类型（如 `SchedulerStatusSnapshot` 内的若干 DTO）允许同文件。

**其他约定的边界**

- 配置键：`Sections:Keys`（如 `Logging:Console:FormatterName`）。
- JSON：camelCase，与 `plugin.json`、`mcp.json`、`schemas/*.json` 保持一致。
- 数据库表名 / 列名：沿用既有 snake_case，不因本条约束改动。

**门禁**

- [Directory.Build.props](./Directory.Build.props)：`AnalysisLevel=latest`、`EnableNETAnalyzers=true`、`EnforceCodeStyleInBuild=true`、`TreatWarningsAsErrors=true`。
- [.editorconfig](./.editorconfig)：`dotnet_naming_rule` 系列声明上述风格（类型 / 接口 / 方法 / 属性事件 / 常量 / 私有字段 / 参数 / 局部变量 / 泛型参数 / 异步方法），违规以 `IDE1006` warning 输出，再被 `TreatWarningsAsErrors` 提升为错误。
- 禁止用 `#pragma` 批量压制换取"全绿"；确需压制必须逐处注释理由并在评审中说明。

---

## 验证门禁（改完必须全绿）

```powershell
dotnet build DevHubd.sln              # 0 警告 0 错误
dotnet test DevHubd.sln               # 走 Microsoft.Testing.Platform，全绿
node scripts/validate-plugin.mjs
node scripts/check-encoding.mjs
node scripts/smoke-mcp.mjs            # 需先启动 devhubd
```

> `dotnet test` 在 MTP 模式下**不要带 `--nologo`**：该参数会被转发给测试进程，导致测试发现数为 0（退出码 5）。
