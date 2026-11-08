# 角色工具面 / 任务状态回写 / attempt.model 修复计划

## Context

上一轮真机 E2E 暴露：`TurnRunner` 固定使用 `ToolDispatcher.Describe()` 的文件工具集，角色声明的 `tools` 从未参与构建工具面，导致 `task_upsert`、`queue_*`、`result_*` 在后台回合内不可见；模型只能靠会话代录任务。同时：

- `task_view.status` 只镜像任务文件，审批通过后没有任何路径写 `done`，出现「审批态变了、任务状态不动」的半边同步。
- `attempt.model` 恒为 `NULL`：`TurnRequest.Model` 传 null（表示用 provider 默认值），provider 实际模型名没有回传。

本轮目标：

1. 按角色声明构建回合工具集，未声明即越权并返回稳定错误码；服务层 MCP 工具在后台回合内可用。
2. 审批通过 → 任务 `done`；驳回 → 任务保持 `ready`（可重跑）且 `approval_state=rejected`；待审保持 `ready`；任务文件与 `task_view` 同时回写。
3. `attempt.model` 落 provider 实际使用的模型名。
4. 重跑完整业务链，triage 模型自己调用 `task_upsert`。

## 依赖方向

`DevHubd → DevHubd.Mcp → DevHubd.Core`。Core 不能引用 Mcp，因此：

- Core 定义回合工具端口 `ITurnToolService`；
- Mcp 实现该端口，复用 `ITaskWriteService` / `IQueueQuerySource` / `IResultQuerySource` / `IInputRequestService` / `IResultDecisionSource` / `ISchedulerStatusSource`；
- 宿主 DI 已注册这些实现，只需在 `McpModule.AddDevHubdMcp` 里注册端口实现。

## 改动清单

### 1. 角色工具面（Core）

- 新增 [src/DevHubd.Core/Execution/ITurnToolService.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Execution/ITurnToolService.cs)
  - `IReadOnlyList<ToolDescriptor> Describe()`
  - `Task<ToolExecutionResult> InvokeAsync(string toolName, JsonElement input, CancellationToken ct)`
- 修改 [ToolDispatcher.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Execution/ToolDispatcher.cs)
  - 构造参数新增 `IReadOnlyCollection<string> allowedTools` 与 `ITurnToolService? serviceTools`。
  - `Describe()` = 文件工具（`read_file/list_dir/search/write_file/apply_patch`，`ask_input` 需同时 `canRequestInput`）+ 服务工具，全部按 `allowedTools` 过滤。
  - `ExecuteAsync()` 先判白名单：未声明 → `TOOL_NOT_ALLOWED`（新增 `DevHubErrorCodes.ToolNotAllowed`）；服务工具转交 `serviceTools`。
- 修改 [TurnRunner.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Execution/TurnRunner.cs)
  - 从 `RoleSnapshotStore` 读取角色快照的 `Tools` JSON，解析为集合；找不到角色时用显式常量 `DefaultRoleId = "implementer"` 与显式 `DefaultToolNames`（文件工具 + `scheduler_status/queue_list/queue_get/result_list/result_ack/ask_input`），不依赖隐式兜底。
  - 构造 `ToolDispatcher` 时传入允许集合与 `ITurnToolService`。
  - 系统提示词明确列出本回合可用工具与越权后果。
- 修改角色声明 [plugin/roles/implementer.json](file:///e:/copilot_workspace/userdrive-devhub/plugin/roles/implementer.json)、[triage.json](file:///e:/copilot_workspace/userdrive-devhub/plugin/roles/triage.json)、[reviewer.json](file:///e:/copilot_workspace/userdrive-devhub/plugin/roles/reviewer.json)
  - implementer：`read_file/list_dir/search/write_file/apply_patch/ask_input` + 既有工具。
  - triage：`read_file/list_dir/search/ask_input` + 既有工具（不写工作区）。
  - reviewer：`read_file/list_dir/search` + 既有工具。
- 修改 [scripts/validate-plugin.mjs](file:///e:/copilot_workspace/userdrive-devhub/scripts/validate-plugin.mjs) 的 `KNOWN_TOOLS`，加入 6 个文件工具名。
- 新增 [src/DevHubd.Mcp/Turns/McpTurnToolService.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Mcp/Turns/McpTurnToolService.cs)
  - 8 个工具的 descriptor（`task_upsert`、`queue_list`、`queue_get`、`scheduler_status`、`result_list`、`result_ack`、`input_request_list`、`input_request_get`、`result_approve`、`result_reject`）。
  - `InvokeAsync` 按名分派到既有服务接口，输出用 `ToolJson.Options` 与既有 `DevHubd.Mcp.Contracts` 记录整形；失败返回稳定错误码。
- 修改 [McpModule.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Mcp/McpModule.cs)：`services.AddSingleton<ITurnToolService, McpTurnToolService>();`

### 2. 任务状态回写

- 新增 [src/DevHubd.Core/Tasks/TaskFileStatusUpdater.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Tasks/TaskFileStatusUpdater.cs)：按 `task_view.source_path` 解析任务 JSON，只改 `status` 字段后原子写回（临时文件 + rename，UTF-8 无 BOM）。
- 修改 [src/DevHubd.Core/Results/ResultDecisionStore.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Results/ResultDecisionStore.cs) 的 `DecideAsync`：
  - 通过：`result.outcome=delivered`、`task_view.approval_state=approved`、`task_view.status=done`，并回写任务文件 `status=done`。
  - 驳回：`result.outcome=failed`、`task_view.approval_state=rejected`、`task_view.status=ready`，并回写任务文件 `status=ready`（可重新入队）。
  - 同一事务内完成 result 与 task_view；任务文件写入在 DB 提交后执行，失败时记录日志并由扫描器下一轮以文件为准（文件已改则一致）。
- 待审批：`ExecutionCoordinator.SettleAsync` 现有 `approval_state=pending` 保持不变，任务状态仍 `ready`。
- 文档 [docs/mcp-tool-contract.md](file:///e:/copilot_workspace/userdrive-devhub/docs/mcp-tool-contract.md) 记录状态迁移表：`pending → (approve) done / (reject) ready`。

### 3. attempt.model 回写

- [ModelContracts.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Models/ModelContracts.cs)：`TurnResult` 增加 `public string? Model { get; init; }`（非位置参数，兼容既有构造）。
- [ChatTurnExecutor.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Providers/ChatTurnExecutor.cs)：成功时 `Model = response.ModelId ?? request.Model`；失败/取消路径 `Model = request.Model`。
- [OpenAiCompatibleModelClient.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Providers/OpenAiCompatibleModelClient.cs)：解析出 `model = request.Model ?? _options.Model` 后，用 `request with { Model = model }` 执行，保证默认模型名可回传。
- [TurnRunner.cs](file:///e:/copilot_workspace/userdrive-devhub/src/DevHubd.Core/Execution/TurnRunner.cs)：`TurnRunOutcome.Model = result.Model ?? request.Model`，不再直接返回 `request.Model`。

### 4. 测试

- 新增 `tests/DevHubd.Tests/TurnToolSurfaceTests.cs`
  - 声明 `task_upsert` 的角色：`Describe()` 含该工具，`ExecuteAsync` 调用 fake `ITurnToolService` 成功。
  - 未声明 `task_upsert` 的角色：`Describe()` 不含，`ExecuteAsync` 返回 `TOOL_NOT_ALLOWED`。
  - 缺省角色：工具集等于显式 `DefaultToolNames`。
- 扩展 `tests/DevHubd.Tests/P2ExecutionTests.cs` 或新增审批测试：
  - 审批通过后 `task_view.status=done` 且任务文件 `status=done`。
  - 驳回后 `status=ready`、`approval_state=rejected`。
  - `attempt.model` 在回合成功后非 NULL。
- 修改既有测试中以旧构造签名创建 `ToolDispatcher` 的位置（传显式允许集合）。

## 验证

1. `dotnet build DevHubd.sln`（0 警告 0 错误）
2. `dotnet test DevHubd.sln`（不带 `--nologo`，全绿 0 跳过）
3. `node scripts/validate-plugin.mjs`
4. `node scripts/check-encoding.mjs`
5. `dotnet publish src/DevHubd -c Release -o artifacts/publish`
6. 按进程名结束残留 `devhubd` → 启动 E2E 实例（独立 data-dir，`DEVHUB_PLUGIN_DIR`，真实 OpenAI key）
7. `node scripts/smoke-mcp.mjs`
8. 真机业务链：问题录入 → 模型自己调 `task_upsert` 建任务 → 角色分派（明确非缺省角色） → 执行 → 清单状态更新 → 结果 `awaiting_review` → 审批
   - 记录真实 task id / queueItemId / attemptId / resultId、状态迁移、token、`usageAvailable`、`attempt.model`
   - 明确是否存在人工预置或脚本代录；若断链如实报告根因
9. 提交并推送

## 约束

- 所有新增/修改文件 UTF-8 无 BOM；中文注释；`ILogger` + 结构化字段，不拼日志字符串。
- 官方包优先；不新增第三方依赖。
- 不新增 `Console.WriteLine`；不在日志中输出凭据。
